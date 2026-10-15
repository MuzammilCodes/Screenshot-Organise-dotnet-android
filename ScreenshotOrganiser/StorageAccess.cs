using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;
using IOPath = System.IO.Path;

namespace ScreenshotOrganiser;

public enum FolderRole
{
    /// <summary>Where the phone saves screenshots; watched for new ones.</summary>
    Source,

    /// <summary>Folder (and its subfolders) screenshots are moved into.</summary>
    Destination
}

/// <summary>
/// Path-based file operations routed through the folder grants the user approved,
/// so the app needs no all-files access. A path is reachable only inside a granted folder.
/// </summary>
public static class StorageAccess
{
    private const string VolumePrefix = "primary:";

    private sealed record Grant(AndroidUri Tree, string RootPath);

    public static string? GetRootPath(Context context, FolderRole role) => GetGrant(context, role)?.RootPath;

    /// <summary>Internal-storage folders only; Android 11+ already blocks the storage root, older versions do not.</summary>
    public static bool IsSupportedTree(AndroidUri tree) => RootPathOf(tree) != null;

    private static string? RootPathOf(AndroidUri tree)
    {
        var documentId = DocumentsContract.GetTreeDocumentId(tree);
        if (documentId == null || !documentId.StartsWith(VolumePrefix, StringComparison.Ordinal) ||
            documentId.Length == VolumePrefix.Length)
            return null;

        return $"{FolderPickerViewFactory.StorageRoot}/{documentId[VolumePrefix.Length..]}";
    }

    private static Grant? GetGrant(Context context, FolderRole role)
    {
        var tree = FolderPreferenceStore.GetTreeUri(context, role);
        if (tree == null) return null;

        // The user can revoke a grant in system settings, so the saved URI alone is not enough
        var persisted = context.ContentResolver?.PersistedUriPermissions?.Any(p =>
            p.IsReadPermission && p.IsWritePermission && tree.Equals(p.Uri)) ?? false;
        if (!persisted) return null;

        var rootPath = RootPathOf(tree);
        return rootPath == null ? null : new Grant(tree, rootPath);
    }

    private static bool IsUnder(string path, string root) =>
        path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static Grant? FindGrant(Context context, string path)
    {
        var trimmed = path.TrimEnd('/');
        return new[] { GetGrant(context, FolderRole.Source), GetGrant(context, FolderRole.Destination) }
            .FirstOrDefault(g => g != null && IsUnder(trimmed, g.RootPath));
    }

    public static bool Exists(Context context, string path)
    {
        var uri = DocumentUri(context, path);
        if (uri == null) return false;

        try
        {
            using var cursor = context.ContentResolver?.Query(
                uri, new[] { DocumentsContract.Document.ColumnDocumentId }, null, null, null);
            return cursor?.MoveToFirst() == true;
        }
        catch
        {
            return false;
        }
    }

    public static string[] ListDirectories(Context context, string path)
    {
        var trimmed = path.TrimEnd('/');
        var grant = FindGrant(context, trimmed);
        if (grant == null) return Array.Empty<string>();

        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(grant.Tree, ToDocumentId(trimmed))!;
        var projection = new[]
        {
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType
        };

        var parent = path.TrimEnd('/');
        var result = new List<string>();

        using var cursor = context.ContentResolver?.Query(childrenUri, projection, null, null, null);
        while (cursor != null && cursor.MoveToNext())
        {
            var name = cursor.GetString(0);
            if (name != null && cursor.GetString(1) == DocumentsContract.Document.MimeTypeDir)
                result.Add($"{parent}/{name}");
        }

        return result.ToArray();
    }

    public static void CreateDirectory(Context context, string path)
    {
        var uri = DocumentUri(context, path) ?? throw NoAccess();
        if (Exists(context, path)) return;

        var trimmed = path.TrimEnd('/');
        var parent = trimmed[..trimmed.LastIndexOf('/')];
        CreateDirectory(context, parent);

        var parentUri = DocumentUri(context, parent)!;
        var created = DocumentsContract.CreateDocument(
            context.ContentResolver!, parentUri, DocumentsContract.Document.MimeTypeDir, IOPath.GetFileName(trimmed));
        if (created == null) throw new IOException($"Could not create folder '{IOPath.GetFileName(trimmed)}'");
    }

    public static void MoveFile(Context context, string sourcePath, string destinationFolder)
    {
        var resolver = context.ContentResolver!;
        var sourceUri = DocumentUri(context, sourcePath) ?? throw NoAccess();

        if (!Exists(context, sourcePath)) throw new FileNotFoundException("Screenshot file not found");

        CreateDirectory(context, destinationFolder);

        var fileName = IOPath.GetFileName(sourcePath);
        var destinationName = UniqueName(context, destinationFolder, fileName);
        var destinationUri = DocumentsContract.CreateDocument(
            resolver, DocumentUri(context, destinationFolder)!, MimeTypeOf(fileName), destinationName)
            ?? throw new IOException("Could not create the destination file");

        try
        {
            using var input = resolver.OpenInputStream(sourceUri) ?? throw new IOException("Could not read the screenshot");
            using var output = resolver.OpenOutputStream(destinationUri) ?? throw new IOException("Could not write the copy");
            input.CopyTo(output);
        }
        catch
        {
            DocumentsContract.DeleteDocument(resolver, destinationUri);
            throw;
        }

        if (!DocumentsContract.DeleteDocument(resolver, sourceUri))
            throw new IOException("Copied, but could not remove the original");
    }

    private static string UniqueName(Context context, string folder, string fileName)
    {
        var stem = IOPath.GetFileNameWithoutExtension(fileName);
        var extension = IOPath.GetExtension(fileName);

        var name = fileName;
        for (int counter = 1; Exists(context, $"{folder.TrimEnd('/')}/{name}"); counter++)
            name = $"{stem}_{counter}{extension}";

        return name;
    }

    private static string MimeTypeOf(string fileName) =>
        Android.Webkit.MimeTypeMap.Singleton?
            .GetMimeTypeFromExtension(IOPath.GetExtension(fileName).TrimStart('.').ToLowerInvariant())
        ?? "application/octet-stream";

    private static string ToDocumentId(string path) =>
        VolumePrefix + path.TrimEnd('/')[(FolderPickerViewFactory.StorageRoot.Length + 1)..];

    private static AndroidUri? DocumentUri(Context context, string path)
    {
        var grant = FindGrant(context, path);
        return grant == null
            ? null
            : DocumentsContract.BuildDocumentUriUsingTree(grant.Tree, ToDocumentId(path));
    }

    private static IOException NoAccess() => new("No access to that folder. Check your folder settings in the app");
}
