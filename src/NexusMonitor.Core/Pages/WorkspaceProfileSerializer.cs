namespace NexusMonitor.Core.Pages;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Schema-versioned JSON persistence for a workspace profile.
/// Envelope: {"schemaVersion":N,"profile":{...}}.</summary>
public static class WorkspaceProfileSerializer
{
    /// <summary>The schema version this build writes and the newest version it can read.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Versioned wrapper persisted around a <see cref="WorkspaceProfile"/>.
    /// SchemaVersion is nullable so a missing property (rather than defaulting to 0) can be
    /// distinguished and rejected explicitly.</summary>
    private sealed record Envelope(int? SchemaVersion, WorkspaceProfile Profile);

    /// <summary>Serializes a profile into the versioned envelope: {"schemaVersion":N,"profile":{...}}.</summary>
    public static string Serialize(WorkspaceProfile profile) =>
        JsonSerializer.Serialize(new Envelope(CurrentSchemaVersion, profile), Options);

    /// <summary>Never throws. False + error for null/empty/whitespace input, malformed JSON,
    /// missing envelope fields (including a missing schemaVersion), or a schemaVersion newer
    /// than this build understands.</summary>
    public static bool TryDeserialize(string? json, out WorkspaceProfile? profile, out string? error)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Profile JSON is null or empty.";
            return false;
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
            if (envelope?.Profile is null)
            {
                error = "Missing 'profile' object in profile file.";
                return false;
            }
            if (envelope.SchemaVersion is null)
            {
                error = "Missing schemaVersion in profile file.";
                return false;
            }
            if (envelope.SchemaVersion > CurrentSchemaVersion)
            {
                error = $"Profile schema version {envelope.SchemaVersion} is newer than supported ({CurrentSchemaVersion}).";
                return false;
            }
            profile = envelope.Profile;
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid profile JSON: {ex.Message}";
            return false;
        }
    }
}
