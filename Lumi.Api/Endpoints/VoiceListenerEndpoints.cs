using Lumi.Api.Services;

namespace Lumi.Api.Endpoints;

public static class VoiceListenerEndpoints
{
    public static IEndpointRouteBuilder MapVoiceListenerEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/voice-listener/status", (
            VoiceListenerManager manager) => manager.Status);

        app.MapPost("/voice-listener/start", async (
            VoiceListenerStartRequest request,
            VoiceListenerManager manager,
            ILogger<VoiceListenerManager> logger,
            HttpContext context) =>
        {
            if (!Guid.TryParse(request.SessionId, out var sessionId))
                return Results.BadRequest(new { error = "Sessão inválida." });

            var apiUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            try
            {
                return await manager.StartAsync(
                    sessionId, apiUrl, context.RequestAborted)
                    ? Results.Ok(manager.Status)
                    : Results.Conflict(new { error = "O VoiceListener já está ativo." });
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(error, "Falha ao iniciar o VoiceListener");
                return Results.Json(
                    new { error = "Não foi possível iniciar a escuta. Verifica o ambiente Python e o microfone." },
                    statusCode: 503);
            }
        });

        app.MapPost("/voice-listener/stop", async (
            VoiceListenerManager manager,
            HttpContext context) =>
        {
            await manager.StopAsync(context.RequestAborted);
            return Results.Ok(manager.Status);
        });

        return app;
    }
}
