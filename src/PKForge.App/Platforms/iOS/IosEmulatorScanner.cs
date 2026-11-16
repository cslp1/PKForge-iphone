using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.App;

/// <summary>
/// Walks a picked folder looking for parseable Pokémon saves: the iOS port of
/// AndroidEmulatorScanner, with the same emulator layouts, pruning, heuristics and parse cache.
/// The folder is reached through its lasting grant (<see cref="IosDocuments"/>) with ordinary
/// file-system calls. File-name filters are only a pre-filter; every candidate's bytes must pass
/// engine validation before being reported.
/// </summary>
/// <remarks>
/// Paths given to the shared heuristics look like Android document ids below the folder
/// ("RetroArch/saves/Emerald.srm"), so folder hints, Switch title guesses and the 3DS layout
/// read them the same way on both platforms.
/// </remarks>
public sealed class IosEmulatorScanner(ISaveEngine engine) : IIncrementalEmulatorDetectionService
{
    private const int EdenMaxDepth = 8;
    private const long MaxSaveBytes = 32 * 1024 * 1024;

    private sealed class ScanState
    {
        public int FilesSeen;
        public EmulatorKind Kind;
        public IReadOnlyList<string>? Siblings;
        public string RootDocId = "";
        public string TreeId = "";
        public readonly List<string> Rejected = [];
        public readonly List<string> Diagnostics = [];

        public void Trace(string message)
        {
            const int maxLines = 2000;
            if (Diagnostics.Count < maxLines)
                Diagnostics.Add(message);
            else if (Diagnostics.Count == maxLines)
                Diagnostics.Add("DIAGNOSTICS TRUNCATED after 2000 lines");
        }
    }

    /// <param name="DocId">The path from the picked folder's name down ("RetroArch/saves/Emerald.srm").</param>
    /// <param name="Relative">The path below the picked folder ("saves/Emerald.srm").</param>
    /// <param name="FullPath">Where the file is on disk while the grant is open.</param>
    private sealed record ChildDocument(string DocId, string Relative, string Name, bool IsDirectory, DateTimeOffset? LastModified, string FullPath);

    public ValueTask<EmulatorScanResult> ScanAsync(string treeId, EmulatorKind kind, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => ScanCore(treeId, kind, cancellationToken, null), cancellationToken));

    public async IAsyncEnumerable<EmulatorScanUpdate> ScanIncrementalAsync(
        string treeId, EmulatorKind kind,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<EmulatorScanUpdate>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        var producer = Task.Run(() =>
        {
            try
            {
                var result = ScanCore(treeId, kind, cancellationToken,
                    save => channel.Writer.TryWrite(new EmulatorScanUpdate(save, false)));
                channel.Writer.TryWrite(new EmulatorScanUpdate(
                    null, true, result.FilesSeen, result.Saves.Count,
                    result.RejectedCandidates, result.Diagnostics));
                ScanCache.Flush();
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
            }
        });

        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
            yield return update;
        await producer;
    }

    private EmulatorScanResult ScanCore(
        string treeId, EmulatorKind kind, CancellationToken cancellationToken,
        Action<DetectedSave>? onSave)
    {
        if (kind == EmulatorKind.LinkedFile)
            return ScanLinkedFile(treeId, onSave);

        var state = new ScanState { Kind = kind, TreeId = treeId };
        state.Trace($"Scanner={nameof(IosEmulatorScanner)} kind={kind}");
        state.Trace($"TreeId={treeId}");
        List<DetectedSave> found;
        try
        {
            using var folder = IosDocuments.Open(treeId);
            var rootName = Path.GetFileName(folder.Path.TrimEnd('/'));
            state.RootDocId = rootName;
            state.Trace($"RootPath={folder.Path}");
            var root = new ChildDocument(rootName, "", rootName, true, null, folder.Path);
            found = kind switch
            {
                EmulatorKind.RetroArch or EmulatorKind.MelonDS or EmulatorKind.Linkboy or
                EmulatorKind.DraStic or EmulatorKind.PizzaBoyGba or EmulatorKind.PizzaBoyGbc or EmulatorKind.Dolphin or
                EmulatorKind.MyBoy or EmulatorKind.MyOldBoy or EmulatorKind.PokeDaisy =>
                    ScanFlatFolder(root, kind, cancellationToken, state, onSave),
                EmulatorKind.Eden => ScanEden(root, cancellationToken, state, onSave),
                EmulatorKind.Azahar or EmulatorKind.CitraMmj => ScanThreeDs(root, kind, cancellationToken, state, onSave),
                _ => [],
            };
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException)
                throw;
            state.Trace($"SCAN EXCEPTION {error}");
            state.Rejected.Add($"The folder could not be read: {error.Message}");
            found = [];
        }
        state.Trace($"Scanner complete files={state.FilesSeen} saves={found.Count} rejected={state.Rejected.Count}");
        ScanCache.Flush();
        return new EmulatorScanResult(EmulatorSaveHeuristics.Normalize(found), state.FilesSeen,
            [.. state.Rejected], [.. state.Diagnostics]);
    }

    /// <summary>A single linked save file, read through its grant and described like any scanned save.</summary>
    private EmulatorScanResult ScanLinkedFile(string documentId, Action<DetectedSave>? onSave)
    {
        var state = new ScanState { Kind = EmulatorKind.LinkedFile };
        state.Trace($"Scanner={nameof(IosEmulatorScanner)} kind={EmulatorKind.LinkedFile}");
        state.Trace($"DocumentId={documentId}");
        var found = new List<DetectedSave>();
        var name = "Pokémon save";
        try
        {
            byte[] bytes;
            DateTimeOffset? modified;
            using (var file = IosDocuments.Open(documentId))
            {
                name = Path.GetFileName(file.Path) is { Length: > 0 } shown ? shown : name;
                modified = File.Exists(file.Path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file.Path)) : null;
                state.FilesSeen = 1;
                bytes = IosDocuments.ReadCoordinated(file.Url, MaxSaveBytes);
            }
            var description = engine.TryDescribe(bytes, name);
            if (description is null)
            {
                state.Trace($"PARSER REJECTED: {engine.DescribeRejection(bytes) ?? "ISaveEngine.TryDescribe returned null"}");
                state.Rejected.Add(name);
            }
            else
            {
                var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, name, null, name);
                state.Trace($"PARSER ACCEPTED game={description.GameName} generation={description.Generation} label={guess.Label}");
                var save = new DetectedSave(documentId, name, guess.Label, EmulatorKind.LinkedFile,
                    RequiresExtraCare: false, modified, description.Generation, description.TrainerName,
                    description.PlayTime, guess, Language: description.Language, TrainerId: description.TrainerId);
                found.Add(save);
                onSave?.Invoke(save);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            state.Trace($"LINKED FILE UNREADABLE {error.Message}");
            state.Rejected.Add($"{name} (linked file not readable: {error.Message})");
        }
        return new EmulatorScanResult(found, state.FilesSeen, [.. state.Rejected], [.. state.Diagnostics]);
    }

    /// <summary>Emulator folders whose contents can never be saves - pruned so a whole-RetroArch grant stays fast.</summary>
    private static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "autoconfig", "cheats", "config", "cores", "database", "downloads", "filters",
        "info", "logs", "overlays", "playlists", "remaps", "screenshots", "shaders", "states",
        "system", "thumbnails", "cache", "temp", "roms", "shader_cache",
    };

    private static bool IsPruned(string folder, EmulatorKind kind) =>
        PrunedDirectories.Contains(folder)
        && (kind == EmulatorKind.RetroArch || !string.Equals(folder, "roms", StringComparison.OrdinalIgnoreCase));

    private List<DetectedSave> ScanFlatFolder(ChildDocument root, EmulatorKind kind,
        CancellationToken cancellationToken, ScanState state, Action<DetectedSave>? onSave)
    {
        const int maxDepth = 8;
        var results = new List<DetectedSave>();
        void OnFile(ChildDocument child)
        {
            state.FilesSeen++;
            if (kind == EmulatorKind.Dolphin && EmulatorSaveHeuristics.IsDolphinMemoryCard(child.Name))
            {
                state.Rejected.Add($"{child.Name}: export Colosseum/XD as GCI with Dolphin's Memory Card Manager, or use GCI Folder for the card slot.");
                state.Trace($"RAW MEMORY CARD {child.Name}: individual GCI export required");
                return;
            }
            if (!EmulatorSaveHeuristics.IsCandidateFileName(child.Name, kind)) return;
            if (TryDetect(child, kind, gameLabel: child.Name, state: state) is { } save)
            {
                results.Add(save);
                onSave?.Invoke(save);
            }
            else
                state.Rejected.Add(child.Name);
        }

        var rootChildren = ListChildren(root);
        string[] preferredFolders = kind switch
        {
            EmulatorKind.Dolphin => ["GC"],
            EmulatorKind.DraStic => ["backup"],
            EmulatorKind.PizzaBoyGba or EmulatorKind.PizzaBoyGbc or EmulatorKind.MyBoy or EmulatorKind.MyOldBoy => ["save", "saves"],
            _ => ["saves"],
        };
        var savesDirs = rootChildren.Where(x => x.IsDirectory && preferredFolders.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (savesDirs.Length > 0)
        {
            state.Siblings = rootChildren.Where(x => !x.IsDirectory).Select(x => x.Name).ToArray();
            foreach (var file in rootChildren.Where(x => !x.IsDirectory))
                OnFile(file);
            foreach (var savesDir in savesDirs)
                FindFilesRecursive(savesDir, maxDepth, cancellationToken, OnFile, state);
        }
        else
        {
            FindFilesRecursive(root, maxDepth, cancellationToken, OnFile, state);
        }
        return results;
    }

    /// <summary>Switch saves ("main", or "*.bin" for BDSP) under nand/user/save/…, from whichever level was picked.</summary>
    private List<DetectedSave> ScanEden(ChildDocument root, CancellationToken cancellationToken,
        ScanState state, Action<DetectedSave>? onSave)
    {
        string[][] prefixes = [["nand", "user", "save"], ["user", "save"], ["save"]];
        ChildDocument? saveDir = null;
        foreach (var prefix in prefixes)
        {
            state.Trace($"Trying Eden prefix {string.Join('/', prefix)}");
            saveDir = NavigatePath(root, prefix, state.Trace);
            state.Trace(saveDir is null ? "Prefix not found" : $"Save root found: {saveDir.DocId}");
            if (saveDir is not null)
                break;
        }
        if (saveDir is null)
        {
            state.Trace("EDEN FAILURE: none of nand/user/save, user/save, or save exists under the picked folder");
            return [];
        }

        var results = new List<DetectedSave>();
        FindFilesRecursive(saveDir, EdenMaxDepth, cancellationToken, child =>
        {
            state.FilesSeen++;
            if (!EmulatorSaveHeuristics.IsEdenSaveFileName(child.Name))
            {
                state.Trace($"SKIP file name={child.Name} docId={child.DocId}");
                return;
            }
            state.Trace($"CANDIDATE name={child.Name} modified={child.LastModified?.ToString("O") ?? "unknown"} docId={child.DocId}");
            var label = EmulatorSaveHeuristics.GuessSwitchGameLabel(child.DocId);
            if (TryDetect(child, EmulatorKind.Eden, label, state) is { } save)
            {
                results.Add(save);
                onSave?.Invoke(save);
            }
            else
                state.Rejected.Add(child.Name);
        }, state, diagnostic: true);
        return results;
    }

    /// <summary>Citra-family 3DS layout, with the root resolved from whichever level was picked.</summary>
    private List<DetectedSave> ScanThreeDs(ChildDocument root, EmulatorKind kind,
        CancellationToken cancellationToken, ScanState state, Action<DetectedSave>? onSave)
    {
        var results = new List<DetectedSave>();
        IEnumerable<TreeEntry<ChildDocument>> List(ChildDocument doc) =>
            ListChildren(doc).Select(x => new TreeEntry<ChildDocument>(x.Name, x.IsDirectory, x));
        foreach (var hit in ThreeDsSaveLayout.FindMainSaves(root, root.Name, List, state.Trace, cancellationToken))
        {
            state.FilesSeen++;
            if (TryDetect(hit.File, kind, gameLabel: $"3DS save ({hit.TitleId})", state: state) is { } save)
            {
                results.Add(save);
                onSave?.Invoke(save);
            }
            else
                state.Rejected.Add($"3DS save {hit.TitleId}");
        }
        return results;
    }

    /// <summary>Reads a candidate's bytes and reports it only if the engine can parse them.</summary>
    private DetectedSave? TryDetect(ChildDocument child, EmulatorKind kind, string gameLabel, ScanState state)
    {
        try
        {
            var documentId = IosDocuments.Child(state.TreeId, child.Relative);
            state.Trace($"DocumentId={documentId}");

            var cacheKey = documentId + "#" + kind + "#" + InstallEpoch;
            var modifiedTicks = child.LastModified?.UtcTicks ?? 0;
            var isCopy = SaveNoise.IsCopyPath(child.Relative, kind == EmulatorKind.DraStic ? "backup" : null);
            if (isCopy) state.Trace("COPY (backup or hidden folder): starts in Hidden saves");
            if (ScanCache.TryGet(cacheKey, modifiedTicks, out var cached))
            {
                state.Trace(cached is null ? "CACHED rejection (unchanged since it last failed to parse)" : $"CACHED {cached.GameLabel}");
                return cached is null ? null : cached with { IsCopy = isCopy };
            }

            var length = new FileInfo(child.FullPath).Length;
            if (length > MaxSaveBytes)
            {
                state.Trace($"REJECT larger than {MaxSaveBytes} bytes");
                return null;
            }
            if (length == 0)
            {
                state.Trace("REJECT zero-byte file");
                return null;
            }
            var bytes = File.ReadAllBytes(child.FullPath);
            var headerLength = Math.Min(16, bytes.Length);
            var bdspSize = bytes.Length is 956456 or 973856 or 978316 or 979108;
            state.Trace($"BYTES length={bytes.Length} header16={Convert.ToHexString(bytes.AsSpan(0, headerLength))} knownBdspSize={bdspSize}");

            var description = engine.TryDescribe(bytes, child.Name);
            if (description is null)
            {
                state.Trace($"PARSER REJECTED: {engine.DescribeRejection(bytes) ?? "ISaveEngine.TryDescribe returned null"}");
                if (!EmulatorSaveHeuristics.RequiresExtraCare(kind))
                    ScanCache.Store(cacheKey, modifiedTicks, null);
                return null;
            }
            state.Trace($"PARSER ACCEPTED game={description.GameName} generation={description.Generation} trainer={description.TrainerName} playTime={description.PlayTime}");

            var romName = EmulatorSaveHeuristics.FindSiblingRom(child.Name, state.Siblings);
            var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, child.Name, romName, gameLabel);
            state.Trace($"IDENTITY label={guess.Label} family={guess.Family} format={guess.Format} language={description.Language ?? "-"} rom={romName ?? "-"}");
            var detected = new DetectedSave(
                documentId,
                child.Name,
                guess.Label,
                kind,
                EmulatorSaveHeuristics.RequiresExtraCare(kind),
                child.LastModified,
                description.Generation,
                description.TrainerName,
                description.PlayTime,
                guess,
                romName,
                EmulatorSaveHeuristics.FolderHint(child.DocId),
                Language: description.Language,
                IsCopy: isCopy,
                TrainerId: description.TrainerId);
            ScanCache.Store(cacheKey, modifiedTicks, detected);
            return detected;
        }
        catch (Exception error)
        {
            state.Trace($"CANDIDATE EXCEPTION {error}");
            return null; // unreadable candidates are simply not saves
        }
    }

    /// <summary>Changes with every installed build, so each new build re-reads every file once.</summary>
    private static long _installEpoch = -1;

    private static long InstallEpoch
    {
        get
        {
            if (_installEpoch >= 0) return _installEpoch;
            try
            {
                var executable = Foundation.NSBundle.MainBundle.ExecutablePath;
                _installEpoch = executable is null ? 0 : File.GetLastWriteTimeUtc(executable).Ticks;
            }
            catch (Exception)
            {
                _installEpoch = 0;
            }
            return _installEpoch;
        }
    }

    /// <summary>Persistent parse cache keyed by document id + last-modified time (as on Android).</summary>
    private static class ScanCache
    {
        private const string Key = "scan_cache_v7";
        private const int MaxEntries = 512;
        private static Dictionary<string, CacheEntry>? _entries;
        private static readonly Lock Gate = new();
        private static bool _dirty;

        private sealed record CacheEntry(long ModifiedTicks, DetectedSave? Save);

        public static bool TryGet(string documentId, long modifiedTicks, out DetectedSave? save)
        {
            lock (Gate)
            {
                Load();
                if (_entries!.TryGetValue(documentId, out var entry) && entry.ModifiedTicks == modifiedTicks && modifiedTicks != 0)
                {
                    save = entry.Save;
                    return true;
                }
            }
            save = null;
            return false;
        }

        public static void Store(string documentId, long modifiedTicks, DetectedSave? save)
        {
            if (modifiedTicks == 0) return;
            lock (Gate)
            {
                Load();
                _entries![documentId] = new CacheEntry(modifiedTicks, save);
                while (_entries.Count > MaxEntries)
                    _entries.Remove(_entries.Keys.First());
                _dirty = true;
            }
        }

        public static void Flush()
        {
            lock (Gate)
            {
                if (!_dirty || _entries is null) return;
                Preferences.Default.Set(Key, System.Text.Json.JsonSerializer.Serialize(_entries));
                _dirty = false;
            }
        }

        private static void Load()
        {
            if (_entries is not null) return;
            try
            {
                var raw = Preferences.Default.Get(Key, string.Empty);
                _entries = string.IsNullOrEmpty(raw)
                    ? []
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(raw) ?? [];
            }
            catch (Exception)
            {
                _entries = [];
            }
        }
    }

    private void FindFilesRecursive(ChildDocument parent, int maxDepth,
        CancellationToken cancellationToken, Action<ChildDocument> onFile, ScanState state,
        bool diagnostic = false)
    {
        if (maxDepth <= 0)
        {
            if (diagnostic) state.Trace($"DEPTH LIMIT reached at {parent.DocId}");
            return;
        }
        var children = ListChildren(parent);
        var siblings = children.Where(x => !x.IsDirectory).Select(x => x.Name).ToArray();
        if (diagnostic) state.Trace($"WALK depthRemaining={maxDepth} parent={parent.DocId} children={children.Count}");
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.IsDirectory)
            {
                if (diagnostic) state.Trace($"DIR name={child.Name} docId={child.DocId}");
                if (IsPruned(child.Name, state.Kind))
                {
                    if (diagnostic) state.Trace($"PRUNED directory {child.Name}");
                    continue;
                }
                FindFilesRecursive(child, maxDepth - 1, cancellationToken, onFile, state, diagnostic);
            }
            else
            {
                state.Siblings = siblings;
                onFile(child);
            }
        }
    }

    private static ChildDocument? NavigatePath(ChildDocument from, string[] segments, Action<string>? trace = null)
    {
        var current = from;
        foreach (var segment in segments)
        {
            var children = ListChildren(current);
            trace?.Invoke($"At {current.DocId}: {children.Count} children [{string.Join(", ", children.Select(x => x.IsDirectory ? x.Name + "/" : x.Name))}]");
            var next = children.FirstOrDefault(x => x.IsDirectory && x.Name == segment);
            if (next is null)
            {
                trace?.Invoke($"Missing directory segment '{segment}'");
                return null;
            }
            trace?.Invoke($"Matched '{segment}' -> {next.DocId}");
            current = next;
        }
        return current;
    }

    private static List<ChildDocument> ListChildren(ChildDocument parent)
    {
        var results = new List<ChildDocument>();
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(parent.FullPath).EnumerateFileSystemInfos(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return results; }
        foreach (var entry in entries)
        {
            var name = entry.Name;
            if (name.EndsWith(".pkforge-tmp", StringComparison.Ordinal)) continue; // our own write in progress
            var relative = parent.Relative.Length == 0 ? name : $"{parent.Relative}/{name}";
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            var modified = entry.LastWriteTimeUtc.Year > 1970 ? new DateTimeOffset(entry.LastWriteTimeUtc) : (DateTimeOffset?)null;
            results.Add(new ChildDocument($"{parent.DocId}/{name}", relative, name, isDirectory, modified, entry.FullName));
        }
        return results;
    }
}
