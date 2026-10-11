using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;

namespace ScreenshotOrganiser;

/// <summary>
/// Watches MediaStore for new images (no polling) and asks the overlay service to show the popup.
/// Watching is suspended while the screen is off or the device is locked.
/// </summary>
public sealed class ScreenshotMonitor
{
    public static ScreenshotMonitor Instance { get; } = new();

    private const string FallbackFolder = "/storage/emulated/0/Pictures/Screenshots";
    private const int DebounceMs = 700;
    private static readonly TimeSpan DirectoryChangeMargin = TimeSpan.FromSeconds(2);

    // In-memory is enough: _lastCheckTime resets on every resume, so old files are never re-detected
    private static readonly ConcurrentDictionary<string, DateTime> RecentlyMoved = new();

    private readonly object _gate = new();
    private readonly object _scanLock = new();
    private readonly HashSet<string> _processedFiles = new();
    private ScreenStateReceiver? _screenReceiver;
    private MediaObserver? _observer;
    private System.Threading.Timer? _debounce;
    private volatile bool _enabled;
    private DateTime _lastCheckTime;

    /// <summary>True when the user has monitoring switched on (even if temporarily paused by a locked screen).</summary>
    public bool IsMonitoring => _enabled;

    public void Start(bool startService = true)
    {
        // Saved first so a service (re)start racing with this call sees the enabled state
        FolderPreferenceStore.SaveMonitoringEnabled(Application.Context, true);

        lock (_gate)
        {
            if (_enabled) return;
            _enabled = true;

            RegisterScreenReceiver();
            if (IsDeviceInUse())
                BeginWatching();
        }

        if (startService)
            OverlayService.Start(Application.Context);
    }

    public void Stop(bool stopService = true)
    {
        lock (_gate)
        {
            _enabled = false;
            EndWatching();
            UnregisterScreenReceiver();
            _processedFiles.Clear();
        }

        FolderPreferenceStore.SaveMonitoringEnabled(Application.Context, false);

        if (stopService)
            OverlayService.Stop(Application.Context);
    }

    internal void Pause()
    {
        lock (_gate) EndWatching();
    }

    internal void ResumeIfUnlocked()
    {
        lock (_gate)
        {
            if (_enabled && IsDeviceInUse())
                BeginWatching();
        }
    }

    public static void MarkFileAsMoved(string filePath) => RecentlyMoved[filePath] = DateTime.Now;

    private static bool IsRecentlyMoved(string filePath) =>
        RecentlyMoved.TryGetValue(filePath, out var movedAt) && (DateTime.Now - movedAt).TotalMinutes < 5;

    // ------------------------------------------------------------------
    // Watching (call with _gate held)
    // ------------------------------------------------------------------
    private void BeginWatching()
    {
        if (_observer != null) return;

        // Files touched while paused must not trigger popups
        _lastCheckTime = DateTime.Now;

        _debounce = new System.Threading.Timer(_ => ScanForNewScreenshots(), null,
            Timeout.Infinite, Timeout.Infinite);

        _observer = new MediaObserver(this);
        Application.Context.ContentResolver?.RegisterContentObserver(
            MediaStore.Images.Media.ExternalContentUri!, true, _observer);
    }

    private void EndWatching()
    {
        if (_observer != null)
        {
            try { Application.Context.ContentResolver?.UnregisterContentObserver(_observer); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error unregistering observer: {ex.Message}"); }
            _observer.Dispose();
            _observer = null;
        }

        _debounce?.Dispose();
        _debounce = null;
    }

    internal void OnMediaChanged()
    {
        // Several notifications arrive per screenshot; collapse them into one scan
        lock (_gate) _debounce?.Change(DebounceMs, Timeout.Infinite);
    }

    // ------------------------------------------------------------------
    // Screen state
    // ------------------------------------------------------------------
    private static bool IsDeviceInUse()
    {
        var context = Application.Context;
        var power = context.GetSystemService(Context.PowerService) as PowerManager;
        var keyguard = context.GetSystemService(Context.KeyguardService) as KeyguardManager;

        return (power?.IsInteractive ?? true) && !(keyguard?.IsKeyguardLocked ?? false);
    }

    private void RegisterScreenReceiver()
    {
        if (_screenReceiver != null) return;

        var filter = new IntentFilter();
        filter.AddAction(Intent.ActionScreenOff);
        filter.AddAction(Intent.ActionScreenOn);
        filter.AddAction(Intent.ActionUserPresent);

        _screenReceiver = new ScreenStateReceiver();

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            Application.Context.RegisterReceiver(_screenReceiver, filter, ReceiverFlags.NotExported);
        else
            Application.Context.RegisterReceiver(_screenReceiver, filter);
    }

    private void UnregisterScreenReceiver()
    {
        if (_screenReceiver == null) return;

        try { Application.Context.UnregisterReceiver(_screenReceiver); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error unregistering receiver: {ex.Message}"); }

        _screenReceiver.Dispose();
        _screenReceiver = null;
    }

    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------
    private void ScanForNewScreenshots()
    {
        lock (_scanLock)
        {
            try
            {
                if (!_enabled) return;

                // Service was killed: bring it back
                if (OverlayService.Instance == null)
                    OverlayService.Start(Application.Context);

                var checkStart = DateTime.Now;
                var folder = FolderPreferenceStore.GetDefaultFolder(Application.Context) ?? FallbackFolder;

                foreach (var screenshot in FindNewScreenshots(folder, _lastCheckTime))
                {
                    lock (_gate)
                    {
                        if (!_processedFiles.Add(screenshot)) continue;
                    }

                    if (IsRecentlyMoved(screenshot) || !File.Exists(screenshot)) continue;

                    OverlayService.Instance?.ShowScreenshotDialog(screenshot);
                }

                _lastCheckTime = checkStart;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking screenshots: {ex.Message}");
            }
        }
    }

    private static List<string> FindNewScreenshots(string folder, DateTime since)
    {
        var result = new List<string>();
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists) return result;

        // The folder's mtime changes whenever a file is added, so an unchanged folder needs no listing
        if (directory.LastWriteTime < since - DirectoryChangeMargin) return result;

        foreach (var file in directory.EnumerateFiles())
        {
            if (file.Name.StartsWith('.') || !IsImageExtension(file.Extension)) continue;

            var changed = file.CreationTime > file.LastWriteTime ? file.CreationTime : file.LastWriteTime;
            if (changed > since && IsScreenshotFile(file.FullName))
                result.Add(file.FullName);
        }

        return result;
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static bool IsScreenshotFile(string filePath)
    {
        var name = Path.GetFileName(filePath).ToLowerInvariant();
        var path = filePath.ToLowerInvariant();

        return name.Contains("screenshot") ||
               name.StartsWith("screen_") ||
               name.StartsWith("scrnshot") ||
               name.Contains("screen-") ||
               path.Contains("/screenshots/") ||
               path.Contains("/screenshot/");
    }

    private sealed class MediaObserver : ContentObserver
    {
        private readonly ScreenshotMonitor _owner;

        public MediaObserver(ScreenshotMonitor owner) : base(new Handler(Looper.MainLooper!)) => _owner = owner;

        public override void OnChange(bool selfChange) => _owner.OnMediaChanged();
    }
}

internal sealed class ScreenStateReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        switch (intent?.Action)
        {
            case Intent.ActionScreenOff:
                ScreenshotMonitor.Instance.Pause();
                break;
            case Intent.ActionScreenOn:
            case Intent.ActionUserPresent:
                ScreenshotMonitor.Instance.ResumeIfUnlocked();
                break;
        }
    }
}
