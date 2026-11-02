using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AoS2.Launcher.Services;

/// <summary>Dinge, die ein Fenster bzw. Windows braucht. In Tests durch Attrappen ersetzt.</summary>
public interface IPlatformServices
{
    /// <summary>Zeigt den Windows-Ordnerdialog. <c>null</c>, wenn abgebrochen.</summary>
    Task<string?> PickFolderAsync(string title, string? startDirectory);

    /// <summary>Öffnet den Ordner im Explorer.</summary>
    void OpenFolder(string path);

    /// <summary>Freier Platz auf dem Laufwerk des Pfads, <c>null</c> wenn unbekannt.</summary>
    long? GetAvailableBytes(string path);

    /// <summary>Öffnet eine Webseite im Standardbrowser.</summary>
    void OpenUrl(string url);
}

public sealed class DesktopPlatformServices(Func<TopLevel?> topLevel) : IPlatformServices
{
    public async Task<string?> PickFolderAsync(string title, string? startDirectory)
    {
        if (topLevel() is not { } top)
            return null;

        var storage = top.StorageProvider;
        var start = startDirectory is null ? null : await storage.TryGetFolderFromPathAsync(NearestExisting(startDirectory));
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public void OpenFolder(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{NearestExisting(path)}\"") { UseShellExecute = true });
    }

    public long? GetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void OpenUrl(string url)
    {
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Der gewählte Spielordner gibt es vor der Installation oft noch nicht.</summary>
    private static string NearestExisting(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current) && Path.GetDirectoryName(current) is { } parent)
            current = parent;
        return current;
    }
}
