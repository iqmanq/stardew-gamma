using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace Gamma.Services;

/// <summary>
/// Talks to any OpenAI-compatible chat completions API: OpenAI, OpenRouter, Groq, DeepSeek,
/// Together, Mistral, xAI, plus local servers like Ollama and LM Studio.
/// </summary>
public class OpenAiClient : LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly ModConfig Config;
    private readonly IMonitor Monitor;

    public OpenAiClient(ModConfig config, IMonitor monitor)
    {
        Config = config;
        Monitor = monitor;
    }

    private string Endpoint =>
        (string.IsNullOrWhiteSpace(Config.ApiBaseUrl) ? "https://api.openai.com/v1" : Config.ApiBaseUrl.TrimEnd('/'))
        + "/chat/completions";

    public override async Task<LlmResult> CompleteAsync(IReadOnlyList<WireMessage> messages, IReadOnlyList<ToolSpec> tools)
    {
        var wire = new JsonArray();
        foreach (var m in messages)
        {
            switch (m.Kind)
            {
                case "system":
                    wire.Add(new JsonObject { ["role"] = "system", ["content"] = m.Text });
                    break;
                case "user":
                    wire.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                case "assistant":
                    if (m.Assistant?.RawMessage != null)
                        wire.Add(JsonNode.Parse(m.Assistant.RawMessage.ToJsonString()));
                    else
                        wire.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Text ?? "" });
                    break;
                case "tool_result":
                    wire.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId,
                        ["content"] = m.ToolOutput ?? ""
                    });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = Config.Model,
            ["messages"] = wire,
            ["temperature"] = Config.Temperature,
            ["max_tokens"] = Config.MaxResponseTokens,
        };
        if (tools.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var t in tools)
            {
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.Parameters.ToJsonString()),
                    }
                });
            }
            body["tools"] = arr;
            body["tool_choice"] = "auto";
        }

        using var resp = await SendAsync(body.ToJsonString(), Config.UseRawKeyAuth);
        if (resp.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            // auth schemes differ between gateways (OpenRouter needs "Bearer <key>", opencode zen
            // wants the bare key) — on a 401, retry once with the other scheme
            Monitor.Log("Gamma: got 401, retrying with the other auth scheme", LogLevel.Trace);
            resp.Dispose();
            using var resp2 = await SendAsync(body.ToJsonString(), rawKey: !Config.UseRawKeyAuth);
            return await ParseResponseAsync(resp2);
        }
        return await ParseResponseAsync(resp);
    }

    private Task<HttpResponseMessage> SendAsync(string json, bool rawKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(Config.ApiKey))
            req.Headers.TryAddWithoutValidation("Authorization", rawKey ? Config.ApiKey : "Bearer " + Config.ApiKey);
        return Http.SendAsync(req);
    }

    private async Task<LlmResult> ParseResponseAsync(HttpResponseMessage resp)
    {
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"LLM API error {(int)resp.StatusCode}: {ExtractError(text)}");

        using var doc = JsonDocument.Parse(text);
        var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

        var result = new LlmResult();
        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            result.Content = content.GetString() ?? "";
        // some reasoning models return an empty content plus a reasoning field; better than nothing
        if (string.IsNullOrWhiteSpace(result.Content) && msg.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            result.Content = reasoning.GetString() ?? "";

        if (msg.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                string id = call.GetProperty("id").GetString();
                string name = call.GetProperty("function").GetProperty("name").GetString();
                string args = "{}";
                if (call.GetProperty("function").TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()))
                    args = a.GetString();
                result.ToolCalls.Add(new ToolCall { Id = id, Name = name, ArgumentsJson = args });
            }
        }

        // Echo the assistant message back exactly as the provider sent it. Some providers
        // attach per-call extra fields (e.g. Gemini's thought_signature) that must be
        // replayed on the next round-trip; rebuilding the message drops them and the
        // provider rejects the follow-up with a 400.
        result.RawMessage = JsonNode.Parse(msg.GetRawText());
        return result;
    }

    /// <summary>Pulls a human-readable message out of an API error body.</summary>
    internal static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
            }
        }
        catch { }
        return Truncate(body, 400);
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);
}
