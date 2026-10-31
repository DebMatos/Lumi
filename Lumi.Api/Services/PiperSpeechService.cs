using Microsoft.Extensions.Options;

namespace Lumi.Api.Services;

public class PiperSpeechService(IOptions<SpeechOptions> options) : ISpeechService
{
    private readonly SpeechOptions _options = options.Value;

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        VoiceValidation.Text(text, _options.MaxTextLength);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-speech-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "speech.wav");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tempDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Piper treats lines as separate utterances; send one normalized utterance.
            var utterance = text.Replace('\r', ' ').Replace('\n', ' ');
            await VoiceProcess.RunAsync(_options.PiperExecutable, [
                "--model", VoiceValidation.ExpandPath(_options.ModelPath), "--output_file", outputPath
            ], utterance, timeout.Token);
            var length = new FileInfo(outputPath).Length;
            if (length < 44 || length > 32 * 1024 * 1024)
                throw new InvalidOperationException("O serviço produziu áudio inválido.");
            return await File.ReadAllBytesAsync(outputPath, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A síntese excedeu o tempo limite.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
