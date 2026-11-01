using Lumi.Api.Data;
using Lumi.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Lumi.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLumiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WhisperOptions>().BindConfiguration("Whisper")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Executable) && !string.IsNullOrWhiteSpace(o.ModelPath)
                && !string.IsNullOrWhiteSpace(o.FfmpegExecutable) && o.TimeoutSeconds is > 0 and <= 600
                && o.MaxAudioBytes is > 0 and <= 10485760 && o.MaxDurationSeconds is > 0 and <= 60,
                "Configuração Whisper inválida.").ValidateOnStart();
        services.AddOptions<SpeechOptions>().BindConfiguration("Speech")
            .Validate(o => !string.IsNullOrWhiteSpace(o.PythonExecutable) && !string.IsNullOrWhiteSpace(o.ModelPath)
                && !string.IsNullOrWhiteSpace(o.WorkerScript) && o.QueueCapacity is > 0 and <= 16
                && o.TimeoutSeconds is > 0 and <= 600 && o.MaxTextLength is > 0 and <= 4000,
                "Configuração Piper inválida.").ValidateOnStart();
        services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 11 * 1024 * 1024);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy("voice", context => System.Threading.RateLimiting.RateLimitPartition.GetConcurrencyLimiter(
                "local", _ => new() { PermitLimit = 1, QueueLimit = 0 }));
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddSingleton<TranscriptionService>();
        services.AddSingleton<PiperSpeechService>();
        services.AddSingleton<ISpeechService>(services => services.GetRequiredService<PiperSpeechService>());
        services.AddHostedService(services => services.GetRequiredService<PiperSpeechService>());
        services.AddSingleton<PcAudioService>();
       services.AddHostedService<FollowUpBackgroundService>();
       services.AddScoped<MicroActionService>();
        services.AddHttpClient("Ollama", client =>
        {
            client.BaseAddress = new Uri(
                configuration["Ollama:BaseUrl"]!
            );
        });

        services.AddScoped<ChatService>();
        services.AddDbContext<LumiDbContext>(options =>
            options.UseSqlite("Data Source=lumi.db"));

        return services;
    }
}
