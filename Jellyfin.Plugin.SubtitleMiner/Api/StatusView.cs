using System;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// What the page learns when it signs in: who the user is and what it is talking to.
/// </summary>
public sealed record StatusView
{
    /// <summary>Gets the plugin's version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the signed-in user's id.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the signed-in user's name.</summary>
    public required string UserName { get; init; }

    /// <summary>Gets a value indicating whether the user is an administrator of the server.</summary>
    public required bool IsAdministrator { get; init; }

    /// <summary>Gets the server's name.</summary>
    public required string ServerName { get; init; }

    /// <summary>Gets the server's version.</summary>
    public required string ServerVersion { get; init; }
}
