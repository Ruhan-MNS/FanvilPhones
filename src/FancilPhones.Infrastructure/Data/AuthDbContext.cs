using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Infrastructure.Data;

// Same user model and schema version as BIDevHub. Schema/provisioning belong to ASP_AUTH.
public class AppUser : IdentityUser;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<AppUser>(options);
