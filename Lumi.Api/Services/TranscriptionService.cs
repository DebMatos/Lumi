using Microsoft.Extensions.Options;

namespace Lumi.Api.Services;

public class TranscriptionService(IOptions<WhisperOptions> options)
{
    private readonly WhisperOptions _options = options.Value;

    public async Task<string> TranscribeAsync(IFormFile audio, CancellationToken cancellationToken)
    {
        VoiceValidation.Audio(audio, _options.MaxAudioBytes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var token = timeout.Token;
        var tempDir = Path.Combine(Path.GetTempPath(), $"lumi-transcribe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "input.audio");
        var wavPath = Path.Combine(tempDir, "audio.wav");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tempDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using (var stream = File.Create(inputPath))
                await audio.CopyToAsync(stream, token);

            // Decode at most limit + 1 second. Size of the fixed PCM output validates
            // real duration, including WebM recordings with missing duration metadata.
            try
            {
                await VoiceProcess.RunAsync(_options.FfmpegExecutable, [
                "-nostdin", "-v", "error", "-y", "-protocol_whitelist", "file,pipe",
                "-format_whitelist", "matroska,webm,ogg,wav,mov,mp3",
                "-i", inputPath, "-map", "0:a:0", "-vn", "-t", (_options.MaxDurationSeconds + 1).ToString(),
                "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wavPath
                ], null, token);
            }
            catch (InvalidOperationException error)
            {
                throw new ArgumentException("Não foi possível descodificar o áudio.", error);
            }
            var pcmBytes = PcmDataLength(wavPath);
            if (pcmBytes == 0 || pcmBytes > _options.MaxDurationSeconds * 32000L)
                throw new ArgumentException($"O áudio está vazio ou excede {_options.MaxDurationSeconds} segundos.");

            var result = await VoiceProcess.RunAsync(_options.Executable, [
                "-m", VoiceValidation.ExpandPath(_options.ModelPath), "-f", wavPath,
                "-l", "pt", "-nt", "-np"
            ], null, token);
            return result.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A transcrição excedeu o tempo limite.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static uint PcmDataLength(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 12; // RIFF size + WAVE
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var name = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            if (name == "data") return length;
            reader.BaseStream.Seek(length + (length % 2), SeekOrigin.Current);
        }
        throw new ArgumentException("O áudio não contém amostras PCM.");
    }
}
