using System.ComponentModel.DataAnnotations;

namespace FancilPhones.Application;

// Passwords are write-only. A blank replacement preserves the handset's secret.
public sealed class SipLineSettings
{
    [MaxLength(63)] public string DisplayName { get; set; } = "";
    [MaxLength(39)] public string Username { get; set; } = "";
    [MaxLength(199)] public string AuthenticationUser { get; set; } = "";
    [MaxLength(199), RegularExpression(@"[\u0000-\u00FF]*", ErrorMessage = "The replacement password contains characters this handset cannot encode.")]
    public string? ReplacementPassword { get; set; }
    [MaxLength(63)] public string Realm { get; set; } = "";
    [MaxLength(63)] public string ServerName { get; set; } = "";
    [MaxLength(255)] public string ServerAddress { get; set; } = "";
    [Range(1, 65535)] public int ServerPort { get; set; } = 5060;
    [MaxLength(255)] public string BackupServerAddress { get; set; } = "";
    [Range(1, 65535)] public int BackupServerPort { get; set; } = 5060;
    public bool RegistrationEnabled { get; set; }

    public SipLineSettings Copy() => (SipLineSettings)MemberwiseClone();
}

public sealed record SipSettingsSnapshot(int PhoneId, int LineIndex, string Fingerprint,
    string LineStatus, SipLineSettings Settings);
