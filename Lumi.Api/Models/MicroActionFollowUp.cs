
namespace Lumi.Api.Models;

public enum FollowUpStatus
{
    Scheduled,
    Due,
    Delivered,
    Cancelled
}

public class MicroActionFollowUp
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MicroActionId { get; set; }
    public MicroAction MicroAction { get; set; } = null!;

    public DateTime DueAt { get; set; }
    public int IntervalMinutes { get; set; }

    public FollowUpStatus Status { get; set; }
        = FollowUpStatus.Scheduled;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
}
