
using System.Diagnostics;

namespace Lumi.Api.Services;

public class PcAudioService
{
    private readonly ISpeechService _speechService;

    public PcAudioService(ISpeechService speechService)
    {
        _speechService = speechService;
    }

    public async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var audio = await _speechService.SynthesizeAsync(
            text,
            cancellationToken
        );

        var path = Path.Combine(
            Path.GetTempPath(),
            $"lumi-{Guid.NewGuid()}.wav"
        );

        try
        {
            await File.WriteAllBytesAsync(
                path, audio, cancellationToken
            );

            using var process = new Process();

            process.StartInfo = new ProcessStartInfo
            {
                FileName = "pw-play",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.StartInfo.ArgumentList.Add(path);

            process.Start();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill();

                await process.WaitForExitAsync(
                    CancellationToken.None
                );

                throw;
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Falha na reprodução de áudio: {process.ExitCode}"
                );
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
