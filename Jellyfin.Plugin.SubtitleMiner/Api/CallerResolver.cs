using System;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Finds out which Jellyfin user sent a request.
/// </summary>
internal interface ICallerResolver
{
    /// <summary>
    /// The user behind the request, or <c>null</c> when it carries no user's login (an API key, for example).
    /// </summary>
    Task<Caller?> ResolveAsync(HttpContext context);
}

/// <summary>
/// Reads the user from Jellyfin's own check of the request's token.
/// </summary>
internal sealed class CallerResolver : ICallerResolver
{
    private readonly IAuthorizationContext _authorization;

    public CallerResolver(IAuthorizationContext authorization)
    {
        _authorization = authorization;
    }

    public async Task<Caller?> ResolveAsync(HttpContext context)
    {
        var info = await _authorization.GetAuthorizationInfo(context).ConfigureAwait(false);
        var user = info.User;
        if (user is null || user.Id == Guid.Empty)
        {
            return null;
        }

        return new Caller(user.Id, user.Username, user.HasPermission(PermissionKind.IsAdministrator));
    }
}
