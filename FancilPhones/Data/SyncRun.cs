using System.ComponentModel.DataAnnotations;

namespace FancilPhones.Data;

public class SyncRun
{
    public int Id { get; set; }

    public int PhoneId { get; set; }
    public Phone? Phone { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "Pending";

    /// <summary>What kind of action this row represents: "Sync" (phonebook
    /// upload), "Push" (SIP line settings push), "Probe" (reachability test).</summary>
    [MaxLength(16)]
    public string Action { get; set; } = "Sync";

    public int? HttpStatusCode { get; set; }

    public string? Message { get; set; }

    public int ContactCount { get; set; }
}
