using Microsoft.AspNetCore.Identity;

namespace FancilPhones.Data;

public class AppUser : IdentityUser
{
    /// <summary>True if the user must change their password on next login (seeded admin / reset by admin).</summary>
    public bool MustChangePassword { get; set; }
}
