using Microsoft.Extensions.Options;

namespace MNS.SingleSignOn;

public sealed class SingleSignOnOptions
{
    public const string Section = "SingleSignOn";
    public const string CookieName = "__Secure-MNS.SingleSignOn";
    public const string ApplicationName = "MNS.SharedIdentity.v1";
    public const string SessionClaim = "mns:sso-session";
    public bool Enabled { get; set; }
    public string CookieDomain { get; set; } = ".multinetsystems.co.za";
    public string KeyRingPath { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    public string CertificatePath { get; set; } = "";
    public string? CertificatePassword { get; set; }
    public int IdleMinutes { get; set; } = 180;
    public int MaximumSessionHours { get; set; } = 12;
    public int CircuitRevalidationSeconds { get; set; } = 30;

    public void Validate(string contentRoot)
    {
        if (!Enabled) return;
        var errors = new List<string>();
        var domain = CookieDomain ?? "";
        if (domain.StartsWith('.')) domain = domain[1..];
        if (domain.Split('.').Length < 2 || Uri.CheckHostName(domain) != UriHostNameType.Dns
            || string.IsNullOrWhiteSpace(CookieDomain) || CookieDomain.Any(char.IsWhiteSpace) || CookieDomain.Any(char.IsControl))
            errors.Add("SingleSignOn:CookieDomain must be a DNS parent domain without a scheme, path or port.");
        if (string.IsNullOrWhiteSpace(KeyRingPath) || !Path.IsPathFullyQualified(KeyRingPath))
            errors.Add("SingleSignOn:KeyRingPath must be an absolute shared directory outside the published application.");
        else if (InsideSite(KeyRingPath, contentRoot))
            errors.Add("SingleSignOn:KeyRingPath must be outside the published application.");
        var thumbprint = !string.IsNullOrWhiteSpace(CertificateThumbprint);
        var certificateFile = !string.IsNullOrWhiteSpace(CertificatePath);
        if (thumbprint == certificateFile)
            errors.Add("Set exactly one of SingleSignOn:CertificateThumbprint or SingleSignOn:CertificatePath to protect shared keys at rest.");
        if (certificateFile && !Path.IsPathFullyQualified(CertificatePath))
            errors.Add("SingleSignOn:CertificatePath must be an absolute path.");
        if (IdleMinutes is < 5 or > 1440 || MaximumSessionHours is < 1 or > 168 || IdleMinutes > MaximumSessionHours * 60)
            errors.Add("SingleSignOn session limits are invalid; idle duration must not exceed the maximum session lifetime.");
        if (CircuitRevalidationSeconds is < 15 or > 300)
            errors.Add("SingleSignOn:CircuitRevalidationSeconds must be between 15 and 300.");
        if (errors.Count > 0) throw new OptionsValidationException(Section, typeof(SingleSignOnOptions), errors);
    }

    private static bool InsideSite(string path, string contentRoot)
    {
        var site = Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .StartsWith(site, StringComparison.OrdinalIgnoreCase);
    }
}
