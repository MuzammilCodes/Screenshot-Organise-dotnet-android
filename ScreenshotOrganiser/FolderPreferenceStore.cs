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

    private static string BackupFilePath
    {
        get
        {
            var documents = AndroidEnvironment
                .GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDocuments)?
                .AbsolutePath ?? "/storage/emulated/0/Documents";

            return Path.Combine(documents, ".ScreenshotOrganiser", "default_folder.cfg");
        }
    }

    public static void SaveDefaultFolder(Context context, string folderPath)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        // Commit (sync) so the value hits disk even if the process is killed right after
        prefs?.Edit()?.PutString(FolderKey, folderPath)?.Commit();

        WriteBackup(folderPath);
    }

    public static string? GetDefaultFolder(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        var folder = prefs?.GetString(FolderKey, null);

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            return folder;

        // Prefs lost (cleared data / OEM cleaner): restore from external backup
        var restored = ReadBackup();
        if (!string.IsNullOrEmpty(restored) && Directory.Exists(restored))
        {
            prefs?.Edit()?.PutString(FolderKey, restored)?.Commit();
            return restored;
        }

        return null;
    }

    private static void WriteBackup(string folderPath)
    {
        try
        {
            if (!HasAllFilesAccess()) return;

            var backupPath = BackupFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath, folderPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error writing folder backup: {ex.Message}");
        }
    }

    private static string? ReadBackup()
    {
        try
        {
            if (!HasAllFilesAccess()) return null;

            var backupPath = BackupFilePath;
            if (File.Exists(backupPath))
                return File.ReadAllText(backupPath).Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading folder backup: {ex.Message}");
        }

        return null;
    }

    private static bool HasAllFilesAccess() =>
        !OperatingSystem.IsAndroidVersionAtLeast(30) || AndroidEnvironment.IsExternalStorageManager;
}
