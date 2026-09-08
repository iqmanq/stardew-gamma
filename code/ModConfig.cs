using StardewModdingAPI;

namespace Gamma;

public class ModConfig
{
    // ---- AI provider -------------------------------------------------------
    // "openai"  : OpenAI or any OpenAI-compatible API (OpenRouter, Groq, DeepSeek, Together, ...)
    // "anthropic": Anthropic Claude
    public string Provider { get; set; } = "openai";

    // Leave empty to use the provider's official endpoint.
    // For local models: Ollama  -> http://localhost:11434/v1   (Provider: openai)
    //                   LM Studio -> http://localhost:1234/v1   (Provider: openai)
    public string ApiBaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    // Some gateways (e.g. opencode zen) want the raw key without the "Bearer " prefix.
    // With this off, the mod sends "Bearer <key>" and automatically retries raw on a 401.
    public bool UseRawKeyAuth { get; set; } = false;
    public string Model { get; set; } = "gpt-4o-mini";
    public float Temperature { get; set; } = 0.7f;
    public int MaxResponseTokens { get; set; } = 1024;
    public int MaxToolRoundtrips { get; set; } = 10;

    // ---- Web search --------------------------------------------------------
    // "none" | "tavily" | "brave" | "searxng"
    public string SearchProvider { get; set; } = "none";
    public string SearchApiKey { get; set; } = "";
    public string SearxngUrl { get; set; } = "";   // e.g. http://localhost:8080

    // ---- Misc ---------------------------------------------------------------
    public string WikiApiUrl { get; set; } = "https://stardewvalleywiki.com/mediawiki/api.php";
    public bool RememberChatHistory { get; set; } = true;
    public int HistoryMessagesKept { get; set; } = 24;
    public SButton OpenChatKey { get; set; } = SButton.O;
    // Controller button that also opens the chat window (RightStick = click the right
    // stick). SMAPI raises Input.ButtonPressed for gamepad buttons, so it's handled
    // exactly like OpenChatKey; RightStick isn't used by vanilla gameplay.
    public SButton OpenChatGamepadButton { get; set; } = SButton.RightStick;

    // ---- Chat history limits --------------------------------------------------
    // Conversations kept at once (not user-configurable; raising the cap just lets
    // empty chats pile up, since auto-delete already trims the oldest ones).
    public const int MaxChats = 25;
    public int MaxTurnsPerChat { get; set; } = 120;      // turns kept in memory per chat
    public int SavedTurnsPerChat { get; set; } = 80;     // turns written to the save file
    public bool AutoDeleteOldChats { get; set; } = true; // drop oldest empty chats over the cap

    // ---- Chatbot identity ----------------------------------------------------
    // Shown as the window title, the reply prefix, and in the AI's system prompt.
    public string ChatbotName { get; set; } = "Gamma";
    // After the first reply in a new chat, ask the model for a short conversation title
    // (one extra small request). Off = title from the first message's text.
    public bool AutoNameChats { get; set; } = true;
}
