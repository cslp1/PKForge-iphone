using AVFoundation;
using PKForge.Domain;

namespace PKForge.App;

/// <summary>
/// The iOS port of Android's MusicPlayer (AVAudioPlayer in place of MediaPlayer; everything else is
/// the same logic). AVAudioPlayer-based background music: one platform player, gapless-enough track
/// advancement on completion, persisted library + order + autostart + each place's music in
/// Preferences. Tracks live as picked-file ids (read through their lasting grants), never copied.
/// Moving to a place with other music fades one out and the other in; coming back to a
/// place picks its music up where it was left.
/// </summary>
public sealed class IosMusicPlayer : IMusicPlayer, IDisposable
{
    private const int FadeSteps = 12, FadeStepMs = 50;

    private AVAudioPlayer? _player;
    private ScopedPath? _playerFile;
    private readonly List<MusicTrack> _library = [];
    private readonly Random _rng = new();
    private readonly Dictionary<string, (int Index, int Position)> _resume = new();
    private int? _index;
    private bool _on;
    private int _fade, _failures;
    private MusicPlace _place = MusicPlace.Menus;
    // What the place plays (null: silence), and what the platform player is actually playing
    // from: they differ while one fades into the other.
    private int[]? _playlist;
    private int[]? _playerList;

    public IReadOnlyList<MusicTrack> Library => _library;
    public bool IsPlaying => _player?.Playing == true;
    public bool IsOn => _on;
    public int? CurrentIndex => _index;
    public MusicPlace Place => _place;
    public MusicOrder Order
    {
        get => Enum.Parse<MusicOrder>(Preferences.Default.Get("music_order", nameof(MusicOrder.InOrder)));
        set => Preferences.Default.Set("music_order", value.ToString());
    }
    public bool Autostart
    {
        get => Preferences.Default.Get("music_autostart", false);
        set => Preferences.Default.Set("music_autostart", value);
    }

    public int Volume => MusicPlaces.ClampVolume(Preferences.Default.Get("music_volume", MusicPlaces.DefaultVolume));

    public void SetVolume(int volume)
    {
        Preferences.Default.Set("music_volume", MusicPlaces.ClampVolume(volume));
        // Heard at once; a fade in progress picks the new gain up on its next step.
        if (_player is { } player) Apply(player, _level);
    }

    private float _level = 1;

    // Fades work in 0..1 and the user's volume scales them.
    private void Apply(AVAudioPlayer player, float level)
    {
        _level = level;
        player.Volume = level * MusicPlaces.Gain(Volume);
    }

    private bool Shuffling => MusicPlaces.Shuffles(_place, SourceFor, Order);

    public IosMusicPlayer()
    {
        var saved = Preferences.Default.Get("music_library", "");
        if (saved.Length > 0)
            foreach (var entry in saved.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('\u0001');
                if (parts.Length == 2) _library.Add(new MusicTrack(parts[0], parts[1]));
            }
    }

    public int Add(IReadOnlyList<PickedDocument> documents)
    {
        var added = 0;
        foreach (var document in documents)
        {
            if (_library.Any(t => t.DocumentId == document.DocumentId)) continue;
            _library.Add(new MusicTrack(document.DocumentId, document.DisplayName));
            added++;
        }
        Persist();
        // Indices moved under the playlists: work them out again.
        _resume.Clear();
        _playlist = Playlist();
        if (_playerList is not null) _playerList = _playlist;
        return added;
    }

    public void Remove(int index)
    {
        if ((uint)index >= _library.Count) return;
        if (_index == index) { StopInternal(); _index = null; }
        else if (_index > index) _index--;
        _library.RemoveAt(index);
        Persist();
        _resume.Clear();
        _playlist = Playlist();
        if (_playerList is not null) _playerList = _playlist;
    }

    public void Clear()
    {
        StopInternal();
        _library.Clear();
        _index = null;
        _resume.Clear();
        _playlist = null;
        Persist();
    }

    public void Play()
    {
        _on = true;
        _playlist = Playlist();
        // A silent place stays silent; the music comes back where there is some.
        if (_playlist is null) return;
        _fade++;
        if (_player is not null && _index is not null && _playerList is not null && SameList(_playerList, _playlist))
        {
            Apply(_player, 1);
            _player.Play();
            return;
        }
        Start(_playlist, fadeIn: false);
    }

    public void Pause()
    {
        _on = false;
        _fade++;
        _player?.Pause();
    }

    public void Skip()
    {
        var list = _playlist ?? Playlist();
        if (list is null) return;
        _fade++;
        var at = _index is { } index ? Array.IndexOf(list, index) : -1;
        var next = Shuffling && list.Length > 1
            ? list.Where(i => i != _index).ElementAt(_rng.Next(list.Length - (at >= 0 ? 1 : 0)))
            : list[(at + 1) % list.Length];
        _on = true;
        _playerList = list;
        PlayIndex(next, 0, 1);
    }

    public void SetOrder(MusicOrder order) => Order = order;

    public void SetAutostart(bool on) => Autostart = on;

    public MusicSource SourceFor(MusicPlace place) => MusicSource.Parse(Preferences.Default.Get(SourceKey(place), ""));

    public void SetSource(MusicPlace place, MusicSource source)
    {
        Preferences.Default.Set(SourceKey(place), source.Serialize());
        EnterPlace(_place);
    }

    public void EnterPlace(MusicPlace place)
    {
        _place = place;
        var list = Playlist();
        if (_playlist is not null && list is not null && SameList(_playlist, list)) return;
        _playlist = list;
        if (!_on) return;
        _ = CrossfadeAsync(list);
    }

    /// <summary>Starts playback at app launch when the user asked for it.</summary>
    public void MaybeAutostart()
    {
        if (Autostart && _library.Count > 0) Play();
    }

    private bool _pausedInBackground;

    /// <summary>The app left the screen (Home, another app): music stops with it.</summary>
    public void PauseForBackground()
    {
        if (_player?.Playing != true) return;
        _player.Pause();
        _pausedInBackground = true;
    }

    /// <summary>The app is back: music paused by <see cref="PauseForBackground"/> carries on.</summary>
    public void ResumeFromBackground()
    {
        if (!_pausedInBackground) return;
        _pausedInBackground = false;
        _player?.Play();
    }

    /// <summary>Set when playback fails; the UI shows it so failures are never silent.</summary>
    public string? LastError { get; private set; }

    private int[]? Playlist() => MusicPlaces.Playlist(_place, SourceFor, _library);

    private static string SourceKey(MusicPlace place) => $"music_place_{place}";

    private static bool SameList(int[] a, int[] b) => a.AsSpan().SequenceEqual(b);

    private static string Key(int[] list) => string.Join(",", list);

    // The old music fades out, its spot is kept, and the new one fades in from where it was left.
    private async Task CrossfadeAsync(int[]? list)
    {
        var fade = ++_fade;
        if (_player is { } old && _playerList is not null && _index is { } index)
        {
            if (!_pausedInBackground) _resume[Key(_playerList)] = (index, (int)(old.CurrentTime * 1000));
            if (old.Playing)
                for (var step = FadeSteps - 1; step >= 0; step--)
                {
                    Apply(old, (float)step / FadeSteps);
                    await Task.Delay(FadeStepMs);
                    if (fade != _fade || _player != old) return;
                }
        }
        StopInternal();
        _playerList = null;
        if (list is null || !_on) return;
        Start(list, fadeIn: true);
        if (_player is not { } player) return;
        for (var step = 1; step <= FadeSteps; step++)
        {
            await Task.Delay(FadeStepMs);
            if (fade != _fade || _player != player) return;
            Apply(player, (float)step / FadeSteps);
        }
    }

    private void Start(int[] list, bool fadeIn)
    {
        _playerList = list;
        var (index, position) = _resume.TryGetValue(Key(list), out var spot) && list.Contains(spot.Index)
            ? spot
            : (Shuffling ? list[_rng.Next(list.Length)] : list[0], 0);
        PlayIndex(index, position, fadeIn ? 0 : 1);
    }

    private void PlayIndex(int index, int position, float volume)
    {
        StopInternal();
        _index = index;
        var track = _library[index];
        LastError = null;
        try
        {
            // The track's grant stays open for as long as it plays.
            _playerFile = IosDocuments.Open(track.DocumentId);
            _player = AVAudioPlayer.FromUrl(_playerFile.Url, out var error)
                ?? throw new IOException(error?.LocalizedDescription ?? $"{track.Title} cannot be played.");
            _player.PrepareToPlay();
            if (position > 0) _player.CurrentTime = position / 1000.0;
            Apply(_player, volume);
            // Hidden by another app or the Bad Apple egg: it starts when they give the screen back.
            if (!_pausedInBackground) _player.Play();
            _failures = 0;
            _player.FinishedPlaying += (_, _) => MainThread.BeginInvokeOnMainThread(Skip);
        }
        catch (Exception error)
        {
            LastError = error.Message;
            // A player that never opened its track cannot be stopped or started again: let it go,
            // so Play tries afresh instead of poking a dead one.
            _player?.Dispose();
            _player = null;
            _playerFile?.Dispose();
            _playerFile = null;
            // Unplayable track: skip forward instead of dying.
            if (_playerList is { Length: > 1 } list && ++_failures < list.Length) Skip();
        }
    }

    private void StopInternal()
    {
        if (_player is not null)
        {
            _player.Stop();
            _player.Dispose();
            _player = null;
        }
        _playerFile?.Dispose();
        _playerFile = null;
    }

    private void Persist() =>
        Preferences.Default.Set("music_library", string.Join("\n", _library.Select(t => $"{t.DocumentId}\u0001{t.Title}")));

    public void Dispose() => StopInternal();
}
