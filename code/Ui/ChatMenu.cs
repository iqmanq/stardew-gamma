using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using Gamma.Services;

namespace Gamma.Ui;

/// <summary>
/// The in-game chat window. Styled after the vanilla multiplayer chat box: wooden menu box,
/// game fonts, the shared chat text field texture, and vanilla menu sounds.
/// </summary>
public class ChatMenu : IClickableMenu
{
    private sealed class Line { public string Text; public Color Color; }
    private sealed class RenderedTurn { public List<Line> Lines = new(); }

    private static readonly Color UserColor = new(255, 220, 130);
    private static readonly Color GammaColor = Color.White;
    private static readonly Color TitleColor = new(255, 236, 160);
    private static readonly Color StatusColor = new(214, 202, 178);

    private readonly ModConfig Config;
    private readonly ChatService Chat;
    private readonly ChatHistory History;
    private readonly Action OpenSettings;
    private readonly TextBox Input;
    private readonly Rectangle SendButton;
    private readonly Rectangle SettingsButton;
    private readonly int InputHeight;

    private readonly ConcurrentQueue<(Conversation Conversation, bool FromUser, string Text, bool Persisted)> PendingMessages = new();

    private List<RenderedTurn> Rendered = new();
    private bool _dirty = true;
    private int _scroll; // pixels back from the newest message (0 = pinned to bottom)
    private int _lastMessageCount = -1;
    private string _status;

    // conversation sidebar (hitboxes rebuilt every draw)
    private const int SidebarW = 224;
    private const int RowH = 42;
    private Rectangle NewChatButton;
    private readonly List<Rectangle> ChatRows = new();
    private readonly List<Rectangle> ChatDeletes = new();
    private readonly List<Rectangle> ChatEdits = new();
    private readonly List<string> RowIds = new();
    private int _sidebarScroll;

    // inline title rename (started from the pencil icon on a chat row)
    private string _editingId;
    private TextBox _renameBox;

    public ChatMenu(ModConfig config, ChatService chat, Action openSettings = null)
        : base(0, 0, 0, 0, showUpperRightCloseButton: true)
    {
        Config = config;
        Chat = chat;
        History = chat.History;
        OpenSettings = openSettings;

        int w = Math.Min(1300, Game1.uiViewport.Width - 100);
        int h = Math.Min(780, Game1.uiViewport.Height - 120);
        width = w;
        height = h;
        xPositionOnScreen = (Game1.uiViewport.Width - w) / 2;
        yPositionOnScreen = Game1.uiViewport.Height - h - 16;
        // re-run layout so the upper-right close button lands on the actual menu bounds
        initialize(xPositionOnScreen, yPositionOnScreen, width, height, showUpperRightCloseButton: true);

        var chatTexture = Game1.content.Load<Texture2D>("LooseSprites\\chatBox");
        // The chatBox texture is a 300x336 sheet: the entry frame is its top 48px, then a
        // gap, then the big message bubble. TextBox samples the top `Height` rows of it, so
        // anything taller than ~56 (vanilla's entry height) starts showing the bubble's
        // top border as a phantom second box.
        InputHeight = 56;
        Input = new TextBox(chatTexture, null, Game1.smallFont, Color.White)
        {
            X = xPositionOnScreen + 16,
            Width = width - 32 - 130 * 2 - 24,
            Height = InputHeight,
            Y = yPositionOnScreen + height - InputHeight - 14,
            Selected = true,
            // without this the game's TextBox deletes typed characters once the text no
            // longer fits the field — long questions would be silently cut off
            limitWidth = false,
        };
        Game1.keyboardDispatcher.Subscriber = Input;
        // Enter is unified through the TextBox's OnEnterPressed event: the keyboard
        // dispatcher feeds it '\r' for a physical Enter, and the game's on-screen
        // keyboard ("OK"/Start) does the same — one path sends for both inputs.
        Input.OnEnterPressed += OnInputEnter;
        SendButton = new Rectangle(xPositionOnScreen + width - 130 - 16, Input.Y, 130, InputHeight);
        SettingsButton = new Rectangle(SendButton.X - 130 - 12, Input.Y, 130, InputHeight);

        // Queue service notifications until the menu updates.
        Chat.MessageAdded += OnChatMessage;

        Game1.playSound("shwip");
    }

    // ---------- conversation-scoped service notifications ----------

    public void OnChatMessage(Conversation conversation, bool fromUser, string text, bool persisted) =>
        PendingMessages.Enqueue((conversation, fromUser, text, persisted));

    // ---------- per-frame ----------

    public override void update(GameTime time)
    {
        base.update(time);
        // Focus is managed explicitly. TextBox.Update opens the keyboard on hover.

        while (PendingMessages.TryDequeue(out var msg))
        {
            if (msg.Conversation != History.Current) continue;
            _dirty = true;
            if (!msg.FromUser)
            {
                _scroll = 0;
                Game1.playSound("smallSelect");
            }
        }
        _status = History.Current.Status;

        if (Chat.Turns.Count != _lastMessageCount)
        {
            _dirty = true;
            _lastMessageCount = Chat.Turns.Count;
        }
    }

    public override void draw(SpriteBatch b)
    {
        VanillaUi.DrawBox(b, xPositionOnScreen, yPositionOnScreen, width, height);

        int sidebarX = xPositionOnScreen + 16;
        int sidebarTop = yPositionOnScreen + 52;
        int sidebarBottom = Input.Y - 8;
        int msgX = sidebarX + SidebarW + 20;
        int titleTop = yPositionOnScreen + 12;
        int areaTop = titleTop + VanillaUi.LineHeight + 8;
        int areaBottom = Input.Y - 8;
        int statusTop = -1;
        if (!string.IsNullOrEmpty(_status))
        {
            statusTop = areaBottom - VanillaUi.LineHeight; // status owns the bottom line
            areaBottom = statusTop;
        }
        int wrapWidth = (xPositionOnScreen + width - 22) - msgX;

        // ----- conversation sidebar -----
        NewChatButton = new Rectangle(sidebarX, sidebarTop, SidebarW, 46);
        VanillaUi.DrawBox(b, NewChatButton.X, NewChatButton.Y, NewChatButton.Width, NewChatButton.Height);
        var newText = Game1.smallFont.MeasureString("+ New chat");
        b.DrawString(Game1.smallFont, "+ New chat",
            new Vector2(NewChatButton.X + (NewChatButton.Width - newText.X) / 2f, NewChatButton.Y + (NewChatButton.Height - newText.Y) / 2f),
            TitleColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.95f);

        ChatRows.Clear();
        ChatDeletes.Clear();
        ChatEdits.Clear();
        RowIds.Clear();
        int rowTop = NewChatButton.Y + NewChatButton.Height + 10;
        int visibleRows = Math.Max(1, (sidebarBottom - rowTop) / (RowH + 4));
        var all = Chat.History.All;
        int maxScroll = Math.Max(0, all.Count - visibleRows);
        _sidebarScroll = Math.Clamp(_sidebarScroll, 0, maxScroll);
        int dividerX = sidebarX + SidebarW + 8;
        b.Draw(Game1.staminaRect, new Rectangle(dividerX, sidebarTop + 4, 2, sidebarBottom - sidebarTop - 8),
            null, new Color(214, 202, 178) * 0.35f, 0f, Vector2.Zero, SpriteEffects.None, 0.9f);

        for (int i = _sidebarScroll; i < all.Count && ChatRows.Count < visibleRows; i++)
        {
            var convo = all[i];
            var row = new Rectangle(sidebarX, rowTop + (ChatRows.Count) * (RowH + 4), SidebarW, RowH);
            bool active = i == Chat.History.ActiveIndex;

            b.Draw(Game1.staminaRect, row, null,
                active ? new Color(255, 236, 160) * 0.18f : Color.White * 0.06f,
                0f, Vector2.Zero, SpriteEffects.None, 0.9f);
            if (active)
                b.Draw(Game1.staminaRect, new Rectangle(row.X, row.Y, 3, row.Height), null,
                    new Color(255, 236, 160) * 0.9f, 0f, Vector2.Zero, SpriteEffects.None, 0.9f);

            if (convo.Id == _editingId && _renameBox != null)
            {
                // the rename field replaces the row's contents; the full 48px entry-frame
                // texture slice is slightly taller than the row so the frame stays intact
                _renameBox.X = row.X + 6;
                _renameBox.Y = row.Y + (RowH - 48) / 2;
                _renameBox.Draw(b, false);
                ChatRows.Add(row);
                ChatDeletes.Add(Rectangle.Empty);
                ChatEdits.Add(Rectangle.Empty);
                RowIds.Add(convo.Id);
                continue;
            }

            string title = FitTitle(convo.Title, SidebarW - 10 - 72); // leave room for the icons
            b.DrawString(Game1.smallFont, title, new Vector2(row.X + 10, row.Y + (RowH - VanillaUi.LineHeight) / 2f),
                active ? TitleColor : GammaColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);

            // pencil icon (diagonal body + tip) opens the inline rename; staminaRect is a
            // 1x1 texture, so the Vector2 scale stretches it into the bar/tip shapes
            var edit = new Rectangle(row.Right - 66, row.Y + (RowH - 30) / 2, 28, 30);
            var cen = new Vector2(edit.Center.X, edit.Center.Y);
            b.Draw(Game1.staminaRect, cen, null, new Color(240, 232, 210), -MathF.PI / 4f,
                new Vector2(0.5f, 0.5f), new Vector2(18f, 3f), SpriteEffects.None, 0.9f);
            b.Draw(Game1.staminaRect, cen + new Vector2(7.1f, -7.1f), null, new Color(255, 170, 90),
                -MathF.PI / 4f, new Vector2(0.5f, 0.5f), new Vector2(4.5f, 4.5f), SpriteEffects.None, 0.9f);

            var del = new Rectangle(row.Right - 34, row.Y + (RowH - 30) / 2, 30, 30);
            b.DrawString(Game1.smallFont, "X", new Vector2(del.X + (del.Width - Game1.smallFont.MeasureString("X").X) / 2f, del.Y + 4),
                new Color(230, 130, 110), 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);

            ChatRows.Add(row);
            ChatDeletes.Add(del);
            ChatEdits.Add(edit);
            RowIds.Add(convo.Id);
        }

        DrawTitleTooltip(b);

        // ----- message column -----
        string botName = Config.ChatbotName ?? "Gamma";
        b.DrawString(Game1.smallFont, botName, new Vector2(msgX, titleTop), TitleColor,
            0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);
        // Give hints their own wrapped rows and reserve the close button's column.
        int hintRight = xPositionOnScreen + width - 22;
        if (upperRightCloseButton != null)
            hintRight = Math.Min(hintRight, upperRightCloseButton.bounds.Left - 12);
        string hints = Game1.options.gamepadControls
            ? "A on field to type  ·  X new chat  ·  LB/RB switch chat  ·  B to close  ·  right stick to review"
            : "Enter to send  ·  Esc to close  ·  scroll wheel to review";
        var hintLines = VanillaUi.Wrap(Game1.smallFont, hints, Math.Max(1, hintRight - msgX));
        int hintY = titleTop + VanillaUi.LineHeight;
        foreach (string line in hintLines)
        {
            b.DrawString(Game1.smallFont, line, new Vector2(msgX, hintY),
                StatusColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);
            hintY += VanillaUi.LineHeight;
        }
        areaTop = hintY + 8;

        if (_dirty)
        {
            Rebuild(wrapWidth);
            _dirty = false;
        }

        int totalHeight = Rendered.Sum(t => t.Lines.Count * VanillaUi.LineHeight);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, totalHeight - (areaBottom - areaTop)));

        int y = areaTop + (areaBottom - areaTop) - totalHeight + _scroll;
        foreach (var turn in Rendered)
        {
            foreach (var line in turn.Lines)
            {
                if (y >= areaTop && y + VanillaUi.LineHeight <= areaBottom)
                {
                    b.DrawString(Game1.smallFont, line.Text, new Vector2(msgX, y), line.Color,
                        0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);
                }
                y += VanillaUi.LineHeight;
            }
        }

        if (statusTop > 0)
        {
            // while busy the indicator animates ("Thinking…", dots cycling) and glows gold
            // so the waiting state is obvious; idle notices stay quiet gray
            string text = _status;
            Color color = StatusColor;
            if (Chat.Busy)
            {
                int dots = 1 + (int)(Game1.currentGameTime?.TotalGameTime.TotalMilliseconds / 350 ?? 0) % 3;
                text = text.TrimEnd('…', '.', ' ') + " " + new string('.', dots);
                float pulse = 0.85f + 0.15f * (float)Math.Sin(Game1.currentGameTime?.TotalGameTime.TotalMilliseconds / 180 ?? 0);
                color = new Color(255, 236, 160) * pulse;
            }
            b.DrawString(Game1.smallFont, text, new Vector2(msgX, statusTop),
                color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9f);
        }

        Input.Draw(b, false);

        VanillaUi.DrawBox(b, SettingsButton.X, SettingsButton.Y, SettingsButton.Width, SettingsButton.Height);
        var settingsSize = Game1.smallFont.MeasureString("Settings");
        b.DrawString(Game1.smallFont, "Settings",
            new Vector2(SettingsButton.X + (SettingsButton.Width - settingsSize.X) / 2f, SettingsButton.Y + (SettingsButton.Height - settingsSize.Y) / 2f),
            GammaColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.95f);

        VanillaUi.DrawBox(b, SendButton.X, SendButton.Y, SendButton.Width, SendButton.Height);
        var size = Game1.smallFont.MeasureString("Send");
        Color sendColor = Chat.Busy ? StatusColor : GammaColor;
        b.DrawString(Game1.smallFont, Chat.Busy ? "…" : "Send",
            new Vector2(SendButton.X + (SendButton.Width - size.X) / 2f, SendButton.Y + (SendButton.Height - size.Y) / 2f),
            sendColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.95f);

        base.draw(b);
        // the game skips its own cursor draw while a menu is open; menus draw it themselves
        drawMouse(b, false, -1);
    }

    /// <summary>Trims a conversation title to actually fit the sidebar row, per the font.</summary>
    private static string FitTitle(string title, float maxWidth)
    {
        title = title.Replace("\n", " ").Trim();
        if (Game1.smallFont.MeasureString(title).X <= maxWidth) return title;
        while (title.Length > 1 && Game1.smallFont.MeasureString(title + "…").X > maxWidth)
            title = title[..^1];
        return title.TrimEnd() + "…";
    }

    /// <summary>Shows the full chat title in a tooltip when a truncated sidebar row is hovered.</summary>
    private void DrawTitleTooltip(SpriteBatch b)
    {
        if (_editingId != null) return;
        int mx = Game1.getMouseX(), my = Game1.getMouseY();
        for (int i = 0; i < ChatRows.Count; i++)
        {
            if (!ChatRows[i].Contains(mx, my) || ChatDeletes[i] != Rectangle.Empty && ChatDeletes[i].Contains(mx, my)) continue;
            var convo = History.All.FirstOrDefault(c => c.Id == RowIds[i]);
            if (convo == null || FitTitle(convo.Title, SidebarW - 10 - 72) == convo.Title.Replace("\n", " ").Trim())
                return; // hovering this row, but the title already fits

            var lines = VanillaUi.Wrap(Game1.smallFont, convo.Title.Replace("\n", " "), 260);
            int tw = (int)lines.Max(l => Game1.smallFont.MeasureString(l).X) + 24;
            int th = lines.Count * VanillaUi.LineHeight + 16;
            int tx = Math.Min(mx + 28, Game1.uiViewport.Width - tw - 8);
            int ty = Math.Min(my + 40, Game1.uiViewport.Height - th - 8);
            VanillaUi.DrawBox(b, tx, ty, tw, th);
            for (int li = 0; li < lines.Count; li++)
                b.DrawString(Game1.smallFont, lines[li], new Vector2(tx + 12, ty + 8 + li * VanillaUi.LineHeight),
                    Color.White, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.99f);
            return;
        }
    }

    private void StartRename(string id)
    {
        var convo = History.All.FirstOrDefault(c => c.Id == id);
        if (convo == null) return;
        Input.Selected = false;
        _editingId = id;
        _renameBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\chatBox"), null, Game1.smallFont, Color.White)
        {
            Width = SidebarW - 16,
            Height = 48,
            Text = convo.Title,
            Selected = true,
            limitWidth = false,
        };
        _renameBox.OnEnterPressed += OnRenameEnter;
        Game1.keyboardDispatcher.Subscriber = _renameBox;
        Game1.playSound("shiny4");
    }

    private void OnRenameEnter(TextBox box) => CommitRename(save: true);

    private void CommitRename(bool save)
    {
        if (_renameBox != null) _renameBox.OnEnterPressed -= OnRenameEnter;
        var convo = History.All.FirstOrDefault(c => c.Id == _editingId);
        if (save && convo != null)
        {
            string title = _renameBox?.Text?.Trim().Replace("\n", " ");
            if (!string.IsNullOrEmpty(title))
            {
                convo.Title = title.Length > 48 ? title[..48].TrimEnd() + "…" : title;
                History.Save();
                Game1.playSound("levelup");
            }
        }
        _editingId = null;
        _renameBox = null;
        Input.Selected = true;
        Game1.keyboardDispatcher.Subscriber = Input;
        _dirty = true;
    }

    private void Rebuild(int wrapWidth)
    {
        Rendered = new List<RenderedTurn>();
        foreach (var turn in Chat.Turns)
        {
            bool user = turn.Role == "user";
            string text = (user ? "You: " : (Config.ChatbotName ?? "Gamma") + ": ") + (turn.Text ?? "").Replace("\r\n", "\n");
            var rendered = new RenderedTurn();
            foreach (var raw in text.Split('\n'))
                foreach (var line in VanillaUi.Wrap(Game1.smallFont, raw, wrapWidth))
                    rendered.Lines.Add(new Line { Text = line, Color = user ? UserColor : GammaColor });
            Rendered.Add(rendered);
        }
        foreach (var text in History.Current.Notices)
        {
            var rendered = new RenderedTurn();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                foreach (var line in VanillaUi.Wrap(Game1.smallFont, raw, wrapWidth))
                    rendered.Lines.Add(new Line { Text = line, Color = StatusColor });
            Rendered.Add(rendered);
        }
        _lastMessageCount = Chat.Turns.Count;
    }

    // ---------- input ----------

    private void OnInputEnter(TextBox box) => Send();

    public override void receiveKeyPress(Keys key)
    {
        // In gamepad compatibility mode the game maps B/Start/Y to the menu key (Escape
        // by default) and feeds them here as well; those buttons are handled properly in
        // receiveGamePadButton, so don't let the mapped Escape clear the input or close.
        if (Game1.options.gamepadControls
            && (Game1.input.GetGamePadState().IsButtonDown(Buttons.B)
                || Game1.input.GetGamePadState().IsButtonDown(Buttons.Start)
                || Game1.input.GetGamePadState().IsButtonDown(Buttons.Y)))
            return;
        if (_editingId != null)
        {
            if (key == Keys.Enter || key == Keys.Tab) CommitRename(save: true);
            else if (key == Keys.Escape) CommitRename(save: false);
            return; // all other keys reach the rename box through the dispatcher
        }
        if (key == Keys.Escape)
        {
            if (Game1.textEntry != null)
            {
                Game1.closeTextEntry(); // Esc with the on-screen keyboard open: just dismiss it
                return;
            }
            if (!string.IsNullOrEmpty(Input.Text))
                Input.Text = "";
            else
                Close();
            return;
        }
        // Enter needs no case here: it arrives through Input.OnEnterPressed (see ctor).
        // Don't call base.receiveKeyPress: it closes the menu when the key matches
        // Options.menuButton (default Escape AND E). Typing reaches the TextBox through
        // the keyboard dispatcher, so unhandled keys can simply be swallowed here.
    }

    // Keep analog cursor movement available even when the player enables snappy menus.
    public override bool overrideSnappyMenuCursorMovementBan() => true;

    /// <summary>
    /// Gamepad support (the menu keeps the game's compatibility mode: left stick moves the
    /// cursor, A/X are converted to left/right clicks, right stick scrolls — see
    /// Game1.Update's !areGamePadControlsImplemented paths). What we add on top:
    /// B clears/closes, A on a text field opens the keyboard, X starts a new chat,
    /// LB/RB (or triggers) switch to the previous/next conversation, D-pad up/down
    /// scrolls the history — so the sidebar never needs pixel-aiming with the stick.
    /// </summary>
    public override void receiveGamePadButton(Buttons button)
    {
        // While the on-screen keyboard is open the game routes gamepad input to it
        // exclusively (updateTextEntry instead of updateActiveMenu), so this menu sees
        // nothing until the keyboard closes; this check is just a safety net.
        if (Game1.textEntry != null)
            return;
        switch (button)
        {
            case Buttons.B:
                if (_editingId != null) { CommitRename(save: false); break; }
                if (!string.IsNullOrEmpty(Input.Text)) { Input.Text = ""; Game1.playSound("tinyWhip"); break; }
                Close();
                break;
            case Buttons.A:
                var box = _renameBox ?? Input;
                if (new Rectangle(box.X, box.Y, box.Width, box.Height).Contains(Game1.getMouseX(), Game1.getMouseY()))
                    Game1.showTextEntry(box);
                break;
            case Buttons.X:
                if (_editingId != null) break; // finish/cancel the rename first (B or Enter)
                History.NewChat();
                ConversationChanged();
                break;
            case Buttons.LeftShoulder:
            case Buttons.LeftTrigger:
                if (_editingId != null) break;
                SwitchChat(-1);
                break;
            case Buttons.RightShoulder:
            case Buttons.RightTrigger:
                if (_editingId != null) break;
                SwitchChat(+1);
                break;
            case Buttons.DPadUp:
                _scroll += 80;
                break;
            case Buttons.DPadDown:
                _scroll -= 80;
                break;
        }
    }

    /// <summary>Moves the active conversation without touching the sidebar cursor.</summary>
    private void SwitchChat(int direction)
    {
        var all = History.All;
        if (all.Count == 0) return;
        int next = (History.ActiveIndex + direction + all.Count) % all.Count;
        if (next == History.ActiveIndex) return;
        History.Select(next);
        ConversationChanged();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        // With the on-screen keyboard open, clicks belong to it (gamepad A hits its keys
        // even though the cursor overlaps our menu underneath)
        if (Game1.textEntry != null)
            return;
        if (_editingId != null)
        {
            // clicking the rename field keeps editing; anywhere else commits the title
            if (_renameBox != null && x >= _renameBox.X && x < _renameBox.X + _renameBox.Width
                && y >= _renameBox.Y && y < _renameBox.Y + _renameBox.Height)
                return;
            CommitRename(save: true);
        }
        if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
        {
            Close();
            return;
        }
        if (SendButton.Contains(x, y))
        {
            Send();
            return;
        }
        if (SettingsButton.Contains(x, y))
        {
            OpenSettings?.Invoke();
            return;
        }

        // conversation sidebar
        int sidebarX = xPositionOnScreen + 16;
        if (x >= sidebarX && x < sidebarX + SidebarW && y >= yPositionOnScreen + 40 && y < Input.Y)
        {
            if (NewChatButton.Contains(x, y))
            {
                History.NewChat();
                ConversationChanged();
                return;
            }
            for (int i = 0; i < ChatDeletes.Count; i++)
            {
                if (ChatDeletes[i] != Rectangle.Empty && ChatDeletes[i].Contains(x, y))
                {
                    History.Delete(RowIds[i]);
                    Game1.playSound("shwip"); // 'throw' was harsh for deleting one chat
                    ConversationChanged();
                    return;
                }
            }
            for (int i = 0; i < ChatEdits.Count; i++)
            {
                if (ChatEdits[i] != Rectangle.Empty && ChatEdits[i].Contains(x, y))
                {
                    StartRename(RowIds[i]);
                    return;
                }
            }
            for (int i = 0; i < ChatRows.Count; i++)
            {
                if (ChatRows[i].Contains(x, y))
                {
                    int index = History.All.ToList().FindIndex(c => c.Id == RowIds[i]);
                    if (index >= 0 && index != History.ActiveIndex)
                    {
                        History.Select(index);
                        ConversationChanged();
                    }
                    return;
                }
            }
            return;
        }

        Input.Selected = true;
        base.receiveLeftClick(x, y, playSound);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (Game1.textEntry != null)
            return; // right stick/wheel scrolls the on-screen keyboard while it's open
        base.receiveScrollWheelAction(direction);
        int mx = Game1.getMouseX(true);
        int my = Game1.getMouseY(true);
        int sidebarX = xPositionOnScreen + 16;
        if (mx >= sidebarX && mx < sidebarX + SidebarW + 10 && my >= yPositionOnScreen + 40 && my < Input.Y)
        {
            _sidebarScroll -= direction * 2; // one row per notch
            int visibleRows = Math.Max(1, (Input.Y - 8 - (yPositionOnScreen + 52 + 56)) / (RowH + 4));
            _sidebarScroll = Math.Clamp(_sidebarScroll, 0, Math.Max(0, Chat.History.All.Count - visibleRows));
        }
        else
        {
            _scroll += direction * 80;
        }
        _dirty = false;
    }

    private void ConversationChanged()
    {
        _dirty = true;
        _scroll = 0;
        _lastMessageCount = -1;
        _status = History.Current.Status;
        Input.Selected = true;
        Game1.playSound("smallSelect");
    }

    private void Send()
    {
        string text = Input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        Input.Text = "";
        _scroll = 0;
        Game1.playSound("smallSelect");
        Chat.Ask(text);
    }

    /// <summary>Test hook for the automated UI harness: fills the input and sends it.</summary>
    internal void TestTypeAndSend(string text)
    {
        Input.Text = text;
        Send();
    }

    /// <summary>Test hook: fills the input without sending (gamepad tests).</summary>
    internal void TestSetInput(string text) => Input.Text = text;

    /// <summary>Test hook: current input text (gamepad tests).</summary>
    internal string TestInputText => Input.Text;

    internal Rectangle TestInputBounds => new(Input.X, Input.Y, Input.Width, Input.Height);

    private void Close()
    {
        Cleanup();
        exitThisMenu(false);
        Game1.playSound("bigDeSelect");
    }

    private void Cleanup()
    {
        Chat.MessageAdded -= OnChatMessage;
        Input.OnEnterPressed -= OnInputEnter;
        Game1.keyboardDispatcher.Subscriber = null;
    }

    public override void emergencyShutDown()
    {
        Cleanup();
        base.emergencyShutDown();
    }
}
