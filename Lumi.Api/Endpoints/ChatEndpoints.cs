using Lumi.Api.Services;

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
            if (string.IsNullOrWhiteSpace(request.SessionId) ||
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

        return app;
    }
}
