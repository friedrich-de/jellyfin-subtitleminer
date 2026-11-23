using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Support;

internal sealed class FakeCallers : ICallerResolver
{
    public Caller? Caller { get; set; }

    public Task<Caller?> ResolveAsync(HttpContext context) => Task.FromResult(Caller);
}

/// <summary>Page files held in memory, by name.</summary>
internal sealed class FakePageFileSource : IPageFileSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FakePageFileSource(params string[] names)
    {
        foreach (var name in names)
        {
            _files[name] = "content of " + name;
        }
    }

    public IReadOnlyCollection<string> Names => _files.Keys;

    public Stream? Open(string name) => _files.TryGetValue(name, out var content) ? new MemoryStream(Encoding.UTF8.GetBytes(content)) : null;
}
