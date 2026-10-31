
using System.Diagnostics;

namespace Lumi.Api.Services;

public class TranscriptionService
{
    private readonly string _whisperPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projetos/whisper.cpp"
        );

    public async Task<string> TranscribeAsync(
        IFormFile audio,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            $"lumi-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, "input.webm");
        var wavPath = Path.Combine(tempDir, "audio.wav");

        try
        {
            await using (var stream = File.Create(inputPath))
            {
                await audio.CopyToAsync(stream, cancellationToken);
            }

            await RunAsync("ffmpeg", [
                "-y", "-i", inputPath,
                "-ar", "16000",
                "-ac", "1",
                "-c:a", "pcm_s16le",
                wavPath
            ], cancellationToken);

            var result = await RunAsync(
                Path.Combine(_whisperPath, "build/bin/whisper-cli"),
                [
                    "-m", Path.Combine(_whisperPath, "models/ggml-base.bin"),
                    "-f", wavPath,
                    "-l", "pt",
                    "-nt",
                    "-np"
                ],
                cancellationToken
            );

            return result.Trim();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar o processo.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(error);

        return output;
    }
}
