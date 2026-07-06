namespace NexusMonitor.Core.Pages;

/// <summary>An in-memory editing transaction over an immutable PageLayout. Every mutation runs
/// through PageLayoutEngine and pushes the prior state onto an undo stack; Cancel returns the
/// untouched original, Commit the final state. Engine no-ops (unknown ids) record no undo entry.</summary>
public sealed class PageEditSession
{
    private PageLayout _original;
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

    /// <summary>Applies <paramref name="transform"/> to every snapshot this session holds — the
    /// original, every undo-stack entry, and <see cref="Current"/> — rather than to just the
    /// working layout. Use this for state that is applied to the live page from OUTSIDE the edit
    /// session (e.g. pop-out window state, set directly via <see cref="PageLayoutEngine.SetPopOut"/>
    /// while an edit is in progress): such state is orthogonal to whatever the user is editing, so
    /// it must survive no matter how the session resolves. Rebasing every snapshot — not just
    /// <see cref="Current"/> — is what makes that true: <see cref="Commit"/> returns the rebased
    /// <see cref="Current"/>, <see cref="Cancel"/> returns the rebased original instead of the
    /// stale pre-transform one, and <see cref="Undo"/> pops rebased undo entries — so none of the
    /// three can ever revert an externally-applied change back to a pre-transform snapshot.</summary>
    public void RebaseAll(Func<PageLayout, PageLayout> transform)
    {
        _original = transform(_original);

        if (_undo.Count > 0)
        {
            var snapshots = _undo.ToArray(); // enumerates top (most recent) first
            _undo.Clear();
            for (var i = snapshots.Length - 1; i >= 0; i--)
                _undo.Push(transform(snapshots[i])); // push oldest→newest so top ends up newest again
        }

        Current = transform(Current);
    }

    private void Apply(PageLayout next)
    {
        if (ReferenceEquals(next, Current)) return; // engine no-op → no undo entry
        _undo.Push(Current);
        Current = next;
    }
}
