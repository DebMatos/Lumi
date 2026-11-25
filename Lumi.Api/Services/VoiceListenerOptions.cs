namespace Lumi.Api.Services;

public sealed class VoiceListenerOptions
{
    public string PythonExecutable { get; set; } = "python3";
    public string Script { get; set; } = "VoiceListener/listener.py";
    public int StopTimeoutSeconds { get; set; } = 5;
}
