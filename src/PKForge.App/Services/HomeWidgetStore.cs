using PKForge.App.Services;
using PKForge.Domain;
using SkiaSharp;
using System.Text.Json;

namespace PKForge.App;

// Shared by every platform: Android's home-screen widgets read these files, and the app writes
// them (the last save, the partner) on every platform. Moved out of Platforms/Android/HomeWidgets.cs.

/// <summary>A painted widget: its frames (the launcher flips through them), how long each stays, and what it says.</summary>
public sealed class WidgetPicture(IReadOnlyList<SKBitmap> frames, int intervalMs, string description) : IDisposable
{
    public IReadOnlyList<SKBitmap> Frames { get; } = frames;
    public int IntervalMs { get; } = intervalMs;
    public string Description { get; } = description;

    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
    }
}

/// <summary>The last save opened in PKForge: what "Open last save" and the egg widget follow.</summary>
public sealed record LastSave(string DocumentId, string Name);

/// <summary>
/// The three new home-screen widgets' files, in the app's own folder (never in a save): the
/// choices made in the Widgets menu, each save's eggs as last read, the last save opened, the
/// day's first pedometer reading, and the sprites the widgets draw (written from the app, so a
/// widget can paint itself while PKForge is closed).
/// </summary>
public static class HomeWidgetStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    internal static readonly object FileLock = new();

#if ANDROID
    // Read straight from the context: a widget update can run in a cold process.
    public static string Folder => System.IO.Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "home-widgets");
#else
    public static string Folder => System.IO.Path.Combine(FileSystem.AppDataDirectory, "home-widgets");
#endif
    private static string PathOf(string name) => System.IO.Path.Combine(Folder, name);

    private static T? Read<T>(string name) where T : class
    {
        lock (FileLock)
        {
            try
            {
                var path = PathOf(name);
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
#if ANDROID
                Android.Util.Log.Warn("Widgets", $"{name} unreadable: {ex.Message}");
#else
                AppLog.Warn("widgets", $"{name} unreadable: {ex.Message}");
#endif
                return null;
            }
        }
    }

    private static void Write<T>(string name, T value)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            var temporary = PathOf(name) + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, PathOf(name), true);
        }
    }

    /// <summary>The Widgets menu's choices, read fresh each time (a widget update may run in a cold process).</summary>
    public static HomeWidgetSettings Settings
    {
        get
        {
            lock (FileLock)
            {
                try
                {
                    var path = PathOf("settings.json");
                    return File.Exists(path) ? HomeWidgetSettings.FromJson(File.ReadAllText(path)) : new();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
    #if ANDROID
                Android.Util.Log.Warn("Widgets", $"settings.json unreadable: {ex.Message}");
#else
                AppLog.Warn("widgets", $"settings.json unreadable: {ex.Message}");
#endif
                    return new();
                }
            }
        }
    }

    public static void Save(HomeWidgetSettings settings)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            var temporary = PathOf("settings.json") + ".tmp";
            File.WriteAllText(temporary, settings.ToJson());
            File.Move(temporary, PathOf("settings.json"), true);
        }
    }

    public static Dictionary<string, EggWidgetSnapshot> Eggs => Read<Dictionary<string, EggWidgetSnapshot>>("eggs.json") ?? [];

    /// <summary>Keeps one save's eggs (the twelve saves read most recently).</summary>
    public static void Save(EggWidgetSnapshot snapshot)
    {
        var all = Eggs;
        all[snapshot.DocumentId] = snapshot;
        Write("eggs.json", all.Values.OrderByDescending(s => s.WrittenAt).Take(12).ToDictionary(s => s.DocumentId));
    }

    public static LastSave? Last => Read<LastSave>("last-save.json");
    public static void Save(LastSave last) => Write("last-save.json", last);

    public static WalkerDayCount? WalkerDay => Read<WalkerDayCount>("walker-day.json");
    public static void Save(WalkerDayCount count) => Write("walker-day.json", count);

    /// <summary>The egg widget's save: the one picked in the menu, or the last one opened.</summary>
    public static EggWidgetSnapshot? EggSnapshot(HomeWidgetSettings settings)
    {
        var id = settings.EggSave ?? Last?.DocumentId;
        return id is not null && Eggs.TryGetValue(id, out var snapshot) ? snapshot : null;
    }

    public static string SpriteFile(int species, int form, bool shiny, string kind) =>
        PathOf($"{kind}-{species}-{form}-{(shiny ? "s" : "n")}.png");

    public static SKBitmap? LoadSprite(string path)
    {
        lock (FileLock)
            return File.Exists(path) ? SKBitmap.Decode(path) : null;
    }

    public static void SaveSprite(string path, SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        lock (FileLock)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(path, data.ToArray());
        }
    }
}
