using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace Gamma.Services;

/// <summary>Searches and reads the Stardew Valley Wiki via its MediaWiki API.</summary>
public class WikiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string ApiUrl;

    public WikiService(IModHelper helper, ModConfig config)
    {
        ApiUrl = string.IsNullOrWhiteSpace(config.WikiApiUrl)
            ? "https://stardewvalleywiki.com/mediawiki/api.php"
            : config.WikiApiUrl;
        if (!Http.DefaultRequestHeaders.UserAgent.ToString().Contains("Gamma"))
            Http.DefaultRequestHeaders.Add("User-Agent", "Gamma/1.0 (SMAPI mod)");
    }

    public async Task<string> SearchAsync(string query, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Empty search query.";
        try
        {
            string url = $"{ApiUrl}?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit={limit}&format=json";
            using var doc = await GetJsonAsync(url);
            var results = doc.RootElement.GetProperty("query").GetProperty("search");
            if (results.GetArrayLength() == 0) return $"No wiki pages matched '{query}'.";

            var sb = new StringBuilder();
            int i = 0;
            foreach (var r in results.EnumerateArray())
            {
                i++;
                string title = r.GetProperty("title").GetString();
                string snippet = WebUtility.HtmlDecode(StripHtml(r.GetProperty("snippet").GetString()));
                sb.AppendLine($"{i}. {title} — {snippet}");
            }
            sb.AppendLine("(Use get_wiki_page with one of these titles for full details.)");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "Wiki search failed: " + ex.Message;
        }
    }

    public async Task<string> GetPageAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Empty page title.";
        try
        {
            // The wiki has no TextExtracts extension, so pull the rendered HTML and strip it.
            string url = $"{ApiUrl}?action=parse&prop=text&redirects=1&formatversion=2&format=json&page={Uri.EscapeDataString(title)}";
            using var doc = await GetJsonAsync(url);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                string info = error.TryGetProperty("info", out var i) ? i.GetString() : "unknown error";
                return $"No wiki page found titled '{title}' ({info}). Try search_wiki first.";
            }

            string html = root.GetProperty("parse").GetProperty("text").GetString() ?? "";
            html = Regex.Replace(html, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<sup[^>]*>.*?</sup>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, "\\s+", " ").Trim();

            if (html.Length > 6000) html = html.Substring(0, 6000) + "…(truncated)";
            return html.Length > 0 ? html : $"The wiki page '{title}' appears to be empty.";
        }
        catch (Exception ex)
        {
            return "Wiki page fetch failed: " + ex.Message;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    internal static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        return Regex.Replace(html, "<[^>]+>", "");
    }
}
