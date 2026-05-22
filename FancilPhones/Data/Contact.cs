using System.ComponentModel.DataAnnotations;

namespace FancilPhones.Data;

public class Contact
{
    public int Id { get; set; }

    [Required, MaxLength(128)]
    public string DisplayName { get; set; } = "";

    [MaxLength(64)]
    public string? OfficeNumber { get; set; }

    [MaxLength(64)]
    public string? MobileNumber { get; set; }

    [MaxLength(64)]
    public string? OtherNumber { get; set; }

    [MaxLength(64)]
    public string? Group { get; set; }

    [MaxLength(16)]
    public string? Ring { get; set; } = "Auto";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
