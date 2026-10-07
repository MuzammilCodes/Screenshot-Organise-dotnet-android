using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidEnvironment = Android.OS.Environment;
using AndroidUri = Android.Net.Uri;
using IOPath = System.IO.Path;

namespace ScreenshotOrganiser;

[Activity(Label = "Screenshot Organiser", MainLauncher = true, Exported = true,
    Theme = "@style/AppTheme", LaunchMode = LaunchMode.SingleTop)]
public class MainActivity : Activity
{
    private const int InitialDelayMs = 600;
    private const int AfterPermissionGrantedDelayMs = 800;
    private const int LegacyStorageRequestCode = 100;

    private ImageView _overlayCheck = null!;
    private ImageView _filesCheck = null!;
    private ImageView _folderCheck = null!;
    private Switch _monitorSwitch = null!;

    private bool _hasOverlayPermission;
    private bool _hasFilePermission;
    private bool _hasDefaultFolder;

    private bool _permissionsRequested;
    private bool _overlayRequested;
    private bool _fileRequested;

    private bool _dialogOpen;
    private bool _suppressSwitchEvent;
    private Dialog? _pickerDialog;
    private Dialog? _confirmDialog;

    private bool CanToggleMonitoring => _hasOverlayPermission && _hasFilePermission && _hasDefaultFolder;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _overlayCheck = FindViewById<ImageView>(Resource.Id.overlay_check)!;
        _filesCheck = FindViewById<ImageView>(Resource.Id.files_check)!;
        _folderCheck = FindViewById<ImageView>(Resource.Id.folder_check)!;
        _monitorSwitch = FindViewById<Switch>(Resource.Id.monitor_switch)!;

        FindViewById(Resource.Id.card_overlay)!.Click += (_, _) =>
        {
            if (!_hasOverlayPermission) OpenOverlayPermissionSettings();
        };
        FindViewById(Resource.Id.card_files)!.Click += async (_, _) =>
        {
            if (!_hasFilePermission) await OpenFilePermissionSettingsAsync();
        };
        FindViewById(Resource.Id.card_folder)!.Click += async (_, _) => await OnFolderCardTappedAsync();

        _monitorSwitch.CheckedChange += OnMonitorToggled;
    }

    protected override async void OnResume()
    {
        base.OnResume();

        try
        {
            if (!_permissionsRequested)
            {
                _permissionsRequested = true;
                RefreshState();
                await Task.Delay(InitialDelayMs);
                await RequestPermissionsSequentiallyAsync();
            }
            else
            {
                await CheckPermissionsAndContinueFlowAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnResume flow: {ex.Message}");
        }
    }

    protected override void OnDestroy()
    {
        _pickerDialog?.Dismiss();
        _confirmDialog?.Dismiss();
        base.OnDestroy();
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------
    private void RefreshState()
    {
        _hasOverlayPermission = Settings.CanDrawOverlays(this);

        _hasFilePermission = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? AndroidEnvironment.IsExternalStorageManager
            : CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted &&
              CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;

        _hasDefaultFolder = FolderPreferenceStore.GetDefaultFolder(this) != null;

        UpdateUi();
    }

    private void UpdateUi()
    {
        _overlayCheck.Visibility = _hasOverlayPermission ? ViewStates.Visible : ViewStates.Gone;
        _filesCheck.Visibility = _hasFilePermission ? ViewStates.Visible : ViewStates.Gone;
        _folderCheck.Visibility = _hasDefaultFolder ? ViewStates.Visible : ViewStates.Gone;

        _monitorSwitch.Enabled = CanToggleMonitoring;
        _monitorSwitch.Alpha = CanToggleMonitoring ? 1f : 0.5f;

        // Restore the persisted toggle state (e.g. after app data was cleared or the process restarted)
        if (CanToggleMonitoring && !ScreenshotMonitor.Instance.IsMonitoring &&
            FolderPreferenceStore.GetMonitoringEnabled(this))
            ScreenshotMonitor.Instance.Start();

        SetSwitch(ScreenshotMonitor.Instance.IsMonitoring);
    }

    private void SetSwitch(bool isChecked)
    {
        _suppressSwitchEvent = true;
        _monitorSwitch.Checked = isChecked;
        _suppressSwitchEvent = false;
    }

    // ------------------------------------------------------------------
    // Permission flow
    // ------------------------------------------------------------------
    private async Task RequestPermissionsSequentiallyAsync()
    {
        RefreshState();

        if (!_hasOverlayPermission && !_overlayRequested)
        {
            await Task.Delay(InitialDelayMs);
            OpenOverlayPermissionSettings();
            return;
        }

        if (!_hasFilePermission && !_fileRequested)
        {
            await OpenFilePermissionSettingsAsync();
            return;
        }

        if (_hasOverlayPermission && _hasFilePermission && !_hasDefaultFolder)
            await EnsureDefaultFolderAsync();
    }

    private async Task CheckPermissionsAndContinueFlowAsync()
    {
        RefreshState();

        if (_overlayRequested && _hasOverlayPermission && !_fileRequested && !_hasFilePermission)
        {
            await Task.Delay(AfterPermissionGrantedDelayMs);
            await OpenFilePermissionSettingsAsync();
            return;
        }

        if (_hasOverlayPermission && _hasFilePermission && !_hasDefaultFolder)
        {
            await Task.Delay(AfterPermissionGrantedDelayMs);
            await EnsureDefaultFolderAsync();
        }
    }

    private void OpenOverlayPermissionSettings()
    {
        _overlayRequested = true;

        var intent = new Intent(Settings.ActionManageOverlayPermission, AndroidUri.Parse($"package:{PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NoHistory | ActivityFlags.ExcludeFromRecents);
        StartActivity(intent);
    }

    private async Task OpenFilePermissionSettingsAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (AndroidEnvironment.IsExternalStorageManager)
            {
                _fileRequested = true;
                _hasFilePermission = true;
                UpdateUi();
                return;
            }

            bool grant = await ShowConfirmationAsync(
                "All Files Access",
                "This permission allows Screenshot Organiser to detect and move screenshots between folders you choose.\n\n🔒 Everything stays on your device, giving you complete control over your files.",
                "Grant Access");

            _fileRequested = true;
            if (!grant) return;

            var intent = new Intent(
                Settings.ActionManageAppAllFilesAccessPermission, AndroidUri.Parse($"package:{PackageName}"));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NoHistory | ActivityFlags.ExcludeFromRecents);
            StartActivity(intent);
        }
        else
        {
            _fileRequested = true;
            RequestPermissions(new[]
            {
                Android.Manifest.Permission.ReadExternalStorage,
                Android.Manifest.Permission.WriteExternalStorage
            }, LegacyStorageRequestCode);
            // Result is picked up by OnResume -> CheckPermissionsAndContinueFlowAsync
        }
    }

    // ------------------------------------------------------------------
    // Default folder
    // ------------------------------------------------------------------
    private async Task OnFolderCardTappedAsync()
    {
        if (_dialogOpen) return;

        if (!_hasDefaultFolder)
        {
            await EnsureDefaultFolderAsync();
            return;
        }

        var current = FolderPreferenceStore.GetDefaultFolder(this);
        var folderName = string.IsNullOrEmpty(current)
            ? "Not set"
            : IOPath.GetFileName(current.TrimEnd('/', '\\'));

        _dialogOpen = true;
        try
        {
            bool change = await ShowConfirmationAsync(
                "Default Screenshot Folder",
                $"Current folder: {folderName}\n\nDo you want to choose a different folder?",
                "Change Folder");

            if (change)
                ShowFolderPicker("Choose default folder", "Select", FolderPickerViewFactory.StorageRoot);
            else
                _dialogOpen = false;
        }
        catch
        {
            _dialogOpen = false;
            throw;
        }
    }

    private async Task EnsureDefaultFolderAsync()
    {
        if (_hasDefaultFolder || _dialogOpen) return;

        _dialogOpen = true;
        try
        {
            bool choose = await ShowConfirmationAsync(
                "Default Screenshot Folder",
                "Select where screenshots are stored",
                "Select Folder");

            if (choose)
                ShowFolderPicker("Choose default folder", "Select", FolderPickerViewFactory.StorageRoot);
            else
                _dialogOpen = false;
        }
        catch
        {
            _dialogOpen = false;
            throw;
        }
    }

    private void OnDefaultFolderChosen(string folderPath)
    {
        FolderPreferenceStore.SaveDefaultFolder(this, folderPath);

        Toast.MakeText(this, $"Default screenshot folder set: {IOPath.GetFileName(folderPath)}", ToastLength.Long)?.Show();

        _dialogOpen = false;
        _hasDefaultFolder = true;
        UpdateUi();
    }

    // ------------------------------------------------------------------
    // Folder picker dialogs (hosted in this activity)
    // ------------------------------------------------------------------
    private void ShowFolderPicker(string title, string confirmText, string currentPath)
    {
        try
        {
            var card = FolderPickerViewFactory.BuildFolderPickerCard(
                this, title, confirmText, currentPath,
                onNavigate: path => ShowFolderPicker(title, confirmText, path),
                onConfirm: path =>
                {
                    HidePickerDialog();
                    OnDefaultFolderChosen(path);
                },
                onNewFolder: path => ShowNewFolder(title, confirmText, path),
                onCancel: () =>
                {
                    HidePickerDialog();
                    _dialogOpen = false;
                });

            ShowCardInPickerDialog(card);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing folder picker: {ex.Message}");
            HidePickerDialog();
            _dialogOpen = false;
        }
    }

    private void ShowNewFolder(string title, string confirmText, string parentPath)
    {
        var card = FolderPickerViewFactory.BuildNewFolderCard(
            this, parentPath,
            onCreated: newPath => ShowFolderPicker(title, confirmText, newPath),
            onBack: () => ShowFolderPicker(title, confirmText, parentPath),
            showMessage: msg => Toast.MakeText(this, msg, ToastLength.Long)?.Show());

        ShowCardInPickerDialog(card);
    }

    private void ShowCardInPickerDialog(View card)
    {
        // Swap content instead of dismiss/show to avoid flicker while navigating
        if (_pickerDialog is { IsShowing: true })
        {
            _pickerDialog.SetContentView(card);
            _pickerDialog.Window?.SetLayout(
                FolderPickerViewFactory.GetPreferredWidth(this), ViewGroup.LayoutParams.WrapContent);
            return;
        }

        _pickerDialog = CreateCardDialog(card);
        _pickerDialog.Show();
    }

    private void HidePickerDialog()
    {
        try { _pickerDialog?.Dismiss(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error dismissing picker: {ex.Message}"); }
        finally { _pickerDialog = null; }
    }

    private Dialog CreateCardDialog(View card)
    {
        var dialog = new Dialog(this);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetContentView(card);
        dialog.SetCancelable(false);

        var window = dialog.Window;
        window?.SetBackgroundDrawable(
            new Android.Graphics.Drawables.ColorDrawable(Android.Graphics.Color.Transparent));
        window?.SetLayout(
            FolderPickerViewFactory.GetPreferredWidth(this), ViewGroup.LayoutParams.WrapContent);

        return dialog;
    }

    private Task<bool> ShowConfirmationAsync(string title, string message, string confirmText)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dialog? dialog = null;

        var card = FolderPickerViewFactory.BuildConfirmationCard(
            this, title, message, confirmText,
            onConfirm: () => { dialog?.Dismiss(); tcs.TrySetResult(true); },
            onCancel: () => { dialog?.Dismiss(); tcs.TrySetResult(false); });

        dialog = CreateCardDialog(card);
        _confirmDialog = dialog;
        dialog.Show();

        return tcs.Task;
    }

    // ------------------------------------------------------------------
    // Monitoring switch
    // ------------------------------------------------------------------
    private void OnMonitorToggled(object? sender, CompoundButton.CheckedChangeEventArgs e)
    {
        if (_suppressSwitchEvent) return;

        if (e.IsChecked)
        {
            if (!CanToggleMonitoring)
            {
                SetSwitch(false);
                return;
            }

            ScreenshotMonitor.Instance.Start();
        }
        else
        {
            ScreenshotMonitor.Instance.Stop();
        }
    }
}
