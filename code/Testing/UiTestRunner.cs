using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using Gamma.Services;
using Gamma.Ui;

namespace Gamma.Testing;

/// <summary>
/// Automated UI test harness, so the chat and settings screens can be exercised (and
/// screenshotted) without anyone touching the game. Activated by the `gamma testui`
/// console command, or automatically by setting GAMMA_AUTO_TEST=1 before launch — in
/// which case the game quits when the test finishes.
///
/// Screenshots are written to &lt;mod folder&gt;/ui-tests/ by reading the GPU backbuffer
/// inside SMAPI's Rendered event (right after a real draw, before present). The game's own
/// screenshot API needs the platform SDK, which doesn't exist headless, and reading from
/// UpdateTicked can capture a stale frame when the unfocused window throttles rendering.
/// </summary>
public class UiTestRunner
{
    private readonly IMonitor Monitor;
    private readonly ModConfig Config;
    private readonly ChatService Chat;
    private readonly ChatHistory History;
    private readonly Func<ChatMenu> OpenChat;
    private readonly Func<bool> OpenSettings;
    private readonly bool AutoExit;
    private readonly string ShotDir;

    private readonly Stopwatch Clock = Stopwatch.StartNew();
    private int Phase;
    private double PhaseStart;
    private ChatMenu Menu;
    private int TurnsAtAsk;
    private int SeededTurns;
    private bool _done;

    private string _requestedShot; // set by Step(), captured by OnRendered
    private string _capturedShot;
    private double _shotRequestTime;
    private bool _thinkingCaptured;

    public UiTestRunner(IMonitor monitor, IModHelper helper, ModConfig config, ChatService chat, ChatHistory history,
        Func<ChatMenu> openChat, Func<bool> openSettings, bool autoExit)
    {
        Monitor = monitor;
        Config = config;
        Chat = chat;
        History = history;
        OpenChat = openChat;
        OpenSettings = openSettings;
        AutoExit = autoExit;
        ShotDir = Path.Combine(helper.DirectoryPath, "ui-tests");

        if (AutoExit)
        {
            // headless runs: optionally override the model via env, without touching the saved config
            var model = Environment.GetEnvironmentVariable("GAMMA_AUTO_TEST_MODEL");
            if (!string.IsNullOrWhiteSpace(model))
            {
                Config.Model = model;
                Monitor.Log($"UI test: using model '{model}' for this run (config file not modified)", LogLevel.Info);
            }
        }
        Monitor.Log("UI test armed — phases: seed → chat → message/reply → gamepad (B clear, A keyboard, B close) → settings → screenshot", LogLevel.Info);
    }

    private bool _dumpedTextures;

    /// <summary>Debug helper: saves the raw chat box texture so its layout can be inspected.</summary>
    private void DumpTexturesOnce()
    {
        if (_dumpedTextures) return;
        _dumpedTextures = true;
        try
        {
            var tex = Game1.content.Load<Texture2D>("LooseSprites\\chatBox");
            Directory.CreateDirectory(ShotDir);
            string path = Path.Combine(ShotDir, "chatbox-texture.png");
            using (var fs = File.Create(path))
                tex.SaveAsPng(fs, tex.Width, tex.Height);
            Monitor.Log($"UI test: dumped chat box texture {tex.Width}x{tex.Height} to '{path}'", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"UI test: texture dump failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>Called by ModEntry after each real draw. Captures a requested screenshot
    /// at the moment the frame is fresh, before present.</summary>
    public void OnRendered()
    {
        DumpTexturesOnce();

        var name = _requestedShot;
        if (name == null || name == _capturedShot) return;
        try
        {
            var device = Game1.graphics.GraphicsDevice;
            int w = device.PresentationParameters.BackBufferWidth;
            int h = device.PresentationParameters.BackBufferHeight;
            var data = new Color[w * h];
            device.GetBackBufferData(data);

            Directory.CreateDirectory(ShotDir);
            string path = Path.Combine(ShotDir, name + ".png");

            // reads before present come back bottom-up; flip into top-down order
            var flipped = new Color[data.Length];
            for (int y = 0; y < h; y++)
                Array.Copy(data, (h - 1 - y) * w, flipped, y * w, w);

            var tex = new Texture2D(device, w, h);
            tex.SetData(flipped);
            using (var fs = File.Create(path))
                tex.SaveAsPng(fs, w, h);
            tex.Dispose();
            _capturedShot = name;
            Monitor.Log($"UI test: screenshot '{path}'", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"UI test: screenshot '{name}' failed: {ex.Message}", LogLevel.Warn);
            _capturedShot = name; // don't retry forever
        }
    }

    public void Update()
    {
        if (_done) return;
        try
        {
            Step();
        }
        catch (Exception ex)
        {
            Monitor.Log($"UI test FAILED in phase {Phase}: {ex}", LogLevel.Error);
            Finish(success: false);
        }
    }

    private void Next()
    {
        Phase++;
        PhaseStart = Clock.Elapsed.TotalSeconds;
    }

    private bool Waited(double seconds) => Clock.Elapsed.TotalSeconds - PhaseStart >= seconds;

    /// <summary>Request a screenshot; returns true once OnRendered has captured it.</summary>
    private bool Shot(string name)
    {
        if (_requestedShot != name)
        {
            _requestedShot = name;
            _capturedShot = null;
            _shotRequestTime = Clock.Elapsed.TotalSeconds;
        }
        if (_capturedShot == name) return true;
        if (Clock.Elapsed.TotalSeconds - _shotRequestTime > 30)
            Monitor.Log($"UI test: screenshot '{name}' still waiting for a draw…", LogLevel.Warn);
        return false;
    }

    private void Step()
    {
        switch (Phase)
        {
            case 0: // wait for the game to reach the title screen
                if (Game1.game1 != null && Game1.gameMode == 0 && Clock.Elapsed.TotalSeconds > 15)
                {
                    Monitor.Log("UI test: game ready (title screen)", LogLevel.Info);
                    // headless runs sit on the title screen; drop any leaked test history first
                    History.Clear();
                    SeedConversation();
                    Next();
                }
                break;

            case 1: // open the chat window
                Menu = OpenChat();
                if (Menu == null)
                {
                    Monitor.Log("UI test: chat menu did not open (provider unconfigured?) — jumping to settings", LogLevel.Warn);
                    Phase = 4;
                    PhaseStart = Clock.Elapsed.TotalSeconds;
                    return;
                }
                Monitor.Log("UI test: chat window opened", LogLevel.Info);
                Next();
                break;

            case 2: // let it render, screenshot, then send a real message
                if (!Waited(3)) return;
                if (!Shot("gamma-test-1-chat")) return;
                // ask in a fresh chat so the real first-exchange flow runs (incl. auto-naming);
                // the seeded conversation stays in the sidebar for the visual check
                History.NewChat();
                TurnsAtAsk = Chat.Turns.Count;
                Menu.TestTypeAndSend(Environment.GetEnvironmentVariable("GAMMA_AUTO_TEST_QUESTION")
                    ?? "Hello! What's my current money, and what should I plant first in spring?");
                Monitor.Log("UI test: message sent, waiting for reply…", LogLevel.Info);
                Next();
                break;

            case 3: // capture the "thinking" state, then wait for the reply and screenshot again
                if (!_thinkingCaptured)
                {
                    if (!Waited(0.8)) return;
                    if (Chat.Busy)
                    {
                        if (!Shot("gamma-test-2-thinking")) return;
                        Monitor.Log("UI test: captured thinking state", LogLevel.Info);
                    }
                    _thinkingCaptured = true;
                }
                if (Chat.Busy && !Waited(100)) return;
                if (!Waited(1.5)) return; // give the reply a moment to render
                if (!Shot("gamma-test-2-reply")) return;
                Next(); // keep the menu open for the gamepad phases
                break;

            case 4: // gamepad: B clears the input (menu stays open), A opens the OSK
                if (!Waited(0.5)) return;
                Menu.TestSetInput("controller typing test");
                Menu.receiveGamePadButton(Buttons.B);
                if (Menu.TestInputText != "")
                    throw new Exception("gamepad B did not clear the input");
                Game1.setMousePosition(Menu.TestInputBounds.Center, ui_scale: true);
                Menu.update(Game1.currentGameTime);
                Menu.receiveGamePadButton(Buttons.Y);
                if (Game1.textEntry != null)
                    throw new Exception("Hovering a field or pressing Y opened the on-screen keyboard");
                Menu.receiveGamePadButton(Buttons.A);
                if (Game1.textEntry == null)
                    throw new Exception("gamepad A on the input did not open the on-screen keyboard");
                Next();
                break;

            case 5: // screenshot the on-screen keyboard over the chat window
                if (!Waited(0.5)) return;
                if (!Shot("gamma-test-3-osk")) return;
                Next();
                break;

            case 6: // gamepad B with the OSK open: the game routes it to the keyboard
                    // exclusively (updateTextEntry replaces updateActiveMenu), so the
                    // keyboard closes and the chat menu must NOT have seen the press
                Game1.textEntry.receiveGamePadButton(Buttons.B);
                if (Game1.textEntry != null)
                    throw new Exception("gamepad B did not close the on-screen keyboard");
                if (Game1.activeClickableMenu is not ChatMenu)
                    throw new Exception("chat menu closed while the on-screen keyboard was open");
                Next();
                break;

            case 7: // a later B press (keyboard now closed) reaches the menu and closes it
                if (!Waited(0.5)) return;
                Menu.receiveGamePadButton(Buttons.B);
                Next();
                break;

            case 8: // confirm the chat menu closed (at the title screen the game's own
                    // TitleMenu re-registers itself as active afterwards, so don't check
                    // for null — check that OUR menu is gone)
                if (!Waited(1)) return;
                if (ReferenceEquals(Game1.activeClickableMenu, Menu))
                    throw new Exception("gamepad B did not close the chat menu");
                Next();
                break;

            case 9: // open settings
                if (!Waited(1.5)) return;
                OpenSettings();
                Next();
                break;

            case 10: // screenshot settings, wrap up
                if (!Waited(3)) return;
                if (!Shot("gamma-test-4-settings")) return;
                Game1.exitActiveMenu();
                Finish(success: true);
                break;
        }
    }

    private void SeedConversation()
    {
        string[] seed =
        {
            "user|What's the best thing to plant in spring?",
            "assistant|Potatoes are the classic first-spring money maker — buy them from Pierre on day 1, "
            + "and they can multiply your seed cost several times over by harvest. Strawberries (from the Egg "
            + "festival on day 13) are even better if you can afford the upfront cost.\n\n"
            + "This is a seeded test message to check how multiline chat text renders in the window.",
            "user|Thanks! And what's my luck today?",
        };
        foreach (var line in seed)
        {
            int i = line.IndexOf('|');
            History.Add(line[..i], line[(i + 1)..]);
            SeededTurns++;
        }
        Monitor.Log($"UI test: seeded {SeededTurns} sample turns", LogLevel.Info);
    }

    private void Finish(bool success)
    {
        _done = true;
        try
        {
            // remove the seeded sample turns; keep the real conversation from the live request
            if (SeededTurns > 0 && History.Turns.Count >= SeededTurns)
            {
                History.Turns.RemoveRange(0, SeededTurns);
                History.Save();
            }
        }
        catch { }

        Monitor.Log($"UI test {(success ? "COMPLETE" : "FAILED")} — {History.Turns.Count} turns in history. "
            + $"Screenshots in {ShotDir}.", LogLevel.Info);
        foreach (var t in History.Turns.TakeLast(4))
            Monitor.Log($"  [{t.Role}] {t.Text.Replace("\n", " / ")[..Math.Min(140, t.Text.Length)]}…", LogLevel.Info);

        if (AutoExit)
        {
            Monitor.Log("UI test: exiting game.", LogLevel.Info);
            Game1.game1.Exit();
        }
    }
}
