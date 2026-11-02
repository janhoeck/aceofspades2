namespace AoS2.Updates;

/// <summary>
/// Verwaltet eine Installation als Ganzes: Zielordner bestimmen, verschieben, entfernen.
/// <para>
/// Grundregel: Der Launcher fasst nur Dateien an, die laut <see cref="InstallState"/> von ihm stammen.
/// Wählt jemand z. B. <c>D:\Spiele</c>, bleiben andere Spiele darin unberührt.
/// </para>
/// </summary>
public static class Installation
{
    /// <summary>Unterordner, der an einen gewählten Ordner angehängt wird.</summary>
    public const string FolderName = "Ace of Spades 2";

    /// <summary>
    /// Macht aus dem Ordner, den jemand im Dialog gewählt hat, den Spielordner:
    /// Heißt er schon „Ace of Spades 2“ oder liegt dort bereits eine Installation, wird er direkt genutzt,
    /// sonst kommt der Unterordner „Ace of Spades 2“ dazu (aus „D:\Spiele“ wird „D:\Spiele\Ace of Spades 2“).
    /// </summary>
    public static string ResolveGameDirectory(string pickedDirectory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pickedDirectory));
        if (Path.GetFileName(full).Equals(FolderName, StringComparison.OrdinalIgnoreCase) || IsInstalled(full))
            return full;
        return Path.Combine(full, FolderName);
    }

    /// <summary>Liegt in diesem Ordner eine Installation des Launchers?</summary>
    public static bool IsInstalled(string directory) => File.Exists(InstallState.GetFilePath(directory));

    /// <summary>
    /// Verschiebt eine Installation in einen anderen Ordner. Auf demselben Laufwerk werden Dateien nur
    /// umbenannt (schnell), sonst kopiert und danach am alten Ort gelöscht. Geht etwas schief, bleibt die
    /// alte Installation vollständig erhalten.
    /// </summary>
    /// <param name="progress">Fortschritt in Bytes, gemeldet nach jeder Datei.</param>
    public static async Task MoveAsync(
        string fromDirectory,
        string toDirectory,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fromDirectory));
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(toDirectory));
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
            return;
        if (IsSameOrInside(to, from) || IsSameOrInside(from, to))
            throw new UpdateException("Der neue Ordner darf nicht im alten liegen (und umgekehrt).");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new UpdateException($"Der Ordner „{to}“ ist nicht leer.");

        var state = await InstallState.LoadAsync(from, cancellationToken)
            ?? throw new UpdateException($"In „{from}“ ist kein Spiel installiert.");

        // Der Installationsstand zieht mit um, damit Änderungszeiten weiter passen und nichts neu gehasht wird.
        var files = state.Files
            .Where(f => ManifestValidator.IsValidPath(f.Path))
            .Select(f => (Relative: f.Path, f.Size))
            .Append((Relative: InstallState.FolderName + "/install.json", Size: 0L))
            .ToList();

        var sameVolume = string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
        var done = new List<(string Source, string Target)>();
        var reporter = new ProgressReporter(progress, files.Sum(f => f.Size));
        try
        {
            foreach (var (relative, size) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(from, relative);
                var target = Path.Combine(to, relative);
                if (File.Exists(source))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (sameVolume)
                        File.Move(source, target);
                    else
                        await Task.Run(() => File.Copy(source, target), cancellationToken);
                    done.Add((source, target));
                }
                reporter.Add(size);
            }
        }
        catch
        {
            // Zurückrollen: Die alte Installation soll danach genau wie vorher sein.
            foreach (var (source, target) in done)
            {
                if (sameVolume)
                    File.Move(target, source);
                else
                    File.Delete(target);
            }
            DeleteEmptyDirectories(to, includeRoot: true);
            throw;
        }

        if (!sameVolume)
        {
            foreach (var (source, _) in done)
                File.Delete(source);
        }
        DeleteEmptyDirectories(from, includeRoot: true);
        reporter.Complete();
    }

    /// <summary>
    /// Entfernt eine Installation: alle Dateien laut Installationsstand, den Ordner <c>.aos2</c> und danach
    /// leere Ordner. Fremde Dateien bleiben liegen. Gibt <c>true</c> zurück, wenn der Ordner danach weg ist.
    /// </summary>
    public static async Task<bool> RemoveAsync(string directory, CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var state = await InstallState.LoadAsync(root, cancellationToken);
        if (state is null)
            return !Directory.Exists(root);

        foreach (var file in state.Files.Where(f => ManifestValidator.IsValidPath(f.Path)))
        {
            var path = Path.Combine(root, file.Path);
            if (File.Exists(path))
                File.Delete(path);
        }

        var stateFolder = Path.Combine(root, InstallState.FolderName);
        if (Directory.Exists(stateFolder))
            Directory.Delete(stateFolder, recursive: true);

        DeleteEmptyDirectories(root, includeRoot: true);
        return !Directory.Exists(root);
    }

    /// <summary>Löscht leere Unterordner von innen nach außen, optional auch <paramref name="root"/> selbst.</summary>
    public static void DeleteEmptyDirectories(string root, bool includeRoot)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var sub in Directory.EnumerateDirectories(root))
            DeleteEmptyDirectories(sub, includeRoot: true);

        if (includeRoot && !Directory.EnumerateFileSystemEntries(root).Any())
            Directory.Delete(root);
    }

    private static bool IsSameOrInside(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
