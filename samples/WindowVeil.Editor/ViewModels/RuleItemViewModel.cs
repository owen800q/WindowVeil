using WindowVeil.Editor.Model;
using WindowVeil.Editor.Mvvm;
using WindowVeil.Geometry;
using WindowVeil.Locators;

namespace WindowVeil.Editor.ViewModels;

/// <summary>A row of the rule list: the rule plus its latest <see cref="RuleStatus"/> from the preview session.</summary>
public sealed class RuleItemViewModel : ObservableObject
{
    private readonly Action<RuleItemViewModel, bool> _setEnabled;
    private OverlayRule _rule;
    private string _state = "Not applied";
    private string _resolvedBy = "";
    private string _boundsText = "";
    private string _message = "";
    private bool _isProblem;

    /// <summary>Creates a row. <paramref name="setEnabled"/> is called when the check box changes.</summary>
    public RuleItemViewModel(OverlayRule rule, Action<RuleItemViewModel, bool> setEnabled)
    {
        _rule = rule ?? throw new ArgumentNullException(nameof(rule));
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
    }

    /// <summary>The rule (source of truth for export).</summary>
    public OverlayRule Rule
    {
        get => _rule;
        set
        {
            if (!Set(ref _rule, value)) return;
            OnPropertiesChanged(nameof(Id), nameof(DisplayName), nameof(LocatorSummary), nameof(LocatorCategory), nameof(AppearanceSummary), nameof(IsEnabled));
        }
    }

    /// <summary>Rule id.</summary>
    public string Id => _rule.Id;

    /// <summary>Name, or the id when unnamed.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(_rule.Name) ? _rule.Id : $"{_rule.Name}  ({_rule.Id})";

    /// <summary>Locator category.</summary>
    public string LocatorCategory => LocatorFormatter.Category(_rule.Target);

    /// <summary>Locator description.</summary>
    public string LocatorSummary => LocatorFormatter.Describe(_rule.Target);

    /// <summary>Appearance description.</summary>
    public string AppearanceSummary => LocatorFormatter.DescribeAppearance(_rule.Appearance);

    /// <summary>Enabled check box. Setting it calls session.EnableRule / DisableRule through the owner.</summary>
    public bool IsEnabled
    {
        get => _rule.Enabled;
        set
        {
            if (value == _rule.Enabled) return;
            _setEnabled(this, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Rule state text.</summary>
    public string State { get => _state; private set => Set(ref _state, value); }

    /// <summary>Which locator resolved the target.</summary>
    public string ResolvedBy { get => _resolvedBy; private set => Set(ref _resolvedBy, value); }

    /// <summary>Overlay bounds (physical pixels).</summary>
    public string BoundsText { get => _boundsText; private set => Set(ref _boundsText, value); }

    /// <summary>Error or diagnostic message.</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    /// <summary>True for Unresolved / Failed / rejected rows (highlighted in the list).</summary>
    public bool IsProblem { get => _isProblem; private set => Set(ref _isProblem, value); }

    /// <summary>Applies a status from the session (UI thread).</summary>
    public void Apply(RuleStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        State = status.State.ToString();
        ResolvedBy = status.Resolution?.ResolvedBy is { } by ? LocatorFormatter.Category(by) : "";
        BoundsText = status.OverlayBounds.Count == 0 ? "" :
            status.OverlayBounds.Count == 1 ? status.OverlayBounds[0].ToString() : $"{PixelRect.UnionAll(status.OverlayBounds)} ({status.OverlayBounds.Count} rects)";
        var error = status.Error is { } code ? $"[{code}] " : "";
        Message = error + (status.Message ?? status.Resolution?.Message ?? "");
        IsProblem = status.State is RuleState.Unresolved or RuleState.Failed;
    }

    /// <summary>Marks the rule as rejected by the session (configuration error).</summary>
    public void SetRejected(string message)
    {
        State = "Rejected";
        ResolvedBy = "";
        BoundsText = "";
        Message = message;
        IsProblem = true;
    }

    /// <summary>Marks the rule as not applied (no target attached).</summary>
    public void SetDetached()
    {
        State = "Not applied";
        ResolvedBy = "";
        BoundsText = "";
        Message = "";
        IsProblem = false;
    }

    /// <summary>Called by the owner after the enabled state changed.</summary>
    internal void SetRuleSilently(OverlayRule rule)
    {
        _rule = rule;
        OnPropertiesChanged(nameof(Rule), nameof(IsEnabled));
    }
}
