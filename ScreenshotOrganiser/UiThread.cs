using Android.OS;

namespace ScreenshotOrganiser;

internal static class UiThread
{
    private static readonly Handler Main = new(Looper.MainLooper!);

    public static void Post(Action action) => Main.Post(action);

    public static void PostDelayed(Action action, long delayMs) => Main.PostDelayed(action, delayMs);
}
