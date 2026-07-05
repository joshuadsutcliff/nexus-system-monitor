namespace NexusMonitor.Core.Pages;

/// <summary>An in-memory editing transaction over an immutable PageLayout. Every mutation runs
/// through PageLayoutEngine and pushes the prior state onto an undo stack; Cancel returns the
/// untouched original, Commit the final state. Engine no-ops (unknown ids) record no undo entry.</summary>
public sealed class PageEditSession
{
    private readonly PageLayout _original;
    private readonly Stack<PageLayout> _undo = new();

    /// <summary>Begins a session over the given layout.</summary>
    public PageEditSession(PageLayout original)
    {
        _original = original;
        Current = original;
    }

    /// <summary>The working layout including all applied edits.</summary>
    public PageLayout Current { get; private set; }

    /// <summary>True when at least one undoable edit has been applied.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when Current differs from the original (by reference — engine ops return new instances).</summary>
    public bool IsDirty => !ReferenceEquals(Current, _original);

    /// <summary>Moves a widget (engine semantics: clamp, pin, push-down). Unknown ids are no-ops.</summary>
    public void Move(Guid instanceId, GridRect target) =>
        Apply(PageLayoutEngine.MoveWidget(Current, instanceId, target));

    /// <summary>Removes a widget. Unknown ids are no-ops.</summary>
    public void Remove(Guid instanceId) =>
        Apply(PageLayoutEngine.RemoveWidget(Current, instanceId));

    /// <summary>Adds a widget via engine placement (clamp + push-down).</summary>
    public void Add(WidgetInstance widget) =>
        Apply(PageLayoutEngine.PlaceWidget(Current, widget));

    /// <summary>Closes vertical gaps (engine Compact).</summary>
    public void CompactPage() => Apply(PageLayoutEngine.Compact(Current)); // Unwired in Phase 3 (no toolbar affordance yet); Phase 4 wires or tests it.

    /// <summary>Reverts the most recent edit.</summary>
    public void Undo()
    {
        if (_undo.Count > 0) Current = _undo.Pop();
    }

    /// <summary>Abandons all edits and returns the original layout.</summary>
    public PageLayout Cancel() => _original;

    /// <summary>Finalizes the session and returns the edited layout.</summary>
    public PageLayout Commit() => Current;

    private void Apply(PageLayout next)
    {
        if (ReferenceEquals(next, Current)) return; // engine no-op → no undo entry
        _undo.Push(Current);
        Current = next;
    }
}
