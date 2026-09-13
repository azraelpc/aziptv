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
/// Incremental parse progress for large playlists.
/// </summary>
public readonly record struct ParseProgressUpdate(int ParsedChannels, double Fraction);

/// <summary>
/// High-performance M3U/M3U8 parser.
/// Parses synchronously inside Task.Run so the UI thread is never blocked
/// and continuations always return to the caller's SynchronizationContext.
/// </summary>
public static class M3uParser
{
    public static Task<ParseResult> ParseAsync(
        Stream stream,
        bool removeDuplicates = false,
        IProgress<ParseProgressUpdate>? progress = null) =>
        Task.Run(() => ParseSync(stream, removeDuplicates, progress));

    private static ParseResult ParseSync(Stream stream, bool removeDuplicates, IProgress<ParseProgressUpdate>? progress)
    {
        int capacity = stream.CanSeek
            ? (int)Math.Min(stream.Length / 80L, 50_000L)
            : 512;
        var channels = new List<Channel>(capacity);
        var groups   = new Dictionary<string, List<Channel>>(
                           StringComparer.OrdinalIgnoreCase);
        var seenUrls = removeDuplicates
            ? new HashSet<string>(capacity, StringComparer.Ordinal) : null;

        using var reader = new StreamReader(stream, bufferSize: 65536, leaveOpen: true);

        string? extinf = null;
        string? line;
        int lineCount = 0;
        int parsedChannels = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineCount++;
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
                // Skip duplicate URLs in O(1) using the pre-allocated HashSet.
                if (seenUrls is not null && !seenUrls.Add(channel.Url))
                {
                    extinf = null;
                    continue;
                }
                channels.Add(channel);
                parsedChannels++;

                bool anyGroup = AddGroups(groups, rawGroup, channel);

                if (!anyGroup)
                {
                    const string uncategorized = "Uncategorized";
                    if (!groups.TryGetValue(uncategorized, out var list))
                        groups[uncategorized] = list = new List<Channel>();
                    list.Add(channel);
                }

                extinf = null;
            }

            if (progress is not null && (lineCount & 0xFF) == 0)
                ReportProgress(stream, parsedChannels, progress);
        }

        progress?.Report(new ParseProgressUpdate(parsedChannels, 1.0));

        return new ParseResult(channels, groups);
    }

    private static void ReportProgress(Stream stream, int parsedChannels, IProgress<ParseProgressUpdate> progress)
    {
        try
        {
            double pct = 0d;
            if (stream.CanSeek && stream.Length > 0)
            {
                pct = (double)stream.Position / stream.Length;
            }
            else if (stream is ProgressReadStream tracked && tracked.TotalBytes > 0)
            {
                pct = (double)tracked.BytesRead / tracked.TotalBytes;
            }
            else
            {
                return;
            }

            progress.Report(new ParseProgressUpdate(parsedChannels, Math.Clamp(pct, 0d, 1d)));
        }
        catch
        {
            // Progress should never break parsing.
        }
    }

    private static bool AddGroups(Dictionary<string, List<Channel>> groups, string rawGroup, Channel channel)
    {
        var span = rawGroup.AsSpan();
        if (span.Length == 0) return false;

        bool anyGroup = false;
        int start = 0;
        while (start <= span.Length)
        {
            int sep = span[start..].IndexOf(';');
            ReadOnlySpan<char> part;
            if (sep < 0)
            {
                part = span[start..].Trim();
                start = span.Length + 1;
            }
            else
            {
                part = span.Slice(start, sep).Trim();
                start += sep + 1;
            }

            if (part.Length == 0) continue;
            anyGroup = true;
            var groupName = part.ToString();
            if (!groups.TryGetValue(groupName, out var list))
                groups[groupName] = list = new List<Channel>();
            list.Add(channel);
        }

        return anyGroup;
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
