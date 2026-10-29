
using System.Net.Http.Json;
using Lumi.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri("http://localhost:11434");
});
builder.Services.AddSingleton<ChatService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
app.UseSwagger();
app.UseSwaggerUI();
}   

app.UseStaticFiles();
app.MapGet("/", () => "Lumi API está a funcionar!");


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

app.Run();



record ChatRequest(string Message);

record StreamChatRequest(string SessionId, string Message);
