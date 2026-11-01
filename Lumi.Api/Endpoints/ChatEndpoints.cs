using Lumi.Api.Services;
using Lumi.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumi.Api.Endpoints;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat", async (
            ChatRequest request,
            ChatService chatService) =>
        {
            var reply = await chatService.SendMessageAsync(request.Message);

            return Results.Ok(new { reply });
        });


        app.MapPost("/chat/stream", async (
            StreamChatRequest request,
            ChatService chatService,
            HttpContext context) =>
        {
            if (!Guid.TryParse(request.SessionId, out _) ||
                string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest();
            }

            context.Response.ContentType = "text/plain; charset=utf-8";

            await foreach (var chunk in chatService.StreamMessageAsync(
                request.SessionId,
                request.Message,
                context.RequestAborted))
            {
                await context.Response.WriteAsync(
                    chunk,
                    context.RequestAborted
                );

                await context.Response.Body.FlushAsync(
                    context.RequestAborted
                );
            }

            return Results.Empty;
        });

        app.MapGet("/conversations/{id}/messages", async (
            string id,
            LumiDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(id, out var conversationId))
                return Results.BadRequest();

            var exists = await db.Conversations
                .AnyAsync(c => c.Id == conversationId, cancellationToken);

            if (!exists)
                return Results.NotFound();

            var messages = await db.Messages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id)
                .Select(m => new
                {
                    m.Id,
                    m.Role,
                    m.Content,
                    m.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(messages);
        });
        return app;
    }
}
