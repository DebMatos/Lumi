using Lumi.Api.Endpoints;
using Lumi.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLumiServices(builder.Configuration);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 11 * 1024 * 1024);

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

app.MapChatEndpoints();
app.MapAudioEndpoints();

app.Run();
