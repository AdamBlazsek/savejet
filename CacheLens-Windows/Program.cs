using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using CacheLens;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (args.Contains("--self-test")) { SelfTest.Run(); return; }
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();
var token = "/" + Guid.NewGuid().ToString("N");
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
if (args.Contains("--fixture")) root = SelfTest.CreateFixture();
var scan = new ScanResult([], 0, 0, []);
var busy = false; var gate = new object();
Dictionary<string, Page> byId = []; Dictionary<string, Page> assets = [];
async Task Scan(string folder)
{
    lock (gate) { if (busy) return; busy = true; }
    try
    {
        var result = await Task.Run(() => CacheReader.Scan(folder));
        var nextAssets = new Dictionary<string, Page>(StringComparer.Ordinal);
        foreach (var p in result.Pages.Where(p => p.Kind == "cache")) nextAssets.TryAdd(p.Url, p);
        lock (gate) { scan = result; byId = result.Pages.ToDictionary(p => p.Id); assets = nextAssets; root = folder; }
    }
    catch (Exception ex) { lock (gate) scan = new([], 0, 0, ["Could not scan that folder: " + ex.Message]); }
    finally { lock (gate) busy = false; }
}
app.Use(async (context, next) =>
{
    // Reject foreign Host headers; the random path also keeps local data off predictable endpoints.
    if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.MapGet(token + "/", () =>
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CacheLens.Windows.ui.html")
        ?? Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.EndsWith("ui.html")).Select(n => Assembly.GetExecutingAssembly().GetManifestResourceStream(n)).First()!;
    using var reader = new StreamReader(stream!);
    return Results.Content(reader.ReadToEnd().Replace("__PREFIX__", token), "text/html");
});
app.MapGet(token + "/status", () => { lock (gate) return Results.Json(new { busy, root, resources = scan.Resources, skipped = scan.Skipped, notices = scan.Notices }); });
app.MapPost(token + "/scan", async (HttpRequest request) =>
{
    if (!request.HasJsonContentType()) return Results.BadRequest();
    var input = await request.ReadFromJsonAsync<ScanInput>();
    var folder = string.IsNullOrWhiteSpace(input?.Folder) ? root : input.Folder;
    if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\")) return Results.BadRequest(new { message = "Enter a full local folder path." });
    _ = Scan(folder); return Results.Ok();
});
app.MapGet(token + "/search", (string? q, string? kind, string? after, string? before) =>
{
    lock (gate)
    {
        var found = scan.Pages.Where(p => p.Kind == "history" || CacheReader.IsHtml(p));
        if (kind is "cache" or "history") found = found.Where(p => p.Kind == kind);
        if (!string.IsNullOrWhiteSpace(q)) found = found.Where(p => p.Url.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || p.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (DateTime.TryParse(after, out var a)) found = found.Where(p => p.Date?.ToLocalTime().Date >= a.Date);
        if (DateTime.TryParse(before, out var b)) found = found.Where(p => p.Date?.ToLocalTime().Date <= b.Date);
        var list = found.ToList();
        return Results.Json(new { count = list.Count, results = list.Take(300).Select(p => new { p.Id, p.Url, p.Title, p.Profile, p.Date, p.Kind, available = p.Kind == "cache" || assets.TryGetValue(p.Url, out var cached) && CacheReader.IsHtml(cached) }) });
    }
});
app.MapGet(token + "/preview/{id}", (string id, HttpContext ctx) =>
{
    Page? p;
    lock (gate)
    {
        byId.TryGetValue(id, out p);
        if (p?.Kind == "history") assets.TryGetValue(p.Url, out p);
    }
    // An iframe sandbox and CSP apply even to malformed cached HTML or active SVG assets.
    ctx.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; media-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'self'";
    if (p == null) return Results.Content("<h2>History record only</h2><p>Chrome remembers this address, but a saved page body was not found. Use Open original or Check archive to look for another copy.</p>", "text/html");
    try { return Results.Content(Preview.Html(Encoding.UTF8.GetString(CacheReader.Body(p)), new Uri(p.Url), token), "text/html"); }
    catch { return Results.Content("<h2>Preview unavailable</h2><p>This entry may have changed or be incomplete. Quit Chrome and rescan.</p>", "text/html"); }
});
app.MapGet(token + "/asset", (string? url, HttpContext ctx) =>
{
    if (url == null) return Results.NotFound();
    Page? p; lock (gate) assets.TryGetValue(url, out p);
    if (p == null) return Results.NotFound();
    ctx.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; form-action 'none'; base-uri 'none'";
    try
    {
        var body = CacheReader.Body(p);
        if (p.Mime.Contains("css")) return Results.Content(Preview.Css(Encoding.UTF8.GetString(body), new Uri(p.Url), token), "text/css");
        if (CacheReader.IsHtml(p)) return Results.Content(Preview.Html(Encoding.UTF8.GetString(body), new Uri(p.Url), token), "text/html");
        return Results.Bytes(body, p.Mime);
    }
    catch { return Results.NotFound(); }
});
app.MapPost(token + "/quit", (HttpRequest request) => { if (!request.HasJsonContentType()) return Results.BadRequest(); app.Lifetime.StopApplication(); return Results.Ok(); });
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + token + "/";
Console.WriteLine("CacheLens — Windows Chrome cache and history viewer\n\n" + address + "\n\nKeep this window open. Close it or press Ctrl+C to quit.\nAll scanning and previews stay on this computer.");
_ = Scan(root);
if (!args.Contains("--no-browser")) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
await app.WaitForShutdownAsync();
record ScanInput(string? Folder);
