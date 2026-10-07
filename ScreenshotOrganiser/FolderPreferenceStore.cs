using Android.Content;
using AndroidEnvironment = Android.OS.Environment;

namespace ScreenshotOrganiser;

/// <summary>
/// Persists the default screenshot folder in SharedPreferences with a backup copy
/// in public external storage, so the setting survives "Clear data" and OEM auto-cleaners.
/// </summary>
public static class FolderPreferenceStore
{
    private const string PrefsName = "screenshot_prefs";
    private const string FolderKey = "default_screenshot_folder";
    private const string MonitoringKey = "monitoring_enabled";
    private const string FolderBackupFile = "default_folder.cfg";
    private const string MonitoringBackupFile = "monitoring_enabled.cfg";

    private static string GetBackupFilePath(string fileName)
    {
        var documents = AndroidEnvironment
            .GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDocuments)?
            .AbsolutePath ?? "/storage/emulated/0/Documents";

        return Path.Combine(documents, ".ScreenshotOrganiser", fileName);
    }

    public static void SaveMonitoringEnabled(Context context, bool enabled)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        prefs?.Edit()?.PutBoolean(MonitoringKey, enabled)?.Commit();

        WriteBackup(MonitoringBackupFile, enabled ? "true" : "false");
    }

    public static bool GetMonitoringEnabled(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        if (prefs != null && prefs.Contains(MonitoringKey))
            return prefs.GetBoolean(MonitoringKey, false);

        // Prefs lost (cleared data / OEM cleaner): restore from external backup
        var restored = ReadBackup(MonitoringBackupFile);
        if (bool.TryParse(restored, out var enabled))
        {
            prefs?.Edit()?.PutBoolean(MonitoringKey, enabled)?.Commit();
            return enabled;
        }

        return false;
    }

    public static void SaveDefaultFolder(Context context, string folderPath)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        // Commit (sync) so the value hits disk even if the process is killed right after
        prefs?.Edit()?.PutString(FolderKey, folderPath)?.Commit();

        WriteBackup(FolderBackupFile, folderPath);
    }

    public static string? GetDefaultFolder(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        var folder = prefs?.GetString(FolderKey, null);

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            return folder;

        // Prefs lost (cleared data / OEM cleaner): restore from external backup
        var restored = ReadBackup(FolderBackupFile);
        if (!string.IsNullOrEmpty(restored) && Directory.Exists(restored))
        {
            prefs?.Edit()?.PutString(FolderKey, restored)?.Commit();
            return restored;
        }

        return null;
    }

    private static void WriteBackup(string fileName, string value)
    {
        try
        {
            if (!HasAllFilesAccess()) return;

            var backupPath = GetBackupFilePath(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath, value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error writing backup {fileName}: {ex.Message}");
        }
    }

    private static string? ReadBackup(string fileName)
    {
        try
        {
            if (!HasAllFilesAccess()) return null;

            var backupPath = GetBackupFilePath(fileName);
            if (File.Exists(backupPath))
                return File.ReadAllText(backupPath).Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading backup {fileName}: {ex.Message}");
        }

        return null;
    }

    private static bool HasAllFilesAccess() =>
        !OperatingSystem.IsAndroidVersionAtLeast(30) || AndroidEnvironment.IsExternalStorageManager;
}
