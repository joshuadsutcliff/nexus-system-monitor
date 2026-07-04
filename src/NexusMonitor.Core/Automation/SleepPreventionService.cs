using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMonitor.Core.Reactive;
using NexusMonitor.Core.Abstractions;
using NexusMonitor.Core.Models;
using NexusMonitor.Core.Rules;

namespace NexusMonitor.Core.Automation;

/// <summary>
/// Each tick, checks whether any running process matches a rule with
/// PreventSleep=true. If yes, calls PreventSleep(); otherwise AllowSleep().
/// </summary>
public sealed class SleepPreventionService : IDisposable
{
    private readonly IProcessProvider        _processProvider;
    private readonly ISleepPreventionProvider _sleepProvider;
    private readonly AppSettings             _settings;
    private readonly ILogger<SleepPreventionService> _logger;

    private bool _currentlyPreventing;
    private bool _running;
    private IDisposable? _subscription;

    // AppSettings is not a direct dependency here; we get rules via the engine's settings
    private readonly Func<IReadOnlyList<ProcessRule>> _rulesGetter;

    public SleepPreventionService(
        IProcessProvider         processProvider,
        ISleepPreventionProvider sleepProvider,
        AppSettings              settings,
        ILogger<SleepPreventionService>? logger = null)
    {
        _processProvider = processProvider;
        _sleepProvider   = sleepProvider;
        _settings        = settings;
        _logger          = logger ?? NullLogger<SleepPreventionService>.Instance;
        _rulesGetter     = () => settings.Rules ?? [];
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _subscription = _processProvider
            .GetProcessStream(TimeSpan.FromSeconds(2))
            .RetryWithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
                onError: ex => _logger.LogWarning(ex, "SleepPreventionService process stream faulted; retrying with backoff"))
            .Subscribe(OnTick, ex =>
            {
                _logger.LogError(ex, "SleepPreventionService stream faulted");
                _running = false;
            });
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _subscription?.Dispose();
        _subscription = null;
        if (_currentlyPreventing)
        {
            _sleepProvider.AllowSleep();
            _currentlyPreventing = false;
        }
    }

    private void OnTick(IReadOnlyList<ProcessInfo> processes)
    {
        var rules = _rulesGetter();
        bool shouldPrevent = false;

        foreach (var proc in processes)
        {
            foreach (var rule in rules)
            {
                if (rule.IsEnabled && rule.PreventSleep && rule.Matches(proc.Name))
                {
                    shouldPrevent = true;
                    goto done;
                }
            }
        }
        done:

        if (shouldPrevent && !_currentlyPreventing)
        {
            _sleepProvider.PreventSleep();
            _currentlyPreventing = true;
        }
        else if (!shouldPrevent && _currentlyPreventing)
        {
            _sleepProvider.AllowSleep();
            _currentlyPreventing = false;
        }
    }

    public void Dispose() => Stop();
}
