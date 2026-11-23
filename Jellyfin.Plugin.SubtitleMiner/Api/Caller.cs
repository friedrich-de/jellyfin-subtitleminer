using System;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Who is calling, as far as the plugin's rules need to know.
/// </summary>
internal sealed record Caller(Guid UserId, string Name, bool IsAdministrator);

/// <summary>
/// The Jellyfin server the plugin runs in, as the page shows it.
/// </summary>
internal sealed record ServerInfo(string Name, string Version);
