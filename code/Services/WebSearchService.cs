using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace Gamma.Services;

/// <summary>Pluggable web search (Tavily / Brave / SearXNG) plus a plain web page reader.</summary>
public class WebSearchService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ModConfig Config;

    public WebSearchService(IModHelper helper, ModConfig config)
    {
        Config = config;
    }

    public bool Enabled
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Config.SearchProvider)) return false;
            if (Config.SearchProvider.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            if (Config.SearchProvider.Equals("searxng", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(Config.SearxngUrl);
            return !string.IsNullOrWhiteSpace(Config.SearchApiKey);
        }
    }

    public async Task<string> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Empty search query.";
        if (!Enabled)
            return "Web search is not configured. Edit Mods/Gamma/config.json and set SearchProvider (tavily, brave, or searxng) with its API key, then restart the game.";
        try
        {
            string provider = Config.SearchProvider.ToLowerInvariant();
            return provider switch
            {
                "tavily" => await SearchTavilyAsync(query),
                "brave" => await SearchBraveAsync(query),
                "searxng" => await SearchSearxngAsync(query),
                _ => $"Unknown search provider '{Config.SearchProvider}'."
            };
        }
        catch (Exception ex)
        {
            return "Web search failed: " + ex.Message;
        }
    }

    private async Task<string> SearchTavilyAsync(string query)
    {
        var body = new { api_key = Config.SearchApiKey, query, max_results = 5 };
        using var resp = await Http.PostAsJsonAsync("https://api.tavily.com/search", body);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return FormatResults(doc.RootElement.GetProperty("results"), "title", "url", "content");
    }

    private async Task<string> SearchBraveAsync(string query)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count=5");
        req.Headers.Add("X-Subscription-Token", Config.SearchApiKey);
        req.Headers.Add("Accept", "application/json");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return FormatResults(doc.RootElement.GetProperty("web").GetProperty("results"), "title", "url", "description");
    }

    private async Task<string> SearchSearxngAsync(string query)
    {
        string baseUrl = Config.SearxngUrl.TrimEnd('/');
        string json;
        try
        {
            json = await Http.GetStringAsync($"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden
                                              || ex.StatusCode == HttpStatusCode.NotFound)
        {
            // SearXNG ships with the JSON API disabled and answers 403; some setups 404
            return "Your SearXNG instance refused the JSON API. In its settings.yml, under 'search:', "
                + "add 'json' to 'formats:' (search:\\n  formats:\\n    - html\\n    - json) and restart the instance.";
        }
        using var doc = JsonDocument.Parse(json);
        return FormatResults(doc.RootElement.GetProperty("results"), "title", "url", "content");
    }

    private static string FormatResults(JsonElement results, string titleProp, string urlProp, string descProp)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var r in results.EnumerateArray())
        {
            i++;
            string title = GetString(r, titleProp);
            string url = GetString(r, urlProp);
            string desc = GetString(r, descProp);
            if (desc.Length > 300) desc = desc.Substring(0, 300) + "…";
            sb.AppendLine($"{i}. {title}");
            sb.AppendLine($"   {url}");
            sb.AppendLine($"   {desc}");
        }
        if (i == 0) sb.AppendLine("(no results)");
        return sb.ToString();
    }

    private static string GetString(JsonElement element, string prop) =>
        element.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    /// <summary>Fetches a web page and strips it down to readable text.</summary>
    public async Task<string> FetchPageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return "Invalid URL.";
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            string html = await resp.Content.ReadAsStringAsync();

            html = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, "\\s+", " ").Trim();

            if (html.Length > 6000) html = html.Substring(0, 6000) + "…(truncated)";
            return html.Length > 0 ? html : "(page had no readable text)";
        }
        catch (Exception ex)
        {
            return "Fetching the page failed: " + ex.Message;
        }
    }
}
