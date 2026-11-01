
using System.Net.Http.Json;
using Lumi.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Lumi.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<WhisperOptions>().BindConfiguration("Whisper")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Executable) && !string.IsNullOrWhiteSpace(o.ModelPath)
        && !string.IsNullOrWhiteSpace(o.FfmpegExecutable) && o.TimeoutSeconds is > 0 and <= 600
        && o.MaxAudioBytes is > 0 and <= 10485760 && o.MaxDurationSeconds is > 0 and <= 60,
        "Configuração Whisper inválida.").ValidateOnStart();
builder.Services.AddOptions<SpeechOptions>().BindConfiguration("Speech")
    .Validate(o => !string.IsNullOrWhiteSpace(o.PythonExecutable) && !string.IsNullOrWhiteSpace(o.ModelPath)
        && !string.IsNullOrWhiteSpace(o.WorkerScript) && o.QueueCapacity is > 0 and <= 16
        && o.TimeoutSeconds is > 0 and <= 600 && o.MaxTextLength is > 0 and <= 4000,
        "Configuração Piper inválida.").ValidateOnStart();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 11 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 11 * 1024 * 1024);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("voice", context => System.Threading.RateLimiting.RateLimitPartition.GetConcurrencyLimiter(
        "local", _ => new() { PermitLimit = 1, QueueLimit = 0 }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<TranscriptionService>();
builder.Services.AddSingleton<PiperSpeechService>();
builder.Services.AddSingleton<ISpeechService>(services => services.GetRequiredService<PiperSpeechService>());
builder.Services.AddHostedService(services => services.GetRequiredService<PiperSpeechService>());
builder.Services.AddHttpClient("Ollama", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Ollama:BaseUrl"]!
    );
});

builder.Services.AddScoped<ChatService>();
builder.Services.AddDbContext<LumiDbContext>(options =>
    options.UseSqlite("Data Source=lumi.db"));

    
var app = builder.Build();

// Local-only V0.1: reject remote peers and browser requests from other origins.
// Host filtering (AllowedHosts) also protects against DNS rebinding.
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    var origin = context.Request.Headers.Origin.ToString();
    if (remote is null || !System.Net.IPAddress.IsLoopback(remote)
        || (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme != context.Request.Scheme))
        || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
    {
        context.Response.StatusCode = 403;
        return;
    }
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        if (!context.Response.HasStarted) context.Response.StatusCode = 499;
    }
    catch (Exception error) when (context.Request.Path.StartsWithSegments("/audio"))
    {
        app.Logger.LogWarning(error, "Falha no pedido de áudio");
        if (context.Response.HasStarted) { context.Abort(); return; }
        var status = error switch
        {
            ArgumentException => 400,
            BadHttpRequestException bad => bad.StatusCode,
            TimeoutException => 504,
            _ => 503
        };
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = status switch
        {
            400 => "Áudio ou texto inválido. Verifica o formato e os limites.",
            413 => "O pedido excede o tamanho permitido.",
            504 => "O serviço de voz excedeu o tempo limite. Tenta novamente.",
            _ => "Não foi possível processar o áudio. Verifica o serviço local."
        }});
    }
});
app.UseRateLimiter();

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
    var text = await transcriptionService.TranscribeAsync(
        audio,
        context.RequestAborted
    );

    return Results.Ok(new { text });
})
.DisableAntiforgery()
.RequireRateLimiting("voice");


app.MapPost("/audio/speak", async (
    SpeakRequest request,
    ISpeechService speechService,
    Microsoft.Extensions.Options.IOptions<SpeechOptions> options,
    CancellationToken cancellationToken) =>
{
    VoiceValidation.Text(request.Text, options.Value.MaxTextLength);

    var audio = await speechService.SynthesizeAsync(
        request.Text,
        cancellationToken
    );

    return Results.File(audio, "audio/wav");
});


app.Run();




record ChatRequest(string Message);

record StreamChatRequest(string SessionId, string Message);

record SpeakRequest(string Text);
