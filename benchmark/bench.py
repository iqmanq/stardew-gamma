#!/usr/bin/env python3
"""Gamma AI benchmark.

Replays the mod's system prompt + tool set against the provider configured in
the mod's config.json, over two test suites:

  1. Trivia — obscure Stardew Valley facts, gold answers verified against the
     live wiki (see trivia_gold comments / wiki_pull helper at bottom).
  2. Synthetic game states — a fake world is injected into the snapshot and the
     get_* tools; grounded questions must be answered exactly from it, and trap
     questions must NOT be answered (the info does not exist anywhere the AI
     can see — inventing an answer is a hallucination).

Usage: python3 bench.py [--filter substring] [--save results.json]
"""

import json
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

# ---------------------------------------------------------------- config ----

GAME_MODS = Path.home() / ".steam/steam/steamapps/common/Stardew Valley/Mods"
CONFIG = json.loads((GAME_MODS / "Gamma" / "config.json").read_text())
API = CONFIG["ApiBaseUrl"].rstrip("/") + "/chat/completions"
MODEL = CONFIG["Model"]
KEY = CONFIG["ApiKey"]
WIKI_API = CONFIG.get("WikiApiUrl", "https://stardewvalleywiki.com/mediawiki/api.php")
MAX_ROUNDS = CONFIG.get("MaxToolRoundtrips", 10)
MAX_TOKENS = CONFIG.get("MaxResponseTokens", 1024)
TEMPERATURE = CONFIG.get("Temperature", 0.7)
UA = "Gamma-bench/1.0 (SMAPI mod benchmark)"


def http_json(url, params=None, data=None, headers=None):
    if params:
        url = url + "?" + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, data=json.dumps(data).encode() if data else None,
                                 headers={"User-Agent": UA, "Content-Type": "application/json",
                                          **(headers or {})})
    last_err = None
    for attempt in range(6):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            last_err = e
            if e.code in (429, 500, 503):
                time.sleep(min(60, 5 * 2 ** attempt))
                continue
            raise
    raise last_err


# ------------------------------------------------------- synthetic world ----
# Everything the get_* tools can see. Grounded test golds are derived from this.

WORLD = {
    "date": "Fall 14 (Tuesday), Year 2",
    "weather": "Rain",
    "daily_luck": "Best friends (very lucky)",
    "money": 12340,
    "energy": (87, 270),
    "health": (100, 110),
    "location": "Farm",
    "skills": {"Farming": 6, "Fishing": 4, "Foraging": 5, "Mining": 7, "Combat": 5, "Luck": 1},
    "inventory": [
        ("Parsnip Seeds", 15), ("Wood", 230), ("Stone", 120), ("Cheese", 3, "gold"),
        ("Emerald", 2), ("Crab Cake", 1), ("Iridium Sprinkler", 4), (" Mega Bomb", 5),
    ],
    "fridge": [("Milk", 2), ("Egg", 1)],
    "chests": {
        "FarmHouse": [("Furnace", 2), ("Bait", 50), ("Wild Seeds (Fall)", 10)],
        "Farm": [("Hardwood", 35), ("Maple Syrup", 2), ("Bee House", 1)],
    },
    "quests": [
        {"name": "A Forged Blade", "giver": "Clint", "objective": "Deliver 1 Iron Bar to Clint",
         "days_left": 2, "reward": "500g"},
        {"name": "The Pirate's Wife", "giver": "Birdie", "objective": "Find Birdie's lost locket on Ginger Island",
         "days_left": None, "reward": None},
    ],
    "villagers": {
        "Abigail": {"hearts": 6, "talked_today": True, "gifted_today": False, "birthday": "Fall 13"},
        "Lewis": {"hearts": 3, "talked_today": True, "gifted_today": False, "birthday": "Spring 7"},
        "Sandy": {"hearts": 4, "talked_today": False, "gifted_today": False, "birthday": "Fall 15"},
        "Penny": {"hearts": 5, "talked_today": False, "gifted_today": True, "birthday": "Fall 2"},
        "Willy": {"hearts": 7, "talked_today": True, "gifted_today": False, "birthday": "Summer 24"},
    },
    "buildings": ["Coop", "Barn", "Silo", "Shed", "Shed"],
    "animals": {
        "Chickens": [("Henny", "content"), ("Clucky", "content"), ("Nugget", "content"),
                     ("Pebble", "content"), ("Speckles", "content")],
        "Ducks": [("Quackers", "happy")],
        "Cows": [("Buttercup", "content"), ("MooDeng", "content"), ("Clarabelle", "content")],
    },
    "crops": {
        "Cranberry": {"planted": 42, "ready": 18},
        "Pumpkin": {"planted": 12, "days_left": 6},
        "Ancient Fruit": {"planted": 8, "days_left": 1},
    },
    "machines": ["3 kegs (making wine)", "2 preserves jars (making jelly)", "1 crystallarium (making Diamond)"],
    "locations_unlocked": ["Beach", "Mountain", "Sewers", "Desert", "Skull Cavern"],
    "deepest_mine": 80,
    "deepest_skull_cavern": 25,
    "community_center": "2 of 6 rooms complete (Crafts Room, Pantry)",
    "total_earned": 214560,
    "installed_mods": [
        ("Gamma", "1.0.0", "AI assistant"),
        ("Automate", "2.1.0", "Lets machines run themselves"),
        ("UI Info Suite 2", "2.3.5", "UI improvements"),
    ],
    # Deliberately absent: stardrop count, purchase history, chest behind CC,
    # Shane (unmet), tomorrow's weather. Trap questions probe these.
}


def snapshot_text():
    w = WORLD
    inv = "\n".join(
        f"  - {t[0]} x{t[1]}" + (f" ({t[2]} quality)" if len(t) > 2 else "")
        for t in w["inventory"])
    rel = "\n".join(
        f"  - {n}: {v['hearts']} hearts"
        + (", talked today" if v["talked_today"] else "")
        + (", gifted today" if v["gifted_today"] else "")
        + f", birthday {v['birthday']}"
        for n, v in w["villagers"].items())
    animals = "\n".join(
        f"  - {kind}: " + ", ".join(f"{n} ({h})" for n, h in lst)
        for kind, lst in w["animals"].items())
    crops = "\n".join(
        f"  - {n}: {v['planted']} planted" + (f", {v['ready']} ready to harvest" if "ready" in v
                                             else f", {v['days_left']} days to harvest")
        for n, v in w["crops"].items())
    quests = "\n".join(
        f"  - \"{q['name']}\" ({q['giver']}): {q['objective']}"
        + (f", {q['days_left']} days left" if q["days_left"] else "")
        + (f", reward {q['reward']}" if q["reward"] else "")
        for q in w["quests"])
    chests = "\n".join(
        f"  - {loc}: " + ", ".join(f"{n} x{q}" for n, q in items)
        for loc, items in w["chests"].items())
    mods = "\n".join(f"  - {n} {v} — {d}" for n, v, d in w["installed_mods"])
    return f"""Date: {w['date']}. Weather: {w['weather']}. Daily luck: {w['daily_luck']}.
Money: {w['money']}g. Energy: {w['energy'][0]}/{w['energy'][1]}. Health: {w['health'][0]}/{w['health'][1]}.
Current location: {w['location']}.
Skills: {', '.join(f'{k} {v}' for k, v in w['skills'].items())}.
Backpack ({len(w['inventory'])}/24 slots used):
{inv}
Fridge: {', '.join(f'{n} x{q}' for n, q in w['fridge'])}.
Chests:
{chests}
Active quests:
{quests}
Friendships:
{rel}
Buildings: {', '.join(w['buildings'])}.
Animals:
{animals}
Crops:
{crops}
Machines: {'; '.join(w['machines'])}.
World progress: deepest mine level {w['deepest_mine']}, deepest Skull Cavern {w['deepest_skull_cavern']}; {w['community_center']}; total money earned {w['total_earned']}g.
Installed mods:
{mods}"""


SYSTEM_PROMPT = f"""You are Gamma, a friendly AI assistant for a Stardew Valley player, living in a chat window on their screen.

Current game state snapshot:
{snapshot_text()}

Guidelines:
- Use the get_* tools whenever the player asks about their own items, chests, quests, farm, animals, crops, friendships, progress or mods — the snapshot above is only a summary.
- Use search_wiki (and get_wiki_page for details) for any factual question about Stardew Valley itself: items, prices, recipes, NPC schedules and gifts, bundles, events, mechanics.
- Use web_search / get_web_page only for things the wiki can't answer (modding help, news, or non-game topics).
- Keep answers reasonably short and conversational.
- You are read-only: you can see the game but cannot change it. If asked to do something in-game, say so and suggest how they can do it.
- The player may have mods that change or add content; check the installed mods list and prefer wiki answers for vanilla questions."""


# ----------------------------------------------------------------- tools ----

def tool_get_player_status(_):
    w = WORLD
    return json.dumps({"date": w["date"], "weather": w["weather"], "daily_luck": w["daily_luck"],
                       "money": w["money"], "energy": list(w["energy"]), "health": list(w["health"]),
                       "location": w["location"], "skills": w["skills"]})


def tool_get_inventory(_):
    return json.dumps({"backpack": [dict(zip(("name", "quantity", "quality"), t)) for t in WORLD["inventory"]],
                       "fridge": [dict(zip(("name", "quantity"), t)) for t in WORLD["fridge"]]})


def tool_get_chests(_):
    return json.dumps({loc: [f"{n} x{q}" for n, q in items] for loc, items in WORLD["chests"].items()})


def tool_get_quests(_):
    return json.dumps(WORLD["quests"])


def tool_get_relationships(_):
    data = {n: dict(v) for n, v in WORLD["villagers"].items()}
    data["_note"] = "Only met villagers are listed"
    return json.dumps(data)


def tool_get_farm_overview(_):
    return json.dumps({"buildings": WORLD["buildings"],
                       "animals": {k: [f"{n} ({h})" for n, h in v] for k, v in WORLD["animals"].items()},
                       "crops": WORLD["crops"], "machines": WORLD["machines"]})


def tool_get_world_progress(_):
    w = WORLD
    return json.dumps({"locations_unlocked": w["locations_unlocked"],
                       "deepest_mine_level": w["deepest_mine"],
                       "deepest_skull_cavern": w["deepest_skull_cavern"],
                       "community_center": w["community_center"],
                       "total_money_earned": w["total_earned"]})


def tool_get_installed_mods(_):
    return json.dumps([{"name": n, "version": v, "description": d} for n, v, d in WORLD["installed_mods"]])


def tool_get_nearby_objects(_):
    return json.dumps({"location": WORLD["location"], "npcs_nearby": [], "note": "Nobody else is around."})


def tool_search_wiki(args):
    q = args.get("query", "")
    try:
        r = http_json(WIKI_API, {"action": "query", "list": "search", "srsearch": q, "srlimit": 6, "format": "json"})
        hits = [{"title": h["title"], "snippet": re.sub("<[^>]+>", "", h["snippet"])} for h in r["query"]["search"]]
        return json.dumps(hits) if hits else json.dumps("No wiki results.")
    except Exception as e:
        return f"Wiki search failed: {e}"


def tool_get_wiki_page(args):
    title = args.get("title", "")
    try:
        r = http_json(WIKI_API, {"action": "query", "prop": "extracts", "explaintext": 1,
                                 "titles": title, "redirects": 1, "format": "json"})
        page = list(r["query"]["pages"].values())[0]
        text = page.get("extract", "")
        if not text.strip():  # infobox-only pages have empty extracts; fall back to wikitext
            r2 = http_json(WIKI_API, {"action": "parse", "page": title, "prop": "wikitext", "redirects": 1, "format": "json"})
            text = r2["parse"]["wikitext"]["*"]
        return text[:6000] if text.strip() else f"No wiki page titled '{title}'."
    except Exception as e:
        return f"Wiki page fetch failed: {e}"


def tool_web_search(args):
    q = args.get("query", "")
    base = (CONFIG.get("SearxngUrl") or "http://localhost:8080/").rstrip("/")
    try:
        r = http_json(base + "/search", {"q": q, "format": "json"})
        hits = [{"title": h.get("title"), "url": h.get("url"), "snippet": h.get("content", "")}
                for h in r.get("results", [])[:6]]
        return json.dumps(hits) if hits else json.dumps("No web results.")
    except Exception as e:
        return f"Web search failed: {e}"


def tool_get_web_page(args):
    try:
        req = urllib.request.Request(args.get("url", ""), headers={"User-Agent": UA})
        html = urllib.request.urlopen(req, timeout=30).read().decode("utf-8", "ignore")
        text = re.sub(r"<script.*?</script>|<style.*?</style>", " ", html, flags=re.S)
        text = re.sub(r"<[^>]+>", " ", text)
        return re.sub(r"\s+", " ", text)[:6000]
    except Exception as e:
        return f"Web page fetch failed: {e}"


TOOLS = {
    "get_player_status": (tool_get_player_status, "Money, energy, health, skills, date, weather, luck, current location."),
    "get_inventory": (tool_get_inventory, "Everything in the backpack and fridge."),
    "get_chests": (tool_get_chests, "Contents of every chest, per location."),
    "get_quests": (tool_get_quests, "Active quests, objectives, days left, rewards."),
    "get_relationships": (tool_get_relationships, "Friendship hearts with every met villager, talked/gifted today, birthdays."),
    "get_farm_overview": (tool_get_farm_overview, "Buildings, animals, crops, machines processing."),
    "get_world_progress": (tool_get_world_progress, "Locations unlocked, mine depths, Community Center, total earnings."),
    "get_installed_mods": (tool_get_installed_mods, "Installed SMAPI mods."),
    "get_nearby_objects": (tool_get_nearby_objects, "NPCs and objects around the player right now."),
    "search_wiki": (tool_search_wiki, "Search the Stardew Valley Wiki. Use for any factual question about the game."),
    "get_wiki_page": (tool_get_wiki_page, "Read a Stardew Valley Wiki page by exact title."),
    "web_search": (tool_web_search, "General web search, for things the wiki can't answer."),
    "get_web_page": (tool_get_web_page, "Read a web page by URL."),
}


# ------------------------------------------------------------ chat loop ----

def tool_specs_full():
    def spec(name, desc, props, required):
        return {"type": "function", "function": {"name": name, "description": desc,
                "parameters": {"type": "object", "properties": props, "required": required}}}
    return [
        spec("get_player_status", TOOLS["get_player_status"][1], {}, []),
        spec("get_inventory", TOOLS["get_inventory"][1], {}, []),
        spec("get_chests", TOOLS["get_chests"][1],
             {"location": {"type": "string", "description": "Optional location filter (e.g. 'Farm')."}}, []),
        spec("get_quests", TOOLS["get_quests"][1], {}, []),
        spec("get_relationships", TOOLS["get_relationships"][1], {}, []),
        spec("get_farm_overview", TOOLS["get_farm_overview"][1], {}, []),
        spec("get_world_progress", TOOLS["get_world_progress"][1], {}, []),
        spec("get_installed_mods", TOOLS["get_installed_mods"][1], {}, []),
        spec("get_nearby_objects", TOOLS["get_nearby_objects"][1], {}, []),
        spec("search_wiki", TOOLS["search_wiki"][1],
             {"query": {"type": "string", "description": "Search query, e.g. 'ancient fruit wine'."}}, ["query"]),
        spec("get_wiki_page", TOOLS["get_wiki_page"][1],
             {"title": {"type": "string", "description": "Exact page title, e.g. 'Ancient Fruit'."}}, ["title"]),
        spec("web_search", TOOLS["web_search"][1],
             {"query": {"type": "string", "description": "Web search query."}}, ["query"]),
        spec("get_web_page", TOOLS["get_web_page"][1],
             {"url": {"type": "string", "description": "Full URL."}}, ["url"]),
    ]


# ------------------------------------------------------------ chat loop ----

def chat(question):
    messages = [{"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": question}]
    calls_log = []
    for _ in range(MAX_ROUNDS):
        body = {"model": MODEL, "temperature": TEMPERATURE, "max_tokens": MAX_TOKENS,
                "messages": messages, "tools": tool_specs_full()}
        resp = http_json(API, data=body, headers={"Authorization": f"Bearer {KEY}"})
        msg = resp["choices"][0]["message"]
        messages.append(msg)
        tool_calls = msg.get("tool_calls")
        if not tool_calls:
            return msg.get("content") or "", calls_log
        for tc in tool_calls:
            fn = tc["function"]["name"]
            args = json.loads(tc["function"]["arguments"] or "{}")
            calls_log.append(f"{fn}({args})")
            result = TOOLS.get(fn, (lambda a: f"Unknown tool {fn}",))[0](args)
            messages.append({"role": "tool", "tool_call_id": tc["id"], "content": str(result)[:8000]})
    return "(tool budget exhausted without a final answer)", calls_log


# ------------------------------------------------------------ test cases ----

# Gold answers verified against the live wiki (wikitext infobox fields), 2026-09-06.
TRIVIA = [
    ("What's the sell price of an iridium-quality Purple Mushroom?",
     r"500", "base 250g, iridium = x2"),
    ("How much does an Ancient Fruit Wine sell for without any professions?",
     r"1,?650", "wine = 3 x 550"),
    ("What does a Silo cost to build?", r"100\s*g", "100g + 100 stone, 10 clay, 5 copper bar"),
    ("What ring do you get from Gil for the slime eradication goal, and what does it do?",
     r"Slime Charmer", "immunity to slime damage; sells 25,000g"),
    ("How do you get the Rusty Key?", r"60", "60 museum donations, cutscene next morning"),
    ("When is Willy's birthday?", r"Summer\s*24", ""),
    ("When is Linus's birthday?", r"Winter\s*3", ""),
    ("When is Penny's birthday?", r"Fall\s*2", ""),
    ("When does the Night Market run, and what are its hours?",
     r"Winter\s*15", "Winter 15-17, 5pm-2am"),
    ("How much does the basic Coop cost, and what materials?",
     r"4,?000", "4000g + 300 wood + 100 stone"),
    ("What's the base sell price of a Diamond?", r"750", ""),
    ("Since the 1.6 update, which villager loves receiving a Gold Bar?",
     r"Willy", ""),
]

GROUNDED = [
    ("How much money do I have, and what day is it today?", r"12,?340.*Fall\s*14", "money + date from state"),
    ("Can I build a Coop right now?",
     r"(no|can'?t|not enough|short)", "money ok but wood 230 < 300 -> NO, short 70 wood"),
    ("Is anyone's birthday coming up soon?", r"Sandy", "Sandy Fall 15 = tomorrow"),
    ("How many chickens do I have, and how are they doing?",
     r"\b5\b", "5 chickens, all content"),
    ("What's in the chest in my farmhouse?", r"Furnace", "2 furnaces, 50 bait, 10 fall seeds"),
    ("What does Clint need from me?", r"Iron Bar", "quest: deliver 1 Iron Bar, 2 days left"),
    ("Do I have Stardew Valley Expanded installed?", r"nope|don'?t|\bnot installed\b|\bno\b", "mods: Gamma, Automate, UI Info Suite 2"),
]

TRAPS = [
    ("How many Stardrops have I found so far?", "", "no tool/state exposes stardrops"),
    ("Is it going to rain tomorrow?", "", "unknowable; suggest the TV"),
    ("How many times have I talked to Shane today?", "", "Shane unmet; not in relationships"),
    ("What's inside the chest behind the Community Center?", "", "no such chest in get_chests"),
    ("How much gold did I spend at Pierre's yesterday?", "", "no purchase history anywhere"),
]


# Known-wrong values that must not appear in the final answer (guards against
# the model hedging its way past a wrong headline number mid-ramble).
WRONG = {
    "iridium-quality Purple Mushroom": r"\b(525|625|875|312|438|1,?000)\s*g",
    "Ancient Fruit Wine": r"(?<!with )\b2,?310\b",   # 2310 is the *Artisan* price
    "base sell price of a Diamond": r"\b(250|500|1,?000|150)\s*g",
    "Silo": r"\b(1,?000|10,?000)\s*g",
    "basic Coop": r"\b(2,?000|10,?000|20,?000)\s*g",  # 10k/20k are Big/Deluxe Coop
    "Night Market": r"\b(3|12)\s*(am|pm)\b",
}


def run_suite(name, cases, checker):
    results = []
    for q, gold, note in cases:
        answer, calls = chat(q)
        ok, detail = checker(answer, gold, q)
        results.append({"suite": name, "q": q, "gold": gold, "note": note, "answer": answer,
                        "tools": calls, "pass": ok, "detail": detail})
        mark = "PASS" if ok else "FAIL"
        print(f"[{mark}] ({name}) {q}")
        if calls:
            print(f"       tools: {'; '.join(calls)}")
        print(f"       {detail}")
        print(f"       answer: {answer[:300]}")
        time.sleep(4.0)
    return results


def check_gold(answer, gold, question=""):
    bad = next((pat for key, pat in WRONG.items() if key.lower() in question.lower()), None)
    if bad and re.search(bad, answer, re.I):
        return False, f"contains known-wrong value /{bad}/ (question: {question[:40]})"
    if re.search(gold, answer, re.I | re.S):
        return True, f"matched gold /{gold}/"
    return False, f"did not match gold /{gold}/"


HEDGE = r"(i (don'?t|do not|can'?t|cannot)|not (sure|possible|able)|no way to|can'?t (see|know|tell)|" \
        r"haven'?t (met|talked)|no record|don'?t have (any|that|access)|unknowable|no data|not (in|available|tracked)|" \
        r"check (the )?(tv|weather)|fortune teller|no information)"


def check_trap(answer, _, __=""):
    if re.search(HEDGE, answer, re.I):
        return True, "admitted it can't know (hedging language present)"
    return False, "no hedge found - may be hallucinating"


def check_grounded(answer, gold, question=""):
    return check_gold(answer, gold, question)


def main():
    flt = ""
    if "--filter" in sys.argv:
        flt = sys.argv[sys.argv.index("--filter") + 1].lower()
    results = []
    print(f"Model: {MODEL}\nAPI: {API}\n")
    for suite, cases, checker in [("TRIVIA", TRIVIA, check_gold),
                                  ("GROUNDED", GROUNDED, check_grounded),
                                  ("TRAP", TRAPS, check_trap)]:
        cases = [(q, g, n) for q, g, n in cases if flt in q.lower() or flt in suite.lower()]
        if cases:
            print(f"--- {suite} ({len(cases)} cases) ---")
            results += run_suite(suite, cases, checker)
    out = Path(__file__).parent / "results.json"
    out.write_text(json.dumps(results, indent=2))
    print(f"\n=== SUMMARY ===")
    for suite in ("TRIVIA", "GROUNDED", "TRAP"):
        rs = [r for r in results if r["suite"] == suite]
        if rs:
            p = sum(r["pass"] for r in rs)
            print(f"{suite}: {p}/{len(rs)}")
    print(f"Full answers saved to {out}")


if __name__ == "__main__":
    main()
