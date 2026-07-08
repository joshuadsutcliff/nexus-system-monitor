using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Rules;
using NexusMonitor.Core.Storage;

namespace NexusMonitor.UI.ViewModels;

public partial class RulesViewModel : ViewModelBase
{
    private readonly RulesPersistence _persistence;
    private readonly ProcessGroupStore? _groupStore;
    private Guid _editingId = Guid.Empty; // Empty = creating a new rule

    // ── Rules list ────────────────────────────────────────────────────────────
    public ObservableCollection<ProcessRule> Rules { get; } = [];
    public ObservableCollection<ProcessRule> FilteredRules { get; } = [];

    [ObservableProperty] private bool _hasFilteredRules;

    [ObservableProperty] private ProcessRule? _selectedRule;
    [ObservableProperty] private string _searchText = string.Empty;

    // ── Editor visibility ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _isEditorVisible;
    [ObservableProperty] private string _editorTitle = "New Rule";

    // ── Edit fields ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _editName    = "";
    [ObservableProperty] private string _editPattern = "";
    [ObservableProperty] private bool   _editEnabled = true;

    // Priority indices: 0 = (none), 1..n = enum values in display order
    [ObservableProperty] private int _editPriorityIndex    = 0;
    [ObservableProperty] private int _editIoPriorityIndex  = 0;
    [ObservableProperty] private int _editMemPriorityIndex = 0;
    [ObservableProperty] private int _editEfficiencyIndex  = 0; // 0=none, 1=enable, 2=disable

    [ObservableProperty] private string _editGroupName   = "";
    [ObservableProperty] private string _editAffinityHex = "";

    public ObservableCollection<string> AvailableGroupNames { get; } = [];

    // Watchdog / condition
    [ObservableProperty] private int    _editConditionTypeIndex     = 0; // 0=Always 1=CpuAbove 2=RamAbove
    [ObservableProperty] private double _editConditionCpuThreshold  = 25.0;
    [ObservableProperty] private double _editConditionRamMb         = 512.0;
    [ObservableProperty] private int    _editConditionDurationSecs  = 5;
    [ObservableProperty] private int    _editWatchdogActionIndex    = 0; // None/BelowNormal/Idle/Terminate

    [ObservableProperty] private bool   _editDisallowed    = false;
    [ObservableProperty] private bool   _editKeepRunning   = false;
    [ObservableProperty] private bool   _editPreventSleep  = false;
    [ObservableProperty] private string _editMaxInstances  = "";  // "" = not set
    [ObservableProperty] private string _editCpuSetIds     = "";  // comma-separated uint IDs
    [ObservableProperty] private string _editReduceCoreCount = ""; // for ReduceAffinity action

    // Validation
    [ObservableProperty] private string _validationError = "";

    /// <summary>Exposes platform capability flags for binding in the View.</summary>
    public IPlatformCapabilities Platform { get; }

    // ── Visibility helpers ────────────────────────────────────────────────────
    public bool IsConditionEnabled => EditConditionTypeIndex > 0;
    public bool IsWatchdogEnabled  => EditWatchdogActionIndex > 0;

    // True when there's a validation error message to display (empty-state/banner
    // guard — ValidationError.Length can't be bound through BoolConverters.Not
    // directly since it returns UnsetValue for non-bool input).
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);

    partial void OnEditConditionTypeIndexChanged(int value)
        => OnPropertyChanged(nameof(IsConditionEnabled));
    partial void OnEditWatchdogActionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWatchdogEnabled));
        OnPropertyChanged(nameof(IsReduceAffinitySelected));
    }
    partial void OnValidationErrorChanged(string value)
        => OnPropertyChanged(nameof(HasValidationError));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selected = SelectedRule;
        FilteredRules.Clear();

        var source = string.IsNullOrWhiteSpace(SearchText)
            ? (IEnumerable<ProcessRule>)Rules
            : Rules.Where(r =>
                r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.ProcessNamePattern.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var rule in source)
            FilteredRules.Add(rule);

        HasFilteredRules = FilteredRules.Count > 0;

        // Restore selection if it still passes the filter
        if (selected is not null && FilteredRules.Contains(selected))
            SelectedRule = selected;
    }

    // ── Static option lists ───────────────────────────────────────────────────

    public static IReadOnlyList<string> PriorityOptions { get; } =
        ["(none)", "Idle", "Below Normal", "Normal", "Above Normal", "High", "Real Time"];

    public static IReadOnlyList<string> IoPriorityOptions { get; } =
        ["(none)", "Very Low", "Low", "Normal", "High"];

    public static IReadOnlyList<string> MemPriorityOptions { get; } =
        ["(none)", "Very Low", "Low", "Medium", "Normal", "High"];

    public static IReadOnlyList<string> EfficiencyOptions { get; } =
        ["(none)", "Enable", "Disable"];

    public static IReadOnlyList<string> ConditionTypeOptions { get; } =
        ["Always (on launch)", "CPU above threshold", "RAM above threshold"];

    public static IReadOnlyList<string> WatchdogActionOptions { get; } =
    [
        "None",
        "Set Below Normal",
        "Set Idle",
        "Terminate process",
        "Reduce Affinity",
        "Set I/O Priority Low",
        "Set Efficiency Mode",
        "Trim Working Set",
        "Restart process",
        "Log Only"
    ];

    // True when the ReduceAffinity action is selected (shows core count input)
    public bool IsReduceAffinitySelected => EditWatchdogActionIndex == 4;

    // ── Priority enum helpers (index ↔ nullable enum) ─────────────────────────

    private static ProcessPriority? IndexToPriority(int index) => index switch
    {
        1 => ProcessPriority.Idle,
        2 => ProcessPriority.BelowNormal,
        3 => ProcessPriority.Normal,
        4 => ProcessPriority.AboveNormal,
        5 => ProcessPriority.High,
        6 => ProcessPriority.RealTime,
        _ => null
    };

    private static int PriorityToIndex(ProcessPriority? p) => p switch
    {
        ProcessPriority.Idle        => 1,
        ProcessPriority.BelowNormal => 2,
        ProcessPriority.Normal      => 3,
        ProcessPriority.AboveNormal => 4,
        ProcessPriority.High        => 5,
        ProcessPriority.RealTime    => 6,
        _                           => 0
    };

    private static IoPriority? IndexToIoPriority(int index) => index switch
    {
        1 => IoPriority.VeryLow,
        2 => IoPriority.Low,
        3 => IoPriority.Normal,
        4 => IoPriority.High,
        _ => null
    };

    private static int IoPriorityToIndex(IoPriority? p) => p switch
    {
        IoPriority.VeryLow => 1,
        IoPriority.Low     => 2,
        IoPriority.Normal  => 3,
        IoPriority.High    => 4,
        _                  => 0
    };

    private static MemoryPriority? IndexToMemPriority(int index) => index switch
    {
        1 => MemoryPriority.VeryLow,
        2 => MemoryPriority.Low,
        3 => MemoryPriority.Medium,
        4 => MemoryPriority.Normal,
        5 => MemoryPriority.High,
        _ => null
    };

    private static int MemPriorityToIndex(MemoryPriority? p) => p switch
    {
        MemoryPriority.VeryLow => 1,
        MemoryPriority.Low     => 2,
        MemoryPriority.Medium  => 3,
        MemoryPriority.Normal  => 4,
        MemoryPriority.High    => 5,
        _                      => 0
    };

    private static bool? IndexToEfficiency(int index) => index switch
    {
        1 => true,
        2 => false,
        _ => null
    };

    private static int EfficiencyToIndex(bool? v) => v switch
    {
        true  => 1,
        false => 2,
        _     => 0
    };

    private static WatchdogAction IndexToWatchdog(int index) => index switch
    {
        1 => WatchdogAction.SetBelowNormal,
        2 => WatchdogAction.SetIdle,
        3 => WatchdogAction.Terminate,
        4 => WatchdogAction.ReduceAffinity,
        5 => WatchdogAction.SetIoPriorityLow,
        6 => WatchdogAction.SetEfficiencyMode,
        7 => WatchdogAction.TrimWorkingSet,
        8 => WatchdogAction.Restart,
        9 => WatchdogAction.LogOnly,
        _ => WatchdogAction.None
    };

    private static int WatchdogToIndex(WatchdogAction a) => a switch
    {
        WatchdogAction.SetBelowNormal   => 1,
        WatchdogAction.SetIdle          => 2,
        WatchdogAction.Terminate        => 3,
        WatchdogAction.ReduceAffinity   => 4,
        WatchdogAction.SetIoPriorityLow => 5,
        WatchdogAction.SetEfficiencyMode => 6,
        WatchdogAction.TrimWorkingSet   => 7,
        WatchdogAction.Restart          => 8,
        WatchdogAction.LogOnly          => 9,
        _                               => 0
    };

    private static ConditionType IndexToConditionType(int index) => index switch
    {
        1 => ConditionType.CpuAbove,
        2 => ConditionType.RamAbove,
        _ => ConditionType.Always
    };

    private static int ConditionTypeToIndex(ConditionType t) => t switch
    {
        ConditionType.CpuAbove => 1,
        ConditionType.RamAbove => 2,
        _                      => 0
    };

    // ── Constructor ───────────────────────────────────────────────────────────

    public RulesViewModel(RulesPersistence persistence,
        IPlatformCapabilities? platformCapabilities = null,
        ProcessGroupStore? groupStore = null)
    {
        Title        = "Rules";
        _persistence = persistence;
        _groupStore  = groupStore;
        Platform     = platformCapabilities ?? new MockPlatformCapabilities();
        LoadRules();
    }

    // ── List management ───────────────────────────────────────────────────────

    private void LoadRules()
    {
        Rules.Clear();
        foreach (var r in _persistence.GetAll())
            Rules.Add(r);
        ApplyFilter();
    }

    [RelayCommand]
    private void AddRule()
    {
        _editingId = Guid.Empty;
        EditorTitle = "New Rule";
        // RefreshAvailableGroupNames must be called BEFORE setting EditGroupName/LoadRuleIntoEditor
        // so the ComboBox can match the incoming value against its already-populated ItemsSource.
        RefreshAvailableGroupNames();
        SetEditorDefaults();
        IsEditorVisible = true;
    }

    [RelayCommand]
    private void EditRule()
    {
        if (SelectedRule is null) return;
        _editingId  = SelectedRule.Id;
        EditorTitle = $"Edit Rule — {SelectedRule.Name}";
        // RefreshAvailableGroupNames must be called BEFORE setting EditGroupName/LoadRuleIntoEditor
        // so the ComboBox can match the incoming value against its already-populated ItemsSource.
        RefreshAvailableGroupNames();
        LoadRuleIntoEditor(SelectedRule);
        IsEditorVisible = true;
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule is null) return;
        _persistence.Remove(SelectedRule.Id);
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleEnabled(ProcessRule? rule)
    {
        if (rule is null) return;
        rule.IsEnabled = !rule.IsEnabled;
        _persistence.Update(rule);
        // Trigger list refresh for the toggle icon
        var idx = Rules.IndexOf(rule);
        if (idx >= 0) { Rules.RemoveAt(idx); Rules.Insert(idx, rule); }
        SelectedRule = rule;
        ApplyFilter();
    }

    // ── Editor ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SaveEdit()
    {
        ValidationError = "";

        bool hasPattern = !string.IsNullOrWhiteSpace(EditPattern);
        bool hasGroup   = EditGroupName is not ("" or "(none)");
        if (!hasPattern && !hasGroup)
        {
            ValidationError = "A process name pattern or a target group is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(EditName))
            EditName = hasPattern ? EditPattern : EditGroupName;

        var rule = _editingId == Guid.Empty
            ? new ProcessRule { Id = Guid.NewGuid() }
            : _persistence.GetAll().FirstOrDefault(r => r.Id == _editingId)
              ?? new ProcessRule { Id = _editingId };

        rule.Name               = EditName.Trim();
        rule.ProcessNamePattern = EditPattern.Trim();
        rule.GroupName          = EditGroupName is "" or "(none)" ? null : EditGroupName;
        rule.IsEnabled          = EditEnabled;
        rule.Priority           = IndexToPriority(EditPriorityIndex);
        rule.IoPriority         = IndexToIoPriority(EditIoPriorityIndex);
        rule.MemoryPriority     = IndexToMemPriority(EditMemPriorityIndex);
        rule.EfficiencyMode     = IndexToEfficiency(EditEfficiencyIndex);
        rule.Disallowed         = EditDisallowed;
        rule.KeepRunning        = EditKeepRunning;
        rule.PreventSleep       = EditPreventSleep;
        rule.WatchdogAction     = IndexToWatchdog(EditWatchdogActionIndex);

        // MaxInstances
        rule.MaxInstances = int.TryParse(EditMaxInstances, out int mi) && mi > 0 ? mi : null;

        // CPU Sets
        if (!string.IsNullOrWhiteSpace(EditCpuSetIds))
        {
            var ids = EditCpuSetIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => uint.TryParse(s.Trim(), out var u) ? (uint?)u : null)
                .Where(u => u.HasValue)
                .Select(u => u!.Value)
                .ToArray();
            rule.CpuSetIds = ids.Length > 0 ? ids : null;
        }
        else
        {
            rule.CpuSetIds = null;
        }

        // ReduceAffinity action params
        if (rule.WatchdogAction == WatchdogAction.ReduceAffinity)
        {
            rule.ActionParams = new WatchdogActionParams
            {
                ReduceCoreCount = int.TryParse(EditReduceCoreCount, out int rc) && rc > 0 ? rc : 1
            };
        }
        else
        {
            rule.ActionParams = null;
        }

        // Affinity mask
        if (!string.IsNullOrWhiteSpace(EditAffinityHex))
        {
            var hex = EditAffinityHex.TrimStart('0', 'x');
            if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                              null, out long mask))
                rule.AffinityMask = mask;
        }
        else
        {
            rule.AffinityMask = null;
        }

        // Condition
        var condType = IndexToConditionType(EditConditionTypeIndex);
        if (condType != ConditionType.Always || EditWatchdogActionIndex > 0)
        {
            rule.Condition = new RuleCondition
            {
                Type                = condType,
                CpuThresholdPercent = EditConditionCpuThreshold,
                RamThresholdBytes   = (long)(EditConditionRamMb * 1024 * 1024),
                DurationSeconds     = EditConditionDurationSecs
            };
        }
        else
        {
            rule.Condition = null;
        }

        if (_editingId == Guid.Empty)
        {
            _persistence.Add(rule);
            Rules.Add(rule);
        }
        else
        {
            _persistence.Update(rule);
            var idx = Rules.IndexOf(Rules.FirstOrDefault(r => r.Id == rule.Id)!);
            if (idx >= 0) { Rules.RemoveAt(idx); Rules.Insert(idx, rule); }
        }

        ApplyFilter();
        IsEditorVisible = false;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditorVisible = false;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshAvailableGroupNames()
    {
        AvailableGroupNames.Clear();
        AvailableGroupNames.Add("(none)"); // sentinel = no group targeting
        if (_groupStore is not null)
            foreach (var g in _groupStore.GetAll())
                AvailableGroupNames.Add(g.Name);
    }

    private void SetEditorDefaults()
    {
        EditName                   = "";
        EditPattern                = "";
        EditGroupName              = "(none)";
        EditEnabled                = true;
        EditPriorityIndex          = 0;
        EditIoPriorityIndex        = 0;
        EditMemPriorityIndex       = 0;
        EditEfficiencyIndex        = 0;
        EditAffinityHex            = "";
        EditConditionTypeIndex     = 0;
        EditConditionCpuThreshold  = 25.0;
        EditConditionRamMb         = 512.0;
        EditConditionDurationSecs  = 5;
        EditWatchdogActionIndex    = 0;
        EditDisallowed             = false;
        EditKeepRunning            = false;
        EditPreventSleep           = false;
        EditMaxInstances           = "";
        EditCpuSetIds              = "";
        EditReduceCoreCount        = "";
        ValidationError            = "";
    }

    private void LoadRuleIntoEditor(ProcessRule rule)
    {
        EditName                  = rule.Name;
        EditPattern               = rule.ProcessNamePattern;
        EditEnabled               = rule.IsEnabled;
        EditPriorityIndex         = PriorityToIndex(rule.Priority);
        EditIoPriorityIndex       = IoPriorityToIndex(rule.IoPriority);
        EditMemPriorityIndex      = MemPriorityToIndex(rule.MemoryPriority);
        EditEfficiencyIndex       = EfficiencyToIndex(rule.EfficiencyMode);
        EditAffinityHex           = rule.AffinityMask.HasValue
                                        ? $"0x{rule.AffinityMask.Value:X}"
                                        : "";
        EditConditionTypeIndex    = ConditionTypeToIndex(rule.Condition?.Type ?? ConditionType.Always);
        EditConditionCpuThreshold = rule.Condition?.CpuThresholdPercent ?? 25.0;
        EditConditionRamMb        = rule.Condition?.RamThresholdBytes / 1024.0 / 1024.0 ?? 512.0;
        EditConditionDurationSecs = rule.Condition?.DurationSeconds ?? 5;
        EditWatchdogActionIndex   = WatchdogToIndex(rule.WatchdogAction);
        EditDisallowed            = rule.Disallowed;
        EditKeepRunning           = rule.KeepRunning;
        EditPreventSleep          = rule.PreventSleep;
        EditMaxInstances          = rule.MaxInstances?.ToString() ?? "";
        EditCpuSetIds             = rule.CpuSetIds is { Length: > 0 }
                                        ? string.Join(",", rule.CpuSetIds)
                                        : "";
        EditReduceCoreCount       = rule.ActionParams?.ReduceCoreCount?.ToString() ?? "";
        EditGroupName             = rule.GroupName is null or "" ? "(none)" : rule.GroupName;
        ValidationError           = "";
    }
}
