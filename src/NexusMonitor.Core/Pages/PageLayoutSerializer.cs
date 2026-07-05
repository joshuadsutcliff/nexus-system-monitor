namespace NexusMonitor.Core.Pages;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Schema-versioned JSON persistence for a single page layout.
/// Envelope: {"schemaVersion":N,"page":{...}}. ConfigJson is carried as an opaque string value.</summary>
public static class PageLayoutSerializer
{
    /// <summary>The schema version this build writes and the newest version it can read.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Versioned wrapper persisted around a <see cref="PageLayout"/>.
    /// SchemaVersion is nullable so a missing property (rather than defaulting to 0) can be
    /// distinguished and rejected explicitly.</summary>
    private sealed record Envelope(int? SchemaVersion, PageLayout Page);

    /// <summary>Serializes a page into the versioned envelope: {"schemaVersion":N,"page":{...}}.</summary>
    public static string Serialize(PageLayout page) =>
        JsonSerializer.Serialize(new Envelope(CurrentSchemaVersion, page), Options);

    /// <summary>Never throws. False + error for null/empty/whitespace input, malformed JSON,
    /// missing envelope fields (including a missing schemaVersion), or a schemaVersion newer
    /// than this build understands.</summary>
    public static bool TryDeserialize(string? json, out PageLayout? page, out string? error)
    {
        page = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Layout JSON is null or empty.";
            return false;
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
            if (envelope?.Page is null)
            {
                error = "Missing 'page' object in layout file.";
                return false;
            }
            if (envelope.SchemaVersion is null)
            {
                error = "Missing schemaVersion in layout file.";
                return false;
            }
            if (envelope.SchemaVersion > CurrentSchemaVersion)
            {
                error = $"Layout schema version {envelope.SchemaVersion} is newer than supported ({CurrentSchemaVersion}).";
                return false;
            }
            page = envelope.Page;
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid layout JSON: {ex.Message}";
            return false;
        }
    }
}
