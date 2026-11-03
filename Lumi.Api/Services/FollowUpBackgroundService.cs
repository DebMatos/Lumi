
using Lumi.Api.Data;
using Lumi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumi.Api.Services;

public class FollowUpBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FollowUpBackgroundService> _logger;

    public FollowUpBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<FollowUpBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(5));

        try
        {
            // Verificar também ao arrancar.
            await ProcessFollowUpsAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessFollowUpsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal.
        }
    }

    private async Task ProcessFollowUpsAsync(
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<LumiDbContext>();

        var audio = scope.ServiceProvider
            .GetRequiredService<PcAudioService>();

        var now = DateTime.UtcNow;

        var pending = await db.MicroActionFollowUps
            .Include(f => f.MicroAction)
            .Where(f =>
                f.Status == FollowUpStatus.Scheduled &&
                f.DueAt <= now &&
                f.MicroAction.Status == MicroActionStatus.InProgress)
            .OrderBy(f => f.DueAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var followUp in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Marcar como vencido antes de reproduzir.
            followUp.Status = FollowUpStatus.Due;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                await audio.SpeakAsync(
                    $"Já passou o tempo combinado. " +
                    $"Conseguiste terminar: {followUp.MicroAction.Description}?",
                    cancellationToken);

                followUp.Status = FollowUpStatus.Delivered;
                followUp.DeliveredAt = DateTime.UtcNow;

                await db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Acompanhamento {Id} reproduzido.",
                    followUp.Id);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Falha na reprodução do acompanhamento {Id}.",
                    followUp.Id);
            }
        }
    }
}
