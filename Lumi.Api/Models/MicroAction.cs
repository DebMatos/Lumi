
namespace Lumi.Api.Models;

public enum MicroActionStatus
{
    Pending,
    InProgress,
    Paused,
    Completed,
    Cancelled
}

public class MicroAction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Description { get; set; } = string.Empty;

    public MicroActionStatus Status { get; set; }
        = MicroActionStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<MicroActionFollowUp> FollowUps { get; set; } = new();
}
