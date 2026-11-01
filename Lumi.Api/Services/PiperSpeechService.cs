using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Lumi.Api.Services;

// One consumer owns the worker and its pipes. The bounded channel is the only
// TTS queue; canceled queued requests never touch the running inference.
public sealed class PiperSpeechService : BackgroundService, ISpeechService
{
    private const int MaxAudioBytes = 32 * 1024 * 1024;
    private readonly SpeechOptions _options;
    private readonly ILogger<PiperSpeechService> _logger;
    private readonly Channel<PendingRequest> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _worker;
    private Task? _stderrDrain;
    private int _disposed;

    public PiperSpeechService(IOptions<SpeechOptions> options, ILogger<PiperSpeechService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _queue = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        VoiceValidation.Text(text, _options.MaxTextLength);
        cancellationToken.ThrowIfCancellationRequested();
        _shutdown.Token.ThrowIfCancellationRequested();
        var request = new PendingRequest(text, cancellationToken, _shutdown.Token, _options.TimeoutSeconds);
        if (!_queue.Writer.TryWrite(request))
        {
            request.Dispose();
            _shutdown.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("A fila de voz está cheia. Tenta novamente.");
        }
        try { return await request.Completion.Task; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            throw new TimeoutException("A síntese excedeu o tempo limite.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using (request)
                {
                    if (request.Token.IsCancellationRequested) continue;
                    try
                    {
                        var bytes = await RunRequestAsync(request);
                        request.Token.ThrowIfCancellationRequested();
                        request.Completion.TrySetResult(bytes);
                    }
                    catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled(request.Token);
                    }
                    catch (Exception error)
                    {
                        _logger.LogWarning(error, "Falha no worker Piper");
                        request.Completion.TrySetException(new InvalidOperationException("Não foi possível gerar a voz.", error));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _shutdown.Cancel();
            _queue.Writer.TryComplete();
            await StopWorkerAsync(graceful: true);
            while (_queue.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetCanceled(_shutdown.Token);
                pending.Dispose();
            }
        }
    }

    private async Task<byte[]> RunRequestAsync(PendingRequest request)
    {
        try
        {
            await EnsureWorkerAsync(request.Token);
            var worker = _worker!;
            var id = Guid.NewGuid().ToString("N");
            var message = JsonSerializer.Serialize(new { id, text = request.Text });
            await worker.StandardInput.WriteLineAsync(message.AsMemory(), request.Token);
            await worker.StandardInput.FlushAsync(request.Token);
            using var reply = await ReadHeaderAsync(worker.StandardOutput.BaseStream, request.Token);
            var header = reply.RootElement;
            if (header.GetProperty("id").GetString() != id)
                throw new InvalidDataException("Resposta Piper fora de sequência.");
            var length = header.GetProperty("length").GetInt32();
            if (!header.GetProperty("ok").GetBoolean())
            {
                if (length != 0) throw new InvalidDataException("Resposta de erro Piper inválida.");
                throw new SynthesisFailedException();
            }
            if (length < 44 || length > MaxAudioBytes)
                throw new InvalidDataException("Tamanho de áudio Piper inválido.");
            var audio = new byte[length];
            await worker.StandardOutput.BaseStream.ReadExactlyAsync(audio, request.Token);
            if (!audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !audio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw new InvalidDataException("Formato de áudio Piper inválido.");
            return audio;
        }
        catch (SynthesisFailedException)
        {
            // A well-framed synthesis error leaves the loaded model usable.
            throw new InvalidOperationException("O Piper não conseguiu sintetizar este pedido.");
        }
        catch
        {
            // A canceled read must not leave a partial frame for the next caller.
            await StopWorkerAsync(graceful: false);
            throw;
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_worker is { HasExited: false }) return;
        await StopWorkerAsync(graceful: false);
        var info = new ProcessStartInfo(VoiceValidation.ExpandPath(_options.PythonExecutable))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        var script = VoiceValidation.ExpandPath(_options.WorkerScript);
        if (!Path.IsPathRooted(script)) script = Path.Combine(AppContext.BaseDirectory, script);
        foreach (var argument in new[] { "-u", script, "--model", VoiceValidation.ExpandPath(_options.ModelPath),
                     "--max-text-length", _options.MaxTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(argument);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        _worker = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o Piper.");
        // Always drain stderr, including model initialization, without retaining or
        // exposing process diagnostics/text in the HTTP response.
        _stderrDrain = _worker.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var ready = await ReadHeaderAsync(_worker.StandardOutput.BaseStream, token);
        if (!ready.RootElement.GetProperty("ready").GetBoolean() || ready.RootElement.GetProperty("protocol").GetInt32() != 1)
            throw new InvalidDataException("Protocolo Piper incompatível.");
        _logger.LogInformation("Worker Piper pronto, PID {Pid}", _worker.Id);
    }

    private static async Task<JsonDocument> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        // Do not use StreamReader: it can buffer bytes belonging to the WAV frame.
        var header = new byte[1024];
        var single = new byte[1];
        for (var count = 0; count < header.Length; count++)
        {
            if (await stream.ReadAsync(single, token) == 0)
                throw new EndOfStreamException("O worker Piper terminou antes da resposta.");
            if (single[0] == (byte)'\n') return JsonDocument.Parse(header.AsMemory(0, count));
            header[count] = single[0];
        }
        throw new InvalidDataException("Cabeçalho Piper demasiado grande.");
    }

    private async Task StopWorkerAsync(bool graceful)
    {
        var worker = _worker;
        var drain = _stderrDrain;
        _worker = null;
        _stderrDrain = null;
        if (worker is null) return;
        using (worker)
        {
            if (graceful && !worker.HasExited)
            {
                worker.StandardInput.Close();
                try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { /* Fall back to killing the process tree. */ }
            }
            if (!worker.HasExited)
            {
                try { worker.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Already exited. */ }
            }
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (drain is not null) await drain.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        base.Dispose();
    }

    private sealed class SynthesisFailedException : Exception;

    private sealed class PendingRequest : IDisposable
    {
        public string Text { get; }
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => _timeout.Token;
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenRegistration _registration;

        public PendingRequest(string text, CancellationToken caller, CancellationToken shutdown, int timeoutSeconds)
        {
            Text = text;
            _timeout = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown);
            _registration = Token.Register(() => Completion.TrySetCanceled(Token));
            _timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        public void Dispose()
        {
            _registration.Dispose();
            _timeout.Dispose();
        }
    }
}
