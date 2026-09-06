using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace Gamma.Services;

/// <summary>Talks to the Anthropic Claude Messages API.</summary>
public class AnthropicClient : LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly ModConfig Config;

    public AnthropicClient(ModConfig config)
    {
        Config = config;
        Http.DefaultRequestHeaders.Remove("x-api-key");
        Http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey ?? "");
        Http.DefaultRequestHeaders.Remove("anthropic-version");
        Http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    private string Endpoint =>
        (string.IsNullOrWhiteSpace(Config.ApiBaseUrl) ? "https://api.anthropic.com/v1" : Config.ApiBaseUrl.TrimEnd('/'))
        + "/messages";

    public override async Task<LlmResult> CompleteAsync(IReadOnlyList<WireMessage> messages, IReadOnlyList<ToolSpec> tools)
    {
        // Anthropic takes the system prompt as a top-level parameter and only user/assistant messages.
        string system = string.Join("\n\n", messages.Where(m => m.Kind == "system").Select(m => m.Text));

        var wire = new JsonArray();
        foreach (var m in messages)
        {
            switch (m.Kind)
            {
                case "user":
                    wire.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                case "assistant":
                    if (m.Assistant?.RawMessage != null)
                        wire.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(m.Assistant.RawMessage.ToJsonString()) });
                    else
                        wire.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Text ?? "" });
                    break;
                case "tool_result":
                    wire.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = m.ToolCallId,
                            ["content"] = m.ToolOutput ?? ""
                        })
                    });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = Config.Model,
            ["max_tokens"] = Config.MaxResponseTokens,
            ["temperature"] = Config.Temperature,
            ["messages"] = wire,
        };
        if (!string.IsNullOrWhiteSpace(system)) body["system"] = system;
        if (tools.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var t in tools)
            {
                arr.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = JsonNode.Parse(t.Parameters.ToJsonString()),
                });
            }
            body["tools"] = arr;
        }

        using var resp = await Http.PostAsync(Endpoint,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Anthropic API error {(int)resp.StatusCode}: {OpenAiClient.ExtractError(text)}");

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("content");

        var result = new LlmResult();
        var blocks = new JsonArray();
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            string type = block.GetProperty("type").GetString();
            if (type == "text")
            {
                string t = block.GetProperty("text").GetString() ?? "";
                sb.Append(t);
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = t });
            }
            else if (type == "tool_use")
            {
                string id = block.GetProperty("id").GetString();
                string name = block.GetProperty("name").GetString();
                string args = block.GetProperty("input").GetRawText();
                if (string.IsNullOrWhiteSpace(args) || args == "null") args = "{}";
                result.ToolCalls.Add(new ToolCall { Id = id, Name = name, ArgumentsJson = args });
                blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = JsonNode.Parse(args) });
            }
        }
        result.Content = sb.ToString();
        result.RawMessage = blocks;
        return result;
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);
}
