using System.ComponentModel.DataAnnotations;

namespace FancilPhones.Data;

public class Phone
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    [Required, MaxLength(64)]
    public string IpAddress { get; set; } = "";

    [MaxLength(64)]
    public string Username { get; set; } = "admin";

    [MaxLength(128)]
    public string Password { get; set; } = "admin";

    /// <summary>HTTP scheme: http or https.</summary>
    [MaxLength(8)]
    public string Scheme { get; set; } = "http";

    /// <summary>
    /// Path the phone's web UI uses to receive an uploaded phonebook (multipart form).
    /// Captured from the phone's Contacts -> Advanced -> Upload button via browser devtools.
    /// </summary>
    [MaxLength(256)]
    public string UploadPath { get; set; } = "/pBookAdv.htm";

    /// <summary>Form field name the upload endpoint expects for the file.</summary>
    [MaxLength(64)]
    public string UploadFieldName { get; set; } = "PHONEBOOK";

    public bool Enabled { get; set; } = true;

    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(32)]
    public string? LastSyncStatus { get; set; }

    public string? LastSyncMessage { get; set; }
}
