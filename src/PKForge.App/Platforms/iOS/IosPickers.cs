using Foundation;
using PKForge.Domain;
using UIKit;
using UniformTypeIdentifiers;

namespace PKForge.App;

/// <summary>Shows the Files picker in place (not as a copy), so PKForge can write the picked file back.</summary>
internal static class IosPicker
{
    // The picker on screen, kept alive until it answers.
    private static UIDocumentPickerViewController? _open;

    public static Task<NSUrl[]> PickAsync(UTType[] types, bool multiple, CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_open is not null) throw new InvalidOperationException("A file picker is already open.");
            var done = new TaskCompletionSource<NSUrl[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var picker = new UIDocumentPickerViewController(types, false)
            {
                AllowsMultipleSelection = multiple,
                ShouldShowFileExtensions = true,
            };
            picker.DidPickDocumentAtUrls += (_, args) => done.TrySetResult(args.Urls ?? []);
            picker.WasCancelled += (_, _) => done.TrySetResult([]);
            var host = Platform.GetCurrentUIViewController()
                ?? throw new InvalidOperationException("No screen is available to show the file picker.");
            _open = picker;
            var registration = cancellationToken.Register(() => MainThread.BeginInvokeOnMainThread(() =>
            {
                picker.DismissViewController(true, null);
                done.TrySetCanceled(cancellationToken);
            }));
            _ = done.Task.ContinueWith(_ =>
            {
                registration.Dispose();
                MainThread.BeginInvokeOnMainThread(() => { if (_open == picker) _open = null; });
            }, TaskScheduler.Default);
            host.PresentViewController(picker, true, null);
            return done.Task;
        });
}

public sealed class IosDocumentPicker : IDocumentPicker
{
    public async ValueTask<PickedDocument?> PickSaveAsync(CancellationToken cancellationToken = default)
    {
        var urls = await IosPicker.PickAsync([UTTypes.Item], multiple: false, cancellationToken).ConfigureAwait(false);
        if (urls.Length == 0) return null;
        var url = urls[0];
        return new PickedDocument(IosDocuments.RegisterFile(url), IosDocuments.DisplayName(url, "Pokémon save"));
    }

    public async ValueTask<IReadOnlyList<PickedDocument>> PickManyAsync(CancellationToken cancellationToken = default)
    {
        var urls = await IosPicker.PickAsync([UTTypes.Item], multiple: true, cancellationToken).ConfigureAwait(false);
        var documents = new List<PickedDocument>();
        foreach (var url in urls)
        {
            try { documents.Add(new PickedDocument(IosDocuments.RegisterFile(url), IosDocuments.DisplayName(url, "file"))); }
            catch (IOException error) { Services.AppLog.Warn("files", $"Skipped {url.LastPathComponent}: {error.Message}"); }
        }
        return documents;
    }
}

public sealed class IosFolderPicker : IFolderPicker
{
    public async ValueTask<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        var urls = await IosPicker.PickAsync([UTTypes.Folder], multiple: false, cancellationToken).ConfigureAwait(false);
        if (urls.Length == 0) return null;
        var url = urls[0];
        return new PickedFolder(IosDocuments.RegisterTree(url), IosDocuments.DisplayName(url, "folder"));
    }
}

/// <summary>Reads and writes saves through their lasting grants (see <see cref="IosDocuments"/>).</summary>
public sealed class IosSaveFileAccess : ISaveFileAccess
{
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => (ReadOnlyMemory<byte>)IosDocuments.ReadAll(documentId), cancellationToken));

    public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        new(Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IosDocuments.WriteAll(documentId, bytes);
        }, cancellationToken));
}

/// <summary>
/// Files inside a picked folder: the flat listing (bank archives, bulk moves) and the whole tree
/// by relative path (the PKForge folder mirror). Names are matched exactly, as on Android.
/// </summary>
public sealed class IosFolderFileAccess : IFolderFileAccess, IFolderTreeAccess
{
    public ValueTask<IReadOnlyList<PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default) =>
        new(Task.Run<IReadOnlyList<PickedDocument>>(() =>
        {
            using var folder = IosDocuments.Open(treeId);
            return [.. Directory.EnumerateFiles(folder.Path)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !name.StartsWith('.'))
                .Select(name => new PickedDocument(IosDocuments.Child(treeId, name), name))];
        }, cancellationToken));

    public ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => (ReadOnlyMemory<byte>)IosDocuments.ReadAll(documentId), cancellationToken));

    public ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        new(Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IosDocuments.WriteAll(IosDocuments.Child(treeId, CheckedName(fileName)), bytes);
        }, cancellationToken));

    public ValueTask<string> WriteBesideAsync(string documentId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        new(Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only a file found through a linked folder has a folder PKForge may write in.
            var (treeId, relative) = IosDocuments.SplitChild(documentId);
            var slash = relative.LastIndexOf('/');
            var parent = slash > 0 ? relative[..slash] : "";
            string free;
            using (var folder = IosDocuments.Open(IosDocuments.Child(treeId, parent)))
            {
                var taken = Directory.EnumerateFileSystemEntries(folder.Path).Select(Path.GetFileName).OfType<string>();
                free = SaveInsights.FreeFileName(CheckedName(fileName), taken);
            }
            var target = IosDocuments.Child(treeId, parent.Length == 0 ? free : $"{parent}/{free}");
            using (var scoped = IosDocuments.Open(target))
                if (File.Exists(scoped.Path)) throw new IOException($"{free} appeared in the folder meanwhile.");
            IosDocuments.WriteAll(target, bytes);
            return free;
        }, cancellationToken));

    // ── The whole tree by relative path ──────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<FolderEntry>> ListTreeAsync(string treeId, CancellationToken cancellationToken = default) =>
        new(Task.Run<IReadOnlyList<FolderEntry>>(() =>
        {
            using var folder = IosDocuments.Open(treeId);
            var root = folder.Path.TrimEnd('/') + "/";
            var found = new List<FolderEntry>();
            foreach (var path in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : Path.GetFileName(path);
                if (relative.Split('/').Any(part => part.StartsWith('.'))) continue; // our temporaries, .DS_Store
                var info = new FileInfo(path);
                found.Add(new FolderEntry(relative, info.Length, new DateTimeOffset(info.LastWriteTimeUtc)));
            }
            return found;
        }, cancellationToken));

    public ValueTask<byte[]> ReadAsync(string treeId, string path, CancellationToken cancellationToken = default) =>
        new(Task.Run(() =>
        {
            var id = IosDocuments.Child(treeId, path);
            using (var scoped = IosDocuments.Open(id))
                if (!File.Exists(scoped.Path)) throw new FileNotFoundException($"{path} is not in the folder any more.");
            return IosDocuments.ReadAll(id);
        }, cancellationToken));

    public ValueTask WriteAsync(string treeId, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => IosDocuments.WriteAll(IosDocuments.Child(treeId, path), bytes, createFolders: true), cancellationToken));

    public ValueTask<bool> DeleteAsync(string treeId, string path, CancellationToken cancellationToken = default) =>
        new(Task.Run(() =>
        {
            using var scoped = IosDocuments.Open(IosDocuments.Child(treeId, path));
            if (!File.Exists(scoped.Path)) return false;
            File.Delete(scoped.Path);
            return true;
        }, cancellationToken));

    private static string CheckedName(string fileName) =>
        fileName.Length == 0 || fileName.Contains('/') || fileName is "." or ".."
            ? throw new ArgumentException($"\"{fileName}\" is not a file name.", nameof(fileName))
            : fileName;
}
