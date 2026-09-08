# Gamma

An AI assistant living inside Stardew Valley. Press a key in game and chat with an AI that
can see your farm and search the Stardew Valley Wiki and the web to answer anything.

> It knows your game, not just the wiki.

## Features

**Knows your game state** — the AI gets a live snapshot and can pull details on demand:

- Your money, energy, health, skills, date, weather, daily luck and current location
- Everything in your backpack (stacks + item quality)
- Contents of every chest you own, per location (plus the farmhouse fridge)
- Active quests, objectives and progress (monsters slain, items delivered, days left)
- Friendship/heart levels with every villager, who you talked to/gifted today, birthdays
- Farm overview: buildings, animals (happiness, friendship), crops planted/ready to harvest,
  machines currently processing
- World progress: locations visited, unlocked areas, deepest mine/Skull Cavern level,
  Community Center completion, story events seen
- Every SMAPI mod you have installed (name, version, description) — so it knows if you're
  playing with SVE, Automation, etc.
- What's around you right now: NPCs in the area and their positions

**Searches the outside world:**

- Full Stardew Valley Wiki search + page reader (prices, schedules, gifts, bundles, mechanics…)
- General web search and web page reader (for modding help, news, anything else)

**Extras:**

- Chat history is remembered per save, across game sessions
- Unread-message badge on the HUD when it replies while the window is closed
- Console commands (see below) so you can use it without opening the UI

The mod never sends anything on its own — requests go out only when you send a
message (chat window, or `gamma ask` in the console).

## Requirements

- [SMAPI](https://smapi.io/) 4.0+ (Stardew Valley 1.6)
- Access to an AI provider — a cloud API key, **or** a free local model (see below)
- Optionally, a web-search API key (the wiki search works with no key at all)

## Setup

1. Install the mod: download `Gamma-1.0.0.zip` from the
   [latest release](https://github.com/iqmanq/stardew-gamma/releases/latest) (or from the
   Nexus page) and unzip it into your game's `Mods/` folder — you should end up with
   `Mods/Gamma/` containing `Gamma.dll` and `manifest.json`. (Alternatively, build from
   source — see *Building* below; that installs into `Mods/` automatically.)
2. In game, press **O** — if no API key is set yet, the **Gamma Settings** screen opens
   automatically. Pick a provider (Google AI Studio, OpenRouter, OpenAI, Groq, Ollama,
   LM Studio, Anthropic…), paste your API key, and hit **Save**. You can also get there any
   time via the **Settings** button in the chat window or `gamma settings` in the SMAPI console.
3. If you prefer editing files by hand, run the game once so SMAPI creates
   `Mods/Gamma/config.json`, edit it, and restart.

### Getting an API key

Gamma needs one API key from any supported provider. Two good free options:

**Option 1 — Google AI Studio (free, recommended)**

1. Go to [aistudio.google.com](https://aistudio.google.com/) and sign in with a Google account.
2. Click **Get API key** in the sidebar (or open [aistudio.google.com/apikey](https://aistudio.google.com/apikey) directly).
3. Click **Create API key**, choose or create a project, and copy the key (starts with `AIza`).
4. In Gamma's settings screen (press **O** in game), pick the **Google AI Studio** preset, paste the key, and hit **Save**. The model is pre-filled to `gemini-flash-lite-latest`.

Recommended model: **`gemini-flash-lite-latest`** — fast, 500 free requests per day, and good enough at tool-calling for everyday questions. If you want stronger reasoning for harder questions, `gemini-flash-latest` or `gemini-3.5-flash` (requires setting up billing, paid) are drop-in upgrades (same key, just change the Model field).

**Option 2 — OpenRouter (free tier available)**

1. Go to [openrouter.ai](https://openrouter.ai/) and sign up (email or Google/GitHub login).
2. Open [openrouter.ai/settings/keys](https://openrouter.ai/settings/keys) (Profile → **Keys**).
3. Click **Create key**, give it any name, and copy it (starts with `sk-or-v1-`, shown only once — save it somewhere).
4. Free models work out of the box; there's no need to add credits unless you pick paid models.
5. In Gamma's settings screen, pick the **OpenRouter** preset, paste the key, and hit **Save**. The model is pre-filled to `minimax/minimax-m3:free`.

Recommended model: **`minimax/minimax-m3:free`** — a large free model with a 1M-token context window. Note that OpenRouter free models have daily limits and can be busy at peak times; if a request fails with a rate-limit error, either retry later or add a few dollars of credit and drop the `:free` suffix to use the paid variant (`minimax/minimax-m3`).

**Manual setup** — if you'd rather edit the config file than use the in-game screen, run the game once so SMAPI creates `Mods/Gamma/config.json`, then set `Provider`, `ApiKey`, and `Model` as in the examples below and restart.

### AI provider examples

**OpenAI**
```json
{
  "Provider": "openai",
  "ApiKey": "sk-...",
  "Model": "gpt-4o-mini"
}
```

**Google AI Studio** (free key — see *Getting an API key* above)
```json
{
  "Provider": "openai",
  "ApiBaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai/",
  "ApiKey": "AIza...",
  "Model": "gemini-flash-lite-latest"
}
```

**Anthropic (Claude)**
```json
{
  "Provider": "anthropic",
  "ApiKey": "sk-ant-...",
  "Model": "claude-sonnet-4-20250514"
}
```

**OpenRouter / Groq / DeepSeek / etc.** (any OpenAI-compatible API)
```json
{
  "Provider": "openai",
  "ApiBaseUrl": "https://openrouter.ai/api/v1",
  "ApiKey": "sk-or-...",
  "Model": "minimax/minimax-m3:free"
}
```

**100% free & local with Ollama** (no API key, no internet)
```bash
ollama pull llama3.1:8b
```
```json
{
  "Provider": "openai",
  "ApiBaseUrl": "http://localhost:11434/v1",
  "ApiKey": "ollama",
  "Model": "llama3.1:8b"
}
```
LM Studio works the same way with `http://localhost:1234/v1`. Local models are slower and
weaker at tool-calling than cloud models — a mid-size instruct model or larger recommended.

### Web search (optional)

The wiki search always works with zero setup. For general web search, set one of:

```json
{
  "SearchProvider": "tavily",       // free tier at tavily.com
  "SearchApiKey": "tvly-..."
}
```
or `"SearchProvider": "brave"` (brave.com/search/api) or `"SearchProvider": "searxng"`
with `"SearxngUrl": "http://localhost:8080"` for a self-hosted instance (no key needed).
SearXNG disables its JSON API by default; enable it in the instance's `settings.yml`:

```yaml
search:
  formats:
    - html
    - json
```

then restart the instance, or the mod will report "refused the JSON API". In the
in-game settings, pick `searxng` and the search row becomes the instance URL field.

## Usage

- **O** (configurable via `OpenChatKey`) opens the chat window. Enter sends, Esc closes,
  scroll wheel reviews history.
- **Controller**: clicking the **right stick** (configurable via `OpenChatGamepadButton`)
  also opens the chat window. Inside it, the game's usual gamepad support applies — left
  stick moves the cursor, **A** clicks (and opens the keyboard on text fields),
  right stick scrolls, **X** starts a new chat, **LB/RB**
  (or triggers) switch chats, **D-pad up/down** scrolls history, and **B** closes the
  keyboard, then clears the input, then closes the window. OSK **Start** submits the
  message. **Y** does nothing in either menu. In settings, **A** on a text field opens
  the keyboard, **LB/RB** (or triggers / D-pad up/down) cycle all fields and buttons, **X** saves,
  **B** cancels; preset rows and Save/Cancel remain clickable with the stick + **A**.
- When a reply arrives while the window is closed, a badge appears on the HUD.
- **Generic Mod Config Menu** — if you have
  [GMCM](https://www.nexusmods.com/stardewvalley/mods/5098) installed, every option
  (provider, key, model, web search, the chat keybinds, and the chatbot's display name)
  is editable in its UI at the title screen or under the game's options cog icon.
  "Reset to defaults" there deliberately keeps your API keys.
- SMAPI console commands:
  - `gamma menu` — open the chat window
  - `gamma ask <text>` — ask without the UI (reply is printed to the console)
  - `gamma wiki <query>` — quick wiki search
  - `gamma state` — dump the same state summary the AI sees
  - `gamma mods` — list installed mods
  - `gamma reset` — clear chat history

## Privacy

When you send a message, the mod sends a summary of your game state (the "snapshot" plus
whatever tools the AI chooses to call — inventory, chests, quests, mod list…) to the AI
provider you configured. Nothing is sent until you ask something. Use a local model via
Ollama/LM Studio if you want everything to stay on your machine.

## Building

Requires the .NET SDK (any recent version) and Stardew Valley 1.6 + SMAPI installed.

```bash
cd Gamma
dotnet build
```

The build compiles against your local game DLLs and copies `Gamma.dll` +
`manifest.json` into `Mods/Gamma/` automatically. If the game isn't in the default
Steam location, point at it:

```bash
dotnet build -p:GamePath="C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
```

## Troubleshooting

- *"I'm not set up yet"* — the config has no working provider. Fill `ApiKey` (or
  `ApiBaseUrl` for local), then restart the game.
- **API key errors** — check the key and that `Model` matches the provider's model names.
- **Web search says it's not configured** — set `SearchProvider` + `SearchApiKey`.
- Anything else — check the SMAPI console/log (`ErrorLogs/SMAPI-latest.txt`) for
  `[Gamma]` lines.

## Testing

The mod can test itself — no manual play needed:

- **In game**: run `gamma testui` in the SMAPI console. It seeds a sample conversation,
  opens the chat window, sends one real chat request, opens the settings screen, and saves
  a screenshot of each step to `Mods/Gamma/ui-tests/`.
- **Fully headless** (no window, no audio, quits when done):

  ```bash
  ./run-headless-test.sh                       # uses a known-working model by default
  GAMMA_AUTO_TEST_MODEL=gpt-4o-mini ./run-headless-test.sh
  ```

  This launches the real game under a virtual display with all audio muted (SDL dummy
  driver + OpenAL null backend), runs the same automated pass, and writes the log and
  screenshots out. Note: headless runs render at a slightly different scale than a real
  window, so use the screenshots to check content/colors, not exact pixel layout.

## How it works (for the curious)

The mod is a state bridge: SMAPI events and the game API (`Game1.*`) are read into compact
text summaries; the AI gets the summaries as context and a set of ~13 tools
(`get_chests`, `search_wiki`, `web_search`, …) implemented as a read-only tool-calling loop
against either an OpenAI-compatible or Anthropic endpoint. Tool bodies are marshalled onto
the game thread so reads never race the game's tick loop. The UI reuses the vanilla menu
box texture, chat text field, fonts and sounds.
