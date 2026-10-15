using Android.Content;
using AndroidUri = Android.Net.Uri;

namespace ScreenshotOrganiser;

/// <summary>Persists the monitoring switch, the two folder-grant URIs and the recently used destination folders.</summary>
public static class FolderPreferenceStore
{
    private const string PrefsName = "screenshot_prefs";
    private const string MonitoringKey = "monitoring_enabled";
    private const string RecentFoldersKey = "recent_folders";
    private const int MaxRecentFolders = 6;

    private static string TreeKey(FolderRole role) => $"tree_uri_{role}";

    public static void SaveMonitoringEnabled(Context context, bool enabled) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?
            .Edit()?.PutBoolean(MonitoringKey, enabled)?.Commit();

    public static bool GetMonitoringEnabled(Context context) =>
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?.GetBoolean(MonitoringKey, false) ?? false;

    public static void SaveTreeUri(Context context, FolderRole role, AndroidUri treeUri) =>
        // Commit (sync) so the value hits disk even if the process is killed right after
        context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?
            .Edit()?.PutString(TreeKey(role), treeUri.ToString())?.Commit();

    public static AndroidUri? GetTreeUri(Context context, FolderRole role)
    {
        var raw = context.GetSharedPreferences(PrefsName, FileCreationMode.Private)?.GetString(TreeKey(role), null);
        return string.IsNullOrEmpty(raw) ? null : AndroidUri.Parse(raw);
    }

    public static void AddRecentFolder(Context context, string folderPath)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        var folders = ReadRecent(prefs);

        folders.RemoveAll(f => string.Equals(f, folderPath, StringComparison.OrdinalIgnoreCase));
        folders.Insert(0, folderPath);
        if (folders.Count > MaxRecentFolders)
            folders.RemoveRange(MaxRecentFolders, folders.Count - MaxRecentFolders);

        prefs?.Edit()?.PutString(RecentFoldersKey, string.Join('\n', folders))?.Apply();
    }

    /// <summary>Most recently used first; folders that no longer exist and <paramref name="excludeFolder"/> are skipped.</summary>
    public static List<string> GetRecentFolders(Context context, string? excludeFolder = null)
    {
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        var exclude = excludeFolder?.TrimEnd('/');

        return ReadRecent(prefs)
            .Where(f => !string.Equals(f.TrimEnd('/'), exclude, StringComparison.OrdinalIgnoreCase))
            .Where(f => StorageAccess.Exists(context, f))
            .ToList();
    }

    private static List<string> ReadRecent(ISharedPreferences? prefs)
    {
        var raw = prefs?.GetString(RecentFoldersKey, null);
        return string.IsNullOrEmpty(raw)
            ? new List<string>()
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
