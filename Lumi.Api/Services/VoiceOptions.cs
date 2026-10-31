namespace Lumi.Api.Services;

public sealed class WhisperOptions
{
    public string Executable { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string FfmpegExecutable { get; set; } = "ffmpeg";
    public int TimeoutSeconds { get; set; } = 120;
    public long MaxAudioBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxDurationSeconds { get; set; } = 60;
}

public sealed class SpeechOptions
{
    public string PiperExecutable { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxTextLength { get; set; } = 4000;
}

public static class VoiceValidation
{
    public static string ExpandPath(string path) => path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path;

    public static void Text(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new ArgumentException($"O texto deve ter entre 1 e {maxLength} caracteres.");
    }

    public static void Audio(IFormFile audio, long maxBytes)
    {
        if (audio.Length == 0 || audio.Length > maxBytes)
            throw new ArgumentException($"O áudio deve ter entre 1 e {maxBytes} bytes.");
        var type = audio.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        if (type is not ("audio/webm" or "video/webm" or "audio/ogg" or "audio/wav"
            or "audio/x-wav" or "audio/mp4" or "audio/mpeg"))
            throw new ArgumentException("Formato de áudio não suportado.");
    }
}
