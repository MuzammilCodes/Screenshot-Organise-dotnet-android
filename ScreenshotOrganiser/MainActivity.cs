using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidUri = Android.Net.Uri;
using IOPath = System.IO.Path;

namespace ScreenshotOrganiser;

[Activity(Label = "Screenshot Organiser", MainLauncher = true, Exported = true,
    Theme = "@style/AppTheme", LaunchMode = LaunchMode.SingleTop)]
public class MainActivity : Activity
{
    private const int InitialDelayMs = 600;
    private const int AfterPermissionGrantedDelayMs = 800;
    private const int MediaPermissionRequestCode = 100;
    private const int SourceTreeRequestCode = 101;
    private const int DestinationTreeRequestCode = 102;
    private const string SourceInitialUri =
        "content://com.android.externalstorage.documents/document/primary%3APictures%2FScreenshots";
    private const string DestinationInitialUri =
        "content://com.android.externalstorage.documents/document/primary%3APictures";

    private ImageView _overlayCheck = null!;
    private ImageView _filesCheck = null!;
    private ImageView _folderCheck = null!;
    private ImageView _destinationCheck = null!;
    private Switch _monitorSwitch = null!;

    private bool _hasOverlayPermission;
    private bool _hasMediaPermission;
    private bool _hasDefaultFolder;
    private bool _hasDestination;

    private bool _permissionsRequested;
    private bool _overlayRequested;
    private bool _mediaRequested;
    private readonly HashSet<FolderRole> _requestedRoles = new();

    private bool _dialogOpen;
    private bool _suppressSwitchEvent;
    private Dialog? _confirmDialog;

    private bool CanToggleMonitoring =>
        _hasOverlayPermission && _hasMediaPermission && _hasDefaultFolder && _hasDestination;

    private static string MediaPermission => OperatingSystem.IsAndroidVersionAtLeast(33)
        ? Android.Manifest.Permission.ReadMediaImages
        : Android.Manifest.Permission.ReadExternalStorage;

    private bool HasMediaPermission() => CheckSelfPermission(MediaPermission) == Permission.Granted;

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
        _destinationCheck = FindViewById<ImageView>(Resource.Id.destination_check)!;
        _monitorSwitch = FindViewById<Switch>(Resource.Id.monitor_switch)!;

        FindViewById(Resource.Id.card_overlay)!.Click += (_, _) =>
        {
            if (!_hasOverlayPermission) OpenOverlayPermissionSettings();
        };
        FindViewById(Resource.Id.card_files)!.Click += (_, _) =>
        {
            if (!_hasMediaPermission) RequestMediaPermission();
        };
        FindViewById(Resource.Id.card_folder)!.Click += async (_, _) => await OnFolderCardTappedAsync(FolderRole.Source);
        FindViewById(Resource.Id.card_destination)!.Click += async (_, _) =>
            await OnFolderCardTappedAsync(FolderRole.Destination);

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
        _confirmDialog?.Dismiss();
        base.OnDestroy();
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------
    private void RefreshState()
    {
        _hasOverlayPermission = Settings.CanDrawOverlays(this);
        _hasMediaPermission = HasMediaPermission();
        _hasDefaultFolder = StorageAccess.GetRootPath(this, FolderRole.Source) != null;
        _hasDestination = StorageAccess.GetRootPath(this, FolderRole.Destination) != null;

        UpdateUi();
    }

    private void UpdateUi()
    {
        _overlayCheck.Visibility = _hasOverlayPermission ? ViewStates.Visible : ViewStates.Gone;
        _filesCheck.Visibility = _hasMediaPermission ? ViewStates.Visible : ViewStates.Gone;
        _folderCheck.Visibility = _hasDefaultFolder ? ViewStates.Visible : ViewStates.Gone;
        _destinationCheck.Visibility = _hasDestination ? ViewStates.Visible : ViewStates.Gone;

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

        await ContinueSetupAsync(0);
    }

    private Task CheckPermissionsAndContinueFlowAsync() => ContinueSetupAsync(AfterPermissionGrantedDelayMs);

    // Each step is offered once automatically; the cards let the user retry
    private async Task ContinueSetupAsync(int delayMs)
    {
        RefreshState();
        if (!_hasOverlayPermission) return;

        if (!_hasMediaPermission)
        {
            if (_mediaRequested) return;

            await Task.Delay(delayMs);
            RequestMediaPermission();
        }
        else if (!_hasDefaultFolder)
        {
            if (_requestedRoles.Contains(FolderRole.Source)) return;

            await Task.Delay(delayMs);
            await OnFolderCardTappedAsync(FolderRole.Source);
        }
        else if (!_hasDestination)
        {
            if (_requestedRoles.Contains(FolderRole.Destination)) return;

            await Task.Delay(delayMs);
            await OnFolderCardTappedAsync(FolderRole.Destination);
        }
    }

    private void OpenOverlayPermissionSettings()
    {
        _overlayRequested = true;

        var intent = new Intent(Settings.ActionManageOverlayPermission, AndroidUri.Parse($"package:{PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NoHistory | ActivityFlags.ExcludeFromRecents);
        StartActivity(intent);
    }

    private void RequestMediaPermission()
    {
        _mediaRequested = true;
        RequestPermissions(new[] { MediaPermission }, MediaPermissionRequestCode);
        // Result is picked up by OnResume -> CheckPermissionsAndContinueFlowAsync
    }

    // ------------------------------------------------------------------
    // Folder grants: default screenshot folder (Source) and move-to folder (Destination)
    // ------------------------------------------------------------------
    private async Task OnFolderCardTappedAsync(FolderRole role)
    {
        if (_dialogOpen) return;
        _requestedRoles.Add(role);

        var current = StorageAccess.GetRootPath(this, role);
        var (title, message) = role == FolderRole.Source
            ? ("Default Screenshot Folder",
                "Open the folder where your phone saves screenshots, then tap \"Use this folder\" and \"Allow\".\n\nYou only need to do this once.")
            : ("Move-to Folder",
                "Choose the folder you want to move screenshots into, such as Pictures. You can also create new folders inside it.\n\nTap \"Use this folder\" and \"Allow\". You only need to do this once.");

        _dialogOpen = true;
        try
        {
            bool proceed = current == null
                ? await ShowConfirmationAsync(title, message, "Choose Folder")
                : await ShowConfirmationAsync(
                    title,
                    $"Current folder: {IOPath.GetFileName(current)}\n\nDo you want to choose a different folder?",
                    "Change Folder");

            if (proceed) LaunchTreePicker(role);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void LaunchTreePicker(FolderRole role)
    {
        var initialUri = role == FolderRole.Source ? SourceInitialUri : DestinationInitialUri;

        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.PutExtra(DocumentsContract.ExtraInitialUri, AndroidUri.Parse(initialUri));
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        StartActivityForResult(intent, role == FolderRole.Source ? SourceTreeRequestCode : DestinationTreeRequestCode);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        FolderRole role;
        if (requestCode == SourceTreeRequestCode) role = FolderRole.Source;
        else if (requestCode == DestinationTreeRequestCode) role = FolderRole.Destination;
        else return;

        if (resultCode != Result.Ok || data?.Data is not { } treeUri) return;

        if (!StorageAccess.IsSupportedTree(treeUri))
        {
            Toast.MakeText(this, "Please choose a specific folder on internal storage", ToastLength.Long)?.Show();
            _requestedRoles.Remove(role);
            return;
        }

        var grantFlags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;
        ContentResolver?.TakePersistableUriPermission(treeUri, grantFlags);

        var previous = FolderPreferenceStore.GetTreeUri(this, role);
        FolderPreferenceStore.SaveTreeUri(this, role, treeUri);

        // Android caps the number of persisted grants, so drop the replaced one
        if (previous != null && !IsGrantInUse(previous))
            ContentResolver?.ReleasePersistableUriPermission(previous, grantFlags);

        var name = IOPath.GetFileName(StorageAccess.GetRootPath(this, role));
        Toast.MakeText(this, $"Folder set: {name}", ToastLength.Long)?.Show();
    }

    private bool IsGrantInUse(AndroidUri uri) =>
        uri.Equals(FolderPreferenceStore.GetTreeUri(this, FolderRole.Source)) ||
        uri.Equals(FolderPreferenceStore.GetTreeUri(this, FolderRole.Destination));

    // ------------------------------------------------------------------
    // Dialogs
    // ------------------------------------------------------------------
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
