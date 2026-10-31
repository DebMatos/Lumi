
using System.Net.Http.Json;
using Lumi.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<TranscriptionService>();

builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Ollama:BaseUrl"]!
    );
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


app.MapPost("/audio/transcribe", async (
    IFormFile audio,
    TranscriptionService transcriptionService,
    HttpContext context) =>
{
    if (audio.Length == 0)
        return Results.BadRequest("O ficheiro áudio está vazio.");

    if (audio.Length > 10 * 1024 * 1024)
        return Results.BadRequest("O ficheiro excede 10 MB.");

    var text = await transcriptionService.TranscribeAsync(
        audio,
        context.RequestAborted
    );

    return Results.Ok(new { text });
})
.DisableAntiforgery();

app.Run();




record ChatRequest(string Message);

record StreamChatRequest(string SessionId, string Message);
