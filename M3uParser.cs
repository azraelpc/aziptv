using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AzIPTV;

/// <summary>
/// Result of parsing an M3U/M3U8 playlist.
/// <para>
/// <see cref="Channels"/> is the flat ordered list of all channels.
/// <see cref="Groups"/> maps each group name to the channels that belong to it;
/// a channel with multiple semicolon-separated group-titles appears in each
/// group but is the same object reference (no cloning).
/// Channels with no group-title are placed under "Uncategorized".
/// </para>
/// </summary>
public sealed record ParseResult(
    List<Channel> Channels,
    Dictionary<string, List<Channel>> Groups);

/// <summary>
/// High-performance M3U/M3U8 parser.
/// Parses synchronously inside Task.Run so the UI thread is never blocked
/// and continuations always return to the caller's SynchronizationContext.
/// </summary>
public static class M3uParser
{
    public static Task<ParseResult> ParseAsync(Stream stream) =>
        Task.Run(() => ParseSync(stream));

    private static ParseResult ParseSync(Stream stream)
    {
        int capacity = stream.CanSeek
            ? (int)Math.Min(stream.Length / 80L, 50_000L)
            : 512;
        var channels = new List<Channel>(capacity);
        var groups   = new Dictionary<string, List<Channel>>(
                           StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(stream, bufferSize: 65536, leaveOpen: true);

        string? extinf = null;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
            {
                extinf = line;
            }
            else if (extinf is not null && line[0] != '#')
            {
                string name     = ExtractName(extinf.AsSpan());
                string rawGroup = ExtractAttribute(extinf.AsSpan(), "group-title");
                string logo     = ExtractAttribute(extinf.AsSpan(), "tvg-logo");

                var channel = new Channel(name, line.Trim(), rawGroup, logo);
                channels.Add(channel);

                // Split by ';' to support multiple group memberships.
                bool anyGroup = false;
                foreach (var part in rawGroup.Split(';'))
                {
                    var groupName = part.Trim();
                    if (groupName.Length == 0) continue;
                    anyGroup = true;
                    if (!groups.TryGetValue(groupName, out var list))
                        groups[groupName] = list = new List<Channel>();
                    list.Add(channel);
                }

                if (!anyGroup)
                {
                    const string uncategorized = "Uncategorized";
                    if (!groups.TryGetValue(uncategorized, out var list))
                        groups[uncategorized] = list = new List<Channel>();
                    list.Add(channel);
                }

                extinf = null;
            }
        }

        return new ParseResult(channels, groups);
    }

    // -- Span helpers ---------------------------------------------------------

    private static string ExtractName(ReadOnlySpan<char> span)
    {
        int comma = span.LastIndexOf(',');
        return comma >= 0 && comma + 1 < span.Length
            ? span[(comma + 1)..].Trim().ToString()
            : string.Empty;
    }

    private static string ExtractAttribute(ReadOnlySpan<char> span, string attr)
    {
        int idx = span.IndexOf(attr.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;

        idx += attr.Length;
        if (idx >= span.Length || span[idx] != '=') return string.Empty;
        idx++;

        if (idx >= span.Length) return string.Empty;
        char q = span[idx];
        if (q != '"' && q != '\'') return string.Empty;
        idx++;

        int end = span[idx..].IndexOf(q);
        return end < 0 ? string.Empty : span[idx..(idx + end)].ToString();
    }
}
