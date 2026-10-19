using System.IO;
using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace OverInk.Remote;

/// <summary>
/// Serves Remote/wwwroot files from embedded resources without a generated manifest.
/// </summary>
public sealed class EmbeddedWwwRootFileProvider : IFileProvider
{
    private const string Prefix = "OverInk.Remote.wwwroot.";

    private static readonly Dictionary<string, (string ResourceName, string ContentType)> Files =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = ($"{Prefix}index.html", "text/html; charset=utf-8"),
            ["app.js"] = ($"{Prefix}app.js", "text/javascript; charset=utf-8"),
            ["icons.js"] = ($"{Prefix}icons.js", "text/javascript; charset=utf-8"),
            ["styles.css"] = ($"{Prefix}styles.css", "text/css; charset=utf-8"),
        };

    private readonly Assembly _assembly = typeof(EmbeddedWwwRootFileProvider).Assembly;

    public IDirectoryContents GetDirectoryContents(string subpath) =>
        new EmbeddedDirectoryContents(subpath);

    public IFileInfo GetFileInfo(string subpath)
    {
        var key = subpath.TrimStart('/').Replace('\\', '/');
        if (string.IsNullOrEmpty(key))
        {
            key = "index.html";
        }

        if (!Files.TryGetValue(key, out var entry))
        {
            return new NotFoundFileInfo(subpath);
        }

        return new EmbeddedFileInfo(_assembly, entry.ResourceName, key);
    }

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class EmbeddedDirectoryContents : IDirectoryContents
    {
        public EmbeddedDirectoryContents(string subpath)
        {
            Exists = string.IsNullOrEmpty(subpath.Trim('/'));
        }

        public bool Exists { get; }
        public IEnumerator<IFileInfo> GetEnumerator() => Enumerable.Empty<IFileInfo>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EmbeddedFileInfo : IFileInfo
    {
        private readonly Assembly _assembly;
        private readonly string _resourceName;
        private byte[]? _cached;

        public EmbeddedFileInfo(Assembly assembly, string resourceName, string name)
        {
            _assembly = assembly;
            _resourceName = resourceName;
            Name = name;
            Exists = assembly.GetManifestResourceNames().Contains(resourceName);
            LastModified = DateTimeOffset.UtcNow;
            IsDirectory = false;
            Length = Exists ? ReadBytes().Length : -1;
        }

        public bool Exists { get; }
        public long Length { get; }
        public string? PhysicalPath => null;
        public string Name { get; }
        public DateTimeOffset LastModified { get; }
        public bool IsDirectory { get; }

        public Stream CreateReadStream()
        {
            return new MemoryStream(ReadBytes(), writable: false);
        }

        private byte[] ReadBytes()
        {
            if (_cached is not null)
            {
                return _cached;
            }

            using var stream = _assembly.GetManifestResourceStream(_resourceName)
                ?? throw new FileNotFoundException($"Embedded resource not found: {_resourceName}");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _cached = ms.ToArray();
            return _cached;
        }
    }
}
