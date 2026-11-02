using System.Text.Json;
using System.Text.Json.Serialization;

namespace AoS2.Updates;

public static class ManifestSerializer
{
    public static async Task WriteAsync(GameManifest manifest, Stream stream, CancellationToken cancellationToken = default) =>
        await JsonSerializer.SerializeAsync(stream, manifest, UpdatesJsonContext.Default.GameManifest, cancellationToken);

    /// <summary>Liest ein Manifest und prüft es mit <see cref="ManifestValidator"/>.</summary>
    public static async Task<GameManifest> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        GameManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync(stream, UpdatesJsonContext.Default.GameManifest, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidManifestException($"Das Manifest ist kein gültiges JSON: {ex.Message}");
        }

        if (manifest is null)
            throw new InvalidManifestException("Das Manifest ist leer.");

        ManifestValidator.Validate(manifest);
        return manifest;
    }
}

/// <summary>
/// JSON-Einstellungen für Manifest und Installationsstand. Der Source-Generator erzeugt den
/// Serialisierungscode beim Kompilieren, dadurch braucht es zur Laufzeit keine Reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(GameManifest))]
[JsonSerializable(typeof(InstallState))]
internal sealed partial class UpdatesJsonContext : JsonSerializerContext;
