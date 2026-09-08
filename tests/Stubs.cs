// Minimal game boundaries: these tests run the real history and request orchestration
// without launching Stardew Valley, touching saved chats, or calling a provider.
namespace StardewModdingAPI
{
    public enum SButton { O, RightStick }
    public enum LogLevel { Debug, Warn, Error }
    public interface IMonitor { void Log(string message, LogLevel level); }
    public interface IModHelper { TestData Data { get; } }
    public static class Context { public static bool IsWorldReady => false; }
    public class TestData
    {
        public object Saved;
        public T ReadGlobalData<T>(string key) => default;
        public void WriteGlobalData<T>(string key, T value) => Saved = value;
    }
}
namespace StardewValley
{
    public static class Game1
    {
        public static TestPlayer player;
        public static TestFont smallFont;
    }
    public class TestPlayer { public long UniqueMultiplayerID; }
    public class TestFont { public List<char> Characters = new(); }
}
namespace Gamma.Services
{
    public class GameStateService { public string GetSnapshot() => "test farm"; }
    public class ToolRegistry
    {
        public IReadOnlyList<ToolSpec> Specs => Array.Empty<ToolSpec>();
        public string DisplayLabel(string name) => "Checking inventory…";
        public string Execute(ToolCall call) => "test inventory";
    }
    public class OpenAiClient : LlmClient
    {
        public OpenAiClient(ModConfig config, StardewModdingAPI.IMonitor monitor) { }
        public override Task<LlmResult> CompleteAsync(IReadOnlyList<WireMessage> messages, IReadOnlyList<ToolSpec> tools)
            => throw new Exception("Tests must not use a real provider");
    }
    public class AnthropicClient : OpenAiClient
    {
        public AnthropicClient(ModConfig config) : base(config, null) { }
    }
}
