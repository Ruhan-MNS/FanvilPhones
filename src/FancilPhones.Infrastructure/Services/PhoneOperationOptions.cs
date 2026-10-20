using System.ComponentModel.DataAnnotations;

namespace FancilPhones.Infrastructure.Services;

public sealed class PhoneOperationOptions
{
    [Range(1, 32)] public int MaxConcurrentSyncs { get; set; } = 8;
    [Range(5, 600)] public int TimeoutSeconds { get; set; } = 60;
    [Range(1, 15)] public int CleanupTimeoutSeconds { get; set; } = 3;
}
