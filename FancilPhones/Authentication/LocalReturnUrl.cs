namespace FancilPhones.Authentication;

public static class LocalReturnUrl
{
    public static bool IsLocal(string? url) => !string.IsNullOrWhiteSpace(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);
}
