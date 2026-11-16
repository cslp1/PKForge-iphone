using Foundation;
using UIKit;

namespace PKForge.App;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var started = base.FinishedLaunching(application, launchOptions!);
        IosGamepad.Start();
        return started;
    }
}
