using System.Collections.Concurrent;
using Gamma;
using Gamma.Services;
using StardewModdingAPI;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Pump(ChatService service, Func<bool> done)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        service.Update();
        if (done()) return;
        Thread.Sleep(5);
    }
    throw new Exception("Timed out waiting for background request");
}

var helper = new Helper();
var monitor = new Monitor();
var config = new ModConfig { ApiBaseUrl = "test", AutoNameChats = true };
var history = new ChatHistory(helper, monitor, config);
var clients = new Queue<DelayedClient>();
var service = new ChatService(helper, monitor, config, new GameStateService(), new ToolRegistry(), history, () => clients.Dequeue());
var events = new List<(Conversation Conversation, string Text)>();
int mainThread = Environment.CurrentManagedThreadId;
service.MessageAdded += (chat, user, text, persisted) =>
{
    Check(Environment.CurrentManagedThreadId == mainThread, "Message events must run on game thread");
    if (!user) events.Add((chat, text));
};

var a = history.Current;
var clientA = new DelayedClient();
clients.Enqueue(clientA);
_ = service.Ask("question A");
Check(a.Busy && a.Status == "Thinking…", "Origin should show thinking");
var b = history.NewChat();
Check(!service.Busy && b.Status == null, "Thinking leaked into another chat");
var clientB = new DelayedClient();
clients.Enqueue(clientB);
_ = service.Ask("question B");
Pump(service, () => clientA.Requests.Count == 1 && clientB.Requests.Count == 1);
Check(clientA.Requests.First().Count(m => m.Kind == "user" && m.Text == "question A") == 1, "Prompt duplicated the current message");
Check(!clientA.Requests.First().Any(m => m.Text == "question B"), "Prompt mixed conversations");
clientA.Reply(new LlmResult { ToolCalls = new() { new ToolCall { Id = "tool-a", Name = "get_inventory" } } });
Pump(service, () => a.Status == "Checking inventory…" && clientA.Requests.Count == 2);
Check(b.Status == "Thinking…", "Tool progress leaked into selected chat");
history.Select(history.All.ToList().IndexOf(a));
Check(service.Busy && history.Current.Status == "Checking inventory…", "Returning to origin lost tool progress");
history.Select(history.All.ToList().IndexOf(b));
clientA.Reply(new LlmResult { Content = "answer A" });
Pump(service, () => a.Turns.Count == 2 && clientA.Requests.Count == 3);
Check(b.Turns.Count == 1, "Background answer landed in selected chat");
clientA.Reply(new LlmResult { Content = "Title A" });
Pump(service, () => !a.Busy);
Check(a.Title == "Title A" && b.Title == "question B", "Auto-title changed the wrong conversation");
Check(b.Busy && b.Status == "Thinking…", "Completing A cleared B's progress");
clientB.Fail(new Exception("test failure"));
Pump(service, () => !b.Busy);
Check(b.Notices.Count == 1 && a.Notices.Count == 0, "Failure went to the wrong conversation");
Check(a.Status == null && b.Status == null, "Completed requests left stale progress");
Check(events.Any(e => e.Conversation == a && e.Text == "answer A"), "Answer event lost its conversation");
Check(events.Any(e => e.Conversation == b && e.Text.Contains("test failure")), "Error event lost its conversation");
var saved = (SavedChats)helper.Data.Saved;
Check(saved.Conversations.Single(c => c.Id == a.Id).Turns.Last().Text == "answer A", "Saved answer has wrong destination");
Console.WriteLine("PASS: switching, concurrent requests, tool progress, replies, titles, errors, persistence, and event threading");

config.AutoNameChats = false;
var deleted = history.NewChat();
var deletedClient = new DelayedClient();
clients.Enqueue(deletedClient);
var deletedRequest = service.Ask("deleted request");
Pump(service, () => deletedClient.Requests.Count == 1);
history.Delete(deleted.Id);
var survivor = history.Current;
int survivorTurns = survivor.Turns.Count;
int eventCount = events.Count;
deletedClient.Reply(new LlmResult { Content = "must be discarded" });
Check(deletedRequest.Wait(TimeSpan.FromSeconds(5)), "Deleted request did not finish");
service.Update();
Check(!history.All.Contains(deleted) && survivor.Turns.Count == survivorTurns, "Deleted chat result was redirected or resurrected");
Check(events.Count == eventCount, "Deleted chat emitted a late message");
Console.WriteLine("PASS: deleting a chat discards its delayed result");

var oldSave = history.Current;
var oldClient = new DelayedClient();
clients.Enqueue(oldClient);
var oldRequest = service.Ask("old save request");
Pump(service, () => oldClient.Requests.Count == 1);
history.Load();
var newSave = history.Current;
oldClient.Reply(new LlmResult { Content = "old save reply" });
Check(oldRequest.Wait(TimeSpan.FromSeconds(5)), "Old save request did not finish");
service.Update();
Check(newSave.Turns.Count == 0 && newSave.Status == null && !newSave.Busy, "Old save request leaked into newly loaded history");
Console.WriteLine("PASS: loading a save discards results from the previous history");


sealed class Helper : IModHelper { public TestData Data { get; } = new(); }
sealed class Monitor : IMonitor { public void Log(string message, LogLevel level) { } }
sealed class DelayedClient : LlmClient
{
    private readonly ConcurrentQueue<TaskCompletionSource<LlmResult>> Pending = new();
    public readonly ConcurrentQueue<List<WireMessage>> Requests = new();
    public override Task<LlmResult> CompleteAsync(IReadOnlyList<WireMessage> messages, IReadOnlyList<ToolSpec> tools)
    {
        var completion = new TaskCompletionSource<LlmResult>();
        Pending.Enqueue(completion);
        Requests.Enqueue(messages.ToList());
        return completion.Task;
    }
    public void Reply(LlmResult result)
    {
        if (!Pending.TryDequeue(out var pending)) throw new Exception("No pending request");
        pending.SetResult(result);
    }
    public void Fail(Exception error)
    {
        if (!Pending.TryDequeue(out var pending)) throw new Exception("No pending request");
        pending.SetException(error);
    }
}
