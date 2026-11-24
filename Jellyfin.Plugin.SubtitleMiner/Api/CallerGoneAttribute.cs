using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Ends a request quietly when the caller gave up on it, as the page does with a preview it no longer
/// needs. Jellyfin would log the cancellation as an error.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class CallerGoneAttribute : ExceptionFilterAttribute
{
    /// <summary>What a request the caller gave up on ends with, as nginx says it: nobody reads it.</summary>
    internal const int ClientClosedRequest = 499;

    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.Result = new StatusCodeResult(ClientClosedRequest);
            context.ExceptionHandled = true;
        }
    }
}
