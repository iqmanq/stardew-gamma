using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Quests;
using StardewValley.Network;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace Gamma.Services;

/// <summary>Reads the player's game state and turns it into compact text the AI can use.</summary>
public class GameStateService
{
    private readonly IModHelper Helper;
    private readonly IMonitor Monitor;

    public GameStateService(IModHelper helper, IMonitor monitor)
    {
        Helper = helper;
        Monitor = monitor;
    }

    private static bool WorldReady => Context.IsWorldReady && Game1.player != null && Game1.currentLocation != null;

    // ---------- shared reflection helpers (dodge fragile field types) ----------

    internal static object FieldVal(object obj, string name)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        var f = type.GetField(name);
        if (f != null) return f.GetValue(obj);
        var p = type.GetProperty(name);
        return p != null ? p.GetValue(obj) : null;
    }

    internal static int IntVal(object obj, string name)
    {
        return FieldVal(obj, name) switch
        {
            int i => i,
            long l => (int)l,
            NetInt n => n.Value,
            _ => 0
        };
    }

    internal static bool BoolVal(object obj, string name)
    {
        return FieldVal(obj, name) switch
        {
            bool b => b,
            NetBool n => n.Value,
            _ => false
        };
    }

    internal static string StringVal(object obj, string name)
    {
        return FieldVal(obj, name) switch
        {
            string s => s,
            NetString n => n.Value,
            _ => null
        };
    }

    private static string TileOf(Character c) =>
        c == null ? "(?)" : $"({(int)(c.Position.X / 64f)},{(int)(c.Position.Y / 64f)})";

    private static string QualityTag(Item item) => IntVal(item, "Quality") switch
    {
        1 => " (silver)",
        2 => " (gold)",
        4 => " (iridium)",
        _ => ""
    };

    private static string DescribeItem(Item item)
    {
        if (item == null) return null;
        string stack = item.Stack > 1 ? $" x{item.Stack}" : "";
        return $"{item.DisplayName}{stack}{QualityTag(item)}";
    }

    private static int StatValue(string key)
    {
        try
        {
            var m = Game1.player.stats.GetType().GetMethod("Get", new[] { typeof(string) });
            var v = m?.Invoke(Game1.player.stats, new object[] { key });
            return v == null ? 0 : Convert.ToInt32(v);
        }
        catch
        {
            return 0;
        }
    }

    // ---------- date / weather / misc strings ----------

    private static string DateString()
    {
        string[] days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        string weekday = days[(Game1.dayOfMonth - 1) % 7];
        bool festival = false;
        try
        {
            var season = (Season)Enum.Parse(typeof(Season), Game1.currentSeason, true);
            festival = Utility.isFestivalDay(Game1.dayOfMonth, season);
        }
        catch { }
        return $"{weekday} {Game1.currentSeason} {Game1.dayOfMonth}, year {Game1.year}{(festival ? " [festival today]" : "")}";
    }

    private static string TimeString()
    {
        int t = Game1.timeOfDay;
        int hour = t / 100 % 24;
        int min = t % 100;
        string ampm = hour >= 12 && hour < 24 ? "pm" : "am";
        int h12 = hour % 12 == 0 ? 12 : hour % 12;
        return $"{h12}:{min:00}{ampm}";
    }

    private static string WeatherString()
    {
        if (Game1.isLightning) return "stormy";
        if (Game1.isSnowing) return "snowing";
        if (Game1.isRaining) return "raining";
        if (Game1.isDebrisWeather) return "windy (leaves/petals)";
        return "sunny";
    }

    private static string LuckString()
    {
        try
        {
            double luck = Game1.player.team.sharedDailyLuck.Value;
            if (luck >= 0.07) return "very good";
            if (luck >= 0.02) return "good";
            if (luck > -0.02) return "neutral";
            if (luck > -0.07) return "bad";
            return "very bad";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string SkillsString()
    {
        string[] names = { "Farming", "Fishing", "Foraging", "Mining", "Combat", "Luck" };
        var parts = new List<string>();
        for (int i = 0; i < names.Length; i++)
        {
            try { parts.Add($"{names[i]} {Game1.player.GetSkillLevel(i)}"); }
            catch { }
        }
        return string.Join(", ", parts);
    }

    private static int CountCcRooms()
    {
        string[] rooms = { "ccBoilerRoom", "ccCraftsRoom", "ccFishTank", "ccPantry", "ccVault", "ccBulletin" };
        var mail = Game1.MasterPlayer.mailReceived;
        return rooms.Count(room => mail.Contains(room));
    }

    // ---------- player status / snapshot ----------

    public string GetPlayerStatus()
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var sb = new StringBuilder();
        sb.AppendLine($"Player: {Game1.player.Name} of {Game1.player.farmName.Value} Farm");
        sb.AppendLine($"Date: {DateString()}. Time: {TimeString()}. Weather: {WeatherString()}. Daily luck: {LuckString()}.");
        sb.AppendLine($"Money: {Game1.player.Money:N0}g");
        sb.AppendLine($"Energy: {(int)Game1.player.Stamina} / {Game1.player.MaxStamina}");
        sb.AppendLine($"Health: {Game1.player.health} / {Game1.player.maxHealth}");
        sb.AppendLine($"Location: {Game1.currentLocation.Name} at tile {TileOf(Game1.player)}");
        sb.AppendLine($"Skills: {SkillsString()}");
        return sb.ToString();
    }

    /// <summary>Compact context block injected into the AI's system prompt every request.</summary>
    public string GetSnapshot()
    {
        if (!WorldReady)
            return "The game is at the main menu (no save loaded). General Stardew Valley questions can still be answered from the wiki.";

        var sb = new StringBuilder();
        sb.AppendLine($"Player {Game1.player.Name} of {Game1.player.farmName.Value} Farm. Date: {DateString()}. Time: {TimeString()}. Weather: {WeatherString()}. Daily luck: {LuckString()}.");
        sb.AppendLine($"Money: {Game1.player.Money:N0}g. Energy {(int)Game1.player.Stamina}/{Game1.player.MaxStamina}. Health {Game1.player.health}/{Game1.player.maxHealth}. Currently in {Game1.currentLocation.Name} at tile {TileOf(Game1.player)}.");
        sb.AppendLine($"Skills: {SkillsString()}.");

        try
        {
            var active = Game1.player.questLog.Where(q => q != null && !q.completed.Value).ToList();
            if (active.Count > 0)
                sb.AppendLine($"Active quests ({active.Count}): {string.Join("; ", active.Take(8).Select(QuestTitle))}.");
            else
                sb.AppendLine("No active quests.");
        }
        catch { }

        try
        {
            var flags = new List<string>();
            if (Game1.player.locationsVisited.Contains("Desert")) flags.Add("Desert unlocked");
            if (Game1.player.locationsVisited.Contains("IslandSouth")) flags.Add("Ginger Island unlocked");
            int ccRooms = CountCcRooms();
            if (ccRooms > 0) flags.Add($"Community Center {ccRooms}/6 rooms");
            if (Game1.MasterPlayer.mailReceived.Contains("JojaMember")) flags.Add("Joja member");
            int mine = StatValue("deepestMineLevel");
            if (mine > 0) flags.Add($"deepest mine level {mine}");
            if (flags.Count > 0) sb.AppendLine("Progress: " + string.Join("; ", flags) + ".");
        }
        catch { }

        try
        {
            var mods = Helper.ModRegistry.GetAll().OrderBy(m => m.Manifest.Name).ToList();
            sb.AppendLine($"Installed mods ({mods.Count}): {string.Join(", ", mods.Select(m => m.Manifest.Name))}.");
        }
        catch { }

        return sb.ToString();
    }

    // ---------- quests ----------

    private static string QuestTitle(Quest q)
    {
        try
        {
            string title = q.GetName();
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        catch { }
        return $"quest #{q.id.Value}";
    }

    private static string QuestProgress(Quest q)
    {
        string typeName = q.GetType().Name;
        if (typeName == "SlayMonstersQuest")
        {
            int killed = IntVal(q, "MonstersKilled");
            int need = IntVal(q, "Number");
            if (need > 0) return $"{killed}/{need} monsters slain";
        }
        if (typeName == "ItemDeliveryQuest")
        {
            int done = IntVal(q, "Completed");
            int need = IntVal(q, "Number");
            if (need > 0) return $"{done}/{need} delivered";
        }
        return "";
    }

    public string GetQuests()
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var sb = new StringBuilder("Active quests:\n");
        int count = 0;
        foreach (var q in Game1.player.questLog)
        {
            if (q == null || q.completed.Value) continue;
            count++;
            string objectives;
            try { objectives = string.Join("; ", q.GetObjectiveDescriptions()); }
            catch { objectives = StringVal(q, "currentObjective") ?? ""; }
            if (string.IsNullOrWhiteSpace(objectives)) objectives = QuestTitle(q);
            string progress = QuestProgress(q);
            sb.AppendLine($"- {QuestTitle(q)}: {objectives}{(progress.Length > 0 ? $" ({progress})" : "")}");
            int daysLeft;
            try { daysLeft = q.GetDaysLeft(); }
            catch { daysLeft = IntVal(q, "daysLeft"); }
            if (daysLeft > 0) sb.AppendLine($"  days left: {daysLeft}");
        }
        if (count == 0) sb.AppendLine("  (none)");
        return sb.ToString();
    }

    // ---------- inventory & chests ----------

    public string GetInventory()
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var sb = new StringBuilder("Inventory (backpack):\n");
        int n = 0;
        foreach (var item in Game1.player.Items)
        {
            if (item == null) continue;
            string desc = DescribeItem(item);
            if (desc == null) continue;
            sb.AppendLine($"  {desc}");
            n++;
        }
        if (n == 0) sb.AppendLine("  (empty)");
        return sb.ToString();
    }

    public string GetChests(string locationFilter)
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var sb = new StringBuilder();
        int chests = 0;
        const int maxChests = 60;
        foreach (var loc in Game1.locations)
        {
            if (loc == null || loc.Name == null) continue;
            if (!string.IsNullOrWhiteSpace(locationFilter) &&
                loc.Name.IndexOf(locationFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            foreach (var obj in loc.Objects.Values)
            {
                if (obj is Chest chest && chest.playerChest.Value)
                {
                    var items = chest.Items == null
                        ? "(empty)"
                        : (string.Join(", ", chest.Items.Where(i => i != null).Select(DescribeItem)) is { Length: > 0 } s ? s : "(empty)");
                    sb.AppendLine($"- Chest at ({(int)chest.TileLocation.X},{(int)chest.TileLocation.Y}) in {loc.Name}: {items}");
                    if (++chests >= maxChests)
                    {
                        sb.AppendLine("... (more chests exist; ask about a specific location)");
                        return sb.ToString();
                    }
                }
            }
            if (loc is FarmHouse house && house.fridge != null && house.fridge.Value != null)
            {
                var items = string.Join(", ", house.fridge.Value.Items.Where(i => i != null).Select(DescribeItem));
                sb.AppendLine($"- Fridge ({loc.Name}): {(items.Length > 0 ? items : "(empty)")}");
                chests++;
            }
        }
        if (chests == 0)
            sb.AppendLine(string.IsNullOrWhiteSpace(locationFilter) ? "No player chests found." : $"No chests found matching '{locationFilter}'.");
        return sb.ToString();
    }

    // ---------- relationships ----------

    private static NPC TodaysBirthdayNpc()
    {
        try { return Utility.getTodaysBirthdayNPC(); }
        catch { return null; }
    }

    public string GetRelationships()
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var sb = new StringBuilder("Friendships:\n");
        string birthdayName = TodaysBirthdayNpc()?.Name;
        foreach (var kv in Game1.player.friendshipData.Pairs)
        {
            var f = kv.Value;
            if (f == null) continue;
            int hearts = IntVal(f, "Points") / 250;
            string extra = "";
            if (BoolVal(f, "TalkedToToday")) extra += " (talked today)";
            int gifts = IntVal(f, "GiftsToday");
            if (gifts > 0) extra += $" (gifted {gifts}x today)";
            string birthday = kv.Key == birthdayName ? " [BIRTHDAY TODAY]" : "";
            string datable = "";
            try
            {
                var npc = Game1.getCharacterFromName(kv.Key, false, false);
                if (npc != null && !BoolVal(npc, "datable")) datable = " (not marriageable)";
            }
            catch { }
            sb.AppendLine($"- {kv.Key}: {hearts} hearts{datable}{extra}{birthday}");
        }
        return sb.ToString();
    }

    // ---------- farm overview ----------

    public string GetFarmOverview()
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var sb = new StringBuilder();
        var farm = Game1.getFarm();

        var buildings = farm.buildings
            .Where(b => b != null)
            .GroupBy(b => b.buildingType.Value)
            .Select(g => $"{g.Count()}x {g.Key}");
        sb.AppendLine($"Buildings: {(buildings.Any() ? string.Join(", ", buildings) : "none")}");

        var animals = farm.getAllFarmAnimals();
        sb.AppendLine($"Animals ({animals.Count}):");
        foreach (var a in animals.Take(30))
        {
            string name = a.displayName;
            sb.AppendLine($"  - {a.type.Value} '{(string.IsNullOrEmpty(name) ? a.Name : name)}': age {IntVal(a, "age")}, happiness {IntVal(a, "happiness")}/255, friendship {IntVal(a, "friendshipTowardFarmer")}/1000");
        }
        if (animals.Count > 30) sb.AppendLine($"  ... and {animals.Count - 30} more");

        int planted = 0, ready = 0;
        foreach (var tf in farm.terrainFeatures.Values)
        {
            if (tf is HoeDirt dirt && dirt.crop != null && !dirt.crop.dead.Value)
            {
                planted++;
                if (dirt.readyForHarvest()) ready++;
            }
        }
        sb.AppendLine($"Crops: {planted} planted, {ready} ready to harvest.");

        var machines = new Dictionary<string, int>();
        foreach (var loc in Game1.locations)
        {
            if (loc == null) continue;
            foreach (var obj in loc.Objects.Values)
            {
                if (obj == null || IntVal(obj, "MinutesUntilReady") <= 0) continue;
                string key = string.IsNullOrEmpty(obj.DisplayName) ? (obj.Name ?? "machine") : obj.DisplayName;
                machines[key] = machines.GetValueOrDefault(key) + 1;
            }
        }
        sb.AppendLine($"Machines working: {(machines.Count > 0 ? string.Join(", ", machines.Select(kv => $"{kv.Value}x {kv.Key}")) : "none")}");
        return sb.ToString();
    }

    // ---------- world progress ----------

    public string GetWorldProgress()
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var sb = new StringBuilder();

        var visited = new List<string>();
        foreach (var name in Game1.player.locationsVisited) visited.Add(name);
        sb.AppendLine($"Locations visited ({visited.Count}): {string.Join(", ", visited)}");

        int mine = StatValue("deepestMineLevel");
        int skull = StatValue("skullCavernDepth");
        if (mine > 0) sb.AppendLine($"Deepest mine level reached: {mine}");
        if (skull > 0) sb.AppendLine($"Deepest Skull Cavern floor reached: {skull}");

        sb.AppendLine($"Community Center rooms complete: {CountCcRooms()}/6{(Game1.MasterPlayer.mailReceived.Contains("JojaMember") ? " (Joja member route)" : "")}");
        sb.AppendLine($"Story events seen: {Game1.player.eventsSeen.Count}");
        return sb.ToString();
    }

    // ---------- nearby ----------

    public string GetNearby()
    {
        if (!WorldReady) return "The save is not loaded yet.";
        var loc = Game1.currentLocation;
        var sb = new StringBuilder($"You are in {loc.Name} at tile {TileOf(Game1.player)}.\n");

        var npcs = new List<string>();
        foreach (var npc in loc.characters)
        {
            if (npc == null || npc.IsInvisible || string.IsNullOrEmpty(npc.Name)) continue;
            npcs.Add($"{npc.Name} at tile {TileOf(npc)}");
        }
        sb.AppendLine(npcs.Count > 0 ? "NPCs here: " + string.Join("; ", npcs) : "No NPCs in this area.");

        var counts = new Dictionary<string, int>();
        foreach (var obj in loc.Objects.Values)
        {
            if (obj == null) continue;
            string key = string.IsNullOrEmpty(obj.DisplayName) ? obj.Name : obj.DisplayName;
            if (string.IsNullOrEmpty(key)) continue;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        if (counts.Count > 0)
        {
            sb.AppendLine("Objects here: " + string.Join(", ",
                counts.OrderByDescending(kv => kv.Value).Take(25)
                    .Select(kv => kv.Value > 1 ? $"{kv.Value}x {kv.Key}" : kv.Key)));
        }
        return sb.ToString();
    }

    // ---------- mods ----------

    public string GetMods()
    {
        var mods = Helper.ModRegistry.GetAll().OrderBy(m => m.Manifest.Name).ToList();
        var sb = new StringBuilder($"Installed mods ({mods.Count}):\n");
        foreach (var m in mods)
        {
            string desc = m.Manifest.Description;
            if (!string.IsNullOrEmpty(desc) && desc.Length > 90) desc = desc.Substring(0, 90) + "…";
            sb.AppendLine($"- {m.Manifest.Name} {m.Manifest.Version} [{m.Manifest.UniqueID}]{(string.IsNullOrEmpty(desc) ? "" : " — " + desc)}");
        }
        return sb.ToString();
    }
}
