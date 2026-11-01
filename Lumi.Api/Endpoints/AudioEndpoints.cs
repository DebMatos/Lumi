using Lumi.Api.Services;

namespace Lumi.Api.Endpoints;

public static class AudioEndpoints
{
    public static IEndpointRouteBuilder MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
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

        return app;
    }
}
