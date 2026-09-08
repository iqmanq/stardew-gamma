Run `dotnet run --project tests/ConversationRouting.csproj` from the repository root (.NET 8 SDK).

These regression checks compile the real chat service and history against minimal game
stubs and a delayed fake provider. They verify chat switching during tool calls, concurrent
requests, response/error/title ownership, persistence, main-thread notifications, and
late responses after deleting a chat or loading another save. They do not launch the
game, read real chat history, or make network requests.
