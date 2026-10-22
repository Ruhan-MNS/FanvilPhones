using System.ComponentModel.DataAnnotations;

namespace FancilPhones.Domain;

public sealed class SwitchboardProfile
{
    public int PhoneId { get; set; }
    public Phone Phone { get; set; } = null!;
    public string BaselineJson { get; set; } = "";
    public string? DraftJson { get; set; }
    public Guid DraftRevision { get; set; } = Guid.NewGuid();
    public bool SortAlphabetically { get; set; } = true;
    public DateTime CapturedAt { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseUntil { get; set; }
}

public sealed class SwitchboardMember
{
    public int ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    [MaxLength(128)] public string? SortName { get; set; }
}

public sealed class SwitchboardOverride
{
    public int PhoneId { get; set; }
    public Phone Phone { get; set; } = null!;
    public int ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    [MaxLength(31)] public string? Extension { get; set; }
    [MaxLength(31)] public string? Value2 { get; set; }
    public string? CapturedKeyJson { get; set; }
}

// Each preview is immutable server-side data. BeforeJson is also the restoration backup.
public sealed class SwitchboardDeployment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int PhoneId { get; set; }
    public Phone Phone { get; set; } = null!;
    [MaxLength(64)] public string IpAddress { get; set; } = "";
    public Guid ProfileRevision { get; set; }
    public int? SourcePhoneId { get; set; }
    public Guid? SourceRevision { get; set; }
    [MaxLength(64)] public string DirectoryHash { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string TargetJson { get; set; } = "";
    [MaxLength(32)] public string Status { get; set; } = "Preview";
    [MaxLength(32)] public string Purpose { get; set; } = "Layout";
    [MaxLength(512)] public string Message { get; set; } = "Preview ready";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
