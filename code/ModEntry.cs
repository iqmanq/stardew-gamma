using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Gamma.Services;
using Gamma.Ui;

namespace Gamma;

public class ModEntry : Mod
{
    private ModConfig Config;
    private GameStateService State;
    private WikiService Wiki;
    private WebSearchService Web;
    private ToolRegistry Tools;
    private ChatService Chat;
    private ChatHistory History;

    private readonly ConcurrentQueue<Action> GameThreadActions = new();
    private int Unread;
    private bool MenuOpen;
    private ChatMenu LastChatMenu;
    private Testing.UiTestRunner TestRunner;

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();

        State = new GameStateService(helper, Monitor);
        Wiki = new WikiService(helper, Config);
        Web = new WebSearchService(helper, Config);
        Tools = new ToolRegistry(helper, Monitor, State, Wiki, Web) { GameRunner = RunOnGameThread };
        History = new ChatHistory(helper, Monitor, Config);
        Chat = new ChatService(helper, Monitor, Config, State, Tools, History);

        Chat.MessageAdded += OnMessageAdded;

        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.Events.Display.Rendered += (_, _) => TestRunner?.OnRendered();
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => History.Load();
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            DrainGameThreadActions();
            Chat.Update();
            TestRunner?.Update();
        };

        RegisterCommands();
        // GMCM's API can only be fetched once every mod has initialized
        helper.Events.GameLoop.GameLaunched += (_, _) => SetupGenericModConfigMenu();

        if (Environment.GetEnvironmentVariable("GAMMA_AUTO_TEST") == "1")
            StartUiTest(autoExit: true);

        Monitor.Log("Gamma loaded — press O (configurable) or click the right stick in game to chat.", LogLevel.Info);
    }

    // ---------- game-thread marshalling ----------
    // Tool bodies read game state, so they must run on the game thread; the chat loop
    // waits on a background thread while the next UpdateTicked executes the work.

    private string RunOnGameThread(Func<string> fn)
    {
        var done = new ManualResetEventSlim(false);
        string result = null;
        Exception error = null;
        GameThreadActions.Enqueue(() =>
        {
            try { result = fn(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(15)))
            throw new Exception("Timed out waiting for the game thread.");
        if (error != null) throw error;
        return result ?? "";
    }

    private void DrainGameThreadActions()
    {
        while (GameThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Monitor.Log($"Gamma game-thread task failed: {ex}", LogLevel.Warn); }
        }
    }

    // ---------- chat events ----------

    private void OnMessageAdded(Conversation conversation, bool fromUser, string text, bool persisted)
    {
        if (fromUser) return;
        Monitor.Log("Gamma: " + text, LogLevel.Info);
        Interlocked.Increment(ref Unread);
        GameThreadActions.Enqueue(() => { Game1.playSound("smallSelect"); });
    }

    // ---------- input & UI ----------

    private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady) return;
        if (e.Button != Config.OpenChatKey && e.Button != Config.OpenChatGamepadButton) return;
        if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.dialogueUp) return;
        OpenMenu();
    }

    private void OpenMenu()
    {
        if (!Chat.Configured)
        {
            // First run (or missing key): go straight to the settings screen.
            ShowSettings();
            return;
        }
        Unread = 0;
        MenuOpen = true;
        LastChatMenu = new ChatMenu(Config, Chat, () => ShowSettings());
        Game1.activeClickableMenu = LastChatMenu;
        Game1.playSound("bigSelect");
    }

    private void StartUiTest(bool autoExit)
    {
        if (TestRunner != null) return;
        TestRunner = new Testing.UiTestRunner(Monitor, Helper, Config, Chat, History,
            openChat: () => { OpenMenu(); return LastChatMenu; },
            openSettings: () => { ShowSettings(); return true; },
            autoExit: autoExit);
    }

    private void ShowSettings()
    {
        Unread = 0;
        Game1.activeClickableMenu = new SettingsMenu(
            Config,
            saveConfig: () =>
            {
                Helper.WriteConfig(Config);
                Monitor.Log("Gamma settings saved to Mods/Gamma/config.json.", LogLevel.Info);
            },
            onSaved: () =>
            {
                if (Chat.Configured)
                    Game1.activeClickableMenu = new ChatMenu(Config, Chat, () => ShowSettings());
            });
        Game1.playSound("bigSelect");
    }

    private void OnMenuChanged(object sender, MenuChangedEventArgs e)
    {
        if (e.OldMenu is ChatMenu)
        {
            MenuOpen = false;
            History.Save();
        }
    }

    private void OnRenderedHud(object sender, RenderedHudEventArgs e)
    {
        if (Unread <= 0 || MenuOpen || !Context.IsWorldReady) return;
        if (Game1.activeClickableMenu != null) return;

        var b = e.SpriteBatch;
        string label = $"{Config.ChatbotName ?? "Gamma"} ({Unread}) — press {Config.OpenChatKey}";
        int w = (int)Game1.smallFont.MeasureString(label).X + 28;
        int h = VanillaUi.LineHeight + 20;
        int x = 24;
        int y = (int)Game1.uiViewport.Height - h - 300;

        VanillaUi.DrawBox(b, x, y, w, h);
        b.DrawString(Game1.smallFont, label, new Vector2(x + 14, y + 10), Color.White,
            0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);
    }

    // ---------- lifecycle ----------

    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        History.Load();
        Unread = 0;
    }

    // ---------- Generic Mod Config Menu ----------

    private void SetupGenericModConfigMenu()
    {
        if (!Helper.ModRegistry.IsLoaded("spacechase0.GenericModConfigMenu"))
            return;
        var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (api == null)
        {
            Monitor.Log("Generic Mod Config Menu is installed but its API is unavailable.", LogLevel.Trace);
            return;
        }

        api.Register(
            mod: ModManifest,
            // GMCM edits Config live through the setters below, and the services hold this
            // same Config instance, so changes apply without a restart once saved
            reset: ResetConfigToDefaults,
            save: () =>
            {
                Helper.WriteConfig(Config);
                Monitor.Log($"Gamma settings saved to Mods/Gamma/config.json.", LogLevel.Info);
            });

        api.AddSectionTitle(ModManifest, () => "AI provider");
        api.AddTextOption(ModManifest,
            () => PresetLabelFor(Config.Provider, Config.ApiBaseUrl),
            v => ApplyProviderPreset(v),
            () => "Provider preset", () => "Fills in the API base URL and a starting model. Custom leaves your settings untouched",
            Gamma.Ui.SettingsMenu.ProviderPresets.Select(p => p.Label).ToArray());
        api.AddTextOption(ModManifest, () => Config.Provider, v => Config.Provider = v,
            () => "Wire protocol", () => "Advanced: the API format (openai-compatible or anthropic). The preset above sets this for you",
            new[] { "openai", "anthropic" });
        api.AddTextOption(ModManifest, () => Config.ApiBaseUrl, v => Config.ApiBaseUrl = v,
            () => "API base URL", () => "Leave empty for the provider's official endpoint. Ollama: http://localhost:11434/v1, LM Studio: http://localhost:1234/v1");
        api.AddTextOption(ModManifest, () => Config.ApiKey, v => Config.ApiKey = v,
            () => "API key", () => "Not needed for local models. Stored in plain text in config.json");
        api.AddBoolOption(ModManifest, () => Config.UseRawKeyAuth, v => Config.UseRawKeyAuth = v,
            () => "Raw key auth", () => "Send the key without the 'Bearer ' prefix. Some gateways require it; the mod retries the other scheme automatically on a 401");
        api.AddTextOption(ModManifest, () => Config.Model, v => Config.Model = v,
            () => "Model", () => "Use 'gamma settings' in the SMAPI console for a Load-list button, or your provider's model list");
        api.AddNumberOption(ModManifest, () => Config.Temperature, v => Config.Temperature = v,
            () => "Temperature", () => "Higher is more creative, lower is more focused", 0f, 2f, 0.05f);
        api.AddNumberOption(ModManifest, () => Config.MaxResponseTokens, v => Config.MaxResponseTokens = v,
            () => "Max response tokens", () => "Maximum length of each reply", 64, 8192, 64);
        api.AddNumberOption(ModManifest, () => Config.MaxToolRoundtrips, v => Config.MaxToolRoundtrips = v,
            () => "Max tool round-trips", () => "How many tool-calling steps the AI may take per question before it must answer", 1, 30, 1);

        api.AddSectionTitle(ModManifest, () => "Web search");
        api.AddTextOption(ModManifest, () => Config.SearchProvider, v => Config.SearchProvider = v,
            () => "Search provider", () => "Wiki search works with no setup; this adds general web search",
            new[] { "none", "tavily", "brave", "searxng" });
        api.AddTextOption(ModManifest, () => Config.SearchApiKey, v => Config.SearchApiKey = v,
            () => "Search API key", () => "For tavily or brave. Leave empty for searxng");
        api.AddTextOption(ModManifest, () => Config.SearxngUrl, v => Config.SearxngUrl = v,
            () => "SearXNG URL", () => "Your instance's URL, e.g. http://localhost:8080. The instance must have 'json' in its search formats");
        api.AddTextOption(ModManifest, () => Config.WikiApiUrl, v => Config.WikiApiUrl = v,
            () => "Wiki API URL", () => "Stardew Valley wiki API endpoint; leave as default unless you mirror the wiki");

        api.AddSectionTitle(ModManifest, () => "General");
        api.AddTextOption(ModManifest, () => Config.ChatbotName, v => Config.ChatbotName = v,
            () => "Chatbot name", () => "The name shown in the chat window, above replies, and used in the AI's personality");
        api.AddBoolOption(ModManifest, () => Config.AutoNameChats, v => Config.AutoNameChats = v,
            () => "Name chats with AI", () => "After the first reply in a new chat, asks the model for a short conversation title (one extra small request). Off: titles come from the first message's text");
        api.AddBoolOption(ModManifest, () => Config.RememberChatHistory, v => Config.RememberChatHistory = v,
            () => "Remember chat history", () => "Keeps conversations per save file across game sessions");
        api.AddNumberOption(ModManifest, () => Config.HistoryMessagesKept, v => Config.HistoryMessagesKept = v,
            () => "History messages kept", () => "How many recent messages the AI can see for context", 2, 100, 1);
        api.AddKeybind(ModManifest, () => Config.OpenChatKey, v => Config.OpenChatKey = v,
            () => "Open chat keybind", () => "Key that opens the chat window while playing");
        api.AddKeybind(ModManifest, () => Config.OpenChatGamepadButton, v => Config.OpenChatGamepadButton = v,
            () => "Open chat gamepad button", () => "Controller button that opens the chat window (e.g. right-stick click). In the chat window: A on a field types, X is new chat, LB/RB switch chats, B closes. In settings: A on a field types, LB/RB cycle options, X saves, B cancels");

        api.AddSectionTitle(ModManifest, () => "Chat history limits");
        api.AddNumberOption(ModManifest, () => Config.MaxTurnsPerChat, v => Config.MaxTurnsPerChat = v,
            () => "Turns kept in memory", () => "Messages per chat kept while playing; older ones are forgotten", 10, 500, 10);
        api.AddNumberOption(ModManifest, () => Config.SavedTurnsPerChat, v => Config.SavedTurnsPerChat = v,
            () => "Turns kept in the save", () => "Messages per chat written to disk; the rest are gone after the game closes", 10, 500, 10);
        api.AddBoolOption(ModManifest, () => Config.AutoDeleteOldChats, v => Config.AutoDeleteOldChats = v,
            () => "Auto-delete oldest empty chats", () => "When the chat cap is reached, empty chats are removed oldest-first. Off: unlimited chats");

        Monitor.Log("Registered config options with Generic Mod Config Menu.", LogLevel.Info);
    }

    private static string PresetLabelFor(string provider, string baseUrl)
    {
        var presets = Ui.SettingsMenu.ProviderPresets;
        int i = Array.FindIndex(presets, p =>
            p.Provider.Equals(provider ?? "", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(p.BaseUrl)
            && p.BaseUrl.Equals(baseUrl ?? "", StringComparison.OrdinalIgnoreCase));
        return i < 0 ? presets[presets.Length - 1].Label : presets[i].Label; // Custom
    }

    private void ApplyProviderPreset(string label)
    {
        var preset = Ui.SettingsMenu.ProviderPresets.FirstOrDefault(p => p.Label == label);
        if (preset.Label == null || string.IsNullOrEmpty(preset.Provider)) return; // Custom: leave as-is
        Config.Provider = preset.Provider;
        Config.ApiBaseUrl = preset.BaseUrl;
        if (!string.IsNullOrEmpty(preset.Model))
            Config.Model = preset.Model;
    }

    private void ResetConfigToDefaults()
    {
        // API keys are deliberately preserved — losing a long pasted key to the reset
        // button would be an awful way to discover this menu
        var savedKey = Config.ApiKey;
        var savedSearchKey = Config.SearchApiKey;
        var fresh = new ModConfig();
        Config.Provider = fresh.Provider;
        Config.ApiBaseUrl = fresh.ApiBaseUrl;
        Config.ApiKey = savedKey;
        Config.UseRawKeyAuth = fresh.UseRawKeyAuth;
        Config.Model = fresh.Model;
        Config.Temperature = fresh.Temperature;
        Config.MaxResponseTokens = fresh.MaxResponseTokens;
        Config.MaxToolRoundtrips = fresh.MaxToolRoundtrips;
        Config.SearchProvider = fresh.SearchProvider;
        Config.SearchApiKey = savedSearchKey;
        Config.SearxngUrl = fresh.SearxngUrl;
        Config.WikiApiUrl = fresh.WikiApiUrl;
        Config.ChatbotName = fresh.ChatbotName;
        Config.AutoNameChats = fresh.AutoNameChats;
        Config.RememberChatHistory = fresh.RememberChatHistory;
        Config.HistoryMessagesKept = fresh.HistoryMessagesKept;
        Config.MaxTurnsPerChat = fresh.MaxTurnsPerChat;
        Config.SavedTurnsPerChat = fresh.SavedTurnsPerChat;
        Config.AutoDeleteOldChats = fresh.AutoDeleteOldChats;
        Config.OpenChatKey = fresh.OpenChatKey;
        Config.OpenChatGamepadButton = fresh.OpenChatGamepadButton;
    }

    // ---------- console commands ----------

    private void RegisterCommands()
    {
        Helper.ConsoleCommands.Add("gamma",
            "Gamma AI assistant.\n\n"
            + "Usage:\n"
            + "  gamma menu          open the chat window\n"
            + "  gamma settings      open the settings screen (API key, model, search)\n"
            + "  gamma ask <text>    ask the AI something\n"
            + "  gamma wiki <query>  search the Stardew Valley wiki\n"
            + "  gamma state         dump a summary of your game state\n"
            + "  gamma mods          list installed mods\n"
            + "  gamma reset         clear the chat history\n"
            + "  gamma testui        run the automated UI test (screenshots + live request)",
            OnGammaCommand);
    }

    private void OnGammaCommand(string command, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string rest = string.Join(" ", args.Skip(1));
        switch (sub)
        {
            case "menu":
                OpenMenu();
                break;
            case "settings":
                ShowSettings();
                break;
            case "ask":
                if (string.IsNullOrWhiteSpace(rest)) { Monitor.Log("Usage: gamma ask <text>", LogLevel.Info); break; }
                Chat.Ask(rest);
                break;
            case "wiki":
                if (string.IsNullOrWhiteSpace(rest)) { Monitor.Log("Usage: gamma wiki <query>", LogLevel.Info); break; }
                Monitor.Log(Wiki.SearchAsync(rest, 5).GetAwaiter().GetResult(), LogLevel.Info);
                break;
            case "state":
                Monitor.Log(State.GetSnapshot(), LogLevel.Info);
                break;
            case "mods":
                Monitor.Log(State.GetMods(), LogLevel.Info);
                break;
            case "reset":
                Chat.ResetHistory();
                Monitor.Log("Gamma chat history cleared.", LogLevel.Info);
                break;
            case "testui":
                StartUiTest(autoExit: false);
                break;
            default:
                Monitor.Log("Usage: gamma menu | settings | ask <text> | wiki <query> | state | mods | reset", LogLevel.Info);
                break;
        }
    }
}
