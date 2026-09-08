using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace Gamma.Ui;

/// <summary>
/// In-game settings screen for the AI/search providers, styled like a vanilla menu.
/// Save writes the mod's config.json through SMAPI, so no file editing is needed.
/// Rows are "label left, field right" so the whole form fits any window height.
/// </summary>
public class SettingsMenu : IClickableMenu
{
    internal static readonly (string Label, string Provider, string BaseUrl, string Model)[] ProviderPresets =
    {
        ("OpenAI",            "openai",    "https://api.openai.com/v1",      "gpt-4o-mini"),
        ("OpenRouter",        "openai",    "https://openrouter.ai/api/v1",   "minimax/minimax-m3:free"),
        ("Google AI Studio",  "openai",    "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-flash-lite-latest"),
        ("Groq",              "openai",    "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
        ("Ollama (local)",    "openai",    "http://localhost:11434/v1",      "llama3.1:8b"),
        ("LM Studio (local)", "openai",    "http://localhost:1234/v1",       ""),
        ("Anthropic",         "anthropic", "https://api.anthropic.com/v1",   "claude-sonnet-4-20250514"),
        ("Custom",            "",          "",                               ""),
    };

    private static readonly string[] SearchPresets = { "none", "tavily", "brave", "searxng" };

    private const int Pad = 30;
    private const int Rows = 7;
    private const int LabelW = 235;

    private readonly ModConfig Config;
    private readonly Action SaveConfig;
    private readonly Action OnSaved;

    private readonly Texture2D BoxTexture;
    private readonly TextBox ApiBaseUrlBox;
    private readonly TextBox ApiKeyBox;
    private readonly TextBox ModelBox;
    private readonly TextBox SearchApiKeyBox;
    private readonly List<TextBox> Boxes;

    private readonly Rectangle ProviderButton;
    private readonly Rectangle AuthButton;
    private readonly Rectangle SearchProviderButton;
    private readonly Rectangle AutoNameButton;
    private readonly Rectangle ModelsButton;
    private readonly Rectangle SaveButton;
    private readonly Rectangle CancelButton;

    // model-list fetching (network work happens on a background thread)
    private readonly ConcurrentQueue<List<string>> PendingModels = new();
    private readonly ConcurrentQueue<string> PendingNotice = new();
    private List<string> _models;
    private int _modelIndex = -1;
    private volatile bool _fetching;
    private string _notice;

    // shared row geometry, computed once so draw() always matches the hitboxes
    private readonly int TopY;
    private readonly int RowH;
    private readonly int BoxH;
    private readonly int BoxW;

    private int PresetIndex;
    private int SearchPresetIndex;
    private TextBox Focused;
    private readonly List<(Rectangle Bounds, TextBox Box)> Navigation = new();

    public SettingsMenu(ModConfig config, Action saveConfig, Action onSaved)
        : base(0, 0, 0, 0, showUpperRightCloseButton: true)
    {
        Config = config;
        SaveConfig = saveConfig;
        OnSaved = onSaved;

        int w = Math.Min(1000, Game1.uiViewport.Width - 120);
        int h = Math.Min(780, Game1.uiViewport.Height - 60);
        width = w;
        height = h;
        xPositionOnScreen = (Game1.uiViewport.Width - w) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - h) / 2;
        // re-run layout so the upper-right close button lands on the actual menu bounds
        initialize(xPositionOnScreen, yPositionOnScreen, width, height, showUpperRightCloseButton: true);

        BoxTexture = Game1.content.Load<Texture2D>("LooseSprites\\chatBox");

        // find which preset (if any) matches the current config
        PresetIndex = Array.FindIndex(ProviderPresets, p =>
            p.Provider.Equals(Config.Provider, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(p.BaseUrl)
            && p.BaseUrl.Equals(Config.ApiBaseUrl ?? "", StringComparison.OrdinalIgnoreCase));
        if (PresetIndex < 0) PresetIndex = ProviderPresets.Length - 1; // Custom
        SearchPresetIndex = Math.Max(0, Array.IndexOf(SearchPresets, (Config.SearchProvider ?? "none").ToLowerInvariant()));

        BoxW = width - Pad * 2 - LabelW;
        TopY = yPositionOnScreen + 84;
        int bottomArea = 96; // save/cancel + notice lines
        // capped at 56: the chat box texture's entry frame is 48px tall and the message
        // bubble's border starts at row 56 — taller fields show a phantom second frame
        BoxH = Math.Clamp((height - TopY + yPositionOnScreen - bottomArea - Rows * 12) / Rows, 40, 56);
        RowH = BoxH + 12;

        int x = xPositionOnScreen + Pad;
        int boxX = x + LabelW;
        int y = TopY;

        ProviderButton = new Rectangle(boxX, y, BoxW, BoxH);
        y += RowH;

        // API base URL + auth-scheme toggle share a row
        ApiBaseUrlBox = MakeBox(new Rectangle(boxX, y, BoxW - 170, BoxH));
        ApiBaseUrlBox.Text = Config.ApiBaseUrl ?? "";
        AuthButton = new Rectangle(boxX + BoxW - 160, y, 160, BoxH);
        y += RowH;

        ApiKeyBox = MakeBox(new Rectangle(boxX, y, BoxW, BoxH));
        ApiKeyBox.Text = Config.ApiKey ?? "";
        y += RowH;

        ModelBox = MakeBox(new Rectangle(boxX, y, BoxW - 160, BoxH));
        ModelBox.Text = Config.Model ?? "";
        ModelsButton = new Rectangle(boxX + BoxW - 150, y, 150, BoxH);
        y += RowH;

        SearchProviderButton = new Rectangle(boxX, y, BoxW, BoxH);
        y += RowH;

        SearchApiKeyBox = MakeBox(new Rectangle(boxX, y, BoxW, BoxH));
        LoadSearchBox(); // shows the URL for searxng, the key for the hosted providers
        y += RowH;

        AutoNameButton = new Rectangle(boxX, y, BoxW, BoxH);

        int buttonY = yPositionOnScreen + height - 70;
        SaveButton = new Rectangle(xPositionOnScreen + width / 2 - 260, buttonY, 250, 44);
        CancelButton = new Rectangle(xPositionOnScreen + width / 2 + 10, buttonY, 250, 44);

        Boxes = new List<TextBox> { ApiBaseUrlBox, ApiKeyBox, ModelBox, SearchApiKeyBox };
        Navigation.Add((ProviderButton, null));
        Navigation.Add((Bounds(ApiBaseUrlBox), ApiBaseUrlBox));
        Navigation.Add((AuthButton, null));
        Navigation.Add((Bounds(ApiKeyBox), ApiKeyBox));
        Navigation.Add((Bounds(ModelBox), ModelBox));
        Navigation.Add((ModelsButton, null));
        Navigation.Add((SearchProviderButton, null));
        Navigation.Add((Bounds(SearchApiKeyBox), SearchApiKeyBox));
        Navigation.Add((AutoNameButton, null));
        Navigation.Add((SaveButton, null));
        Navigation.Add((CancelButton, null));
        if (upperRightCloseButton != null)
            Navigation.Add((upperRightCloseButton.bounds, null));
        Focus(ApiBaseUrlBox);

        Game1.playSound("shwip");
    }

    private TextBox MakeBox(Rectangle bounds)
    {
        return new TextBox(BoxTexture, null, Game1.smallFont, Color.White)
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Selected = false,
            // the game's TextBox deletes trailing characters on assignment until the text
            // fits the box when this is on — it would silently chop pasted API keys/URLs.
            // With it off, Draw scrolls the text so the caret end stays visible.
            limitWidth = false,
        };
    }

    private void Focus(TextBox box)
    {
        foreach (var b in Boxes) b.Selected = b == box;
        Focused = box;
        Game1.keyboardDispatcher.Subscriber = box;
    }

    // the search row doubles as the SearXNG URL field when searxng is the provider,
    // since a local instance needs a URL rather than a key
    private bool UsingSearxng => SearchPresets[SearchPresetIndex] == "searxng";

    private void SaveSearchBox()
    {
        string value = SearchApiKeyBox.Text.Trim();
        if (UsingSearxng) Config.SearxngUrl = value;
        else Config.SearchApiKey = value;
    }

    private void LoadSearchBox()
    {
        SearchApiKeyBox.Text = UsingSearxng ? Config.SearxngUrl ?? "" : Config.SearchApiKey ?? "";
    }

    private void ApplyPreset()
    {
        var preset = ProviderPresets[PresetIndex];
        if (!string.IsNullOrEmpty(preset.Provider)) Config.Provider = preset.Provider;
        if (!string.IsNullOrEmpty(preset.BaseUrl))
        {
            Config.ApiBaseUrl = preset.BaseUrl;
            ApiBaseUrlBox.Text = preset.BaseUrl;
        }
        if (!string.IsNullOrEmpty(preset.Model))
        {
            Config.Model = preset.Model;
            ModelBox.Text = preset.Model;
        }
    }

    private void StartFetchModels()
    {
        if (_fetching) return;
        _fetching = true;
        _notice = "Loading model list…";

        string baseUrl = ApiBaseUrlBox.Text.Trim().TrimEnd('/');
        string key = ApiKeyBox.Text.Trim();
        string defaultBase = ProviderPresets[PresetIndex].BaseUrl;

        Task.Run(() =>
        {
            try
            {
                string url = (string.IsNullOrEmpty(baseUrl) ? defaultBase : baseUrl) + "/models";
                if (string.IsNullOrEmpty(url.Trim('/')))
                    throw new Exception("no API base URL set — pick a provider preset first");

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(key))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                using var doc = JsonDocument.Parse(json);
                var list = new List<string>();
                foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
                    if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        list.Add(id.GetString());
                list.Sort(StringComparer.OrdinalIgnoreCase);
                if (list.Count == 0) throw new Exception("the provider returned no models");

                PendingModels.Enqueue(list);
            }
            catch (Exception ex)
            {
                PendingNotice.Enqueue("Couldn't load models: " + ex.Message);
            }
            finally
            {
                _fetching = false;
            }
        });
    }

    private void SaveAndExit()
    {
        Config.ApiBaseUrl = ApiBaseUrlBox.Text.Trim();
        Config.ApiKey = ApiKeyBox.Text.Trim();
        Config.Model = ModelBox.Text.Trim();
        Config.SearchProvider = SearchPresets[SearchPresetIndex];
        SaveSearchBox();

        Game1.keyboardDispatcher.Subscriber = null;
        SaveConfig();
        Game1.playSound("levelup");
        exitThisMenu(false);
        OnSaved?.Invoke();
    }

    private void Cancel()
    {
        Game1.keyboardDispatcher.Subscriber = null;
        exitThisMenu(false);
        Game1.playSound("bigDeSelect");
    }

    // ---------- per-frame ----------

    public override void update(GameTime time)
    {
        base.update(time);
        // Focus is managed by clicks/navigation. TextBox.Update opens the keyboard on hover.

        while (PendingModels.TryDequeue(out var list))
        {
            _models = list;
            _modelIndex = 0;
            ModelBox.Text = list[0];
            _notice = $"{list.Count} models loaded — click \"Load list\" again to cycle through them";
            Game1.playSound("levelup");
        }
        while (PendingNotice.TryDequeue(out var n))
            _notice = n;
    }

    public override void draw(SpriteBatch b)
    {
        VanillaUi.DrawBox(b, xPositionOnScreen, yPositionOnScreen, width, height);

        int labelX = xPositionOnScreen + Pad;
        b.DrawString(Game1.smallFont, $"{Config.ChatbotName ?? "Gamma"} Settings", new Vector2(labelX, yPositionOnScreen + 16),
            new Color(255, 236, 160), 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);
        b.DrawString(Game1.smallFont, Game1.options.gamepadControls
                ? "A on field: type  ·  LB/RB: option  ·  X: save  ·  B: cancel"
                : "Tab: next field   ·   Enter: save   ·   Esc: cancel",
            new Vector2(labelX, yPositionOnScreen + 16 + VanillaUi.LineHeight + 2), new Color(214, 202, 178),
            0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);

        void Label(string text, int rowY) => b.DrawString(Game1.smallFont, text,
            new Vector2(labelX, rowY + (RowH - VanillaUi.LineHeight) / 2f),
            Color.White, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);

        int y = TopY;

        Label("AI provider", y);
        DrawCycleButton(b, ProviderButton, ProviderPresets[PresetIndex].Label);
        y += RowH;

        Label("API base URL", y);
        DrawBoxWithFocus(b, ApiBaseUrlBox);
        DrawCycleButton(b, AuthButton, Config.UseRawKeyAuth ? "Key: raw" : "Key: Bearer");
        y += RowH;

        Label("API key", y);
        DrawBoxWithFocus(b, ApiKeyBox);
        y += RowH;

        Label("Model", y);
        DrawBoxWithFocus(b, ModelBox);
        DrawCycleButton(b, ModelsButton,
            _models == null ? (_fetching ? "Loading…" : "Load list") : $"Next ({_modelIndex + 1}/{_models.Count})");
        y += RowH;

        Label("Web search", y);
        DrawCycleButton(b, SearchProviderButton, SearchPresets[SearchPresetIndex]);
        y += RowH;

        Label(UsingSearxng ? "SearXNG URL" : "Search key", y);
        DrawBoxWithFocus(b, SearchApiKeyBox);
        y += RowH;

        Label("Name chats with AI", y);
        DrawCycleButton(b, AutoNameButton, Config.AutoNameChats ? "On" : "Off");

        VanillaUi.DrawBox(b, SaveButton.X, SaveButton.Y, SaveButton.Width, SaveButton.Height);
        DrawCentered(b, "Save", SaveButton, Color.White);
        VanillaUi.DrawBox(b, CancelButton.X, CancelButton.Y, CancelButton.Width, CancelButton.Height);
        DrawCentered(b, "Cancel", CancelButton, Color.White);

        // notice / validation lines above the buttons
        var preset = ProviderPresets[PresetIndex];
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Text) && preset.Label.Contains("local"))
            b.DrawString(Game1.smallFont, "Local provider — no API key needed.", new Vector2(labelX, SaveButton.Y - 56),
                new Color(214, 202, 178), 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);

        string keyText = (ApiKeyBox.Text ?? "").Trim();
        if (keyText.StartsWith("sk-or-v1-") && keyText.Length != 73)
            b.DrawString(Game1.smallFont,
                $"OpenRouter keys are 73 chars, this one is {keyText.Length}. Click the field, Ctrl+A, then Ctrl+V.",
                new Vector2(labelX, SaveButton.Y - 28), new Color(255, 150, 80),
                0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);
        else if (!string.IsNullOrEmpty(_notice))
            b.DrawString(Game1.smallFont, _notice, new Vector2(labelX, SaveButton.Y - 28),
                new Color(214, 202, 178), 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);

        base.draw(b);
        // the game skips its own cursor draw while a menu is open; menus draw it themselves
        drawMouse(b, false, -1);
    }

    private void DrawCycleButton(SpriteBatch b, Rectangle bounds, string text)
    {
        VanillaUi.DrawBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        DrawCentered(b, text, bounds, new Color(255, 236, 160));
    }

    private void DrawBoxWithFocus(SpriteBatch b, TextBox box)
    {
        // second arg = "draw as a shadow": paints a whole extra dialogue box behind the
        // field and skips nothing we need — always false for normal rendering
        box.Draw(b, false);
        if (box.Selected)
            b.Draw(Game1.staminaRect, new Rectangle(box.X, box.Y, box.Width, box.Height),
                null, new Color(255, 236, 160) * 0.16f, 0f, Vector2.Zero, SpriteEffects.None, 1f);
    }

    private static void DrawCentered(SpriteBatch b, string text, Rectangle bounds, Color color)
    {
        var size = Game1.smallFont.MeasureString(text);
        b.DrawString(Game1.smallFont, text,
            new Vector2(bounds.X + (bounds.Width - size.X) / 2f, bounds.Y + (bounds.Height - size.Y) / 2f),
            color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.95f);
    }

    // ---------- input ----------

    public override void receiveKeyPress(Keys key)
    {
        // In gamepad compatibility mode the game maps B/Start/Y to the menu key (Escape
        // by default) and feeds them here as well; those buttons are handled properly in
        // receiveGamePadButton, so don't let the mapped Escape cancel the form or save it.
        // Y and Start are intentionally ignored while this menu is active.
        if (Game1.options.gamepadControls
            && (Game1.input.GetGamePadState().IsButtonDown(Buttons.B)
                || Game1.input.GetGamePadState().IsButtonDown(Buttons.Start)
                || Game1.input.GetGamePadState().IsButtonDown(Buttons.Y)))
            return;
        var kb = Keyboard.GetState();
        bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
        if (ctrl && Focused != null && key == Keys.V)
        {
            // Stardew text boxes have no clipboard support of their own; long API keys are
            // exactly what people paste here. Replace the whole field so stale content
            // (e.g. an old key) can't linger before or after the paste.
            string clip = null;
            if (DesktopClipboard.GetText(ref clip) && !string.IsNullOrEmpty(clip))
            {
                Focused.Text = clip;
                Game1.playSound("coin");
            }
            return;
        }
        if (ctrl && Focused != null && key == Keys.A)
        {
            Focused.Text = "";
            Game1.playSound("shiny4");
            return;
        }
        if (ctrl && Focused != null && key == Keys.C && !string.IsNullOrEmpty(Focused.Text))
        {
            DesktopClipboard.SetText(Focused.Text);
            Game1.playSound("coin");
            return;
        }
        switch (key)
        {
            case Keys.Escape:
                Cancel();
                return;
            case Keys.Enter:
                SaveAndExit();
                return;
            case Keys.Tab:
                Focus(NextBox());
                return;
        }
        // Don't call base.receiveKeyPress: it closes the menu when the key matches
        // Options.menuButton (default Escape AND E). Typing reaches the TextBoxes through
        // the keyboard dispatcher, so unhandled keys can simply be swallowed here.
    }

    private TextBox NextBox()
    {
        int i = Focused == null ? -1 : Boxes.IndexOf(Focused);
        return Boxes[(i + 1) % Boxes.Count];
    }

    private static Rectangle Bounds(TextBox box) => new(box.X, box.Y, box.Width, box.Height);

    private void MoveToOption(int direction)
    {
        // Start at the cursor so free movement and bumper navigation work together.
        int index = Navigation.FindIndex(option => option.Bounds.Contains(Game1.getMouseX(), Game1.getMouseY()));
        if (index < 0 && Focused != null)
            index = Navigation.FindIndex(option => option.Box == Focused);
        if (index < 0) index = direction > 0 ? -1 : 0;
        var next = Navigation[(index + direction + Navigation.Count) % Navigation.Count];
        Focus(next.Box);
        Game1.setMousePosition(next.Bounds.Center.X, next.Bounds.Center.Y, ui_scale: true);
        Game1.playSound("shiny4");
    }

    // Keep analog cursor movement available even when the player enables snappy menus.
    public override bool overrideSnappyMenuCursorMovementBan() => true;

    /// <summary>
    /// Gamepad support (mirrors ChatMenu: left stick moves the cursor, A clicks, right
    /// stick scrolls — see Game1.Update's !areGamePadControlsImplemented paths). What we
    /// add on top: A on a text field opens the game's on-screen keyboard,
    /// LB/RB (or triggers, or D-pad up/down) cycle all options without aiming, X saves,
    /// B cancels. While the OSK is open the game routes gamepad input to it exclusively
    /// (updateTextEntry instead of updateActiveMenu), so this menu sees nothing until
    /// the keyboard closes; the textEntry checks below are just safety nets.
    /// </summary>
    public override void receiveGamePadButton(Buttons button)
    {
        if (Game1.textEntry != null)
            return;
        switch (button)
        {
            case Buttons.B:
            case Buttons.Back:
                Cancel();
                break;
            case Buttons.A:
                var box = Boxes.Find(field => Bounds(field).Contains(Game1.getMouseX(), Game1.getMouseY()));
                if (box != null)
                {
                    Focus(box);
                    Game1.showTextEntry(box);
                }
                break;
            case Buttons.X:
                SaveAndExit();
                break;
            case Buttons.LeftShoulder:
            case Buttons.LeftTrigger:
            case Buttons.DPadUp:
                MoveToOption(-1);
                break;
            case Buttons.RightShoulder:
            case Buttons.RightTrigger:
            case Buttons.DPadDown:
                MoveToOption(1);
                break;
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        // With the on-screen keyboard open, clicks belong to it (gamepad A hits its keys
        // even though the cursor overlaps our menu underneath)
        if (Game1.textEntry != null)
            return;
        if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
        {
            Cancel();
            return;
        }
        var target = Navigation.Find(option => option.Bounds.Contains(x, y));
        if (target.Bounds != Rectangle.Empty)
            Focus(target.Box);
        if (ProviderButton.Contains(x, y))
        {
            PresetIndex = (PresetIndex + 1) % ProviderPresets.Length;
            ApplyPreset();
            Game1.playSound("shiny4");
            return;
        }
        if (AuthButton.Contains(x, y))
        {
            Config.UseRawKeyAuth = !Config.UseRawKeyAuth;
            Game1.playSound("shiny4");
            return;
        }
        if (SearchProviderButton.Contains(x, y))
        {
            SaveSearchBox();
            SearchPresetIndex = (SearchPresetIndex + 1) % SearchPresets.Length;
            LoadSearchBox();
            Game1.playSound("shiny4");
            return;
        }
        if (AutoNameButton.Contains(x, y))
        {
            Config.AutoNameChats = !Config.AutoNameChats;
            Game1.playSound("shiny4");
            return;
        }
        if (ModelsButton.Contains(x, y))
        {
            if (_models == null)
                StartFetchModels();
            else
            {
                _modelIndex = (_modelIndex + 1) % _models.Count;
                ModelBox.Text = _models[_modelIndex];
                Game1.playSound("shiny4");
            }
            return;
        }
        if (SaveButton.Contains(x, y))
        {
            SaveAndExit();
            return;
        }
        if (CancelButton.Contains(x, y))
        {
            Cancel();
            return;
        }

        foreach (var box in Boxes)
        {
            if (new Rectangle(box.X, box.Y, box.Width, box.Height).Contains(x, y))
            {
                if (Focused != box)
                {
                    Focus(box);
                    Game1.playSound("shiny4");
                }
                break;
            }
        }
        base.receiveLeftClick(x, y, playSound);
    }

    public override void emergencyShutDown()
    {
        Game1.keyboardDispatcher.Subscriber = null;
        base.emergencyShutDown();
    }
}
