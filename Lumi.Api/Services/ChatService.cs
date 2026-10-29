
using System.Net.Http.Json;

namespace Lumi.Api.Services;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;

public class ChatService
{
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private const int MaxMessages = 20;
    public ChatService(IHttpClientFactory httpClientFactory)
    {
        _client = httpClientFactory.CreateClient("Ollama");
    }

    public async Task<string> SendMessageAsync(string message)
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new
        {
            model = "lumi-v3",
            messages = new[]
            {
                new { role = "user", content = message }
            },
            stream = false
        });

        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<OllamaResponse>();

        return result?.Message?.Content ?? "";
    }



public async IAsyncEnumerable<string> StreamMessageAsync(
    string sessionId,
    string message,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var session = _sessions.GetOrAdd(
        sessionId,
        _ => new ChatSession()
    );

    await session.Lock.WaitAsync(cancellationToken);

    try
    {
        // Recuperar o histórico e adicionar a nova mensagem
        var messages = session.Messages
            .Select(m => new { role = m.Role, content = m.Content })
            .ToList();

        messages.Add(new { role = "user", content = message });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/chat"
        );

        request.Content = JsonContent.Create(new
        {
            model = "lumi-v3",
            messages,
            stream = true
        });

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken
        );

        using var reader = new StreamReader(stream);
        var reply = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;

            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.ToString());

            var content = root.GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (!string.IsNullOrEmpty(content))
            {
                reply.Append(content);
                yield return content;
            }
        }

        // Só guardar depois de receber a resposta completa
        session.Messages.Add(new ChatMessage("user", message));
        session.Messages.Add(
            new ChatMessage("assistant", reply.ToString())
        );

        // Manter apenas as últimas 10 interações
        if (session.Messages.Count > MaxMessages)
        {
            session.Messages.RemoveRange(
                0,
                session.Messages.Count - MaxMessages
            );
        }
    }
    finally
    {
        session.Lock.Release();
    }
}

}

public class ChatSession
{
    public List<ChatMessage> Messages { get; } = new();
    public SemaphoreSlim Lock { get; } = new(1, 1);
}
public record OllamaResponse(OllamaMessage? Message);
public record OllamaMessage(string Content);

public record ChatMessage(string Role, string Content);


