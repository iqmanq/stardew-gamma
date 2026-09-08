# Changelog

## 1.0.1 — 2026-09-08

- Added a configurable controller shortcut to open chat (right-stick click by default).
- Enabled free left-stick cursor movement in chat and settings, including with snappy menus enabled.
- Expanded settings LB/RB navigation to every field and button, including Web search, Name chats with AI, and Load list.
- Text fields open the on-screen keyboard only when A is pressed over them. Hovering, opening chat, and bumper navigation no longer open it; Y does nothing in either menu.
- Wrapped chat control hints so they remain inside the panel and clear of the close button.
- Kept replies, thinking indicators, tool progress, errors, and automatic titles in the originating chat when switching conversations or closing the menu.
- Allowed requests in separate chats to run independently, and discarded late results for deleted chats or previously loaded saves.
- Applied history and UI notifications on the game thread and removed duplicate copies of the current message from request prompts.
- Added offline regression checks for conversation routing and updated the UI test harness and controller documentation.
- Set `openrouter/free` as the default and recommended OpenRouter model in the README and in-game settings.

## 1.0.0

- Initial release: in-game AI chat, game-state tools, wiki/web search, provider settings, and saved conversations.
