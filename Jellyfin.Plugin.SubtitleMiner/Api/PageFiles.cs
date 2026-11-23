using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Where the page's files come from.
/// </summary>
internal interface IPageFileSource
{
    /// <summary>
    /// The files' names, as paths below the page's address, with forward slashes.
    /// </summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>
    /// Opens one of the files.
    /// </summary>
    Stream? Open(string name);
}

/// <summary>
/// The page's files as the build embedded them in the plugin's assembly, under names that start with <c>page/</c>.
/// </summary>
internal sealed class EmbeddedPageFiles : IPageFileSource
{
    internal const string Prefix = "page/";

    private readonly Assembly _assembly;
    private readonly FrozenDictionary<string, string> _resources;

    public EmbeddedPageFiles(Assembly assembly)
    {
        _assembly = assembly;

        // A build on Windows names the files with backslashes.
        _resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .ToFrozenDictionary(name => name[Prefix.Length..].Replace('\\', '/'), name => name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Names => _resources.Keys;

    public Stream? Open(string name) => _resources.TryGetValue(name, out var resource) ? _assembly.GetManifestResourceStream(resource) : null;
}

/// <summary>
/// One file of the page.
/// </summary>
/// <param name="Name">Its path below the page's address.</param>
/// <param name="ContentType">The type it is served as.</param>
/// <param name="Immutable">Whether its name changes whenever its content does, so a browser may keep it for good.</param>
internal sealed record PageFile(string Name, string ContentType, bool Immutable);

/// <summary>
/// The page's files by the path a browser asks for. Only files the build put there are found, and only
/// of the kinds listed here, so no request can reach anything else.
/// </summary>
internal sealed class PageFiles
{
    /// <summary>
    /// The page itself.
    /// </summary>
    internal const string IndexName = "index.html";

    /// <summary>
    /// The folder the build puts files into whose names carry a hash of their content.
    /// </summary>
    internal const string HashedFolder = "assets/";

    private static readonly FrozenDictionary<string, string> _contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".webmanifest"] = "application/manifest+json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".wasm"] = "application/wasm",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly IPageFileSource _source;
    private readonly FrozenDictionary<string, PageFile> _files;

    public PageFiles(IPageFileSource source)
    {
        _source = source;
        var files = new Dictionary<string, PageFile>(StringComparer.Ordinal);
        foreach (var name in source.Names)
        {
            if (_contentTypes.TryGetValue(Path.GetExtension(name), out var contentType))
            {
                files[name] = new PageFile(name, contentType, name.StartsWith(HashedFolder, StringComparison.Ordinal));
            }
        }

        _files = files.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets a value indicating whether the plugin was built with its page.
    /// </summary>
    public bool HasPage => _files.ContainsKey(IndexName);

    /// <summary>
    /// The file a path below the page's address names, or <c>null</c>. An empty path names the page.
    /// </summary>
    public PageFile? Find(string? path) => _files.GetValueOrDefault(string.IsNullOrEmpty(path) ? IndexName : path);

    /// <summary>
    /// Opens a file <see cref="Find"/> returned.
    /// </summary>
    public Stream? Open(PageFile file) => _source.Open(file.Name);
}
