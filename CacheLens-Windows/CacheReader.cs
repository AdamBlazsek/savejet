using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CacheLens;

public sealed record Page(string Id, string Url, string Title, string Profile, DateTime? Date,
    string Kind, string Mime, string Encoding, string File, long Offset, int Length, string SearchText = "");
public sealed record ScanResult(List<Page> Pages, int Resources, int Skipped, List<string> Notices);

public static class CacheReader
{
    public const int MaxSize = 64 * 1024 * 1024;
    static uint U(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
    static ulong Q(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8));
    public static byte[] Read(string path, long offset = 0, int? count = null)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var n = count ?? checked((int)f.Length);
        if (offset < 0 || n < 0 || n > MaxSize || offset > f.Length - n) throw new InvalidDataException("Invalid cache bounds");
        f.Position = offset;
        var b = new byte[n]; f.ReadExactly(b); return b;
    }
    static string Url(string key)
    {
        var matches = Regex.Matches(key, @"https?://[^\s\x00]+", RegexOptions.IgnoreCase);
        if (matches.Count == 0) throw new InvalidDataException("No HTTP URL");
        return matches[^1].Value;
    }
    static (string mime, string encoding) Headers(byte[] metadata, byte[]? sample = null)
    {
        var lines = Encoding.UTF8.GetString(metadata).Split('\0');
        string Header(string name) => lines.FirstOrDefault(x => x.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "";
        var mime = Header("content-type").Split(';')[0].ToLowerInvariant();
        if (mime.Length == 0)
        {
            var t = Encoding.UTF8.GetString((sample ?? []).AsSpan(0, Math.Min(sample?.Length ?? 0, 512)));
            mime = Regex.IsMatch(t, "<!doctype html|<html", RegexOptions.IgnoreCase) ? "text/html" : "application/octet-stream";
        }
        return (mime, Header("content-encoding").ToLowerInvariant());
    }
    public static Page Simple(string file, string profile)
    {
        var b = Read(file);
        if (b.Length < 72 || Q(b, 0) != 0xfcfb6d1ba7725c30) throw new InvalidDataException();
        var k = checked((int)U(b, 12));
        if (k <= 0 || k > b.Length - 72) throw new InvalidDataException();
        var eof = b.Length - 24;
        if (Q(b, eof) != 0xf4fa6f45970d41d8) throw new InvalidDataException();
        var end = eof - ((U(b, eof + 8) & 2) != 0 ? 32 : 0);
        var start = end - checked((int)U(b, eof + 16));
        var bodyStart = 24 + k; var bodyEnd = start - 24;
        if (bodyEnd < bodyStart || Q(b, bodyEnd) != 0xf4fa6f45970d41d8) throw new InvalidDataException();
        var (mime, enc) = Headers(b[start..end], b[bodyStart..Math.Min(bodyEnd, bodyStart + 512)]);
        return new Page(Guid.NewGuid().ToString("N"), Url(Encoding.UTF8.GetString(b, 24, k)), "", profile,
            System.IO.File.GetLastWriteTimeUtc(file), "cache", mime, enc, file, bodyStart, bodyEnd - bodyStart);
    }
    // Legacy Chromium block-file addresses, including external f_ files.
    static (string file, long offset, int capacity) Address(string folder, uint addr)
    {
        if ((addr & 0x80000000) == 0) throw new InvalidDataException();
        var type = (addr >> 28) & 7;
        if (type == 0) return (Path.Combine(folder, "f_" + (addr & 0x0fffffff).ToString("x6")), 0, MaxSize);
        var block = type switch { 1 => 36, 2 => 256, 3 => 1024, 4 => 4096, _ => throw new InvalidDataException() };
        return (Path.Combine(folder, "data_" + ((addr >> 16) & 255)), 8192L + (addr & 65535) * block, (int)(((addr >> 24) & 3) + 1) * block);
    }
    static byte[] Block(string folder, uint addr, int size)
    {
        var a = Address(folder, addr);
        if (size > a.capacity) throw new InvalidDataException();
        return Read(a.file, a.offset, size);
    }
    public static IEnumerable<Page> Legacy(string folder, string profile)
    {
        var index = Read(Path.Combine(folder, "index"));
        if (index.Length < 368 || U(index, 0) != 0xc103cac3) yield break;
        var visited = new HashSet<uint>();
        for (var i = 368; i + 4 <= index.Length; i += 4)
        {
            var addr = U(index, i);
            while (addr != 0 && visited.Add(addr))
            {
                var entryAddress = addr;
                byte[] entry;
                try { entry = Block(folder, addr, 256); } catch { break; }
                addr = U(entry, 4);
                Page? page = null;
                try
                {
                    if (U(entry, 20) != 0) continue;
                    var keySize = checked((int)U(entry, 32));
                    if (keySize <= 0 || keySize > 1024 * 1024) continue;
                    var longKey = U(entry, 36);
                    byte[] key;
                    if (longKey != 0) key = Block(folder, longKey, keySize);
                    else
                    {
                        var current = Address(folder, entryAddress);
                        if (keySize > current.capacity - 96) continue;
                        key = Read(current.file, current.offset + 96, keySize);
                    }
                    var meta = Block(folder, U(entry, 56), checked((int)U(entry, 40)));
                    var size = checked((int)U(entry, 44));
                    if (size <= 0 || size > MaxSize) continue;
                    var body = Address(folder, U(entry, 60));
                    if (size > body.capacity) continue;
                    var (mime, enc) = Headers(meta, Read(body.file, body.offset, Math.Min(size, 512)));
                    page = new Page(Guid.NewGuid().ToString("N"), Url(Encoding.UTF8.GetString(key)), "", profile,
                        System.IO.File.GetLastWriteTimeUtc(body.file), "cache", mime, enc, body.file, body.offset, size);
                }
                catch { }
                if (page != null) yield return page;
            }
        }
    }
    public static byte[] Decode(byte[] data, string encoding)
    {
        if (encoding is "" or "identity") return data;
        using var input = new MemoryStream(data);
        using Stream decoder = encoding switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new ZLibStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new InvalidDataException("Unsupported compression: " + encoding)
        };
        using var output = new MemoryStream(); var buffer = new byte[65536]; int n;
        while ((n = decoder.Read(buffer)) != 0)
        {
            if (output.Length + n > MaxSize) throw new InvalidDataException("Decoded entry exceeds 64 MB");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
    public static byte[] Body(Page p) => Decode(Read(p.File, p.Offset, p.Length), p.Encoding);
    static IEnumerable<string> Files(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
    }
    public static ScanResult Scan(string root)
    {
        var pages = new List<Page>(); var skipped = 0; var notices = new List<string>();
        if (!Directory.Exists(root)) return new(pages, 0, 0, ["Chrome folder not found. Choose a copied profile or cache folder below."]);
        var profiles = Path.GetFileName(root).Equals("User Data", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetDirectories(root).Where(x => Path.GetFileName(x) is "Default" or "Guest Profile" || Path.GetFileName(x).StartsWith("Profile ")).ToArray()
            : [root];
        foreach (var profile in profiles)
        {
            var name = Path.GetFileName(profile);
            var cache = Directory.Exists(Path.Combine(profile, "Cache")) ? Path.Combine(profile, "Cache") : profile;
            foreach (var file in Files(cache, "*_0"))
                try { pages.Add(Simple(file, name)); } catch { skipped++; }
            foreach (var index in Files(cache, "index"))
                try { pages.AddRange(Legacy(Path.GetDirectoryName(index)!, name)); } catch { skipped++; }
            var history = Path.Combine(profile, "History");
            if (System.IO.File.Exists(history))
            {
                try
                {
                    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = history, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
                    db.Open();
                    using var command = db.CreateCommand();
                    command.CommandText = "SELECT url, title, last_visit_time FROM urls ORDER BY last_visit_time DESC";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var url = reader.GetString(0);
                        if (!url.StartsWith("http://") && !url.StartsWith("https://")) continue;
                        var ticks = reader.GetInt64(2);
                        DateTime? date = null;
                        try { date = DateTime.FromFileTimeUtc(checked(ticks * 10)); } catch { }
                        pages.Add(new(Guid.NewGuid().ToString("N"), url, reader.IsDBNull(1) ? "" : reader.GetString(1), name, date, "history", "", "", "", 0, 0));
                    }
                }
                catch { notices.Add(name + ": history could not be read. Quit Chrome and rescan; a locked database or a copied profile missing its WAL file can cause this."); }
            }
        }
        int resources = pages.Count(p => p.Kind == "cache");
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            if (p.Kind != "cache" || !IsHtml(p)) continue;
            try
            {
                var bytes = Body(p);
                var text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 2_000_000)));
                var title = System.Net.WebUtility.HtmlDecode(Regex.Match(text, @"<title\b[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase).Groups[1].Value);
                pages[i] = p with { Title = title, SearchText = text };
            }
            catch { notices.Add("A cached page could not be decoded; its entry may be incomplete or have changed."); }
        }
        return new(pages.OrderByDescending(p => p.Date).ToList(), resources, skipped, notices.Distinct().ToList());
    }
    public static bool IsHtml(Page p) => p.Mime.Contains("html", StringComparison.OrdinalIgnoreCase);
}
