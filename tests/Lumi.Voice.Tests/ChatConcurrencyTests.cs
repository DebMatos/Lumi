using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Lumi.Api.Data;
using Lumi.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

internal static class ChatConcurrencyTests
{
    public static async Task RunAsync(string root)
    {
        await SameSessionIsSerializedAsync(root);
        await SimultaneousFirstMessagesAsync(root);
        await DifferentSessionsRunConcurrentlyAsync(root);
        await CancellationReleasesSessionAsync(root);
        await HistorySurvivesServiceRestartAsync(root);
    }

    private static async Task SameSessionIsSerializedAsync(string root)
    {
        var secondStarted = NewSignal();
        var releaseSecond = NewSignal();
        var fixture = await Fixture.CreateAsync(root, "same-session", async (call, _, token) =>
        {
            if (call == 1) return "ALFA";
            if (call == 2)
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(token);
                return "BETA";
            }
            return "BETA confirmada";
        });
        await using (fixture)
        {
            var session = Guid.NewGuid();
            await fixture.SendAsync(session, "Memoriza ALFA");

            var change = fixture.SendAsync(session, "Muda para BETA");
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var verify = fixture.SendAsync(session, "Qual é a palavra?");
            await Task.Delay(100);
            Check(fixture.Handler.CallCount == 2,
                "same session waits for the active generation");
            releaseSecond.TrySetResult();
            await Task.WhenAll(change, verify);

            Check(PayloadContains(fixture.Handler.Payloads[2], "BETA"),
                "same session reads history persisted by the previous request");
            Check(await fixture.MessageCountAsync(session) == 6,
                "same session persists every complete turn once");
            Check(fixture.Coordinator.TrackedSessionCount == 0,
                "same session lock is removed after use");
        }
    }

    private static async Task SimultaneousFirstMessagesAsync(string root)
    {
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var fixture = await Fixture.CreateAsync(root, "first-messages", async (call, _, token) =>
        {
            if (call == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
                return "primeira resposta";
            }
            return "segunda resposta";
        });
        await using (fixture)
        {
            var session = Guid.NewGuid();
            var first = fixture.SendAsync(session, "primeira pergunta");
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = fixture.SendAsync(session, "segunda pergunta");
            await Task.Delay(100);
            Check(fixture.Handler.CallCount == 1,
                "simultaneous first messages are serialized before conversation creation");
            releaseFirst.TrySetResult();
            await Task.WhenAll(first, second);

            Check(await fixture.ConversationCountAsync(session) == 1,
                "simultaneous first messages create one conversation");
            Check(await fixture.MessageCountAsync(session) == 4,
                "simultaneous first messages persist two complete turns");
            Check(PayloadContains(fixture.Handler.Payloads[1], "primeira resposta"),
                "second first-message request receives the first turn as history");
        }
    }

    private static async Task DifferentSessionsRunConcurrentlyAsync(string root)
    {
        var bothStarted = NewSignal();
        var release = NewSignal();
        var callCount = new AtomicCounter();
        var fixture = await Fixture.CreateAsync(root, "different-sessions", async (_, _, token) =>
        {
            if (callCount.Increment() == 2) bothStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return "resposta";
        });
        await using (fixture)
        {
            var first = fixture.SendAsync(Guid.NewGuid(), "sessão um");
            var second = fixture.SendAsync(Guid.NewGuid(), "sessão dois");
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(fixture.Handler.CallCount == 2,
                "different sessions enter generation concurrently");
            release.TrySetResult();
            await Task.WhenAll(first, second);
            Check(fixture.Coordinator.TrackedSessionCount == 0,
                "different session locks are removed after use");
        }
    }

    private static async Task CancellationReleasesSessionAsync(string root)
    {
        var generationStarted = NewSignal();
        var fixture = await Fixture.CreateAsync(root, "cancellation", async (call, _, token) =>
        {
            if (call == 1)
            {
                generationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return "recuperada";
        });
        await using (fixture)
        {
            var session = Guid.NewGuid();
            using var cancellation = new CancellationTokenSource();
            var cancelled = fixture.SendAsync(session, "cancelar", cancellation.Token);
            await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await ThrowsCancellation(cancelled,
                "cancellation interrupts generation");
            Check(fixture.Coordinator.TrackedSessionCount == 0,
                "cancellation releases and removes the session lock");

            var reply = await fixture.SendAsync(session, "tentar novamente");
            Check(reply == "recuperada",
                "same session can generate again after cancellation");
            Check(await fixture.MessageCountAsync(session) == 2,
                "cancelled generation does not persist a partial turn");
        }
    }

    private static async Task HistorySurvivesServiceRestartAsync(string root)
    {
        var fixture = await Fixture.CreateAsync(root, "restart", (_, _, _) =>
            Task.FromResult("resposta persistida"));
        var session = Guid.NewGuid();
        await fixture.SendAsync(session, "pergunta persistida");
        await fixture.DisposeAsync();

        var reopened = await Fixture.OpenAsync(fixture.DatabasePath, (_, payload, _) =>
            Task.FromResult(PayloadContains(payload, "resposta persistida")
                ? "histórico recuperado"
                : "histórico em falta"));
        await using (reopened)
        {
            var reply = await reopened.SendAsync(session, "continua");
            Check(reply == "histórico recuperado",
                "history is recovered after service restart");
            Check(await reopened.MessageCountAsync(session) == 4,
                "reopened service appends to the existing conversation");
        }
    }

    private static bool PayloadContains(string payload, string content)
    {
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("messages").EnumerateArray()
            .Any(message => message.GetProperty("content").GetString() == content);
    }

    private static async Task ThrowsCancellation(Task task, string name)
    {
        try { await task; }
        catch (OperationCanceledException) { Check(true, name); return; }
        throw new Exception($"Expected cancellation: {name}");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine($"PASS {name}");
    }

    private sealed class AtomicCounter
    {
        private int _value;
        public int Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DbContextOptions<LumiDbContext> _options;
        private readonly IConfiguration _configuration;
        private readonly TestHttpClientFactory _clientFactory;

        private Fixture(string databasePath, ControlledHandler handler)
        {
            DatabasePath = databasePath;
            Handler = handler;
            Coordinator = new ChatSessionCoordinator();
            _options = new DbContextOptionsBuilder<LumiDbContext>()
                .UseSqlite($"Data Source={databasePath}").Options;
            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ollama:DefaultModel"] = "test-model"
                }).Build();
            _clientFactory = new TestHttpClientFactory(handler);
        }

        public string DatabasePath { get; }
        public ControlledHandler Handler { get; }
        public ChatSessionCoordinator Coordinator { get; }

        public static async Task<Fixture> CreateAsync(
            string root,
            string name,
            Func<int, string, CancellationToken, Task<string>> responder)
        {
            var path = Path.Combine(root, $"chat-{name}.db");
            var fixture = new Fixture(path, new ControlledHandler(responder));
            await using var database = new LumiDbContext(fixture._options);
            await database.Database.MigrateAsync();
            return fixture;
        }

        public static Task<Fixture> OpenAsync(
            string path,
            Func<int, string, CancellationToken, Task<string>> responder) =>
            Task.FromResult(new Fixture(path, new ControlledHandler(responder)));

        public async Task<string> SendAsync(
            Guid sessionId,
            string message,
            CancellationToken cancellationToken = default)
        {
            await using var database = new LumiDbContext(_options);
            var service = new ChatService(
                _clientFactory, _configuration, database, Coordinator);
            var reply = new StringBuilder();
            await foreach (var chunk in service.StreamMessageAsync(
                sessionId.ToString(), message, cancellationToken))
            {
                reply.Append(chunk);
            }
            return reply.ToString();
        }

        public async Task<int> ConversationCountAsync(Guid sessionId)
        {
            await using var database = new LumiDbContext(_options);
            return await database.Conversations.CountAsync(c => c.Id == sessionId);
        }

        public async Task<int> MessageCountAsync(Guid sessionId)
        {
            await using var database = new LumiDbContext(_options);
            return await database.Messages.CountAsync(m => m.ConversationId == sessionId);
        }

        public ValueTask DisposeAsync()
        {
            _clientFactory.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ControlledHandler(
        Func<int, string, CancellationToken, Task<string>> responder) : HttpMessageHandler
    {
        private int _callCount;
        private readonly ConcurrentDictionary<int, string> _payloads = new();

        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyDictionary<int, string> Payloads => _payloads;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            _payloads[call - 1] = payload;
            var reply = await responder(call, payload, cancellationToken);
            var line = JsonSerializer.Serialize(new
            {
                message = new { content = reply }
            }) + "\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(line, Encoding.UTF8, "application/x-ndjson")
            };
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler)
        {
            BaseAddress = new Uri("http://ollama.test")
        };

        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }
}
