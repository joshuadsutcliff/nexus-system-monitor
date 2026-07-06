using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.Messaging;
using NexusMonitor.Core.Health;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Motion;
using NexusMonitor.Core.Pages;
using NexusMonitor.Core.Services;
using NexusMonitor.UI.Messages;
using NexusMonitor.UI.Services;
using ReactiveUI;
using Serilog;

namespace NexusMonitor.UI.ViewModels;

public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly SystemHealthService        _healthService;
    private readonly MemoryLeakDetectionService _leakService;
    private readonly AppSettings                _settings;
    private readonly MotionSettingsService      _motionSettingsService;
    private IDisposable?                        _subscription;
    private IDisposable?                        _predictionsSubscription;
    private IDisposable?                        _staleSubscription;
    private readonly HashSet<string>            _dismissedResources = new();
    private SystemHealthSnapshot?               _latestSnapshot;
    private int                                 _consumerTickCounter;

    // ── Overall health ────────────────────────────────────────────────────────

    // True when the health pipeline has stalled (no snapshot for > 3× the interval).
    [ObservableProperty] private bool _isDataStale;

    [ObservableProperty] private double _overallScore = 100;
    [ObservableProperty] private string _overallLabel = "Excellent";
    [ObservableProperty] private string _overallDescription = "Your system is running smoothly.";
    [ObservableProperty] private IBrush _healthRingBrush  = Brushes.Green;
    [ObservableProperty] private string _trendArrow = "→";

    // ── Subsystem cards ───────────────────────────────────────────────────────

    [ObservableProperty] private SubsystemCardViewModel _cpuCard    = new("CPU",    "\ue9d5");
    [ObservableProperty] private SubsystemCardViewModel _memoryCard = new("Memory", "\ue9d6");
    [ObservableProperty] private SubsystemCardViewModel _diskCard   = new("Disk",   "\ue9d7");
    [ObservableProperty] private SubsystemCardViewModel _gpuCard    = new("GPU",    "\ue9d9");

    // ── Top consumers ─────────────────────────────────────────────────────────

    public ObservableCollection<ProcessImpactViewModel> TopConsumers { get; } = new();

    // ── Active automations ────────────────────────────────────────────────────

    [ObservableProperty] private int    _activeAutomations;
    [ObservableProperty] private string _automationStatus = "No automations active";

    // ── Bottleneck analysis ───────────────────────────────────────────────────

    [ObservableProperty] private BottleneckCardViewModel _bottleneckCard = new();

    // ── Recommendations ───────────────────────────────────────────────────────

    public ObservableCollection<RecommendationViewModel> Recommendations { get; } = new();

    // ── Resource Predictions ──────────────────────────────────────────────────

    public ObservableCollection<PredictionCardViewModel> PredictionCards { get; } = new();
    [ObservableProperty] private bool _hasPredictions;

    // ── Health Trends ─────────────────────────────────────────────────────────

    public HealthTrendsViewModel HealthTrendsViewModel { get; }

    // ── Page engine (Phase 7: unconditional) ──────────────────────────────────

    /// <summary>The page rendered by the engine path; null only if no factory-default layout could
    /// be built at all (see the constructor's catch block).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditLayout))]
    private PageLayout? _enginePage;

    /// <summary>True when there is a loaded page to edit. False only in the catastrophic
    /// double-failure case where even the factory-default layout failed to load (see the
    /// constructor's nested catch), leaving <see cref="EnginePage"/> null — gates the pencil
    /// "Edit layout" button (<see cref="Views.DashboardView"/>) so it goes disabled instead of
    /// staying visible-but-dead with nothing left to edit.</summary>
    public bool CanEditLayout => EnginePage is not null;

    // Phase 5 superseded PageLayoutStore as the read/write path here (see WorkspaceProfileStore
    // below) — PageLayoutStore itself stays registered in DI only as the legacy-pages migration
    // source (App.axaml.cs); DashboardViewModel no longer reads or writes it.
    private readonly WorkspaceProfileStore? _profileStore;

    // ── Page engine (Phase 6) — pop-out windows ───────────────────────────────

    private readonly IInAppNotificationService? _notificationService;

    /// <summary>The main window, needed to open/clamp pop-out windows. Set once <see cref="Views.DashboardView"/>
    /// loads (unavailable at VM construction time); see <see cref="AttachOwnerWindow"/>.</summary>
    private Window? _ownerWindow;

    /// <summary>Owns open pop-out windows. Created lazily on the first <see cref="PopOutWidget"/> call —
    /// most sessions never pop anything out, and it needs <see cref="_ownerWindow"/>, which isn't
    /// available yet at construction time.</summary>
    private PopOutCoordinator? _popOutCoordinator;

    public DashboardViewModel(SystemHealthService healthService, MemoryLeakDetectionService leakService, AppSettings settings, HealthTrendsViewModel healthTrendsViewModel, MotionSettingsService motionSettingsService, PredictionService? predictionService = null, WorkspaceProfileStore? profileStore = null, IInAppNotificationService? notificationService = null)
    {
        _healthService      = healthService;
        _leakService        = leakService;
        _settings           = settings;
        HealthTrendsViewModel = healthTrendsViewModel;
        _motionSettingsService = motionSettingsService;
        _profileStore       = profileStore;
        _notificationService = notificationService;

        // Phase 8 Task 3 carryover B: re-raise HoverEffectsEnabled whenever the ANIMATIONS
        // settings page applies a change (speed slider or the AnimateHoverEffects toggle) — Task 2
        // left this un-wired because no live change source existed yet at that point (see
        // HoverEffectsEnabled's own doc comment).
        _motionSettingsService.MotionChanged += OnMotionSettingsChanged;

        // Subscribed BEFORE the LoadActive() call just below: WorkspaceProfileStore.ProfileRecovered
        // can fire synchronously from inside that very call (a corrupt or stale/tampered active
        // profile), so the handler must already be wired up or that first-launch signal is missed.
        if (_profileStore is not null)
            _profileStore.ProfileRecovered += OnProfileRecovered;

        // Phase 7: the page engine is unconditional — there is no classic fallback left, so a
        // load failure here must still leave the user with a renderable (factory-default) page
        // rather than silently disabling the engine. The active profile's own on-disk file is
        // deliberately left untouched: the user can fix it by hand, or a later launch may succeed,
        // so this path never calls SaveActivePage()/Save() on the profile store.
        try
        {
            EnginePage = _profileStore?.LoadActive().Pages.GetValueOrDefault("dashboard") ?? TryFactoryLoad();
        }
        catch (InvalidOperationException ex)
        {
            // The only way this throws is BuiltInPageLayouts.Load hitting a missing/invalid
            // embedded resource (a packaging bug — see BuiltInPageLayouts' own doc comment),
            // whether reached directly via TryFactoryLoad() above or indirectly through
            // WorkspaceProfileStore.LoadActive()'s internal factory-default fallback. Try once
            // more for the factory-default page; if that ALSO throws (the same deterministic
            // packaging bug), give up on a page rather than let the exception escape the
            // constructor — PageHostControl already renders a null Page as an empty surface.
            Log.Error(ex, "Failed to load the dashboard page layout; falling back to factory default.");
            try { EnginePage = TryFactoryLoad(); }
            catch (InvalidOperationException ex2)
            {
                Log.Fatal(ex2, "Factory-default dashboard layout is also unavailable — Dashboard will render empty.");
            }

            ShowLayoutNotLoadedToast();
        }

        _subscription = _healthService.HealthStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ApplySnapshot);

        _staleSubscription = _healthService.IsStaleStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(stale => IsDataStale = stale);

        if (predictionService != null)
        {
            _predictionsSubscription = predictionService.Predictions
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(UpdatePredictions);
        }

        // Restart health service when the user changes the metrics polling interval
        WeakReferenceMessenger.Default.Register<MetricsIntervalChangedMessage>(this, (_, msg) =>
        {
            _healthService.Start(msg.Interval);
        });

        // Page engine Phase 6: BEFORE SettingsViewModel flips the active workspace profile pointer,
        // persist every open pop-out's live geometry into the (still active) OUTGOING profile and
        // close its OS windows. WeakReferenceMessenger.Send is synchronous, so this fully completes
        // — including the SaveActivePage() write inside PersistAndCloseAllPopOuts's callback chain,
        // which reads _profileStore.LoadActive() — before SwitchWorkspaceProfile's very next line
        // calls WorkspaceProfileStore.SetActive(name). Ordering matters: reversing it (persisting
        // after SetActive) would read/save into the INCOMING profile instead, corrupting it with the
        // OUTGOING page's widget layout.
        //
        // Post-review hardening: a live Dashboard edit session is force-exited via CancelEdit()
        // FIRST, before PersistAndCloseAllPopOuts. Why: entering edit mode snapshots the OUTGOING
        // profile's page into _editSession (see EnterEditMode). If a profile switch lands mid-edit,
        // WorkspaceProfileSwitchedMessage's handler below reassigns EnginePage to the INCOMING
        // profile's page and calls RestorePopOuts() — but it never touches _editSession/IsEditMode,
        // which stay pointed at the outgoing profile's stale history. Any edit op fired afterward
        // (AfterEdit, SaveEdit, CancelEdit, UndoEdit) would then read/write through that stale
        // session and revert EnginePage back to outgoing-derived state; SaveEdit would persist that
        // corrupted layout INTO the incoming profile. CancelEdit() already no-ops when _editSession
        // is null, so calling it unconditionally here is a no-op when the switch happens outside
        // edit mode. Its Cancel semantics are intentional: any uncommitted in-progress edit to the
        // OUTGOING profile is discarded, and that profile simply keeps its last-saved layout.
        WeakReferenceMessenger.Default.Register<WorkspaceProfileSwitchingMessage>(this, (_, _) =>
        {
            CancelEdit();
            PersistAndCloseAllPopOuts();
        });

        // Page engine Phase 5: reload the dashboard layout when the user switches the active
        // workspace profile in Settings. Theme is applied separately (synchronously, before this
        // message is sent) by SettingsViewModel — this handler only ever touches layout. Phase 6:
        // also restore the incoming profile's own popped-out widgets (RestorePopOuts is idempotent,
        // same as AttachOwnerWindow's initial restore).
        WeakReferenceMessenger.Default.Register<WorkspaceProfileSwitchedMessage>(this, (_, _) =>
        {
            EnginePage = _profileStore?.LoadActive().Pages.GetValueOrDefault("dashboard") ?? EnginePage;
            RestorePopOuts();
        });
    }

    /// <summary>Loads the factory-default "dashboard" layout (used when no layout store is available).</summary>
    private static PageLayout TryFactoryLoad() => BuiltInPageLayouts.Load("dashboard");

    /// <summary>Handles <see cref="WorkspaceProfileStore.ProfileRecovered"/>: the active workspace
    /// profile failed to load (a corrupt file preserved as .bak, or a stale/tampered active
    /// pointer) and the caller silently fell back to a factory-default layout. Without this, the
    /// user gets zero feedback that their own saved layout didn't apply. Reuses
    /// <see cref="ShowLayoutNotLoadedToast"/> — the same copy the constructor's own
    /// packaging-bug fallback shows — so there is exactly one copy of the string.
    /// <para>
    /// Safe to call during construction: this handler is subscribed BEFORE the constructor's own
    /// <c>LoadActive()</c> call, since the event can fire synchronously from within that very call.
    /// <see cref="IInAppNotificationService.Show"/> only pushes onto a plain Subject (see
    /// <see cref="Services.InAppNotificationService"/>) — no UI-thread affinity of its own; the
    /// actual toast rendering marshals onto the UI thread itself via <c>Dispatcher.UIThread.Post</c>
    /// (see <see cref="Controls.NotificationHost"/>). So calling Show() synchronously here mirrors
    /// exactly what the constructor's pre-existing packaging-bug catch already does.
    /// </para></summary>
    private void OnProfileRecovered(string profileName) => ShowLayoutNotLoadedToast();

    /// <summary>Single copy of the "layout not loaded" toast — shown by the constructor's
    /// packaging-bug catch and by <see cref="OnProfileRecovered"/>.</summary>
    private void ShowLayoutNotLoadedToast() => _notificationService?.Show(new InAppNotification(
        Title:       "Layout Not Loaded",
        Body:        "Your saved layout could not be loaded — showing defaults. Your profile file was preserved.",
        Severity:    InAppSeverity.Warning,
        AutoDismiss: TimeSpan.FromSeconds(6)));

    // ── Page engine editing (Phase 3) ──────────────────────────────────────
    private PageEditSession? _editSession;

    /// <summary>True while the Dashboard page is in edit mode.</summary>
    [ObservableProperty] private bool _isEditMode;
    /// <summary>True while the add-widget gallery overlay is open.</summary>
    [ObservableProperty] private bool _isGalleryOpen;
    /// <summary>True when the edit session has an undoable step.</summary>
    [ObservableProperty] private bool _canUndoEdit;

    /// <summary>True when widget-tile hover lift should be able to animate — forwards to
    /// <see cref="MotionSettingsService.EffectEnabled"/> for <see cref="MotionEffect.HoverEffects"/>.
    /// Bound to <see cref="Controls.PageHostControl.HoverLiftEnabled"/> (see that property's doc
    /// for why gating lives there rather than per-tile). Not an <c>[ObservableProperty]</c> (there
    /// is no backing field to generate one from — <see cref="AppSettings"/> is a plain POCO with no
    /// change events of its own); instead it's re-raised via <see cref="OnMotionSettingsChanged"/>,
    /// subscribed to <see cref="MotionSettingsService.MotionChanged"/> in the constructor — Phase 8
    /// Task 3 carryover B, resolving the "not wired yet" gap Task 2 originally left here.</summary>
    public bool HoverEffectsEnabled => MotionSettingsService.EffectEnabled(_settings, MotionEffect.HoverEffects);

    /// <summary>Phase 8 Task 3 carryover B: re-raises <see cref="HoverEffectsEnabled"/> whenever
    /// <see cref="MotionSettingsService.Apply"/> runs again — i.e. whenever the ANIMATIONS settings
    /// page's speed slider or any Animate* toggle changes — so a live edit immediately re-gates the
    /// widget-tile hover lift instead of only taking effect after a restart.</summary>
    private void OnMotionSettingsChanged() => OnPropertyChanged(nameof(HoverEffectsEnabled));

    /// <summary>Enters edit mode over the current page.</summary>
    [RelayCommand]
    private void EnterEditMode()
    {
        if (EnginePage is null || IsEditMode) return;
        _editSession = new PageEditSession(EnginePage);
        IsEditMode = true;
        CanUndoEdit = false;
        WeakReferenceMessenger.Default.Send(new PageEditModeChangedMessage(true));
    }

    /// <summary>Commits edits, persists into the ACTIVE workspace profile (load-modify-save via
    /// the store, replacing just the "dashboard" page — the profile's other pages and theme are
    /// left untouched), leaves edit mode.</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        if (_editSession is null) return;
        var committed = _editSession.Commit();
        EnginePage = committed;

        if (_profileStore is not null)
        {
            var active = _profileStore.LoadActive();
            var pages = active.Pages.ToDictionary(kv => kv.Key, kv => kv.Value);
            pages["dashboard"] = committed;
            _profileStore.Save(active with { Pages = pages });
        }

        ExitEdit();
    }

    /// <summary>Abandons edits, restores the pre-edit layout, leaves edit mode.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        if (_editSession is null) return;
        EnginePage = _editSession.Cancel();
        ExitEdit();
    }

    /// <summary>Reverts the most recent edit.</summary>
    [RelayCommand]
    private void UndoEdit()
    {
        if (_editSession is null) return;
        _editSession.Undo();
        EnginePage = _editSession.Current;
        CanUndoEdit = _editSession.CanUndo;
    }

    /// <summary>Closes vertical gaps in the current layout (engine Compact).</summary>
    [RelayCommand]
    private void TidyLayout()
    {
        if (_editSession is null) return;
        _editSession.CompactPage();
        AfterEdit();
    }

    /// <summary>Opens the add-widget gallery overlay.</summary>
    [RelayCommand]
    private void OpenGallery() { if (IsEditMode) IsGalleryOpen = true; }

    /// <summary>Adds a widget of the given type at the top of the page (engine push-down applies).</summary>
    [RelayCommand]
    private void AddWidget(string typeId)
    {
        if (_editSession is null) return;
        _editSession.Add(new WidgetInstance(Guid.NewGuid(), typeId, new GridRect(0, 0, 4, 2)));
        AfterEdit();
        IsGalleryOpen = false;
    }

    /// <summary>Adorner callback: commit a drag/resize result.</summary>
    public void EditMove(Guid id, GridRect target)
    {
        if (_editSession is null) return;
        _editSession.Move(id, target);
        AfterEdit();
    }

    /// <summary>Adorner callback: remove a widget. If the widget is currently popped out into its
    /// own OS window (or the coordinator otherwise still has one open for it), that window is
    /// closed first — suppressing its <c>onReturned</c> callback, since the widget is being
    /// deleted outright and there is no page-side widget left to persist returned geometry into —
    /// so removing a widget from the page never orphans an open pop-out window.</summary>
    public void EditRemove(Guid id)
    {
        if (_editSession is null) return;

        var poppedOut = _editSession.Current.FindWidget(id)?.PopOut?.IsPoppedOut == true;
        if (poppedOut || (_popOutCoordinator?.Open.ContainsKey(id) ?? false))
            _popOutCoordinator?.CloseWindow(id, suppressReturn: true);

        _editSession.Remove(id);
        AfterEdit();
    }

    // ── Page engine (Phase 6) — pop-out windows ───────────────────────────────
    // Pop-out changes are NOT edit-session ops (no undo stack): PageLayoutEngine.SetPopOut applies
    // directly to the live EnginePage and is persisted immediately via SaveActivePage, mirroring
    // its own doc'd contract. Entry point v1 is the edit-chrome pop-out button only — a view-mode
    // context-menu entry point is deferred (see the Phase 6 plan's Task 3 note): it needs per-widget
    // menu plumbing across nine separate widget controls, out of scope for this task.

    /// <summary>Wires the main window reference <see cref="PopOutCoordinator"/> needs to open and
    /// screen-clamp pop-out windows. Called by <see cref="Views.DashboardView"/> once it loads (not
    /// available at VM construction time). Safe to call more than once — <see cref="Views.DashboardView"/>
    /// is recreated on every navigation to the Dashboard tab, so this fires again on each re-Loaded.
    /// Also triggers <see cref="RestorePopOuts"/> (restore-on-launch): the very first attach is the
    /// earliest point at which both a loaded <see cref="EnginePage"/> AND an owner window exist —
    /// neither is available at VM construction time.</summary>
    public void AttachOwnerWindow(Window window)
    {
        _ownerWindow = window;
        RestorePopOuts();
    }

    /// <summary>Reopens a pop-out window for every widget on <see cref="EnginePage"/> whose
    /// <see cref="WidgetInstance.PopOut"/> has <c>IsPoppedOut</c> true — i.e. every widget that was
    /// still popped out the last time its state was persisted (app shutdown, a profile switch, or a
    /// crash). No-ops entirely when no page is loaded. Called from <see cref="AttachOwnerWindow"/>
    /// (restore-on-launch) and from the <see cref="WorkspaceProfileSwitchedMessage"/> handler
    /// (restore for the profile just switched into). Idempotent and safe to call repeatedly —
    /// <see cref="PopOutCoordinator.TryPopOut"/> is itself a no-op (returns true without opening a
    /// second window) for a widget it already has open, so re-attaching the owner window (e.g. tab
    /// navigation back to Dashboard) never duplicates windows.
    /// <para>
    /// Logs a single INFO line every call, before the guard can return early — diagnostic gold for a
    /// restore that silently opens nothing: it reports whether <see cref="EnginePage"/> is loaded,
    /// whether <see cref="_ownerWindow"/> is attached yet, and how many widgets on the page are
    /// actually marked popped-out, so a future no-windows-opened report can be diagnosed from the
    /// log alone instead of needing to reproduce it live.
    /// </para></summary>
    private void RestorePopOuts()
    {
        var poppedOutCount = EnginePage?.Widgets.Count(w => w.PopOut?.IsPoppedOut == true) ?? 0;
        Log.Information(
            "RestorePopOuts: EnginePageLoaded={EnginePageLoaded} OwnerWindowAttached={OwnerWindowAttached} PoppedOutWidgetCount={PoppedOutWidgetCount}",
            EnginePage is not null, _ownerWindow is not null, poppedOutCount);

        if (EnginePage is null) return;

        _popOutCoordinator ??= CreateCoordinator();
        if (_popOutCoordinator is null) return; // no owner window attached yet

        foreach (var widget in EnginePage.Widgets)
            if (widget.PopOut?.IsPoppedOut == true)
                _popOutCoordinator.TryPopOut(widget);
    }

    /// <summary>Lazily builds <see cref="_popOutCoordinator"/> the first time a pop-out is
    /// requested. Returns null (and the caller no-ops the open) if no owner window has been
    /// attached yet — shouldn't happen in practice since the pop-out button only exists once the
    /// view is loaded, but is handled defensively so the model never lies about an open window.</summary>
    private PopOutCoordinator? CreateCoordinator() =>
        _ownerWindow is null
            ? null
            : new PopOutCoordinator(_ownerWindow, this, _notificationService, OnPopOutReturned, OnPopOutPersistedForShutdown);

    /// <summary>Edit-chrome pop-out button callback (<see cref="Controls.EditAdornerControl.PopOutRequested"/>,
    /// wired in <see cref="Views.DashboardView"/>): tears <paramref name="instanceId"/>'s widget off
    /// into its own OS window. Marks the widget popped-out and persists immediately — reusing its
    /// last remembered geometry if this widget has been popped out before, otherwise a zeroed
    /// placeholder purely for the "is it popped out" fact, since the coordinator below is handed the
    /// widget instance as it was BEFORE that update (so a first-time pop-out still resolves as "no
    /// remembered geometry" to the coordinator, which then computes its own cascade-default
    /// placement/size, rather than seeing these persisted zeros as real remembered geometry). If
    /// <see cref="PopOutCoordinator.TryPopOut"/> refuses (the open-pop-out cap was hit, already
    /// toasted by the coordinator, or no owner window is attached yet), the pop-out mark is reverted
    /// to its exact pre-call value so the model never claims a window is open when none actually is.
    /// Double-click/redundant-call guard: if the widget is already marked popped-out AND the
    /// coordinator still has its window open, this is a no-op — there's nothing to rebuild or
    /// re-persist. A widget that's marked popped-out but NOT actually open in the coordinator (e.g.
    /// right after a cap-refusal revert, or recovering from a crash between "marked popped out" and
    /// "window opened") still falls through to the normal path below so it can actually open.</summary>
    public void PopOutWidget(Guid instanceId)
    {
        if (EnginePage is null) return;
        var widget = EnginePage.FindWidget(instanceId);
        if (widget is null) return;

        if (widget.PopOut?.IsPoppedOut == true && (_popOutCoordinator?.Open.ContainsKey(instanceId) ?? false))
            return;

        var markedPoppedOut = widget.PopOut is { } prior
            ? prior with { IsPoppedOut = true }
            : new PopOutState(true, 0, 0, 0, 0, false);
        ApplyPopOutStateAndSave(instanceId, markedPoppedOut);

        _popOutCoordinator ??= CreateCoordinator();
        if (_popOutCoordinator is null || !_popOutCoordinator.TryPopOut(widget))
            ApplyPopOutStateAndSave(instanceId, widget.PopOut);
    }

    /// <summary>Coordinator callback: fires when the user closes a pop-out window directly. The
    /// delivered <paramref name="state"/> already has <c>IsPoppedOut</c> false with its final
    /// X/Y/Width/Height retained (per <see cref="PopOutCoordinator"/>'s contract), so the widget
    /// reopens where it was left next time.</summary>
    private void OnPopOutReturned(Guid instanceId, PopOutState state) => ApplyPopOutStateAndSave(instanceId, state);

    /// <summary>Coordinator callback wired for <see cref="PopOutCoordinator.PersistAndCloseAll"/>
    /// (Task 4's shutdown/profile-switch path calls it via the coordinator): the delivered
    /// <paramref name="state"/> already has <c>IsPoppedOut</c> still true (the widget is
    /// conceptually still popped out — only its OS window is being torn down) with its final
    /// geometry, so it reopens in place on restore.</summary>
    private void OnPopOutPersistedForShutdown(Guid instanceId, PopOutState state) => ApplyPopOutStateAndSave(instanceId, state);

    /// <summary>Persists every open pop-out's current geometry (still marked <c>IsPoppedOut</c> true —
    /// see <see cref="OnPopOutPersistedForShutdown"/>) and closes their OS windows. No-op if no
    /// pop-out has ever been opened this session (<see cref="_popOutCoordinator"/> is still null).
    /// <para>
    /// Two callers, both requiring the pop-out windows — which host live widget bindings into
    /// services this VM depends on — to be torn down before anything they might reference stops:
    /// </para>
    /// <list type="bullet">
    /// <item>App shutdown (<c>App.axaml.cs</c>'s <c>ShutdownRequested</c> handler): called as the
    /// first step, before any service teardown.</item>
    /// <item>A workspace-profile switch (<see cref="WorkspaceProfileSwitchingMessage"/>, sent by
    /// <c>SettingsViewModel.SwitchWorkspaceProfile</c> before it flips the active profile pointer):
    /// this persists each pop-out's geometry into the still-active OUTGOING profile. The incoming
    /// profile's own popped-out widgets are then reopened via <see cref="RestorePopOuts"/> once
    /// <see cref="WorkspaceProfileSwitchedMessage"/> reloads <see cref="EnginePage"/> from it.</item>
    /// </list></summary>
    public void PersistAndCloseAllPopOuts() => _popOutCoordinator?.PersistAndCloseAll();

    /// <summary>Applies a pop-out state change directly to the live page and persists it into the
    /// active workspace profile immediately — shared by every pop-out transition above, none of
    /// which are edit-session ops. Reassigning <see cref="EnginePage"/> re-triggers PageHostControl's
    /// rebuild (same mechanism <see cref="AfterEdit"/> relies on), so the placeholder tile
    /// appears/disappears immediately on both the pop-out and the return transition.
    /// <para>
    /// Pop-out state is applied here regardless of edit mode — the pop-out button is edit-chrome-only,
    /// so a live <see cref="_editSession"/> always exists when this fires. Without rebasing, the
    /// session's stale snapshot (captured on <see cref="EnterEditMode"/>, before this pop-out
    /// happened) would silently win on <see cref="SaveEdit"/> (its Commit overwrites this exact
    /// change back to disk), <see cref="CancelEdit"/>, or <see cref="UndoEdit"/> — reverting the
    /// pop-out on the model while the OS window stays open. So when a session is live, the same
    /// transform is also rebased into it via <see cref="PageEditSession.RebaseAll"/>, which applies
    /// to the session's original, every undo entry, and its current snapshot — pop-out state is
    /// orthogonal metadata applied from outside the edit session, so no session outcome (Commit,
    /// Cancel, or Undo) should ever be able to revert it.
    /// </para></summary>
    private void ApplyPopOutStateAndSave(Guid instanceId, PopOutState? state)
    {
        if (EnginePage is null) return;
        EnginePage = PageLayoutEngine.SetPopOut(EnginePage, instanceId, state);
        _editSession?.RebaseAll(p => PageLayoutEngine.SetPopOut(p, instanceId, state));
        SaveActivePage();
    }

    /// <summary>Persists <see cref="EnginePage"/> into the active workspace profile's "dashboard"
    /// page slot (load-modify-save via the store; the profile's other pages and theme are left
    /// untouched) — the same persistence shape <see cref="SaveEdit"/> uses for edit-session commits.
    /// No-op when no profile store is configured or no page is loaded.</summary>
    private void SaveActivePage()
    {
        if (_profileStore is null || EnginePage is null) return;
        var active = _profileStore.LoadActive();
        var pages = active.Pages.ToDictionary(kv => kv.Key, kv => kv.Value);
        pages["dashboard"] = EnginePage;
        _profileStore.Save(active with { Pages = pages });
    }

    private void AfterEdit()
    {
        EnginePage = _editSession!.Current;
        CanUndoEdit = _editSession.CanUndo;
    }

    private void ExitEdit()
    {
        _editSession = null;
        IsEditMode = false;
        IsGalleryOpen = false;
        CanUndoEdit = false;
        WeakReferenceMessenger.Default.Send(new PageEditModeChangedMessage(false));
    }

    private void OnPredictionCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PredictionCardViewModel.IsDismissed))
            HasPredictions = PredictionCards.Any(c => !c.IsDismissed);
    }

    private void UpdatePredictions(IReadOnlyList<ResourcePrediction> predictions)
    {
        foreach (var existing in PredictionCards)
            existing.PropertyChanged -= OnPredictionCardPropertyChanged;
        PredictionCards.Clear();
        foreach (var p in predictions)
        {
            var card = new PredictionCardViewModel(p, _dismissedResources);
            card.PropertyChanged += OnPredictionCardPropertyChanged;
            PredictionCards.Add(card);
        }
        HasPredictions = PredictionCards.Any(c => !c.IsDismissed);
    }

    private void ApplySnapshot(SystemHealthSnapshot snapshot)
    {
        OverallScore       = snapshot.OverallScore;
        OverallLabel       = snapshot.OverallHealth.ToString();
        OverallDescription = DescribeHealth(snapshot.OverallHealth, snapshot.OverallScore);
        HealthRingBrush    = HealthLevelToBrush(snapshot.OverallHealth);
        TrendArrow         = snapshot.OverallTrend switch
        {
            TrendDirection.Improving => "↑",
            TrendDirection.Degrading => "↓",
            _                        => "→",
        };

        UpdateCard(CpuCard,    snapshot.Cpu);
        UpdateCard(MemoryCard, snapshot.Memory);
        UpdateCard(DiskCard,   snapshot.Disk);
        UpdateCard(GpuCard,    snapshot.Gpu);

        // Cache latest snapshot for on-demand use by RunAnalysisCommand and RefreshConsumersCommand
        _latestSnapshot = snapshot;

        // Top consumers — throttled to every 5th tick (~15s at 3s interval)
        _consumerTickCounter++;
        if (_consumerTickCounter >= 5)
        {
            _consumerTickCounter = 0;
            UpdateTopConsumers(snapshot.TopConsumers);
        }

        // Automation status
        ActiveAutomations = snapshot.ActiveAutomations;
        AutomationStatus = snapshot.ActiveAutomations == 0
            ? "No automations active"
            : $"{snapshot.ActiveAutomations} automation{(snapshot.ActiveAutomations > 1 ? "s" : "")} active";

        // Bottleneck analysis — NOT auto-updated; user triggers via RunAnalysisCommand

        // Recommendations — only rebuild when content changes (recommendations are stable tick-to-tick)
        var recs = RecommendationEngine.Evaluate(snapshot, _settings, _leakService.CurrentSuspects);
        bool recsChanged = Recommendations.Count != recs.Count ||
            recs.Where((r, i) => i < Recommendations.Count && Recommendations[i].Title != r.Title).Any();
        if (recsChanged)
        {
            Recommendations.Clear();
            foreach (var r in recs)
                Recommendations.Add(new RecommendationViewModel(r));
        }
    }

    [RelayCommand]
    private void RunAnalysis()
    {
        if (_latestSnapshot?.Bottleneck is not null)
            BottleneckCard.Apply(_latestSnapshot.Bottleneck);
    }

    [RelayCommand]
    private void RefreshConsumers()
    {
        if (_latestSnapshot is not null)
        {
            _consumerTickCounter = 0;
            UpdateTopConsumers(_latestSnapshot.TopConsumers);
        }
    }

    private void UpdateTopConsumers(IReadOnlyList<ProcessImpact> newConsumers)
    {
        bool consumersChanged = TopConsumers.Count != newConsumers.Count ||
            newConsumers.Where((c, i) => i < TopConsumers.Count && TopConsumers[i].Name != c.Name).Any();
        if (consumersChanged)
        {
            TopConsumers.Clear();
            foreach (var c in newConsumers)
                TopConsumers.Add(new ProcessImpactViewModel(c));
        }
    }

    private static void UpdateCard(SubsystemCardViewModel card, SubsystemHealth health)
    {
        card.Score        = health.Score;
        card.Level        = health.Level.ToString();
        card.Summary      = health.Summary;
        card.Value        = health.CurrentValue;
        card.TrendArrow   = health.Trend switch
        {
            TrendDirection.Improving => "↑",
            TrendDirection.Degrading => "↓",
            _                        => "→",
        };
        card.LevelBrush   = HealthLevelToBrush(health.Level);
    }

    private static string DescribeHealth(HealthLevel level, double score) => level switch
    {
        HealthLevel.Excellent => "Your system is running smoothly.",
        HealthLevel.Good      => "Your system is in good shape.",
        HealthLevel.Fair      => "Your system is under moderate load.",
        HealthLevel.Poor      => "Your system is under heavy load. Consider closing unused apps.",
        HealthLevel.Critical  => "Your system is critically stressed. Immediate action recommended.",
        _                     => string.Empty,
    };

    // Static brush cache — avoids allocating new SolidColorBrush objects every tick
    private static readonly IBrush _brushExcellent = new SolidColorBrush(Color.Parse("#30D158"));
    private static readonly IBrush _brushGood      = new SolidColorBrush(Color.Parse("#34C759"));
    private static readonly IBrush _brushFair      = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush _brushPoor      = new SolidColorBrush(Color.Parse("#FF6B35"));
    private static readonly IBrush _brushCritical  = new SolidColorBrush(Color.Parse("#FF3B30"));

    private static IBrush HealthLevelToBrush(HealthLevel level) => level switch
    {
        HealthLevel.Excellent => _brushExcellent,
        HealthLevel.Good      => _brushGood,
        HealthLevel.Fair      => _brushFair,
        HealthLevel.Poor      => _brushPoor,
        HealthLevel.Critical  => _brushCritical,
        _                     => Brushes.Gray,
    };

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        if (_profileStore is not null)
            _profileStore.ProfileRecovered -= OnProfileRecovered;
        _motionSettingsService.MotionChanged -= OnMotionSettingsChanged;
        _subscription?.Dispose();
        _subscription = null;
        _predictionsSubscription?.Dispose();
        _predictionsSubscription = null;
        _staleSubscription?.Dispose();
        _staleSubscription = null;
    }
}

// ── Sub-ViewModels ─────────────────────────────────────────────────────────────

public partial class SubsystemCardViewModel : ObservableObject
{
    public string Name { get; }
    public string Icon { get; }

    [ObservableProperty] private double _score = 100;
    [ObservableProperty] private string _level = "Excellent";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private double _value;
    [ObservableProperty] private string _trendArrow = "→";
    [ObservableProperty] private IBrush _levelBrush = Brushes.Green;

    public SubsystemCardViewModel(string name, string icon)
    {
        Name = name;
        Icon = icon;
    }
}

public sealed class ProcessImpactViewModel
{
    public string Name         { get; }
    public double ImpactScore  { get; }
    public double CpuPercent   { get; }
    public string MemoryLabel  { get; }
    public double BarWidth     => Math.Min(ImpactScore, 100);

    public ProcessImpactViewModel(ProcessImpact impact)
    {
        Name        = impact.Name;
        ImpactScore = impact.ImpactScore;
        CpuPercent  = impact.CpuPercent;
        MemoryLabel = FormatBytes(impact.MemoryBytes);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F0} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}

/// <summary>
/// Wraps a <see cref="BottleneckReport"/> for the Dashboard UI card.
/// Mutable so it can be updated in-place every tick without recreating the object.
/// </summary>
public partial class BottleneckCardViewModel : ObservableObject
{
    // Static brush cache — avoids allocating new brushes every tick
    private static readonly IBrush _severityIdle     = new SolidColorBrush(Color.Parse("#8E8E93"));
    private static readonly IBrush _severityBalanced = new SolidColorBrush(Color.Parse("#30D158"));
    private static readonly IBrush _severityMild     = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush _severityModerate = new SolidColorBrush(Color.Parse("#FF6B35"));
    private static readonly IBrush _severitySevere   = new SolidColorBrush(Color.Parse("#FF3B30"));

    [ObservableProperty] private string _headline       = "Click Run Analysis to check for bottlenecks";
    [ObservableProperty] private string _explanation    = string.Empty;
    [ObservableProperty] private string _upgradeAdvice  = string.Empty;
    [ObservableProperty] private string _workloadLabel  = string.Empty;
    [ObservableProperty] private string _workloadProcess = string.Empty;
    [ObservableProperty] private bool   _hasUpgradeAdvice;
    [ObservableProperty] private bool   _isIdle = true;
    [ObservableProperty] private IBrush _severityBrush = Brushes.Gray;
    [ObservableProperty] private string _bottleneckIcon = "⚙";

    // Detail bars
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _cpuTempLabel  = string.Empty;
    [ObservableProperty] private string _gpuTempLabel  = string.Empty;
    [ObservableProperty] private bool   _showThermalWarning;

    // HVCI / Memory Integrity hint
    [ObservableProperty] private bool   _showCpuTempHint;
    [ObservableProperty] private string _cpuTempHintText = string.Empty;

    public void Apply(BottleneckReport r)
    {
        Headline        = r.Headline;
        Explanation     = r.Explanation;
        UpgradeAdvice   = r.UpgradeAdvice;
        WorkloadLabel   = WorkloadClassifier.WorkloadLabel(r.Workload);
        WorkloadProcess = string.IsNullOrEmpty(r.WorkloadProcess) ? string.Empty : $"({r.WorkloadProcess})";
        HasUpgradeAdvice = !string.IsNullOrEmpty(r.UpgradeAdvice);
        IsIdle           = r.Bottleneck == BottleneckType.Idle;

        SeverityBrush = r.Bottleneck switch
        {
            BottleneckType.Idle     => _severityIdle,
            BottleneckType.Balanced => _severityBalanced,
            _ => r.Severity switch
            {
                BottleneckSeverity.Mild     => _severityMild,
                BottleneckSeverity.Moderate => _severityModerate,
                BottleneckSeverity.Severe   => _severitySevere,
                _                           => Brushes.Gray,
            }
        };

        BottleneckIcon = r.Bottleneck switch
        {
            BottleneckType.GpuBound      => "🖥",
            BottleneckType.VramBound     => "💾",
            BottleneckType.CpuBound      => "⚡",
            BottleneckType.MemoryBound   => "🧠",
            BottleneckType.StorageBound  => "💿",
            BottleneckType.ThermalThrottle => "🌡",
            BottleneckType.Balanced      => "✅",
            _                            => "⏸",
        };

        CpuPercent  = r.CpuPercent;
        GpuPercent  = r.GpuPercent;
        VramPercent = r.GpuVramPercent;
        MemPercent  = r.MemoryPercent;
        DiskPercent = r.DiskPercent;

        CpuTempLabel = r.CpuTempCelsius > 0 ? $"{r.CpuTempCelsius:F0}°C" : "—";
        GpuTempLabel = r.GpuTempCelsius > 0 ? $"{r.GpuTempCelsius:F0}°C" : "—";
        ShowThermalWarning = r.CpuIsThrottling || r.CpuTempCelsius > 95 || r.GpuTempCelsius > 90;

        // Show HVCI hint only when CPU temp is unavailable and Memory Integrity is on
#if WINDOWS
        if (r.CpuTempCelsius < 1)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                if (key?.GetValue("Enabled") is int enabled && enabled == 1)
                {
                    ShowCpuTempHint = true;
                    if (string.IsNullOrEmpty(CpuTempHintText) || CpuTempHintText.StartsWith("CPU temperature"))
                        CpuTempHintText = "CPU temperature requires a kernel-mode sensor driver. Windows Memory Integrity (Core Isolation) is currently blocking it. Disabling Memory Integrity allows the driver to load — a restart is required to take effect.";
                    return;
                }
            }
            catch { }
        }
#endif
        ShowCpuTempHint = false;
    }

    [RelayCommand]
    private void DisableMemoryIntegrity()
    {
#if WINDOWS
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                writable: true);
            key?.SetValue("Enabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            CpuTempHintText = "Memory Integrity disabled. Restart your PC to enable CPU temperature monitoring.";
        }
        catch
        {
            CpuTempHintText = "Registry update failed — ensure the app is running as Administrator.";
        }
#endif
    }
}

public sealed class RecommendationViewModel
{
    // Static brush cache
    private static readonly IBrush _recCriticalBrush = new SolidColorBrush(Color.Parse("#FF3B30"));
    private static readonly IBrush _recWarningBrush  = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush _recInfoBrush     = new SolidColorBrush(Color.Parse("#30D158"));

    public string Title    { get; }
    public string Body     { get; }
    public IBrush IconBrush { get; }
    public string Icon     { get; }

    public RecommendationViewModel(Recommendation rec)
    {
        Title    = rec.Title;
        Body     = rec.Body;
        (Icon, IconBrush) = rec.Severity switch
        {
            RecommendationSeverity.Critical => ("\ue9b2", _recCriticalBrush),
            RecommendationSeverity.Warning  => ("\ue9b1", _recWarningBrush),
            _                               => ("\ue9b0", _recInfoBrush),
        };
    }
}
