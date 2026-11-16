using System.Runtime.ExceptionServices;
using System.Text.Json;
using Foundation;

namespace PKForge.App;

/// <summary>
/// iOS's counterpart of Android's persisted SAF grants. A file or folder picked in the Files
/// picker comes with a security-scoped URL that only lasts until the app quits; PKForge keeps a
/// bookmark of it (in its own folder, never in a save) and hands the rest of the app a short,
/// stable id instead:
/// <list type="bullet">
/// <item><c>ios://f/&lt;key&gt;</c> - a single picked file (a linked save, a music track, a .pk file).</item>
/// <item><c>ios://t/&lt;key&gt;</c> - a picked folder (an emulator's save folder, the PKForge folder).</item>
/// <item><c>ios://t/&lt;key&gt;/saves/Emerald.srm</c> - a file found below a picked folder.</item>
/// </list>
/// Picking the same place again gives the same id, so save identities, the music library and
/// the linked folders survive a re-pick.
/// </summary>
internal static class IosDocuments
{
    private const string FilePrefix = "ios://f/";
    private const string TreePrefix = "ios://t/";

    private static readonly Lock Gate = new();
    private static Dictionary<string, Grant>? _grants;

    private sealed record Grant(string Path, string Bookmark);

    private static string RegistryPath => Path.Combine(FileSystem.AppDataDirectory, "ios-grants.json");

    public static bool IsTree(string id) => id.StartsWith(TreePrefix, StringComparison.Ordinal);

    /// <summary>Remembers a picked file; call while the picker's grant is still fresh.</summary>
    public static string RegisterFile(NSUrl url) => FilePrefix + Register(url);

    /// <summary>Remembers a picked folder; call while the picker's grant is still fresh.</summary>
    public static string RegisterTree(NSUrl url) => TreePrefix + Register(url);

    /// <summary>The id of a file at <paramref name="relativePath"/> below a picked folder.</summary>
    public static string Child(string treeId, string relativePath)
    {
        if (!IsTree(treeId)) throw new ArgumentException("Not a picked folder.", nameof(treeId));
        var key = Parse(treeId).Key;
        var relative = relativePath.Replace('\\', '/').Trim('/');
        return relative.Length == 0 ? TreePrefix + key : $"{TreePrefix}{key}/{relative}";
    }

    /// <summary>The folder id a child id was found under, and its path below that folder.</summary>
    public static (string TreeId, string Relative) SplitChild(string id)
    {
        var (key, relative, tree) = Parse(id);
        if (!tree) throw new NotSupportedException("This file was not found through a linked folder.");
        return (TreePrefix + key, relative);
    }

    public static string DisplayName(NSUrl url, string fallback) =>
        url.LastPathComponent is { Length: > 0 } name ? name : fallback;

    /// <summary>
    /// Opens the grant behind <paramref name="id"/> and returns the file-system path it names.
    /// Dispose the result to give the grant back; paths below it are reachable until then.
    /// </summary>
    public static ScopedPath Open(string id)
    {
        var (key, relative, _) = Parse(id);
        var root = ResolveRoot(key);
        var accessing = root.StartAccessingSecurityScopedResource();
        try
        {
            var rootPath = root.Path ?? throw new IOException("The picked location has no path.");
            if (relative.Length == 0) return new ScopedPath(root, accessing, rootPath, root);
            var path = Path.GetFullPath(Path.Combine(rootPath, relative));
            var inside = rootPath.TrimEnd('/') + "/";
            if (!path.StartsWith(inside, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("That path leads outside the linked folder.");
            return new ScopedPath(root, accessing, path, NSUrl.FromFilename(path));
        }
        catch
        {
            if (accessing) root.StopAccessingSecurityScopedResource();
            throw;
        }
    }

    /// <summary>Reads a whole file through a file coordinator (iCloud and other providers download it first).</summary>
    public static byte[] ReadAll(string id, long maxBytes = long.MaxValue)
    {
        using var scoped = Open(id);
        return ReadCoordinated(scoped.Url, maxBytes);
    }

    public static byte[] ReadCoordinated(NSUrl url, long maxBytes = long.MaxValue)
    {
        byte[]? bytes = null;
        Exception? failure = null;
        using var coordinator = new NSFileCoordinator();
        coordinator.CoordinateRead(url, default(NSFileCoordinatorReadingOptions), out var error, actual =>
        {
            try
            {
                var path = actual.Path ?? throw new IOException("The file has no path.");
                var length = new FileInfo(path).Length;
                if (length > maxBytes) throw new IOException($"larger than {maxBytes} bytes");
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception caught) { failure = caught; }
        });
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        if (error is not null) throw new IOException(error.LocalizedDescription);
        return bytes ?? throw new IOException("The file could not be read.");
    }

    /// <summary>
    /// Writes a whole file. Below a picked folder the bytes go to a temporary file beside it that
    /// then replaces it in one rename; a single picked file only grants itself, so it is
    /// truncated and rewritten in place (Android's "rwt" path does the same).
    /// </summary>
    public static void WriteAll(string id, ReadOnlyMemory<byte> bytes, bool createFolders = false)
    {
        using var scoped = Open(id);
        var replace = IsTree(id) && scoped.Path != scoped.RootPath;
        if (createFolders && Path.GetDirectoryName(scoped.Path) is { } folder) Directory.CreateDirectory(folder);
        WriteCoordinated(scoped.Url, bytes, replace);
    }

    public static void WriteCoordinated(NSUrl url, ReadOnlyMemory<byte> bytes, bool replace)
    {
        Exception? failure = null;
        using var coordinator = new NSFileCoordinator();
        var options = replace ? NSFileCoordinatorWritingOptions.ForReplacing : default;
        coordinator.CoordinateWrite(url, options, out var error, actual =>
        {
            try
            {
                var path = actual.Path ?? throw new IOException("The file has no path.");
                if (replace)
                {
                    var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.pkforge-tmp");
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        output.Write(bytes.Span);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, path, overwrite: true);
                }
                else
                {
                    using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    output.Write(bytes.Span);
                    output.Flush(flushToDisk: true);
                }
            }
            catch (Exception caught) { failure = caught; }
        });
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        if (error is not null) throw new IOException(error.LocalizedDescription);
    }

    // ── The grant registry ───────────────────────────────────────────────────────────────────

    private static (string Key, string Relative, bool Tree) Parse(string id)
    {
        bool tree;
        string rest;
        if (id.StartsWith(TreePrefix, StringComparison.Ordinal)) { tree = true; rest = id[TreePrefix.Length..]; }
        else if (id.StartsWith(FilePrefix, StringComparison.Ordinal)) { tree = false; rest = id[FilePrefix.Length..]; }
        else throw new ArgumentException("This file was not picked on this device: pick it again.", nameof(id));
        var slash = rest.IndexOf('/');
        var key = slash < 0 ? rest : rest[..slash];
        var relative = slash < 0 ? "" : rest[(slash + 1)..];
        if (key.Length == 0 || (!tree && relative.Length > 0))
            throw new ArgumentException("The document id is malformed.", nameof(id));
        return (key, relative, tree);
    }

    private static string Register(NSUrl url)
    {
        var accessing = url.StartAccessingSecurityScopedResource();
        try
        {
            var bookmark = CreateBookmark(url);
            var path = url.StandardizedUrl?.Path ?? url.Path ?? "";
            lock (Gate)
            {
                var grants = Load();
                var key = grants.FirstOrDefault(g => g.Value.Path == path).Key ?? Guid.NewGuid().ToString("N");
                grants[key] = new Grant(path, bookmark);
                Save(grants);
                return key;
            }
        }
        finally
        {
            if (accessing) url.StopAccessingSecurityScopedResource();
        }
    }

    private static string CreateBookmark(NSUrl url)
    {
        using var data = url.CreateBookmarkData(NSUrlBookmarkCreationOptions.MinimalBookmark, null, null, out var error);
        if (data is null || error is not null)
            throw new IOException($"iOS refused a lasting permission for {url.LastPathComponent}: {error?.LocalizedDescription}");
        return Convert.ToBase64String(data.ToArray());
    }

    private static NSUrl ResolveRoot(string key)
    {
        Grant? grant;
        lock (Gate)
            Load().TryGetValue(key, out grant);
        if (grant is null) throw new FileNotFoundException("PKForge no longer has permission for this file: pick it again.");

        using var data = NSData.FromArray(Convert.FromBase64String(grant.Bookmark));
        var url = NSUrl.FromBookmarkData(data, NSUrlBookmarkResolutionOptions.WithoutUI, null, out var stale, out var error);
        if (url is null || error is not null)
            throw new IOException($"The file or folder could not be found again ({error?.LocalizedDescription ?? "moved or deleted"}): pick it again.");
        if (stale)
        {
            // Moved or renamed since: still reachable, so the bookmark is renewed for next time.
            var accessing = url.StartAccessingSecurityScopedResource();
            try
            {
                var renewed = new Grant(url.StandardizedUrl?.Path ?? url.Path ?? grant.Path, CreateBookmark(url));
                lock (Gate)
                {
                    var grants = Load();
                    grants[key] = renewed;
                    Save(grants);
                }
            }
            catch (IOException) { /* the old bookmark still resolved; try renewing next time */ }
            finally
            {
                if (accessing) url.StopAccessingSecurityScopedResource();
            }
        }
        return url;
    }

    private static Dictionary<string, Grant> Load()
    {
        if (_grants is not null) return _grants;
        try
        {
            _grants = File.Exists(RegistryPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Grant>>(File.ReadAllText(RegistryPath)) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            Services.AppLog.Error("files", "The list of picked files could not be read", error);
            _grants = [];
        }
        return _grants;
    }

    private static void Save(Dictionary<string, Grant> grants)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        var temporary = RegistryPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(grants));
        File.Move(temporary, RegistryPath, overwrite: true);
    }
}

/// <summary>An opened grant: a path that stays reachable until this is disposed.</summary>
internal sealed class ScopedPath(NSUrl root, bool accessing, string path, NSUrl url) : IDisposable
{
    public string Path { get; } = path;
    public NSUrl Url { get; } = url;
    public string RootPath => root.Path ?? "";

    public void Dispose()
    {
        if (accessing) root.StopAccessingSecurityScopedResource();
    }
}
