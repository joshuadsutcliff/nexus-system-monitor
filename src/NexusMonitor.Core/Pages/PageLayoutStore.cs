namespace NexusMonitor.Core.Pages;

/// <summary>Per-page layout persistence. Mirrors SettingsService's shape: debounced (250 ms)
/// atomic writes (tmp + move), synchronous flush on dispose, IO failures logged-by-silence
/// (never thrown). A corrupt page file is renamed to .bak and the factory default returned —
/// never a blank page (spec §8). Note: the debounce holds a single pending layout — concurrent saves of DIFFERENT pages within one window would drop the earlier one; fine for the single-page Phase 3, needs a keyed map before multi-page editing.</summary>
public sealed class PageLayoutStore : IDisposable
{
    private readonly string _dir;
    private readonly object _lock = new();
    private Timer? _debounce;
    private PageLayout? _pending;

    /// <summary>Creates a store rooted at <paramref name="baseDirectory"/> (tests) or the
    /// per-user app-data pages directory (production, when null).</summary>
    public PageLayoutStore(string? baseDirectory = null)
    {
        _dir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusMonitor", "pages");
    }

    /// <summary>Loads the saved layout for a page, falling back to the factory default when no
    /// file exists or the file is corrupt (corrupt files are preserved as .bak).</summary>
    public PageLayout LoadOrDefault(string pageId)
    {
        var path = PathFor(pageId);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (PageLayoutSerializer.TryDeserialize(json, out var page, out _))
                    return page!;
                File.Move(path, path + ".bak", overwrite: true);
            }
        }
        catch (Exception) { /* fall through to factory default — never throw from load */ }
        return BuiltInPageLayouts.Load(pageId);
    }

    /// <summary>Queues a debounced save (250 ms, restart-on-call). Dispose flushes synchronously.</summary>
    public void Save(PageLayout page)
    {
        lock (_lock)
        {
            _pending = page;
            _debounce?.Dispose();
            _debounce = new Timer(_ => WriteToDisk(), null,
                dueTime: TimeSpan.FromMilliseconds(250), period: Timeout.InfiniteTimeSpan);
        }
    }

    private void WriteToDisk()
    {
        try
        {
            PageLayout? page;
            lock (_lock) { page = _pending; }
            if (page is null) return;

            Directory.CreateDirectory(_dir);
            var path = PathFor(page.PageId);
            File.WriteAllText(path + ".tmp", PageLayoutSerializer.Serialize(page));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception) { /* timer-thread escape would be process-fatal; mirror SettingsService's broad catch */ }
    }

    private string PathFor(string pageId) => Path.Combine(_dir, pageId + ".json");

    /// <summary>Stops the debounce timer and flushes any pending save synchronously.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        WriteToDisk();
    }
}
