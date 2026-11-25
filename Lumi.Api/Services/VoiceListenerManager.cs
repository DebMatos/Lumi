using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lumi.Api.Services;

public sealed record VoiceListenerStatus(
    string State,
    bool Running,
    long Revision,
    long ConversationRevision = 0,
    string? Transcription = null,
    string? Reply = null,
    string? Error = null,
    Guid? SessionId = null);

public sealed class VoiceListenerManager : IAsyncDisposable
{
    private readonly VoiceListenerOptions _options;
    private readonly ILogger<VoiceListenerManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private static readonly HashSet<string> ValidStates =
    [
        "starting", "listening", "recording", "transcribing", "responding",
        "speaking", "stopping", "stopped", "error"
    ];
    private Process? _process;
    private VoiceListenerStatus _status = new("stopped", false, 0);
    private bool _stopping;

    public VoiceListenerManager(
        IOptions<VoiceListenerOptions> options,
        ILogger<VoiceListenerManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public VoiceListenerStatus Status => Volatile.Read(ref _status);

    public async Task<bool> StartAsync(
        Guid sessionId,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false }) return false;
            _process?.Dispose();

            var script = ResolveScript(_options.Script);
            if (!File.Exists(script))
                throw new InvalidOperationException(
                    $"VoiceListener não encontrado em {script}.");

            var info = new ProcessStartInfo(
                VoiceValidation.ExpandPath(_options.PythonExecutable))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            foreach (var argument in new[]
            {
                script, "--managed", "--session-id", sessionId.ToString(),
                "--api-url", apiUrl
            }) info.ArgumentList.Add(argument);

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => HandleEvent(process, args.Data);
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogInformation("VoiceListener: {Message}", args.Data);
            };
            process.Exited += (_, _) => HandleExit(process);

            _stopping = false;
            if (!process.Start())
                throw new InvalidOperationException("Não foi possível iniciar o VoiceListener.");
            _process = process;
            SetStatus("starting", true, resetDetails: true, sessionId: sessionId);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var process = _process;
            if (process is null || process.HasExited)
            {
                SetStatus("stopped", false);
                return;
            }

            _stopping = true;
            SetStatus("stopping", true);
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.StopTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            SetStatus("stopped", false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void HandleEvent(Process source, string? line)
    {
        if (source != _process || string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("event").GetString() != "state") return;
            var state = root.GetProperty("state").GetString();
            if (string.IsNullOrWhiteSpace(state) || !ValidStates.Contains(state)) return;
            SetStatus(
                state,
                true,
                ReadString(root, "transcription"),
                ReadString(root, "reply"),
                ReadString(root, "error"));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning("Evento inválido do VoiceListener ignorado.");
        }
    }

    private void HandleExit(Process source)
    {
        if (source != _process) return;
        var expected = _stopping;
        SetStatus(
            expected ? "stopped" : "error",
            false,
            error: expected ? null : Status.Error
                ?? $"O VoiceListener terminou inesperadamente ({source.ExitCode}).");
    }

    private void SetStatus(
        string state,
        bool running,
        string? transcription = null,
        string? reply = null,
        string? error = null,
        bool resetDetails = false,
        Guid? sessionId = null)
    {
        lock (_statusGate)
        {
            var previous = _status;
            Volatile.Write(ref _status, new VoiceListenerStatus(
                state,
                running,
                previous.Revision + 1,
                previous.ConversationRevision + (reply is null ? 0 : 1),
                resetDetails ? null : transcription ?? previous.Transcription,
                resetDetails ? null : reply ?? previous.Reply,
                error,
                sessionId ?? previous.SessionId));
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolveScript(string configured) =>
        Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(configured, AppContext.BaseDirectory);

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None); }
        finally
        {
            _process?.Dispose();
            _gate.Dispose();
        }
    }
}
