using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CacheLens;
public static class SelfTest
{
    public static void WriteSimple(string file, string url, byte[] body, string headers, bool sha = false)
    {
        var key = Encoding.UTF8.GetBytes("1/0/" + url);
        var meta = Encoding.UTF8.GetBytes("pickle\0HTTP/1.1 200 OK\0" + headers + "\0\0");
        using var writer = new BinaryWriter(System.IO.File.Create(file));
        writer.Write(0xfcfb6d1ba7725c30UL); writer.Write(9U); writer.Write(key.Length); writer.Write(0UL);
        writer.Write(key); writer.Write(body); Footer(writer, 0, false); writer.Write(meta);
        if (sha) writer.Write(new byte[32]);
        Footer(writer, meta.Length, sha);
    }
    static void Footer(BinaryWriter writer, int size, bool sha)
    { writer.Write(0xf4fa6f45970d41d8UL); writer.Write(sha ? 2U : 0U); writer.Write(0U); writer.Write(size); writer.Write(0U); }
    public static string CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "CacheLens-test-" + Guid.NewGuid().ToString("N"), "User Data");
        var folder = Path.Combine(root, "Default", "Cache", "Cache_Data"); Directory.CreateDirectory(folder);
        WriteSimple(Path.Combine(folder, "page_0"), "https://example.test/recovered", Encoding.UTF8.GetBytes("<!doctype html><html><head><title>Recovered evidence</title><link rel='stylesheet' href='/style.css'></head><body><h1>Recovered evidence</h1><p>This is a saved page.</p><script>document.body.textContent='SCRIPT RAN'</script><img src='/pixel.png'><img src='https://never-contact.invalid/track'></body></html>"), "Content-Type: text/html", true);
        WriteSimple(Path.Combine(folder, "css_0"), "https://example.test/style.css", Encoding.UTF8.GetBytes("h1 { color: rgb(10, 120, 80); }"), "Content-Type: text/css");
        WriteSimple(Path.Combine(folder, "png_0"), "https://example.test/pixel.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="), "Content-Type: image/png");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "Default", "History"), Pooling = false }.ToString()); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE urls(url TEXT,title TEXT,last_visit_time INTEGER); INSERT INTO urls VALUES ('https://example.test/old','Important older visit', $date)";
        cmd.Parameters.AddWithValue("$date", DateTime.UtcNow.AddMonths(-3).ToFileTimeUtc() / 10); cmd.ExecuteNonQuery();
        return root;
    }
    public static void Run()
    {
        var root = CreateFixture();
        try
        {
            void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
            var scan = CacheReader.Scan(root);
            Assert(scan.Resources == 3, "resource count");
            Assert(scan.Pages.Count(p => p.Kind == "history") == 1, "history count");
            var page = scan.Pages.Single(p => CacheReader.IsHtml(p));
            Assert(page.Url == "https://example.test/recovered" && page.Title == "Recovered evidence", "URL, SHA footer and title");
            Assert(page.SearchText.Contains("saved page"), "search text");
            var history = scan.Pages.Single(p => p.Kind == "history");
            Assert(history.Date < DateTime.UtcNow.AddMonths(-2), "old history kept without cutoff");
            var html = Preview.Html(Encoding.UTF8.GetString(CacheReader.Body(page)), new Uri(page.Url), "/test");
            Assert(!html.Contains("<script") && html.Contains("/test/asset?url="), "offline HTML rewrite");
            Assert(Preview.Css("a{background:url('../a.png')}", new Uri("https://example.test/css/main.css"), "/test").Contains("https%3A%2F%2Fexample.test%2Fa.png"), "CSS relative URL");
            foreach (var enc in new[] { "gzip", "deflate", "br" })
            {
                using var data = new MemoryStream();
                using (Stream stream = enc switch { "gzip" => new GZipStream(data, CompressionLevel.Fastest, true), "br" => new BrotliStream(data, CompressionLevel.Fastest, true), _ => new ZLibStream(data, CompressionLevel.Fastest, true) }) stream.Write(Encoding.UTF8.GetBytes("hello"));
                Assert(Encoding.UTF8.GetString(CacheReader.Decode(data.ToArray(), enc)) == "hello", enc);
            }
            var bad = Path.Combine(root, "bad_0");
            System.IO.File.WriteAllBytes(bad, [1, 2, 3]);
            bool rejected = false; try { CacheReader.Simple(bad, "test"); } catch { rejected = true; }
            Assert(rejected, "malformed entry rejected");
            TestLegacy(Path.Combine(root, "Legacy"));
            TestLiveHistory(Path.Combine(root, "Live"));
            using (var raw = new MemoryStream())
            {
                using (var deflater = new DeflateStream(raw, CompressionLevel.Fastest, true)) deflater.Write(Encoding.UTF8.GetBytes("raw deflate"));
                Assert(Encoding.UTF8.GetString(CacheReader.Decode(raw.ToArray(), "deflate")) == "raw deflate", "raw deflate fallback");
            }
            using (var gzip = new MemoryStream())
            {
                using (var compressor = new GZipStream(gzip, CompressionLevel.Fastest, true)) compressor.Write(Encoding.UTF8.GetBytes("stacked"));
                using var brotli = new MemoryStream();
                using (var compressor = new BrotliStream(brotli, CompressionLevel.Fastest, true)) compressor.Write(gzip.ToArray());
                Assert(Encoding.UTF8.GetString(CacheReader.Decode(brotli.ToArray(), "gzip, br")) == "stacked", "stacked encoding");
            }
            var plain = Path.Combine(root, "plain_0");
            WriteSimple(plain, "https://example.test/plain", Encoding.UTF8.GetBytes("<html>Already decoded</html>"), "Content-Type: text/html\0Content-Encoding: gzip");
            Assert(Encoding.UTF8.GetString(CacheReader.Body(CacheReader.Simple(plain, "test"))).Contains("Already decoded"), "already decoded HTML");
            WriteSimple(plain, "https://example.test/broken", [1,2,3,4], "Content-Type: text/html\0Content-Encoding: gzip");
            var brokenScan = CacheReader.Scan(root);
            Assert(brokenScan.Pages.Any(p => p.Url == "https://example.test/broken" && p.Problem.Length > 0), "per-page decoding diagnostic");
            Assert(brokenScan.Pages.Any(p => p.Kind == "history"), "bad page does not stop history");
            Console.WriteLine("PASS: Simple and legacy cache, SHA footer, history without cutoff, title and text search, gzip/deflate/Brotli, malformed entry, offline rewrite");
            Console.WriteLine("PASS: open WAL database snapshot includes committed visits, excludes uncommitted visits, preserves source files; raw deflate, stacked encodings, already-decoded HTML, per-entry errors");
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
    }
    static void TestLiveHistory(string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "History");
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()); writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE urls(url TEXT,title TEXT,last_visit_time INTEGER); INSERT INTO urls VALUES ('https://wal.test/committed','WAL visit',0); BEGIN IMMEDIATE; INSERT INTO urls VALUES ('https://wal.test/uncommitted','Not committed',0);";
        command.ExecuteNonQuery();
        var before = System.IO.File.ReadAllBytes(file); var walBefore = System.IO.File.ReadAllBytes(file + "-wal");
        var found = HistoryReader.Read(file, "Live");
        if (found.Count != 1 || found[0].Url != "https://wal.test/committed") throw new Exception("Live WAL snapshot returned wrong rows");
        if (!before.SequenceEqual(System.IO.File.ReadAllBytes(file)) || !walBefore.SequenceEqual(System.IO.File.ReadAllBytes(file + "-wal"))) throw new Exception("Source database was changed");
        command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
    }
    static void TestLegacy(string root)
    {
        Directory.CreateDirectory(root);
        var index = new byte[372]; BitConverter.GetBytes(0xc103cac3U).CopyTo(index, 0); BitConverter.GetBytes(0xa0010000U).CopyTo(index, 368);
        System.IO.File.WriteAllBytes(Path.Combine(root, "index"), index);
        var data = new byte[8192 + 256]; var key = Encoding.UTF8.GetBytes("https://legacy.test/page");
        BitConverter.GetBytes(key.Length).CopyTo(data, 8192 + 32); key.CopyTo(data, 8192 + 96);
        var meta = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\0Content-Type: text/html\0\0"); var body = Encoding.UTF8.GetBytes("<html>Legacy page</html>");
        BitConverter.GetBytes(meta.Length).CopyTo(data, 8192 + 40); BitConverter.GetBytes(body.Length).CopyTo(data, 8192 + 44);
        BitConverter.GetBytes(0x80000001U).CopyTo(data, 8192 + 56); BitConverter.GetBytes(0x80000002U).CopyTo(data, 8192 + 60);
        System.IO.File.WriteAllBytes(Path.Combine(root, "data_1"), data); System.IO.File.WriteAllBytes(Path.Combine(root, "f_000001"), meta); System.IO.File.WriteAllBytes(Path.Combine(root, "f_000002"), body);
        var page = CacheReader.Legacy(root, "test").Single();
        if (page.Url != "https://legacy.test/page" || Encoding.UTF8.GetString(CacheReader.Body(page)) != "<html>Legacy page</html>") throw new Exception("Legacy reader failed");
    }
}
