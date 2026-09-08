using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace Gamma.Services;

/// <summary>Orchestrates a conversation: builds the prompt, runs the tool loop, raises UI events.</summary>
public class ChatService
{
    private readonly IModHelper Helper;
    private readonly IMonitor Monitor;
    private readonly ModConfig Config;
    private readonly GameStateService State;
    private readonly ToolRegistry Tools;
    /// <summary>The conversation store (multi-chat).</summary>
    public readonly ChatHistory History;

    private readonly ConcurrentQueue<Action> PendingUpdates = new();
    private readonly Func<LlmClient> ClientFactory;

    /// <summary>True while the displayed conversation has a request in flight.</summary>
    public bool Busy => History.Current.Busy;

    /// <summary>Raised on the game thread with the conversation that owns the message.</summary>
    public event Action<Conversation, bool, string, bool> MessageAdded;

    /// <summary>Apply background results on the game thread, even while the menu is closed.</summary>
    public void Update()
    {
        while (PendingUpdates.TryDequeue(out var update)) update();
    }

    private void QueueUpdate(Conversation conversation, Action update) => PendingUpdates.Enqueue(() =>
    {
        if (History.All.Contains(conversation)) update();
    });

    private void SetStatus(Conversation conversation, string status) =>
        QueueUpdate(conversation, () => conversation.Status = status);

    private void Notice(Conversation conversation, string text)
    {
        conversation.Notices.Add(text);
        MessageAdded?.Invoke(conversation, false, text, false);
    }

    public ChatService(IModHelper helper, IMonitor monitor, ModConfig config,
        GameStateService state, ToolRegistry tools, ChatHistory history, Func<LlmClient> clientFactory = null)
    {
        Helper = helper;
        Monitor = monitor;
        Config = config;
        State = state;
        Tools = tools;
        History = history;
        ClientFactory = clientFactory;
    }

    public bool Configured
    {
        get
        {
            if (string.Equals(Config.Provider, "anthropic", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(Config.ApiKey);
            return !string.IsNullOrWhiteSpace(Config.ApiKey) || !string.IsNullOrWhiteSpace(Config.ApiBaseUrl);
        }
    }

    public IReadOnlyList<ChatTurn> Turns => History.Turns;

    public void ResetHistory() => History.Clear();

    /// <summary>Start a request for the current chat. The task completes when background work
    /// finishes; Update applies its queued results on the game thread.</summary>
    public Task Ask(string userText, bool showUserInChat = true, bool addToHistory = true, string extraSystem = null)
    {
        if (string.IsNullOrWhiteSpace(userText)) return Task.CompletedTask;

        var conversation = History.Current;
        if (conversation.Busy)
        {
            Notice(conversation, "(I'm still working on your last question — give me a moment!)");
            return Task.CompletedTask;
        }
        if (!Configured)
        {
            Notice(conversation,
                "I'm not set up yet! Click the Settings button below (or run `gamma settings` in the SMAPI console) "
                + "to pick a provider and add your API key. Local models via Ollama or LM Studio need no key at all.");
            return Task.CompletedTask;
        }

        // Capture the destination and prompt before the user can switch conversations.
        var historySnapshot = History.Recent(Config.HistoryMessagesKept);
        if (addToHistory) History.Add(conversation, "user", userText);
        if (showUserInChat) MessageAdded?.Invoke(conversation, true, userText, addToHistory);
        string systemPrompt = BuildSystemPrompt(extraSystem);
        LlmClient client = ClientFactory?.Invoke() ?? CreateClient();
        string originalTitle = conversation.Title;
        string namingText = Config.AutoNameChats && conversation.Turns.Count == 1
            && conversation.Turns[0].Role == "user" ? conversation.Turns[0].Text : null;
        conversation.Busy = true;
        conversation.Status = "Thinking…";
        Monitor.Log($"Gamma: sending request (provider '{Config.Provider}', model '{Config.Model}')", LogLevel.Debug);

        return Task.Run(async () =>
        {
            try
            {
                string answer = await RunConversation(client, historySnapshot, userText, systemPrompt, conversation);
                QueueUpdate(conversation, () =>
                {
                    History.Add(conversation, "assistant", answer);
                    History.Save();
                    MessageAdded?.Invoke(conversation, false, answer, true);
                });
                if (namingText != null)
                    await TryAutoNameChat(client, conversation, namingText, originalTitle);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Gamma chat error: {ex}", LogLevel.Error);
                QueueUpdate(conversation, () => Notice(conversation,
                    "Sorry, something went wrong talking to the AI: " + FriendlyError(ex)));
            }
            finally
            {
                QueueUpdate(conversation, () =>
                {
                    conversation.Busy = false;
                    conversation.Status = null;
                });
            }
        });
    }

    private async Task<string> RunConversation(LlmClient client, List<ChatTurn> history, string userText, string systemPrompt, Conversation conversation)
    {
        var wire = new List<WireMessage> { WireMessage.System(systemPrompt) };
        foreach (var turn in history)
            wire.Add(turn.Role == "user" ? WireMessage.User(turn.Text) : WireMessage.AssistantText(turn.Text));
        wire.Add(WireMessage.User(userText));

        var specs = Tools.Specs;
        var seenCalls = new HashSet<string>(StringComparer.Ordinal);
        int rounds = Math.Max(1, Config.MaxToolRoundtrips);

        for (int round = 0; round < rounds; round++)
        {
            var result = await client.CompleteAsync(wire, specs);

            if (result.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(result.Content))
                    Monitor.Log($"Gamma: model returned no content and no tool calls. Raw message: {result.RawMessage?.ToJsonString()}", LogLevel.Debug);
                return string.IsNullOrWhiteSpace(result.Content) ? "(I couldn't think of a reply.)" : Sanitize(result.Content.Trim());
            }

            wire.Add(WireMessage.AssistantResult(result));
            foreach (var call in result.ToolCalls)
            {
                SetStatus(conversation, Tools.DisplayLabel(call.Name));
                Monitor.Log($"Tool call: {call.Name}({call.ArgumentsJson})", LogLevel.Debug);
                string output = Tools.Execute(call);

                // looping models re-run the identical call; push back instead of burning rounds
                if (!seenCalls.Add(call.Name + "|" + call.ArgumentsJson))
                    output = "You already ran this exact tool and got the result above. Do not run it again — use that result to answer.\n" + output;
                wire.Add(WireMessage.ToolResult(call.Id, output));
            }
        }

        // out of tool budget: force a final answer from what was gathered instead of a dead end
        SetStatus(conversation, "Writing the answer…");
        Monitor.Log("Gamma: tool budget exhausted, forcing a final answer", LogLevel.Debug);
        wire.Add(WireMessage.User(
            "You've used up your tool budget for this question. Do not request any more tools. "
            + "Answer the user's original question now using what you already gathered; if you "
            + "couldn't find it, say so plainly."));
        var final = await client.CompleteAsync(wire, Array.Empty<ToolSpec>());
        return string.IsNullOrWhiteSpace(final.Content)
            ? "(I couldn't reach an answer — try rephrasing your question.)"
            : Sanitize(final.Content.Trim());
    }

    /// <summary>
    /// After the first exchange in a fresh chat, asks the model for a short conversation
    /// title. Best-effort: any failure keeps the first-message auto-title.
    /// </summary>
    private async Task TryAutoNameChat(LlmClient client, Conversation convo, string firstMessage, string originalTitle)
    {
        try
        {
            var prompt = "Reply with ONLY a short title (3 to 6 words) for a chat that starts with the message below. "
                + "No quotes, no ending punctuation, same language as the message.\n\n"
                + firstMessage;
            var result = await client.CompleteAsync(
                new List<WireMessage> { WireMessage.User(prompt) }, Array.Empty<ToolSpec>());

            string title = Sanitize(result.Content ?? "");
            title = title.Split('\n')[0].Trim().Trim('"', '\'', ' ').TrimEnd('.', '!');
            if (title.Length > 48) title = title.Substring(0, 48).TrimEnd() + "…";
            if (title.Length < 2) return;

            QueueUpdate(convo, () =>
            {
                if (convo.Title != originalTitle) return; // preserve a manual rename during the request
                convo.Title = title;
                History.Save();
            });
            Monitor.Log($"Gamma: chat auto-named '{title}'", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Monitor.Log("Gamma: auto-naming the chat failed: " + ex.Message, LogLevel.Debug);
        }
    }

    private string BuildSystemPrompt(string extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are {Config.ChatbotName ?? "Gamma"}, a friendly AI assistant for a Stardew Valley player, living in a chat window on their screen.");
        sb.AppendLine();
        sb.AppendLine("Current game state snapshot:");
        sb.AppendLine(State.GetSnapshot());
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Use the get_* tools whenever the player asks about their own items, chests, quests, farm, animals, crops, friendships, progress or mods — the snapshot above is only a summary.");
        sb.AppendLine("- Use search_wiki (and get_wiki_page for details) for any factual question about Stardew Valley itself: items, prices, recipes, NPC schedules and gifts, bundles, events, mechanics.");
        sb.AppendLine("- Use web_search / get_web_page only for things the wiki can't answer (modding help, news, or non-game topics).");
        sb.AppendLine("- Keep answers reasonably short and conversational.");
        sb.AppendLine("- You are read-only: you can see the game but cannot change it. If asked to do something in-game, say so and suggest how they can do it.");
        sb.AppendLine("- The player may have mods that change or add content; check the installed mods list and prefer wiki answers for vanilla questions.");
        if (!string.IsNullOrWhiteSpace(extra)) sb.AppendLine().AppendLine(extra);
        return sb.ToString();
    }

    private LlmClient CreateClient() =>
        string.Equals(Config.Provider, "anthropic", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicClient(Config)
            : new OpenAiClient(Config, Monitor);

    private static string FriendlyError(Exception ex)
    {
        string msg = ex.Message ?? "";
        string lower = msg.ToLowerInvariant();
        if (lower.Contains("is not supported") || lower.Contains("model_not_found") || lower.Contains("does not exist"))
            return "the provider doesn't offer that model — open Settings and use the \"Load list\" button next to Model to pick one your key has access to.";
        if (lower.Contains("user not found"))
            return "the key doesn't match any account on this provider — it's probably incomplete or was revoked. OpenRouter keys look like 'sk-or-v1-' followed by 64 characters (73 total): yours doesn't match that, so something was mistyped or lost — clear the field in Settings and press Ctrl+V to paste it.";
        if (lower.Contains("401") || lower.Contains("unauthorized") || lower.Contains("invalid api key") || lower.Contains("missing api key"))
            return "the provider rejected your API key for this request — the key may be expired, or it may not have access to that model. (Some gateways also return this for unavailable models.)";
        if (lower.Contains("404") && lower.Contains("model"))
            return "the model name may be wrong for this provider — check \"Model\" in settings.";
        if (lower.Contains("429") || lower.Contains("rate"))
            return "the AI provider is rate-limiting you — wait a bit and try again.";
        if (lower.Contains("connection") || lower.Contains("refused") || lower.Contains("name resolved") || lower.Contains("no such host"))
            return "couldn't reach the AI server — check \"ApiBaseUrl\" and your internet connection.";
        if (lower.Contains("is unavailable"))
            return "the model is temporarily unavailable upstream — try again, or pick another model in Settings.";
        return msg;
    }

    /// <summary>Maps or strips characters the game's UI font can't draw — models love fancy
    /// quotes, dashes, arrows and emoji, and every one of those renders as a box in-game.</summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = text
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace("\u2013", "-").Replace("\u2014", "-").Replace("\u2212", "-")
            .Replace("\u2026", "...")
            .Replace("\u2192", "->").Replace("\u2190", "<-").Replace("\u2194", "<->")
            .Replace("\u00B7", "*").Replace("\u2022", "*")
            .Replace("\u2605", "*").Replace("\u2606", "*")
            .Replace("\u00A0", " ");

        // models answer in Markdown; the game font draws * as a boxy glyph, so strip the
        // lightweight syntax (bold, italics, inline code, heading hashes)
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\*\\*([^*\n]+)\\*\\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, "(?<!\\*)\\*([^*\n]+)\\*(?!\\*)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, "`([^`\n]+)`", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, "(?m)^#{1,6}\\s+", "");

        try
        {
            var glyphs = Game1.smallFont?.Characters;
            if (glyphs == null) return text;
            var drawable = new HashSet<char>(glyphs);
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c == '\n' || c == '\r' || c == '\t' || (drawable.Contains(c) && !IsSymbolOrEmoji(c)))
                    sb.Append(c);
                // anything else the font can't draw is dropped rather than shown as a box
            }
            return sb.ToString();
        }
        catch
        {
            return text;
        }
    }

    /// <summary>Symbol/emoji ranges the game font claims to cover but draws as empty boxes.</summary>
    private static bool IsSymbolOrEmoji(char c) =>
        (c >= 0xD800 && c <= 0xDFFF)   // surrogate halves (emoji)
        || (c >= 0xFE00 && c <= 0xFE0F) // variation selectors
        || c == 0x200D                  // zero-width joiner
        || (c >= 0x2190 && c <= 0x21FF) // arrows
        || (c >= 0x2300 && c <= 0x27BF) // misc technical + dingbats
        || (c >= 0x2B00 && c <= 0x2BFF) // symbols and arrows
        || (c >= 0x1F000);              // emoji and beyond
}
