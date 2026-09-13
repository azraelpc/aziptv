using System;
using System.IO;
using System.Text;

namespace AzIPTV;

public sealed class InvalidM3uContentException : Exception
{
    public InvalidM3uContentException(string sourceName, string reason)
        : base($"Invalid M3U content from {sourceName}: {reason}")
    {
        SourceName = sourceName;
        Reason = reason;
    }

    public string SourceName { get; }
    public string Reason { get; }
}

public static class InvalidM3uContent
{
    public const int MinimumLength = 15;

    private static readonly string[] ValidM3uMarkers =
    {
        "#EXTM3U",
        "#EXTINF",
    };

    private static readonly string[] InvalidMarkers =
    {
        "acceso bloqueado",
        "por causas ajenas a",
        "esta web no est\u00e1 disponible",
        "esta web no esta disponible",
    };

    public static void EnsureValid(byte[] bytes, string sourceName)
    {
        if (TryGetInvalidReason(bytes, out var reason))
            throw new InvalidM3uContentException(sourceName, reason);
    }

    public static bool TryGetInvalidReason(byte[] bytes, out string reason)
        => TryGetInvalidReason(Decode(bytes), out reason);

    public static bool TryGetInvalidReason(string? text, out string reason)
    {
        var trimmed = (text ?? string.Empty).Trim();

        // Avoid false positives: explicit M3U tags take priority.
        foreach (var marker in ValidM3uMarkers)
        {
            if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                reason = string.Empty;
                return false;
            }
        }

        if (trimmed.Length < MinimumLength)
        {
            reason = $"Content shorter than {MinimumLength} characters.";
            return true;
        }

        foreach (var marker in InvalidMarkers)
        {
            if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Matched marker: {marker}";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        return Encoding.UTF8.GetString(bytes);
    }
}