
using Lumi.Api.Data;
using Lumi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumi.Api.Services;

public class MicroActionService
{
    private readonly LumiDbContext _db;

    public MicroActionService(LumiDbContext db)
    {
        _db = db;
    }

    public async Task<MicroAction> StartAsync(
        string description,
        int followUpMinutes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException(
                "A descrição é obrigatória.");

        if (followUpMinutes < 1 || followUpMinutes > 1440)
            throw new ArgumentOutOfRangeException(
                nameof(followUpMinutes),
                "O intervalo deve ser entre 1 e 1440 minutos.");

        var now = DateTime.UtcNow;

        var action = new MicroAction
        {
            Description = description.Trim(),
            Status = MicroActionStatus.InProgress,
            StartedAt = now,
            FollowUps =
            {
                new MicroActionFollowUp
                {
                    DueAt = now.AddMinutes(followUpMinutes),
                    IntervalMinutes = followUpMinutes,
                    Status = FollowUpStatus.Scheduled
                }
            }
        };

        _db.MicroActions.Add(action);

        await _db.SaveChangesAsync(cancellationToken);

        return action;
    }
    public async Task<bool> CompleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var action = await _db.MicroActions.FindAsync(
            [id], cancellationToken);

        if (action is null ||
            action.Status is MicroActionStatus.Completed
                or MicroActionStatus.Cancelled)
            return false;

        action.Status = MicroActionStatus.Completed;
        action.CompletedAt = DateTime.UtcNow;

        var pending = await _db.MicroActionFollowUps
            .Where(f => f.MicroActionId == id &&
                (f.Status == FollowUpStatus.Scheduled ||
                 f.Status == FollowUpStatus.Due))
            .ToListAsync(cancellationToken);

        foreach (var followUp in pending)
            followUp.Status = FollowUpStatus.Cancelled;

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

}

