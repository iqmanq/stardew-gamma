using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace Gamma.Services;

/// <summary>
/// The tools the AI can call. Tool bodies read game state, so they are marshalled onto the
/// game thread through <see cref="GameRunner"/> before executing.
/// </summary>
public class ToolRegistry
{
    private sealed class ToolDef
    {
        public ToolSpec Spec;
        public Func<string, string> Run;
        public string StatusLabel;
    }

    private readonly Dictionary<string, ToolDef> Tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameStateService State;
    private readonly WikiService Wiki;
    private readonly WebSearchService Web;

    /// <summary>Set by ModEntry; runs a func on the game thread and returns its result.</summary>
    public Func<Func<string>, string> GameRunner;

    public ToolRegistry(IModHelper helper, IMonitor monitor, GameStateService state, WikiService wiki, WebSearchService web)
    {
        State = state;
        Wiki = wiki;
        Web = web;
        RegisterAll();
    }

    public IReadOnlyList<ToolSpec> Specs => Tools.Values.Select(t => t.Spec).ToList();

    public string DisplayLabel(string tool) =>
        Tools.TryGetValue(tool ?? "", out var def) ? def.StatusLabel : "Working…";

    public string Execute(ToolCall call)
    {
        if (call == null || string.IsNullOrEmpty(call.Name) || !Tools.TryGetValue(call.Name, out var def))
            return $"Unknown tool '{call?.Name}'.";
        try
        {
            string output = GameRunner != null ? GameRunner(() => def.Run(call.ArgumentsJson ?? "{}")) : def.Run(call.ArgumentsJson ?? "{}");
            return string.IsNullOrEmpty(output) ? "(no data)" : output;
        }
        catch (Exception ex)
        {
            return $"Tool '{call.Name}' failed: {ex.Message}";
        }
    }

    private void RegisterAll()
    {
        Register("get_player_status",
            "Get the player's current status: money, energy, health, date/time, weather, daily luck, current location, and skill levels.",
            "Checking your status…",
            args => State.GetPlayerStatus());

        Register("get_inventory",
            "List everything in the player's backpack inventory, including stack sizes and quality.",
            "Checking your inventory…",
            args => State.GetInventory());

        Register("get_chests",
            "List chests and their contents, optionally filtered by location name (e.g. Farm, FarmHouse, Shed).",
            "Checking your chests…",
            args => State.GetChests(Arg(args, "location")),
            ("location", "string", "Optional location name to filter by (e.g. 'Farm', 'FarmHouse'). Omit for all chests.", false));

        Register("get_quests",
            "List the player's active quests, their objectives and progress (e.g. monsters slain, items delivered).",
            "Checking your quests…",
            args => State.GetQuests());

        Register("get_relationships",
            "List the player's friendship/heart levels with all villagers, who they talked to or gifted today, and birthdays.",
            "Checking friendships…",
            args => State.GetRelationships());

        Register("get_farm_overview",
            "Summarize the farm: buildings, farm animals (happiness/friendship), crop counts and how many are ready to harvest, and machines currently processing.",
            "Surveying the farm…",
            args => State.GetFarmOverview());

        Register("get_world_progress",
            "Get overall world/story progress: locations visited, unlocked areas, deepest mine level, community center completion, events seen.",
            "Recalling your progress…",
            args => State.GetWorldProgress());

        Register("get_nearby_objects",
            "Describe the player's current area: NPCs present with their positions, and objects in the area.",
            "Looking around…",
            args => State.GetNearby());

        Register("get_installed_mods",
            "List every SMAPI mod the player has installed with versions and descriptions.",
            "Listing installed mods…",
            args => State.GetMods());

        Register("search_wiki",
            "Search the Stardew Valley Wiki for game information: items, crop prices, recipes, NPC schedules and gifts, bundles, mechanics, fish, forage, etc. Use this for ANY factual question about Stardew Valley itself.",
            "Looking up the Stardew Valley wiki…",
            args => Sync(Wiki.SearchAsync(Arg(args, "query"))),
            ("query", "string", "The search query, e.g. 'ancient fruit wine' or 'Abigail schedule'.", true));

        Register("get_wiki_page",
            "Fetch the full text of a Stardew Valley wiki page by its exact title. Use search_wiki first if you don't know the title.",
            "Reading a wiki page…",
            args => Sync(Wiki.GetPageAsync(Arg(args, "title"))),
            ("title", "string", "Exact page title, e.g. 'Ancient Fruit'.", true));

        Register("web_search",
            "Search the general web. Use only for things the Stardew Valley wiki can't answer (modding questions, news, non-game topics).",
            "Searching the web…",
            args => Sync(Web.SearchAsync(Arg(args, "query"))),
            ("query", "string", "The web search query.", true));

        Register("get_web_page",
            "Fetch a web page by URL and return its readable text. Use after web_search to read a promising result.",
            "Reading a web page…",
            args => Sync(Web.FetchPageAsync(Arg(args, "url"))),
            ("url", "string", "The full URL to fetch.", true));
    }

    private void Register(string name, string description, string status, Func<string, string> run,
        params (string Param, string Type, string Desc, bool Required)[] parameters)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (param, type, desc, req) in parameters)
        {
            props[param] = new JsonObject { ["type"] = type, ["description"] = desc };
            if (req) required.Add(param);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;

        Tools[name] = new ToolDef
        {
            Spec = new ToolSpec { Name = name, Description = description, Parameters = schema },
            Run = run,
            StatusLabel = status,
        };
    }

    private static string Arg(string argsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson ?? "{}");
            if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string Sync(Task<string> task) => task.GetAwaiter().GetResult();
}
