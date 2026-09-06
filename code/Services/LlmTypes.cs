using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Gamma.Services;

/// <summary>A tool call requested by the model.</summary>
public class ToolCall
{
    public string Id;
    public string Name;
    public string ArgumentsJson = "{}";
}

/// <summary>The model's response: either final text, or a batch of tool calls.</summary>
public class LlmResult
{
    public string Content = "";
    public List<ToolCall> ToolCalls = new();
    /// <summary>Provider-specific node to echo back into the conversation for this assistant turn.</summary>
    public JsonNode RawMessage;
}

/// <summary>A tool exposed to the model.</summary>
public class ToolSpec
{
    public string Name;
    public string Description;
    public JsonNode Parameters;
}

/// <summary>Provider-agnostic message for the conversation wire.</summary>
public class WireMessage
{
    public string Kind; // "system" | "user" | "assistant" | "tool_result"
    public string Text;
    public LlmResult Assistant;      // for Kind == "assistant" carrying tool calls
    public string ToolCallId;        // for Kind == "tool_result"
    public string ToolOutput;

    public static WireMessage System(string text) => new() { Kind = "system", Text = text };
    public static WireMessage User(string text) => new() { Kind = "user", Text = text };
    public static WireMessage AssistantText(string text) => new() { Kind = "assistant", Text = text };
    public static WireMessage AssistantResult(LlmResult result) => new() { Kind = "assistant", Assistant = result };
    public static WireMessage ToolResult(string callId, string output) => new() { Kind = "tool_result", ToolCallId = callId, ToolOutput = output };
}

public abstract class LlmClient
{
    public abstract Task<LlmResult> CompleteAsync(IReadOnlyList<WireMessage> messages, IReadOnlyList<ToolSpec> tools);
}
