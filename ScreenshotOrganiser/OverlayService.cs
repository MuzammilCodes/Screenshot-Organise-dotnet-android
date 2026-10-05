using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using IOPath = System.IO.Path;

namespace ScreenshotOrganiser;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class OverlayService : Service
{
    private const int NotificationId = 1001;
    private const string ChannelId = "screenshot_service";
    private const string ActionStop = "STOP_SERVICE";
    private const string StorageRoot = FolderPickerViewFactory.StorageRoot;
    private const float SnackbarTopOffsetPercent = 0.025f;
    private const int SnackbarDurationMs = 2500;

    private IWindowManager? _windowManager;
    private View? _overlayView;
    private View? _pickerView;
    private PowerManager.WakeLock? _wakeLock;
    private bool _isRunning;

    public static OverlayService? Instance { get; private set; }

    public static void Start(Context context)
    {
        try
        {
            context.StartForegroundService(new Intent(context, typeof(OverlayService)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error starting overlay service: {ex.Message}");
        }
    }

    public static void Stop(Context context)
    {
        try
        {
            context.StopService(new Intent(context, typeof(OverlayService)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error stopping overlay service: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Service lifecycle
    // ------------------------------------------------------------------
    public override void OnCreate()
    {
        base.OnCreate();

        try
        {
            Instance = this;
            _isRunning = true;

            _windowManager = GetSystemService(WindowService)?.JavaCast<IWindowManager>();

            var powerManager = GetSystemService(PowerService) as PowerManager;
            _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "ScreenshotOrganizer::ServiceWakeLock");
            _wakeLock?.Acquire();

            CreateNotificationChannel();
            StartForegroundCompat();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnCreate: {ex.Message}");
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            if (intent?.Action == ActionStop)
            {
                ScreenshotMonitor.Instance.Stop(stopService: false);
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            if (_isRunning)
                StartForegroundCompat();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnStartCommand: {ex.Message}");
        }

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        try
        {
            _isRunning = false;
            HideDialog();
            HidePicker();

            _wakeLock?.Release();
            _wakeLock = null;

            Instance = null;
            StopForeground(StopForegroundFlags.Remove);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnDestroy: {ex.Message}");
        }

        base.OnDestroy();
    }

    // ------------------------------------------------------------------
    // Overlay: "Screenshot detected" card
    // ------------------------------------------------------------------
    public void ShowScreenshotDialog(string screenshotPath)
    {
        if (_windowManager == null || !_isRunning) return;

        UiThread.Post(() =>
        {
            try
            {
                HideDialog();

                _overlayView = FolderPickerViewFactory.BuildScreenshotDetectedCard(
                    this,
                    onSelectFolder: () =>
                    {
                        HideDialog();
                        ShowFolderPickerOverlay(screenshotPath, StorageRoot);
                    },
                    onCancel: () =>
                    {
                        HideDialog();
                        ShowToast("Screenshot kept in original location");
                    });

                var layoutParams = NewLayoutParams(
                    FolderPickerViewFactory.GetPreferredWidth(this),
                    WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal);
                layoutParams.Gravity = GravityFlags.Center;

                _windowManager.AddView(_overlayView, layoutParams);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating overlay view: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------------
    // Overlay: folder picker and new-folder cards
    // ------------------------------------------------------------------
    private void ShowFolderPickerOverlay(string screenshotPath, string currentPath)
    {
        if (_windowManager == null || !_isRunning) return;

        UiThread.Post(() =>
        {
            try
            {
                HidePicker();

                var card = FolderPickerViewFactory.BuildFolderPickerCard(
                    this,
                    title: "Move screenshot to…",
                    confirmText: "Move",
                    currentPath: currentPath,
                    onNavigate: path => ShowFolderPickerOverlay(screenshotPath, path),
                    onConfirm: async path =>
                    {
                        HidePicker();
                        await MoveToFolderAsync(screenshotPath, path);
                    },
                    onNewFolder: path => ShowNewFolderOverlay(screenshotPath, path),
                    onCancel: () =>
                    {
                        HidePicker();
                        ShowToast("Screenshot kept in original location");
                    });

                AddPickerToWindow(card, focusable: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing folder picker overlay: {ex.Message}");
            }
        });
    }

    private void ShowNewFolderOverlay(string screenshotPath, string parentPath)
    {
        UiThread.Post(() =>
        {
            try
            {
                HidePicker();

                var card = FolderPickerViewFactory.BuildNewFolderCard(
                    this,
                    parentPath,
                    onCreated: newPath => ShowFolderPickerOverlay(screenshotPath, newPath),
                    onBack: () => ShowFolderPickerOverlay(screenshotPath, parentPath),
                    showMessage: ShowToast);

                // Must be focusable so the keyboard works for the EditText
                AddPickerToWindow(card, focusable: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing new folder overlay: {ex.Message}");
            }
        });
    }

    private void AddPickerToWindow(View view, bool focusable)
    {
        if (_windowManager == null) return;

        var flags = focusable
            ? WindowManagerFlags.NotTouchModal
            : WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal;

        var layoutParams = NewLayoutParams(FolderPickerViewFactory.GetPreferredWidth(this), flags);
        layoutParams.Gravity = GravityFlags.Center;
        layoutParams.SoftInputMode = SoftInput.AdjustPan;

        _pickerView = view;
        _windowManager.AddView(_pickerView, layoutParams);
    }

    // ------------------------------------------------------------------
    // Overlay: "Moved" snackbar near the top
    // ------------------------------------------------------------------
    private void ShowSnackbar(string message)
    {
        if (_windowManager == null) return;

        UiThread.Post(() =>
        {
            try
            {
                var bar = FolderPickerViewFactory.BuildSnackbarCard(this, message);

                var screenHeight = Resources?.DisplayMetrics?.HeightPixels ?? 1920;
                var layoutParams = NewLayoutParams(
                    WindowManagerLayoutParams.WrapContent,
                    WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable);
                layoutParams.Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal;
                layoutParams.Y = (int)(screenHeight * SnackbarTopOffsetPercent);

                _windowManager.AddView(bar, layoutParams);

                UiThread.PostDelayed(() =>
                {
                    try { _windowManager?.RemoveView(bar); }
                    catch { /* view may already be gone */ }
                }, SnackbarDurationMs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing snackbar: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------------
    // Move
    // ------------------------------------------------------------------
    private async Task MoveToFolderAsync(string screenshotPath, string destinationFolder)
    {
        try
        {
            await Task.Delay(500);

            if (!File.Exists(screenshotPath))
            {
                ShowToast("Screenshot file not found");
                return;
            }

            await Task.Run(() =>
            {
                Directory.CreateDirectory(destinationFolder);

                var fileName = IOPath.GetFileName(screenshotPath);
                var destinationPath = IOPath.Combine(destinationFolder, fileName);

                int counter = 1;
                while (File.Exists(destinationPath))
                {
                    var nameWithoutExt = IOPath.GetFileNameWithoutExtension(fileName);
                    var extension = IOPath.GetExtension(fileName);
                    destinationPath = IOPath.Combine(destinationFolder, $"{nameWithoutExt}_{counter}{extension}");
                    counter++;
                }

                ScreenshotMonitor.MarkFileAsMoved(screenshotPath);

                File.Copy(screenshotPath, destinationPath, overwrite: true);
                File.Delete(screenshotPath);
            });

            ShowSnackbar("Moved");
        }
        catch (Exception ex)
        {
            ShowToast($"Failed to move: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private static WindowManagerLayoutParams NewLayoutParams(int width, WindowManagerFlags flags) =>
        new(width,
            WindowManagerLayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            flags,
            Android.Graphics.Format.Translucent);

    public void HideDialog()
    {
        try
        {
            if (_overlayView != null) _windowManager?.RemoveView(_overlayView);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error hiding overlay: {ex.Message}");
        }
        finally
        {
            _overlayView = null;
        }
    }

    private void HidePicker()
    {
        try
        {
            if (_pickerView != null) _windowManager?.RemoveView(_pickerView);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error hiding picker: {ex.Message}");
        }
        finally
        {
            _pickerView = null;
        }
    }

    private void ShowToast(string message) =>
        UiThread.Post(() => Toast.MakeText(this, message, ToastLength.Long)?.Show());

    // ------------------------------------------------------------------
    // Notification
    // ------------------------------------------------------------------
    private void StartForegroundCompat()
    {
        var notification = BuildNotification();

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);
    }

    private void CreateNotificationChannel()
    {
        var channel = new NotificationChannel(ChannelId, "Screenshot Organizer", NotificationImportance.Low)
        {
            Description = "Monitors for new screenshots and organizes them automatically"
        };
        channel.SetShowBadge(false);
        channel.EnableLights(false);
        channel.EnableVibration(false);
        channel.SetSound(null, null);

        (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var openIntent = new Intent(this, typeof(MainActivity))
            .AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var openPending = PendingIntent.GetActivity(this, 0, openIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

        var stopIntent = new Intent(this, typeof(OverlayService)).SetAction(ActionStop);
        var stopPending = PendingIntent.GetService(this, 1, stopIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

        var stopAction = new Notification.Action.Builder(
            Icon.CreateWithResource(this, Resource.Drawable.ic_folder), "Stop", stopPending).Build();

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("Screenshot Organizer Active")
            .SetContentText("Monitoring screenshots in background")
            .SetSmallIcon(Resource.Drawable.ic_folder)
            .SetContentIntent(openPending)
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService)
            .AddAction(stopAction)
            .Build()!;
    }
}
