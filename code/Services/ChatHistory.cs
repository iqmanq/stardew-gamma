using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace Gamma.Services;

public class ChatTurn
{
    public string Role; // "user" | "assistant"
    public string Text;
}

public class Conversation
{
    public string Id = Guid.NewGuid().ToString("N");
    public string Title = "New chat";
    public List<ChatTurn> Turns = new();

    // Runtime state belongs to this conversation and is intentionally not persisted.
    public bool Busy;
    public string Status;
    public readonly List<string> Notices = new();
}

public class SavedTurn
{
    public string Role;
    public string Text;
}

public class SavedConversation
{
    public string Id;
    public string Title;
    public List<SavedTurn> Turns = new();
}

public class SavedChats
{
    public List<SavedConversation> Conversations = new();
    public int ActiveIndex;
}

/// <summary>
/// Multiple named conversations, persisted per-save in SMAPI's global data store.
/// The active conversation is what the chat window displays and the AI remembers.
/// </summary>
public class ChatHistory
{
    private readonly IModHelper Helper;
    private readonly IMonitor Monitor;
    private readonly ModConfig Config;
    private readonly List<Conversation> Conversations = new();
    private int Active;

    public ChatHistory(IModHelper helper, IMonitor monitor, ModConfig config)
    {
        Helper = helper;
        Monitor = monitor;
        Config = config;
    }

    // limits are read live from the config so Generic Mod Config Menu edits apply immediately
    private int MaxChats => Math.Max(1, ModConfig.MaxChats);
    private int MaxTurns => Math.Max(2, Config.MaxTurnsPerChat);
    private int SavedTurns => Math.Clamp(Config.SavedTurnsPerChat, 1, MaxTurns);

    public Conversation Current
    {
        get
        {
            if (Conversations.Count == 0) Conversations.Add(new Conversation());
            Active = Math.Clamp(Active, 0, Conversations.Count - 1);
            return Conversations[Active];
        }
    }

    public IReadOnlyList<Conversation> All => Conversations;
    public int ActiveIndex => Active;

    /// <summary>Turns of the active conversation (what the UI renders and the AI sees).</summary>
    public List<ChatTurn> Turns => Current.Turns;

    public void Select(int index)
    {
        if (index < 0 || index >= Conversations.Count) return;
        Active = index;
    }

    public Conversation NewChat()
    {
        var chat = new Conversation();
        Conversations.Insert(0, chat);
        Active = 0;
        TrimConversations();
        return chat;
    }

    /// <summary>Deletes a conversation by id.</summary>
    public void Delete(string id)
    {
        int index = Conversations.FindIndex(c => c.Id == id);
        if (index < 0) return;
        Conversations.RemoveAt(index);
        if (Conversations.Count == 0) Conversations.Add(new Conversation());
        Active = Math.Clamp(Active > index ? Active - 1 : Active, 0, Conversations.Count - 1);
    }

    public void Add(string role, string text) => Add(Current, role, text);

    public void Add(Conversation convo, string role, string text)
    {
        // A deleted chat (or one from a previous save) must not be resurrected.
        if (!Conversations.Contains(convo) || string.IsNullOrWhiteSpace(text)) return;
        var turns = convo.Turns;
        turns.Add(new ChatTurn { Role = role, Text = text });
        if (turns.Count > MaxTurns)
            turns.RemoveRange(0, turns.Count - MaxTurns);

        // auto-title from the first user message
        if (convo.Title == "New chat" && role == "user")
        {
            string title = text.Trim().Replace("\n", " ");
            convo.Title = title.Length <= 48 ? title : title.Substring(0, 48) + "…";
        }
    }

    public List<ChatTurn> Recent(int max) =>
        max <= 0 || Turns.Count <= max ? Turns.ToList() : Turns.GetRange(Turns.Count - max, max);

    /// <summary>Clears the active conversation's turns.</summary>
    public void Clear() => Current.Turns.Clear();

    public void Load()
    {
        Conversations.Clear();
        Active = 0;
        try
        {
            var saved = Helper.Data.ReadGlobalData<SavedChats>(Key());
            if (saved?.Conversations != null)
            {
                foreach (var sc in saved.Conversations)
                {
                    var convo = new Conversation { Id = sc.Id, Title = sc.Title };
                    foreach (var t in sc.Turns)
                        convo.Turns.Add(new ChatTurn { Role = t.Role, Text = t.Text });
                    Conversations.Add(convo);
                }
                Active = Math.Clamp(saved.ActiveIndex, 0, Math.Max(0, Conversations.Count - 1));
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Couldn't load chat history: {ex.Message}", LogLevel.Warn);
        }
    }

    public void Save()
    {
        try
        {
            var saved = new SavedChats { ActiveIndex = Active };
            int kept = Config.AutoDeleteOldChats ? Math.Min(Conversations.Count, MaxChats) : Conversations.Count;
            foreach (var convo in Conversations.Take(kept))
            {
                var sc = new SavedConversation { Id = convo.Id, Title = convo.Title };
                foreach (var t in convo.Turns.Skip(Math.Max(0, convo.Turns.Count - SavedTurns)))
                    sc.Turns.Add(new SavedTurn { Role = t.Role, Text = t.Text });
                saved.Conversations.Add(sc);
            }
            Helper.Data.WriteGlobalData(Key(), saved);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Couldn't save chat history: {ex.Message}", LogLevel.Warn);
        }
    }

    private void TrimConversations()
    {
        if (!Config.AutoDeleteOldChats) return;

        // drop oldest empty conversations when over the cap (never drop the active one)
        while (Conversations.Count > MaxChats)
        {
            int index = Conversations.FindLastIndex(c => c.Turns.Count == 0 && c != Current);
            if (index < 0) break;
            Conversations.RemoveAt(index);
            if (Active > index) Active--;
        }
    }

    private string Key()
    {
        // The host farmer's multiplayer ID is stable per save, which is all we need to
        // keep conversations separate between saves.
        string id = null;
        try { id = Context.IsWorldReady ? Game1.player?.UniqueMultiplayerID.ToString() : null; }
        catch { }
        return "gamma-chats-" + (string.IsNullOrEmpty(id) ? "no-save" : id);
    }
}
