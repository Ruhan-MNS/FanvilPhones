using System.Globalization;

namespace FancilPhones.UI;

public static class UiFormat
{
    public static string Tone(string? status) => status switch
    {
        "Success" => "success", "Failed" or "Error" => "danger",
        "Running" => "info", "Pending" => "warning", _ => "neutral"
    };

    public static string Date(DateTime? value) => value?.ToLocalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture) ?? "Never";
    public static string Duration(DateTime started, DateTime? finished) => finished is null ? "In progress"
        : (finished.Value - started).TotalSeconds < 1 ? $"{(finished.Value - started).TotalMilliseconds:0} ms"
        : $"{(finished.Value - started).TotalSeconds:0.0} s";
}
