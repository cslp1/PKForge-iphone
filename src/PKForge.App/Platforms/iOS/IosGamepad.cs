using Foundation;
using GameController;
using PKForge.App.Services;

namespace PKForge.App;

/// <summary>
/// The iOS side of what MainActivity does with key and motion events on Android: MFi, Xbox,
/// PlayStation and Switch controllers (GameController framework) feed the app's GamepadRouter.
/// Same rules: directions repeat after a hold, L/R repeat where the screen pages with them,
/// every release is reported, and the sticks go to screens that read them (the Poképark camera).
/// </summary>
internal static class IosGamepad
{
    private const int HoldDelayMs = 320, RepeatMs = 80;
    private const int ShoulderHoldDelayMs = 380, ShoulderRepeatMs = 140;
    private const float DeadZone = .25f;

    private static readonly Dictionary<PadButton, int> Held = [];
    private static int _token;
    private static float _leftX, _leftY, _rightX, _rightY;
    private static NSObject? _connected;

    private static GamepadRouter? Router => IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

    public static void Start()
    {
        _connected ??= GCController.Notifications.ObserveDidConnect((_, args) =>
            MainThread.BeginInvokeOnMainThread(() => Attach(args.Notification.Object as GCController)));
        foreach (var controller in GCController.Controllers)
            Attach(controller);
    }

    private static void Attach(GCController? controller)
    {
        if (controller?.ExtendedGamepad is not { } pad) return;
        Bind(pad.ButtonA, PadButton.A);
        Bind(pad.ButtonB, PadButton.B);
        Bind(pad.ButtonX, PadButton.X);
        Bind(pad.ButtonY, PadButton.Y);
        Bind(pad.LeftShoulder, PadButton.L);
        Bind(pad.RightShoulder, PadButton.R);
        Bind(pad.ButtonMenu, PadButton.Start);
        Bind(pad.ButtonOptions, PadButton.Select);
        Bind(pad.DPad.Up, PadButton.Up);
        Bind(pad.DPad.Down, PadButton.Down);
        Bind(pad.DPad.Left, PadButton.Left);
        Bind(pad.DPad.Right, PadButton.Right);
        // GameController's Y points up; the app's sticks follow Android's (down is positive).
        pad.LeftThumbstick.ValueChangedHandler = (_, x, y) =>
            MainThread.BeginInvokeOnMainThread(() => Stick(ref _leftX, ref _leftY, x, -y));
        pad.RightThumbstick.ValueChangedHandler = (_, x, y) =>
            MainThread.BeginInvokeOnMainThread(() => Stick(ref _rightX, ref _rightY, x, -y));
    }

    private static void Bind(GCControllerButtonInput? input, PadButton button)
    {
        if (input is null) return;
        input.PressedChangedHandler = (_, _, pressed) => MainThread.BeginInvokeOnMainThread(() => OnButton(button, pressed));
    }

    private static void OnButton(PadButton button, bool pressed)
    {
        if (Router is not { } router) return;
        if (!pressed)
        {
            Held.Remove(button);
            router.DispatchRelease(button);
            return;
        }
        var token = ++_token;
        Held[button] = token;
        // Asked before dispatching: the press itself may open another screen.
        var repeats = IsDirectional(button) || (button is PadButton.L or PadButton.R && router.TopPagesWithShoulders);
        if (router.Dispatch(button) && repeats)
            _ = RepeatAsync(router, button, token);
    }

    private static async Task RepeatAsync(GamepadRouter router, PadButton button, int token)
    {
        var directional = IsDirectional(button);
        await Task.Delay(directional ? HoldDelayMs : ShoulderHoldDelayMs);
        while (Held.TryGetValue(button, out var held) && held == token)
        {
            if (!directional && !router.TopPagesWithShoulders) return;
            if (!router.Dispatch(button)) return;
            await Task.Delay(directional ? RepeatMs : ShoulderRepeatMs);
        }
    }

    private static void Stick(ref float storedX, ref float storedY, float x, float y)
    {
        storedX = Dead(x);
        storedY = Dead(y);
        Router?.DispatchStick(_leftX, _leftY, _rightX, _rightY);
    }

    // Past the dead zone (worn sticks drift), rescaled to start at 0.
    private static float Dead(float value) =>
        Math.Abs(value) < DeadZone ? 0 : Math.Sign(value) * (Math.Abs(value) - DeadZone) / (1 - DeadZone);

    private static bool IsDirectional(PadButton button) =>
        button is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right;
}
