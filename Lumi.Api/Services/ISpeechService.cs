namespace Lumi.Api.Services;

public interface ISpeechService
{
    Task<byte[]> SynthesizeAsync(
        string text,
        CancellationToken cancellationToken = default);
}