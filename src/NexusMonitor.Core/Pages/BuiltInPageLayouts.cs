namespace NexusMonitor.Core.Pages;

using System.Reflection;

/// <summary>Factory-default page layouts, embedded as resources. These are the same serialized
/// format users edit — "reset to default" simply reloads these. A missing/invalid resource is a
/// packaging bug and throws; it is never a user-facing error path.</summary>
public static class BuiltInPageLayouts
{
    /// <summary>The page ids for which a factory-default layout resource is embedded.</summary>
    public static IReadOnlyList<string> BuiltInPageIds { get; } = new[] { "dashboard" };

    /// <summary>Loads and validates the factory-default layout for the given page id.
    /// Throws <see cref="InvalidOperationException"/> if the resource is missing or invalid —
    /// both are packaging bugs, never a user-facing error.</summary>
    public static PageLayout Load(string pageId)
    {
        var resourceName = $"NexusMonitor.Core.Pages.Defaults.{pageId}.default.json";
        using var stream = typeof(BuiltInPageLayouts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No built-in page layout '{pageId}' (resource '{resourceName}' not found).");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        if (!PageLayoutSerializer.TryDeserialize(json, out var page, out var error))
            throw new InvalidOperationException($"Built-in page layout '{pageId}' is invalid: {error}");
        return page!;
    }
}
