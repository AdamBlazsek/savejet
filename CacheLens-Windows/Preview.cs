using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace CacheLens;
public static class Preview
{
    public static string Asset(string raw, Uri baseUrl, string prefix)
    {
        if (raw.StartsWith("#") || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return raw;
        if (Uri.TryCreate(baseUrl, raw, out var url) && url.Scheme is "http" or "https")
            return prefix + "/asset?url=" + Uri.EscapeDataString(url.GetLeftPart(UriPartial.Path) + url.Query);
        return "about:blank";
    }
    public static string Css(string css, Uri url, string prefix)
    {
        css = Regex.Replace(css, @"url\(\s*(['"" ]?)(.*?)\1\s*\)", m => "url(\"" + Asset(m.Groups[2].Value.Trim(), url, prefix).Replace("\"", "%22") + "\")", RegexOptions.IgnoreCase);
        return Regex.Replace(css, "@import\\s+(['\"])(.*?)\\1", m => "@import \"" + Asset(m.Groups[2].Value, url, prefix).Replace("\"", "%22") + "\"", RegexOptions.IgnoreCase);
    }
    public static string Html(string html, Uri url, string prefix)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var baseElement = doc.QuerySelector("base[href]");
        if (baseElement != null && Uri.TryCreate(url, baseElement.GetAttribute("href"), out var basis) && basis.Scheme is "http" or "https") url = basis;
        foreach (var el in doc.QuerySelectorAll("script,iframe,frame,object,embed,base,meta[http-equiv]").ToArray()) el.Remove();
        foreach (var el in doc.All)
        {
            foreach (var attr in el.Attributes.ToArray())
            {
                var name = attr.Name.ToLowerInvariant();
                if (name.StartsWith("on") || name is "srcdoc" or "srcset" or "integrity" or "nonce" or "ping" or "action" or "formaction") { el.RemoveAttribute(attr.Name); continue; }
                if (name is "src" or "href" or "poster" or "background" or "xlink:href") el.SetAttribute(attr.Name, Asset(attr.Value, url, prefix));
                if (name == "style") el.SetAttribute(attr.Name, Css(attr.Value, url, prefix));
            }
        }
        foreach (var el in doc.QuerySelectorAll("style")) el.TextContent = Css(el.TextContent, url, prefix);
        return "<!doctype html>" + doc.DocumentElement.OuterHtml;
    }
}
