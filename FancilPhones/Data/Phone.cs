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

    /// <summary>
    /// Optional SIP "Display Name" pushed to the handset's lines.htm form
    /// (SIP_DisPlayName_R). Used by the "Push display name" action on the
    /// Phones page — does not affect the regular phonebook sync.
    /// </summary>
    [MaxLength(64)]
    public string? SipDisplayName { get; set; }

    /// <summary>
    /// Which SIP line on the phone (1-based) the display-name push targets.
    /// Most handsets only use line 1.
    /// </summary>
    public int SipLineIndex { get; set; } = 1;

    /// <summary>
    /// Optional SIP extension number pushed to <c>SIP_PhoneNum_R</c> AND
    /// <c>SIP_RegUser_R</c> on the phone's lines.htm form (the phone's UI
    /// labels these "Username" and "Authentication User" — both normally hold
    /// the same extension, e.g. "144").
    /// </summary>
    [MaxLength(32)]
    public string? SipExtension { get; set; }

    /// <summary>
    /// Whether the push action should enable SIP registration on the line
    /// (the "Activate" checkbox in the phone's web UI, field
    /// <c>SIP_EnableSipReg_RW</c>). True = include the field as checked;
    /// false = omit it (Fanvil interprets an absent checkbox as unchecked
    /// when its name is listed in CheckBoxManager).
    /// </summary>
    public bool SipRegistrationEnabled { get; set; } = true;

    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(32)]
    public string? LastSyncStatus { get; set; }

    public string? LastSyncMessage { get; set; }
}
