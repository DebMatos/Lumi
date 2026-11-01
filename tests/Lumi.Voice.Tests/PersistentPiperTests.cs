using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lumi.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

static class PersistentPiperTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine($"PASS {name}");
    }

    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception($"Expected {typeof(T).Name}: {name}");
    }

    public static async Task RunAsync(string root)
    {
        var model = Path.Combine(root, "worker-model");
        var fixture = Path.GetFullPath("tests/fixtures/fake_piper_worker.py");
        var options = new SpeechOptions { PythonExecutable = "python3", WorkerScript = fixture,
            ModelPath = model, QueueCapacity = 3, TimeoutSeconds = 3 };
        using var service = new PiperSpeechService(Options.Create(options), NullLogger<PiperSpeechService>.Instance);
        await service.StartAsync(default);
        try
        {
            var first = await service.SynthesizeAsync("olá\ntexto");
            Check(Encoding.UTF8.GetString(first).Contains("olá\ntexto"), "framed Unicode/newline request");
            await service.SynthesizeAsync("segundo");
            Check(Events(model).Count(e => e.GetProperty("event").GetString() == "start") == 1, "consecutive requests load once");
            await service.SynthesizeAsync("stderr");
            Check(true, "worker stderr drained without deadlock");
            await Throws<InvalidOperationException>(() => service.SynthesizeAsync("failure"), "framed synthesis error");
            await service.SynthesizeAsync("after error");
            Check(Starts(model) == 1, "framed error keeps the model loaded");

            // Wait for the active request before filling exactly three waiting slots.
            var active = service.SynthesizeAsync("delay:300");
            await WaitForRequest(model, "delay:300");
            var queued = new[] { service.SynthesizeAsync("fila 1"), service.SynthesizeAsync("fila 2"), service.SynthesizeAsync("fila 3") };
            await Throws<InvalidOperationException>(() => service.SynthesizeAsync("overflow"), "bounded queue refuses overflow");
            await active;
            var outputs = await Task.WhenAll(queued);
            for (var index = 0; index < outputs.Length; index++)
                Check(Encoding.UTF8.GetString(outputs[index]).Contains($"fila {index + 1}"), "queued reply belongs to its caller");
            var order = Events(model).Where(e => e.GetProperty("event").GetString() == "request")
                .Select(e => e.GetProperty("text").GetString()).Where(t => t!.StartsWith("fila")).ToArray();
            Check(order.SequenceEqual(new[] { "fila 1", "fila 2", "fila 3" }), "FIFO simultaneous requests");
            Check(Starts(model) == 1, "simultaneous requests share the same worker");

            active = service.SynthesizeAsync("delay:400");
            await WaitForRequest(model, "delay:400");
            using (var cancel = new CancellationTokenSource())
            {
                var waiting = service.SynthesizeAsync("canceled queued", cancel.Token);
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => waiting, "queued cancellation returns promptly");
            }
            await active;
            await service.SynthesizeAsync("after queued cancellation");
            Check(Starts(model) == 1, "queued cancellation preserves active worker");
            Check(!Events(model).Any(e => e.TryGetProperty("text", out var text) && text.GetString() == "canceled queued"), "canceled queued job never synthesized");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => service.SynthesizeAsync("never queued", cancel.Token), "cancellation before enqueue");
            }

            var oldPid = Events(model).First().GetProperty("pid").GetInt32();
            using (var cancel = new CancellationTokenSource())
            {
                var held = service.SynthesizeAsync("hold", cancel.Token);
                await WaitForRequest(model, "hold");
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => held, "active cancellation");
            }
            await service.SynthesizeAsync("after active cancellation");
            Check(Starts(model) == 2 && Exited(oldPid), "active cancellation kills old worker and reloads once");
            await service.SynthesizeAsync("reuse restarted worker");
            Check(Starts(model) == 2, "restarted worker stays loaded");

            foreach (var fault in new[] { "crash", "bad-id", "bad-length", "bad-header", "truncated" })
            {
                var starts = Starts(model);
                await Throws<InvalidOperationException>(() => service.SynthesizeAsync(fault), $"worker fault: {fault}");
                await service.SynthesizeAsync("recovered");
                Check(Starts(model) == starts + 1, $"restart after {fault}");
            }
        }
        finally { await service.StopAsync(default); }
        var lastPid = Events(model).Last().GetProperty("pid").GetInt32();
        Check(Exited(lastPid) && Events(model).Last().GetProperty("event").GetString() == "eof", "idle shutdown sends EOF and reaps worker");
        await Throws<OperationCanceledException>(() => service.SynthesizeAsync("after shutdown"), "shutdown rejects new work");
        service.Dispose();
        service.Dispose();
        Check(true, "shared hosted service disposal is idempotent");

        var timeoutModel = Path.Combine(root, "timeout-model");
        options = new SpeechOptions { PythonExecutable = "python3", WorkerScript = fixture, ModelPath = timeoutModel,
            TimeoutSeconds = 1, QueueCapacity = 3 };
        using var slow = new PiperSpeechService(Options.Create(options), NullLogger<PiperSpeechService>.Instance);
        await slow.StartAsync(default);
        await Throws<TimeoutException>(() => slow.SynthesizeAsync("timeout"), "worker timeout");
        await slow.SynthesizeAsync("after timeout");
        Check(Starts(timeoutModel) == 2, "timeout resets framing and recovers");
        var heldDuringShutdown = slow.SynthesizeAsync("hold");
        await WaitForRequest(timeoutModel, "hold");
        var pendingDuringShutdown = slow.SynthesizeAsync("pending at shutdown");
        await slow.StopAsync(default);
        await Throws<OperationCanceledException>(() => heldDuringShutdown, "shutdown cancels active request");
        await Throws<OperationCanceledException>(() => pendingDuringShutdown, "shutdown cancels waiting request");
        Check(Exited(Events(timeoutModel).Last().GetProperty("pid").GetInt32()), "shutdown reaps active worker");

        var badOptions = new SpeechOptions { PythonExecutable = "python3", WorkerScript = fixture,
            ModelPath = Path.Combine(root, "boot-failure"), TimeoutSeconds = 1 };
        using var bad = new PiperSpeechService(Options.Create(badOptions), NullLogger<PiperSpeechService>.Instance);
        await bad.StartAsync(default);
        await Throws<InvalidOperationException>(() => bad.SynthesizeAsync("startup error"), "startup failure propagates safely");
        await bad.StopAsync(default);

        var budgetModel = Path.Combine(root, "queue-budget");
        var budgetOptions = new SpeechOptions { PythonExecutable = "python3", WorkerScript = fixture,
            ModelPath = budgetModel, TimeoutSeconds = 2 };
        using var budget = new PiperSpeechService(Options.Create(budgetOptions), NullLogger<PiperSpeechService>.Instance);
        await budget.StartAsync(default);
        try
        {
            var active = budget.SynthesizeAsync("delay:1500");
            await WaitForRequest(budgetModel, "delay:1500");
            var watch = Stopwatch.StartNew();
            var waiting = budget.SynthesizeAsync("delay:1501");
            await active;
            await Throws<TimeoutException>(() => waiting, "timeout includes queue waiting time");
            Check(watch.Elapsed < TimeSpan.FromSeconds(2.8), "queue wait does not renew timeout budget");
            await budget.SynthesizeAsync("recovered after queue timeout");
        }
        finally { await budget.StopAsync(default); }
    }

    private static JsonElement[] Events(string model) => File.Exists(model + ".events")
        ? File.ReadAllLines(model + ".events").Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray()
        : [];
    private static int Starts(string model) => Events(model).Count(e => e.GetProperty("event").GetString() == "start");
    private static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private static async Task WaitForRequest(string model, string text)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (Events(model).Any(e => e.TryGetProperty("text", out var value) && value.GetString() == text)) return;
            await Task.Delay(10);
        }
        throw new Exception("Worker never received: " + text);
    }
}
