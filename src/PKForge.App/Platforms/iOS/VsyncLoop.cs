using CoreAnimation;
using Foundation;

namespace PKForge.App.Services;

/// <summary>
/// The iOS counterpart of the Android VsyncLoop (Services/VsyncLoop.cs): a loop that runs once
/// per display refresh, on the UI thread, driven by CADisplayLink, and tells each step the real
/// time since the last one. Same contract: Start/Stop from the UI thread, the step returns
/// whether the loop goes on, and a step that throws stops the loop instead of the app.
/// </summary>
public sealed class VsyncLoop
{
    private readonly Func<float, bool> _step;
    private CADisplayLink? _link;
    private double _last;

    /// <param name="step">Called each frame with the milliseconds since the last one (at most 50);
    /// returns whether the loop goes on.</param>
    public VsyncLoop(Func<float, bool> step) => _step = step;

    public bool IsRunning { get; private set; }

    /// <summary>Starts the loop (from the UI thread); nothing happens if it already runs.</summary>
    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _last = CAAnimation.CurrentMediaTime();
        var link = CADisplayLink.Create(() => DoFrame());
        _link = link;
        link.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
    }

    /// <summary>Stops the loop (from the UI thread): no further step runs.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        Release();
    }

    private void Release()
    {
        _link?.Invalidate();
        _link = null;
    }

    private void DoFrame()
    {
        var link = _link;
        if (link is null || !IsRunning) return;
        var now = link.Timestamp;
        // The first frame can predate the start: count it as one frame at 60 Hz.
        var elapsed = (float)((now - _last) * 1000);
        elapsed = elapsed <= 0 ? 1000f / 60f : Math.Min(elapsed, 50f);
        _last = now;
        bool more;
        try { more = _step(elapsed); }
        catch (Exception error)
        {
            IsRunning = false;
            Release();
            AppLog.Error("frame", "An animation step failed; it was stopped", error);
            return;
        }
        if (!IsRunning || _link != link) return; // the step stopped it (and maybe started it again)
        if (!more)
        {
            IsRunning = false;
            Release();
        }
    }
}
