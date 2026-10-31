using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CacheLens;
public static class HistoryReader
{
    static readonly string[] Parts = ["", "-wal", "-journal"];
    // Copy through shared read handles, including SQLite's persistent sidecars.
    // SQLite opens only the private copy, so journal recovery never touches Chrome's files.
    public static List<Page> Read(string source, string profile)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var temp = Path.Combine(Path.GetTempPath(), "CacheLens-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var copy = Path.Combine(temp, "History");
            try
            {
                var copied = new Dictionary<string, byte[]>();
                foreach (var suffix in Parts)
                {
                    var path = source + suffix;
                    if (suffix != "" && !File.Exists(path)) continue;
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using (var output = File.Create(copy + suffix)) input.CopyTo(output);
                    using var check = File.OpenRead(copy + suffix);
                    copied[suffix] = SHA256.HashData(check);
                }
                // A changing database/WAL is not treated as a consistent copy.
                foreach (var suffix in Parts)
                {
                    if (!copied.TryGetValue(suffix, out var expected))
                    { if (File.Exists(source + suffix)) throw new IOException("History changed during copying."); continue; }
                    using var check = new FileStream(source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (!SHA256.HashData(check).SequenceEqual(expected)) throw new IOException("History changed during copying.");
                }
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
                db.Open();
                using (var verify = db.CreateCommand())
                {
                    verify.CommandText = "PRAGMA quick_check";
                    if (!string.Equals(verify.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("History copy failed SQLite's integrity check.");
                }
                var pages = new List<Page>();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT url, title, last_visit_time FROM urls ORDER BY last_visit_time DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    var url = reader.GetString(0);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                    DateTime? date = null;
                    try { if (!reader.IsDBNull(2)) date = DateTime.FromFileTimeUtc(checked(reader.GetInt64(2) * 10)); } catch { }
                    pages.Add(new(Guid.NewGuid().ToString("N"), url, reader.IsDBNull(1) ? "" : reader.GetString(1), profile, date, "history", "", "", "", 0, 0));
                }
                return pages;
            }
            catch (Exception ex) { last = ex; if (attempt < 2) Thread.Sleep(100); }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }
        throw new IOException("Could not read a stable history copy: " + last?.Message, last);
    }
}
