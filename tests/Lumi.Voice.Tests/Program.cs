using System.Diagnostics;
using System.Text;
using Lumi.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}
static async Task Throws<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception($"Expected {typeof(T).Name}: {name}");
}
static IFormFile Audio(byte[] data, string mime = "audio/webm") =>
    new FormFile(new MemoryStream(data), 0, data.Length, "audio", "audio.webm") { Headers = new HeaderDictionary(), ContentType = mime };

await Throws<ArgumentException>(() => { VoiceValidation.Text(" ", 4); return Task.CompletedTask; }, "empty TTS");
await Throws<ArgumentException>(() => { VoiceValidation.Text("12345", 4); return Task.CompletedTask; }, "TTS limit");
VoiceValidation.Text("1234", 4);
await Throws<ArgumentException>(() => { VoiceValidation.Audio(Audio([]), 10); return Task.CompletedTask; }, "empty upload");
await Throws<ArgumentException>(() => { VoiceValidation.Audio(Audio(new byte[11]), 10); return Task.CompletedTask; }, "oversize upload");
await Throws<ArgumentException>(() => { VoiceValidation.Audio(Audio([1], "text/plain"), 10); return Task.CompletedTask; }, "unsupported MIME");
VoiceValidation.Audio(Audio([1], "audio/webm;codecs=opus"), 10);
Check(VoiceValidation.ExpandPath("~/test").StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "home path expansion");

if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux process/fixture tests"); return; }
var root = Path.Combine(Path.GetTempPath(), $"lumi-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var marker = Path.Combine(root, "late");
    using (var cts = new CancellationTokenSource(150))
    {
        var watch = Stopwatch.StartNew();
        await Throws<OperationCanceledException>(() => VoiceProcess.RunAsync("/bin/sh",
            ["-c", "sleep 1; touch \"$1\"", "test", marker], null, cts.Token), "cancel running process");
        Check(watch.Elapsed < TimeSpan.FromSeconds(3), "cancellation returns promptly");
    }
    await Task.Delay(1200);
    Check(!File.Exists(marker), "cancel kills descendants");
    using (var cts = new CancellationTokenSource())
    {
        cts.Cancel();
        await Throws<OperationCanceledException>(() => VoiceProcess.RunAsync("does-not-exist", [], null, cts.Token), "cancel before spawn");
    }
    var output = await VoiceProcess.RunAsync("/bin/sh", ["-c", "head -c 100000 /dev/zero; head -c 100000 /dev/zero >&2"], null, default);
    Check(output.Length == 65536, "drain both pipes with bounded retention");

    await PersistentPiperTests.RunAsync(root);
    string generated;

    // Real ffmpeg fixture, fake Whisper: verifies actual decoded duration, not MIME metadata.
    var whisper = Path.Combine(root, "whisper");
    await File.WriteAllTextAsync(whisper, "#!/bin/sh\necho 'teste de voz'\n");
    File.SetUnixFileMode(whisper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var transcription = new TranscriptionService(Options.Create(new WhisperOptions { Executable = whisper, ModelPath = "unused", MaxDurationSeconds = 1 }));
    var wav = Path.Combine(root, "fixture.wav");
    await VoiceProcess.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "0.2", wav], null, default);
    Check(await transcription.TranscribeAsync(Audio(await File.ReadAllBytesAsync(wav), "audio/wav"), default) == "teste de voz", "transcription compatible response");
    foreach (var (extension, mime) in new[] { ("webm", "audio/webm"), ("ogg", "audio/ogg"), ("mp4", "audio/mp4"), ("mp3", "audio/mpeg") })
    {
        var encoded = Path.Combine(root, "fixture." + extension);
        await VoiceProcess.RunAsync("ffmpeg", ["-v", "error", "-i", wav, encoded], null, default);
        Check(await transcription.TranscribeAsync(Audio(await File.ReadAllBytesAsync(encoded), mime), default) == "teste de voz", $"decode {extension}");
    }
    await VoiceProcess.RunAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "2", wav], null, default);
    await Throws<ArgumentException>(() => transcription.TranscribeAsync(Audio(File.ReadAllBytes(wav), "audio/wav"), default), "decoded duration limit");
    await Throws<ArgumentException>(() => transcription.TranscribeAsync(Audio(Encoding.UTF8.GetBytes("not audio")), default), "invalid audio content");

    var converter = Path.Combine(root, "converter");
    var conversionRecord = Path.Combine(root, "conversion-path");
    await File.WriteAllTextAsync(converter, "#!/bin/sh\nfor arg do last=\"$arg\"; done\nprintf '%s' \"$last\" > \"$LUMI_TEST_PATH_RECORD\"\nsleep 10\n");
    File.SetUnixFileMode(converter, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    Environment.SetEnvironmentVariable("LUMI_TEST_PATH_RECORD", conversionRecord);
    try
    {
        var slow = new TranscriptionService(Options.Create(new WhisperOptions
        { Executable = whisper, ModelPath = "unused", FfmpegExecutable = converter, TimeoutSeconds = 1 }));
        await Throws<TimeoutException>(() => slow.TranscribeAsync(Audio([1]), default), "transcription timeout");
        generated = await File.ReadAllTextAsync(conversionRecord);
        Check(!Directory.Exists(Path.GetDirectoryName(generated)), "transcription timeout cleanup");
        using var cts = new CancellationTokenSource(100);
        await Throws<OperationCanceledException>(() => slow.TranscribeAsync(Audio([1]), cts.Token), "transcription caller cancellation");
        generated = await File.ReadAllTextAsync(conversionRecord);
        Check(!Directory.Exists(Path.GetDirectoryName(generated)), "transcription cancellation cleanup");
    }
    finally { Environment.SetEnvironmentVariable("LUMI_TEST_PATH_RECORD", null); }
}
finally { Directory.Delete(root, true); }
