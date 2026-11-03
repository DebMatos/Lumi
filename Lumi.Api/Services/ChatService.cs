
using System.Net.Http.Json;

namespace Lumi.Api.Services;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;
using Lumi.Api.Data;
using Microsoft.EntityFrameworkCore;
using Lumi.Api.Models;

public class ChatService
{
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();

    private readonly string _model;

    private const int MaxMessages = 20;

    private readonly LumiDbContext _db;
    public ChatService(
      IHttpClientFactory httpClientFactory,
      IConfiguration configuration,
      LumiDbContext db)
    {
        _client = httpClientFactory.CreateClient("Ollama");
        _db = db;

        _model = configuration["Ollama:DefaultModel"]
            ?? throw new InvalidOperationException(
                "Modelo Ollama não configurado.");
    }
    public async Task<string> SendMessageAsync(string message)
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new
        {
            model = _model,
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


    private async Task<Conversation> GetOrCreateConversationAsync(
    string sessionId,
    CancellationToken cancellationToken)
    {
        var conversationId = Guid.Parse(sessionId);

        var conversation = await _db.Conversations
            .FindAsync([conversationId], cancellationToken);

        if (conversation is not null)
            return conversation;

        conversation = new Conversation
        {
            Id = conversationId
        };

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        return conversation;
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

        var conversation = await GetOrCreateConversationAsync(
                   sessionId,
                   cancellationToken
               );

        await session.Lock.WaitAsync(cancellationToken);

        try
        {
            var history = await _db.Messages
           .AsNoTracking()
           .Where(m => m.ConversationId == conversation.Id)
           .OrderByDescending(m => m.CreatedAt)
           .ThenByDescending(m => m.Id)
           .Take(MaxMessages)
           .ToListAsync(cancellationToken);

            var messages = history
                .AsEnumerable()
                .Reverse()
                .Select(m => new { role = m.Role, content = m.Content })
                .ToList();

            messages.Add(new { role = "user", content = message });

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/chat"
            );

            request.Content = JsonContent.Create(new
            {
                model = _model,
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

            // Guardar a interação após a resposta completa
            _db.Messages.AddRange(
                new Message
                {
                    ConversationId = conversation.Id,
                    Role = "user",
                    Content = message
                },
                new Message
                {
                    ConversationId = conversation.Id,
                    Role = "assistant",
                    Content = reply.ToString()
                }
            );

            await _db.SaveChangesAsync(cancellationToken);
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


