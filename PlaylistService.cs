using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace AzIPTV;

/// <summary>A named URL entry in the user's playlist history.</summary>
public sealed record UrlHistoryEntry(string Url, string Name);

public static class PlaylistService
{
    // Single shared HttpClient — avoids socket exhaustion on repeated calls.
    private static readonly HttpClient Http;

    static PlaylistService()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VLC");
    }

    private static string IniPath =>
        Path.Combine(AppContext.BaseDirectory, "user.ini");

    private static string CacheDir =>
        Path.Combine(AppContext.BaseDirectory, "cache");

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Buffers the HTTP response into RAM, then parses on a background thread.
    /// No ConfigureAwait(false) — continuations return to the caller's SynchronizationContext.
    /// </summary>
    public static async Task<ParseResult> LoadFromUrlAsync(string url, IProgress<ParseProgressUpdate>? progress = null)
    {
        var cachePath = GetUrlCachePath(url);
        var hasCache = File.Exists(cachePath);
        var localSize = hasCache ? new FileInfo(cachePath).Length : -1L;
        var remoteSize = await TryGetRemoteContentLengthAsync(url);

        // Reuse local cache only when server reports the same length.
        if (hasCache && remoteSize is long size && size > 0 && size == localSize)
            return await LoadFromValidatedBytesAsync(await File.ReadAllBytesAsync(cachePath), cachePath, progress);

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            InvalidM3uContent.EnsureValid(bytes, url);
            TryWriteCacheFile(cachePath, bytes);

            return await LoadFromValidatedBytesAsync(bytes, url, progress);
        }
        catch (Exception ex) when (hasCache && ex is not InvalidM3uContentException)
        {
            // Offline/server-failure fallback: use last cached version if available.
            return await LoadFromValidatedBytesAsync(await File.ReadAllBytesAsync(cachePath), cachePath, progress);
        }
    }

    public static async Task<ParseResult> LoadFromFileAsync(string path, IProgress<ParseProgressUpdate>? progress = null)
    {
        return await LoadFromValidatedBytesAsync(await File.ReadAllBytesAsync(path), path, progress);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public static (string? playlistUrl, string? playlistFile, string? lastStreamUrl) LoadSettings()
    {
        if (!File.Exists(IniPath)) return (null, null, null);

        string? pu = null, pf = null, ls = null;
        foreach (var line in File.ReadLines(IniPath))
        {
            if      (TryGet(line, "PlaylistUrl=",   out var v)) pu = Decode(v);
            else if (TryGet(line, "PlaylistFile=",  out v))     pf = Decode(v);
            else if (TryGet(line, "LastStreamUrl=", out v))     ls = Decode(v);
        }
        return (pu, pf, ls);
    }

    public static string LoadLastChannelName()
    {
        if (!File.Exists(IniPath)) return string.Empty;
        foreach (var line in File.ReadLines(IniPath))
            if (TryGet(line, "LastChannelName=", out var v))
                return Decode(v) ?? string.Empty;
        return string.Empty;
    }

    public static string LoadLastChannelLogoUrl()
    {
        if (!File.Exists(IniPath)) return string.Empty;
        foreach (var line in File.ReadLines(IniPath))
            if (TryGet(line, "LastChannelLogoUrl=", out var v))
                return Decode(v) ?? string.Empty;
        return string.Empty;
    }

    /// <summary>Returns the saved theme name ("Dark" or "Light"). Defaults to "Dark".</summary>
    public static string LoadTheme()
    {
        if (!File.Exists(IniPath)) return "Dark";
        foreach (var line in File.ReadLines(IniPath))
            if (TryGet(line, "Theme=", out var v) && v is "Dark" or "Light")
                return v!;
        return "Dark";
    }

    /// <summary>Always returns false — duplicate removal is disabled.</summary>
    public static bool LoadRemoveDuplicates() => false;

    /// <summary>Returns the saved UI language ("en" or "es"). Defaults to "en".</summary>
    public static string LoadLanguage()
    {
        if (!File.Exists(IniPath)) return "en";
        foreach (var line in File.ReadLines(IniPath))
            if (TryGet(line, "Language=", out var v) && v is "en" or "es")
                return v!;
        return "en";
    }

    /// <summary>Returns the last group the user selected, or empty string if never saved.</summary>
    public static string LoadLastGroup()
    {
        if (!File.Exists(IniPath)) return string.Empty;
        foreach (var line in File.ReadLines(IniPath))
            if (TryGet(line, "LastGroup=", out var v))
                return v ?? string.Empty;
        return string.Empty;
    }

    public static void SaveTheme(string theme)
    {
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, LoadUrlHistory(), theme);
    }

    public static void SaveLanguage(string lang)
    {
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, LoadUrlHistory(), LoadTheme(), lang);
    }

    /// <summary>Returns the last recording folder, or empty string if never saved.</summary>
    public static string LoadRecordingFolder()
    {
        if (!File.Exists(IniPath)) return string.Empty;
        foreach (var line in File.ReadLines(IniPath))
            if (TryGet(line, "RecordingFolder=", out var v))
                return Decode(v) ?? string.Empty;
        return string.Empty;
    }

    public static void SaveRecordingFolder(string folder)
    {
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty,
                 LoadUrlHistory(), LoadTheme(), LoadLanguage(), LoadLastGroup(), folder);
    }

    public static void SaveLastGroup(string group)
    {
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, LoadUrlHistory(), LoadTheme(), LoadLanguage(), group);
    }

    public static void SaveSettings(string? playlistUrl, string? playlistFile,
                                    string? lastStreamUrl = null)
    {
        var (eu, ef, el) = LoadSettings();
        WriteIni(playlistUrl  ?? eu ?? string.Empty,
                 playlistFile ?? ef ?? string.Empty,
                 lastStreamUrl ?? el ?? string.Empty,
                 LoadUrlHistory(), LoadTheme());
    }

    public static void SaveLastStreamUrl(string url)
    {
        var (pu, pf, _) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, url, LoadUrlHistory(), LoadTheme());
    }

    public static void SaveLastChannelName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, LoadUrlHistory(), LoadTheme(), lastChannelName: name);
    }

    public static void SaveLastChannelLogoUrl(string logoUrl)
    {
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, LoadUrlHistory(), LoadTheme(), lastChannelLogoUrl: logoUrl ?? string.Empty);
    }

    private static void WriteIni(string playlistUrl, string playlistFile,
                                  string lastStreamUrl, List<UrlHistoryEntry> history,
                                  string theme = "Dark", string language = "",
                                  string lastGroup = "", string recordingFolder = "",
                                  string? lastChannelName = null, string? lastChannelLogoUrl = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[AzIPTV]");
        sb.AppendLine($"Theme={theme}");
        sb.AppendLine($"RemoveDuplicateChannels=0");
        sb.AppendLine($"Language={(string.IsNullOrEmpty(language) ? LoadLanguage() : language)}");
        sb.AppendLine($"LastGroup={(string.IsNullOrEmpty(lastGroup) ? LoadLastGroup() : lastGroup)}");
        sb.AppendLine($"RecordingFolder={Encode(string.IsNullOrEmpty(recordingFolder) ? LoadRecordingFolder() : recordingFolder)}");
        sb.AppendLine($"PlaylistUrl={Encode(playlistUrl)}");
        sb.AppendLine($"PlaylistFile={Encode(playlistFile)}");
        sb.AppendLine($"LastStreamUrl={Encode(lastStreamUrl)}");
        sb.AppendLine($"LastChannelName={Encode(lastChannelName ?? LoadLastChannelName())}");
        sb.AppendLine($"LastChannelLogoUrl={Encode(lastChannelLogoUrl ?? LoadLastChannelLogoUrl())}");
        for (int i = 0; i < history.Count; i++)
        {
            sb.AppendLine($"UrlHistoryUrl{i}={Encode(history[i].Url)}");
            sb.AppendLine($"UrlHistoryName{i}={Encode(history[i].Name)}");
        }
        // Preserve any non-[AzIPTV] sections (e.g. [Favs_…] play-count blocks).
        if (File.Exists(IniPath))
        {
            bool inAzSection = false;
            foreach (var rawLine in File.ReadLines(IniPath))
            {
                var t = rawLine.Trim();
                if (t.StartsWith('['))
                    inAzSection = t.Equals("[AzIPTV]", StringComparison.OrdinalIgnoreCase);
                if (!inAzSection)
                    sb.AppendLine(rawLine);
            }
        }
        File.WriteAllText(IniPath, sb.ToString());
    }

    // ── URL history ───────────────────────────────────────────────────────────

    /// <summary>Fixed playlists always shown at the top of the dialog (not persisted).</summary>
    public static readonly UrlHistoryEntry[] FixedPlaylists =
    {
        new("https://iptv-org.github.io/iptv/index.m3u",         "Public 1"),
        new("https://iptv-org.github.io/iptv/languages/spa.m3u", "Public ES"),
    };

    /// <summary>Maintained for call sites that only need the first fixed URL.</summary>
    public static string FixedPlaylistUrl => FixedPlaylists[0].Url;

    private const int MaxUrlHistory = 20;

    public static List<UrlHistoryEntry> LoadUrlHistory()
    {
        var urls  = new SortedDictionary<int, string>();
        var names = new SortedDictionary<int, string>();
        if (!File.Exists(IniPath)) return new List<UrlHistoryEntry>();

        foreach (var rawLine in File.ReadLines(IniPath))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith("UrlHistoryUrl", StringComparison.Ordinal))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!int.TryParse(line.AsSpan(13, eq - 13), out int n)) continue;
                if (Decode(line[(eq + 1)..]) is string u && !string.IsNullOrWhiteSpace(u))
                    urls[n] = u;
            }
            else if (line.StartsWith("UrlHistoryName", StringComparison.Ordinal))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!int.TryParse(line.AsSpan(14, eq - 14), out int n)) continue;
                if (Decode(line[(eq + 1)..]) is string nm)
                    names[n] = nm;
            }
            // Legacy plain UrlHistory{n}= entries (URL only, name inferred).
            else if (line.StartsWith("UrlHistory", StringComparison.Ordinal))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!int.TryParse(line.AsSpan(10, eq - 10), out int n)) continue;
                if (Decode(line[(eq + 1)..]) is string u && !string.IsNullOrWhiteSpace(u))
                    urls[n] = u;
            }
        }

        int idx = 0;
        var result = new List<UrlHistoryEntry>(urls.Count);
        foreach (var kvp in urls)
        {
            var nm = names.TryGetValue(kvp.Key, out var s) ? s : $"List #{idx + 1}";
            result.Add(new UrlHistoryEntry(kvp.Value, nm));
            idx++;
        }
        return result;
    }

    public static void AddUrlToHistory(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (FixedPlaylists.Any(f => string.Equals(f.Url, url.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;
        var history = LoadUrlHistory();
        history.RemoveAll(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
        var defaultName = $"List #{history.Count + 1}";
        history.Insert(0, new UrlHistoryEntry(url, defaultName));
        if (history.Count > MaxUrlHistory)
            history.RemoveRange(MaxUrlHistory, history.Count - MaxUrlHistory);
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, history, LoadTheme());
    }

    public static void SaveUrlHistoryEntry(string originalUrl, string url, string name)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name)) return;
        if (FixedPlaylists.Any(f => string.Equals(f.Url, url.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;

        var history = LoadUrlHistory();
        var originalIndex = history.FindIndex(e => string.Equals(e.Url, originalUrl, StringComparison.OrdinalIgnoreCase));
        if (originalIndex >= 0)
            history.RemoveAt(originalIndex);

        history.RemoveAll(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));

        var entry = new UrlHistoryEntry(url.Trim(), name.Trim());
        if (originalIndex >= 0 && originalIndex <= history.Count)
            history.Insert(originalIndex, entry);
        else
            history.Insert(0, entry);

        if (history.Count > MaxUrlHistory)
            history.RemoveRange(MaxUrlHistory, history.Count - MaxUrlHistory);

        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, history, LoadTheme());
    }

    public static void RemoveUrlFromHistory(string url)
    {
        var history = LoadUrlHistory();
        history.RemoveAll(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, history, LoadTheme());
    }

    public static void UpdateUrlName(string url, string name)
    {
        var history = LoadUrlHistory();
        for (int i = 0; i < history.Count; i++)
        {
            if (string.Equals(history[i].Url, url, StringComparison.OrdinalIgnoreCase))
            {
                history[i] = history[i] with { Name = name };
                break;
            }
        }
        var (pu, pf, ls) = LoadSettings();
        WriteIni(pu ?? string.Empty, pf ?? string.Empty, ls ?? string.Empty, history, LoadTheme());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // ── Play counts (Favourites) ──────────────────────────────────────────────

    /// <summary>Opaque 16-hex-char identifier for a playlist URL or file path.
    /// Derived from SHA-256 so credentials embedded in URLs are never stored.</summary>
    public static string GetPlaylistId(string urlOrPath) => Hash16(urlOrPath);

    /// <summary>Opaque 16-hex-char identifier for a channel URL.</summary>
    public static string GetChannelId(string url) => Hash16(url);

    private static string Hash16(string input)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Returns channel-play counts for the given playlist from user.ini.
    /// Returns an empty dictionary if the section does not exist yet.</summary>
    public static Dictionary<string, int> LoadPlayCounts(string playlistId)
    {
        var result = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(playlistId) || !File.Exists(IniPath)) return result;
        var section = $"[Favs_{playlistId}]";
        bool inSection = false;
        foreach (var rawLine in File.ReadLines(IniPath))
        {
            var t = rawLine.Trim();
            if (t.StartsWith('['))
            {
                inSection = t.Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            var eq = t.IndexOf('=');
            if (eq < 1) continue;
            var key = t[..eq].Trim();
            var raw = t[(eq + 1)..].Trim();
            if (int.TryParse(raw, out var count) && count > 0)
                result[key] = count;
        }
        return result;
    }

    /// <summary>Writes (or replaces) the [Favs_playlistId] section in user.ini
    /// without touching any other section.</summary>
    public static void SavePlayCounts(string playlistId, Dictionary<string, int> counts)
    {
        if (string.IsNullOrEmpty(playlistId)) return;
        var sectionHeader = $"[Favs_{playlistId}]";
        var lines = File.Exists(IniPath)
            ? new List<string>(File.ReadAllLines(IniPath))
            : new List<string>();
        // Strip old section for this playlist.
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            { start = i; break; }
        if (start >= 0)
        {
            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('['))
                end++;
            lines.RemoveRange(start, end - start);
        }
        // Append fresh section.
        if (counts.Count > 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.Add(sectionHeader);
            foreach (var (key, val) in counts.OrderByDescending(kv => kv.Value))
                lines.Add($"{key}={val}");
        }
        File.WriteAllLines(IniPath, lines);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GetUrlCachePath(string url) =>
        Path.Combine(CacheDir, $"playlist_{GetPlaylistId(url)}.m3u8");

    private static async Task<long?> TryGetRemoteContentLengthAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            return resp.Content.Headers.ContentLength;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ParseResult> LoadFromValidatedBytesAsync(byte[] bytes, string sourceName, IProgress<ParseProgressUpdate>? progress)
    {
        InvalidM3uContent.EnsureValid(bytes, sourceName);

        using var src = new MemoryStream(bytes, writable: false);
        if (bytes.LongLength > 0)
        {
            using var tracked = new ProgressReadStream(src, bytes.LongLength);
            return await M3uParser.ParseAsync(tracked, LoadRemoveDuplicates(), progress); // Task.Run inside; continuation on UI thread
        }

        return await M3uParser.ParseAsync(src, LoadRemoveDuplicates(), progress); // Task.Run inside; continuation on UI thread
    }

    private static void TryWriteCacheFile(string cachePath, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var tempPath = cachePath + ".tmp";
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch
        {
            // Cache write failures should not block playback.
        }
    }

    private static string Encode(string value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    private static string? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch { return null; } // ignore corrupt/legacy plain-text entries
    }

    private static bool TryGet(string line, string key, out string? value)
    {
        var span = line.AsSpan().TrimStart();
        if (!span.StartsWith(key.AsSpan(), StringComparison.Ordinal))
        {
            value = null;
            return false;
        }
        var v = span[key.Length..].Trim();
        value = v.IsEmpty ? null : v.ToString();
        return true;
    }
}
