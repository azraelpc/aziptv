namespace AzIPTV;

/// <summary>Represents a single IPTV channel from an M3U8 playlist.</summary>
public sealed record Channel(string Name, string Url, string Group, string LogoUrl);
