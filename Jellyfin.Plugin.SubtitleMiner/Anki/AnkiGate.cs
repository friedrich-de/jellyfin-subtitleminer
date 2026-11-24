using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Settings;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SubtitleMiner.Anki;

/// <summary>
/// Decides whether the server calls a user's AnkiConnect for them, and which one.
/// </summary>
internal sealed class AnkiGate
{
    /// <summary>How many calls of one user may be on their way at a time.</summary>
    internal const int MaxCallsPerUser = 4;

    /// <summary>What a user is told whom an administrator has not let send cards through the server.</summary>
    internal const string NotAllowed = "An administrator of this Jellyfin server has not let users send cards through it. Choose \"Anki on this device\" in the settings, or ask them.";

    private readonly UserSettingsStore _settings;
    private readonly ISharedSettings _shared;
    private readonly System.Threading.Lock _lock = new();
    private readonly Dictionary<Guid, int> _calls = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AnkiGate"/> class.
    /// </summary>
    /// <param name="settings">The users' settings.</param>
    /// <param name="shared">The settings for everyone.</param>
    public AnkiGate(UserSettingsStore settings, ISharedSettings shared)
    {
        _settings = settings;
        _shared = shared;
    }

    /// <summary>
    /// Tells whether a user may have the server send their cards. Administrators always may; anyone else when an
    /// administrator allowed it, because the server calls whatever address the user sets.
    /// </summary>
    /// <param name="caller">The user.</param>
    /// <returns><c>true</c> when the server calls an AnkiConnect for this user.</returns>
    public bool MayUseServer(Caller caller) => caller.IsAdministrator || _shared.AnkiThroughServerForEveryone;

    /// <summary>
    /// Finds the AnkiConnect the server calls for a user.
    /// </summary>
    /// <param name="caller">The user.</param>
    /// <param name="target">The AnkiConnect, when there is one to call.</param>
    /// <param name="status">The HTTP status of the refusal, when there is not.</param>
    /// <param name="message">Why not, for the user.</param>
    /// <returns><c>true</c> when the server calls an AnkiConnect for this user.</returns>
    public bool TryFind(Caller caller, out AnkiTarget? target, out int status, out string message)
    {
        target = null;
        status = StatusCodes.Status200OK;
        message = string.Empty;
        if (!MayUseServer(caller))
        {
            (status, message) = (StatusCodes.Status403Forbidden, NotAllowed);
            return false;
        }

        var settings = _settings.Load(caller.UserId);
        if (settings.Where != AnkiWhere.Server)
        {
            (status, message) = (StatusCodes.Status409Conflict, "Your cards do not go to an Anki on a server. Choose one in the settings.");
            return false;
        }

        if (!AnkiAddress.TryRead(settings.ServerAddress, out var address))
        {
            (status, message) = (StatusCodes.Status409Conflict, "Set the address of the AnkiConnect on your server in the settings.");
            return false;
        }

        target = new AnkiTarget(address!, _settings.Keys.Reveal(settings.ServerKey));
        return true;
    }

    /// <summary>
    /// Counts a call of a user in, unless they have too many on their way.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns><c>true</c> when the call may go; then call <see cref="Leave"/> when it is done.</returns>
    public bool TryEnter(Guid userId)
    {
        lock (_lock)
        {
            var calls = _calls.GetValueOrDefault(userId);
            if (calls >= MaxCallsPerUser)
            {
                return false;
            }

            _calls[userId] = calls + 1;
            return true;
        }
    }

    /// <summary>
    /// Counts a call of a user out.
    /// </summary>
    /// <param name="userId">The user.</param>
    public void Leave(Guid userId)
    {
        lock (_lock)
        {
            var calls = _calls.GetValueOrDefault(userId) - 1;
            if (calls <= 0)
            {
                _calls.Remove(userId);
            }
            else
            {
                _calls[userId] = calls;
            }
        }
    }

    /// <summary>
    /// What a failed call is answered with.
    /// </summary>
    /// <param name="outcome">How the call ended.</param>
    /// <param name="target">The AnkiConnect called.</param>
    /// <returns>The HTTP status and the sentence for the user.</returns>
    public static (int Status, string Message) Failure(AnkiOutcome outcome, AnkiTarget target) => outcome switch
    {
        AnkiOutcome.TimedOut => (StatusCodes.Status504GatewayTimeout, $"The AnkiConnect at {target.Address.Authority} did not answer in time."),
        AnkiOutcome.NotAnkiConnect => (StatusCodes.Status502BadGateway, $"What answers at {target.Address.Authority} is not AnkiConnect. Check the address in the settings."),
        _ => (StatusCodes.Status502BadGateway, $"The AnkiConnect at {target.Address.Authority} could not be reached from the Jellyfin server."),
    };

    /// <summary>
    /// Puts AnkiConnect's own error into words for the user.
    /// </summary>
    /// <param name="error">What AnkiConnect said.</param>
    /// <returns>The sentence.</returns>
    public static string Explain(string error) =>
        error.Contains("api key", StringComparison.OrdinalIgnoreCase)
            ? "The AnkiConnect on your server did not take the key. Check it in the settings."
            : "Anki said: " + error;
}
