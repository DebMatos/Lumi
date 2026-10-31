
using System.Diagnostics;

namespace Lumi.Api.Services;

public class PiperSpeechService : ISpeechService
{
    private readonly string _piperPath;
    private readonly string _modelPath;

    public PiperSpeechService(IConfiguration configuration)
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        _piperPath = configuration["Speech:PiperExecutable"]
            ?? Path.Combine(home, "Projetos/piper/.venv/bin/piper");

        _modelPath = configuration["Speech:ModelPath"]
            ?? Path.Combine(home, "Projetos/piper/pt_PT-tugao-medium.onnx");
    }

    public async Task<byte[]> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("O texto não pode estar vazio.");

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"lumi-speech-{Guid.NewGuid():N}.wav");

        try
        {
            var startInfo = new ProcessStartInfo(_piperPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_modelPath);
            startInfo.ArgumentList.Add("--output_file");
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Não foi possível iniciar o Piper.");

            var outputTask = process.StandardOutput.ReadToEndAsync(
                cancellationToken);

            var errorTask = process.StandardError.ReadToEndAsync(
                cancellationToken);

            await process.StandardInput.WriteLineAsync(
                text.AsMemory(), cancellationToken);

            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken);

            await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Erro do Piper: {error}");

            return await File.ReadAllBytesAsync(
                outputPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }
}
