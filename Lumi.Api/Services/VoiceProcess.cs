using System.Diagnostics;

namespace Lumi.Api.Services;

// One owner for process lifetime: cancellation always kills and reaps the tree
// before callers remove temporary files. Both pipes are drained concurrently.
public static class VoiceProcess
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        string? input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(VoiceValidation.ExpandPath(executable))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Não foi possível iniciar o serviço de voz.");
        // Drain without retaining potentially unbounded diagnostic output.
        var output = ReadBoundedAsync(process.StandardOutput, 65536);
        var error = ReadBoundedAsync(process.StandardError, 4096);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteLineAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
            var result = await output;
            await error;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("O processo de voz terminou sem sucesso.");
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Already exited. */ }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < limit)
                result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        return result.ToString();
    }
}
