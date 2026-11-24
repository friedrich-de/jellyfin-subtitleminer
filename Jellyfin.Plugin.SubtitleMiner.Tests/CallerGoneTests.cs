using System;
using System.Threading;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public class CallerGoneTests
{
    private static ExceptionContext Failed(Exception exception, bool callerGone)
    {
        using var gone = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = gone.Token };
        if (callerGone)
        {
            gone.Cancel();
        }

        var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []) { Exception = exception };
        new CallerGoneAttribute().OnException(context);
        return context;
    }

    [Fact]
    public void A_request_the_caller_gave_up_on_ends_quietly()
    {
        var context = Failed(new OperationCanceledException(), callerGone: true);

        Assert.True(context.ExceptionHandled);
        Assert.Equal(499, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public void Other_failures_and_cancellations_the_caller_did_not_ask_for_are_left_to_Jellyfin()
    {
        Assert.False(Failed(new OperationCanceledException(), callerGone: false).ExceptionHandled);
        Assert.False(Failed(new InvalidOperationException(), callerGone: true).ExceptionHandled);
    }

    [Theory]
    [InlineData(typeof(ItemsController))]
    [InlineData(typeof(DictionariesController))]
    [InlineData(typeof(AnkiController))]
    public void Is_on_the_controllers_whose_answers_the_page_may_stop_waiting_for(Type controller)
    {
        Assert.Single(controller.GetCustomAttributes(typeof(CallerGoneAttribute), inherit: false));
    }
}
