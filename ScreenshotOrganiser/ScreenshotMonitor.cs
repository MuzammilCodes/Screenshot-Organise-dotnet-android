using System.Collections.Concurrent;
using System.Timers;
using Android.App;

namespace ScreenshotOrganiser;

/// <summary>Polls the default folder every 3 s and asks the overlay service to show the popup.</summary>
public sealed class ScreenshotMonitor
{
    public static ScreenshotMonitor Instance { get; } = new();

    private const string FallbackFolder = "/storage/emulated/0/Pictures/Screenshots";
    private const int PollIntervalMs = 3000;

    // In-memory is enough: _lastCheckTime resets on process start, so old files are never re-detected
    private static readonly ConcurrentDictionary<string, DateTime> RecentlyMoved = new();

    private readonly object _gate = new();
    private readonly HashSet<string> _processedFiles = new();
    private System.Timers.Timer? _timer;
    private DateTime _lastCheckTime;

    public bool IsMonitoring => _timer?.Enabled ?? false;

    public void Start()
    {
        lock (_gate)
        {
            if (IsMonitoring) return;

            _lastCheckTime = DateTime.Now;
            OverlayService.Start(Application.Context);

            _timer = new System.Timers.Timer(PollIntervalMs) { AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
        }
    }

    public void Stop(bool stopService = true)
    {
        lock (_gate)
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
            _processedFiles.Clear();
        }

        if (stopService)
            OverlayService.Stop(Application.Context);
    }

    public static void MarkFileAsMoved(string filePath) => RecentlyMoved[filePath] = DateTime.Now;

    private static bool IsRecentlyMoved(string filePath) =>
        RecentlyMoved.TryGetValue(filePath, out var movedAt) && (DateTime.Now - movedAt).TotalMinutes < 5;

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        try
        {
            // Service was killed: bring it back
            if (OverlayService.Instance == null)
                OverlayService.Start(Application.Context);

            var checkStart = DateTime.Now;

            // Re-read each tick so a folder changed while monitoring is picked up
            var folder = FolderPreferenceStore.GetDefaultFolder(Application.Context) ?? FallbackFolder;

            foreach (var screenshot in FindNewScreenshots(folder))
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

    private IEnumerable<string> FindNewScreenshots(string folder)
    {
        if (!Directory.Exists(folder)) return Enumerable.Empty<string>();

        return Directory.GetFiles(folder, "*.png")
            .Concat(Directory.GetFiles(folder, "*.jpg"))
            .Concat(Directory.GetFiles(folder, "*.jpeg"))
            .Where(f =>
            {
                var created = File.GetCreationTime(f);
                var modified = File.GetLastWriteTime(f);
                return (created > modified ? created : modified) > _lastCheckTime;
            })
            .Where(IsScreenshotFile)
            .ToList();
    }

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
}
