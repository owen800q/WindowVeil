using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowVeil.Appearance;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.Mvvm;
using WindowVeil.Editor.Overlays;
using WindowVeil.Platform;
using WindowVeil.Profiles;
using WindowVeil.Rendering;
using WindowVeil.Wpf.Controls;

namespace WindowVeil.Editor.ViewModels;

public sealed partial class MainViewModel
{
    private RuleItemViewModel? _selectedRule;
    private string _profileName = "My profile";
    private string _profileProcessName = "";
    private string _profileTitleContains = "";
    private string _profileClassName = "";
    private string? _suggestedProcessName;

    private void InitializeRuleCommands()
    {
        AddRuleCommand = new RelayCommand(AddRule);
        UpdateRuleCommand = new RelayCommand(UpdateRule, () => _selectedRule is not null);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => _selectedRule is not null);
        DuplicateRuleCommand = new RelayCommand(DuplicateRule, () => _selectedRule is not null);
        NewRuleCommand = new RelayCommand(NewRule);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => Rules.Count > 0, ReportError);
        ImportCommand = new AsyncRelayCommand(ImportAsync, onError: ReportError);
        UseGalleryItemCommand = new RelayCommand(p => UseGalleryItem(p as GalleryItemViewModel));
    }

    // ================================================================ rule list

    /// <summary>Rules (the editor's source of truth; mirrored into the preview session).</summary>
    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];

    /// <summary>Selected rule; selecting loads it into the designer.</summary>
    public RuleItemViewModel? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (!Set(ref _selectedRule, value) || value is null) return;
            Designer.LoadRule(value.Rule);
        }
    }

    /// <summary>Adds the designer's rule.</summary>
    public ICommand AddRuleCommand { get; private set; } = null!;

    /// <summary>Replaces the selected rule with the designer's rule.</summary>
    public ICommand UpdateRuleCommand { get; private set; } = null!;

    /// <summary>Removes the selected rule.</summary>
    public ICommand RemoveRuleCommand { get; private set; } = null!;

    /// <summary>Duplicates the selected rule.</summary>
    public ICommand DuplicateRuleCommand { get; private set; } = null!;

    /// <summary>Starts a new rule in the designer.</summary>
    public ICommand NewRuleCommand { get; private set; } = null!;

    private bool TryBuildFromDesigner(out OverlayRule rule)
    {
        var result = Designer.Build();
        if (result.Rule is null)
        {
            foreach (var e in result.Errors) Log.Warn("designer", e);
            StatusText = "The rule is not valid: " + string.Join(" ", result.Errors);
            rule = null!;
            return false;
        }
        rule = result.Rule;
        return true;
    }

    private void AddRule()
    {
        if (!TryBuildFromDesigner(out var rule)) return;
        if (Rules.Any(r => r.Id == rule.Id))
        {
            // Adding again after "Add" creates a second rule with a fresh id.
            rule = rule with { Id = RuleBuilder.NewId() };
        }
        var item = new RuleItemViewModel(rule, SetRuleEnabled);
        Rules.Add(item);
        ApplyToSession(item);
        _selectedRule = item;
        OnPropertyChanged(nameof(SelectedRule));
        Designer.LoadRule(rule);
        Log.Info("rule", $"Added '{rule.Id}': {LocatorFormatter.Summary(rule.Target)} · {LocatorFormatter.DescribeAppearance(rule.Appearance)}.");
        if (_preview is not null && !_isLivePreview) StatusText = "Rule added. Turn on \"Live preview\" to see it on the target.";
    }

    private void UpdateRule()
    {
        if (_selectedRule is not { } item || !TryBuildFromDesigner(out var rule)) return;
        if (rule.Id != item.Id)
        {
            if (Rules.Any(r => r.Id == rule.Id))
            {
                Log.Warn("rule", $"Another rule already uses the id '{rule.Id}'.");
                return;
            }
            _preview?.RemoveRule(item.Id);
        }
        item.Rule = rule;
        ApplyToSession(item);
        Designer.MarkEditing(rule.Id);
        Log.Info("rule", $"Updated '{rule.Id}'.");
    }

    private void RemoveRule()
    {
        if (_selectedRule is not { } item) return;
        _preview?.RemoveRule(item.Id);
        int index = Rules.IndexOf(item);
        Rules.Remove(item);
        Log.Info("rule", $"Removed '{item.Id}'.");
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
        if (SelectedRule is null) Designer.NewRule();
    }

    private void DuplicateRule()
    {
        if (_selectedRule is not { } item) return;
        var copy = item.Rule with
        {
            Id = RuleBuilder.NewId(),
            Name = string.IsNullOrWhiteSpace(item.Rule.Name) ? null : item.Rule.Name + " (copy)",
        };
        var dup = new RuleItemViewModel(copy, SetRuleEnabled);
        Rules.Insert(Rules.IndexOf(item) + 1, dup);
        ApplyToSession(dup);
        SelectedRule = dup;
        Log.Info("rule", $"Duplicated '{item.Id}' as '{copy.Id}'.");
    }

    private void NewRule()
    {
        _selectedRule = null;
        OnPropertyChanged(nameof(SelectedRule));
        Designer.NewRule();
    }

    private void SetRuleEnabled(RuleItemViewModel item, bool enabled)
    {
        item.SetRuleSilently(item.Rule with { Enabled = enabled });
        if (_preview is null || _preview.GetRule(item.Id) is null) return;
        try
        {
            if (enabled) _preview.EnableRule(item.Id);
            else _preview.DisableRule(item.Id);
            Log.Info("rule", $"{(enabled ? "Enabled" : "Disabled")} '{item.Id}'.");
        }
        catch (Exception ex) when (ex is WindowVeilException or KeyNotFoundException or ObjectDisposedException)
        {
            item.SetRejected(ErrorMessages.Describe(ex));
            Log.Warn("rule", $"{item.Id}: {ErrorMessages.Describe(ex)}");
        }
    }

    /// <summary>Adds or updates the rule in the preview session; configuration errors mark the row instead of throwing.</summary>
    private void ApplyToSession(RuleItemViewModel item)
    {
        var session = _preview;
        if (session is null) { item.SetDetached(); return; }
        try
        {
            if (session.GetRule(item.Id) is null) session.AddRule(item.Rule);
            else session.UpdateRule(item.Rule);
            item.Apply(session.GetRuleStatus(item.Id));
        }
        catch (Exception ex) when (ex is WindowVeilException or KeyNotFoundException or ObjectDisposedException)
        {
            item.SetRejected(ErrorMessages.Describe(ex));
            Log.Warn("rule", $"{item.Id} was not applied: {ErrorMessages.Describe(ex)}");
        }
    }

    // ================================================================ profiles

    /// <summary>Profile name.</summary>
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value ?? ""); }

    /// <summary>Profile application: process name (derived from the target window).</summary>
    public string ProfileProcessName { get => _profileProcessName; set => Set(ref _profileProcessName, value ?? ""); }

    /// <summary>Profile application: title substring (optional).</summary>
    public string ProfileTitleContains { get => _profileTitleContains; set => Set(ref _profileTitleContains, value ?? ""); }

    /// <summary>Profile application: window class (optional).</summary>
    public string ProfileClassName { get => _profileClassName; set => Set(ref _profileClassName, value ?? ""); }

    /// <summary>Exports the rules as a JSON profile.</summary>
    public ICommand ExportCommand { get; private set; } = null!;

    /// <summary>Imports a JSON profile.</summary>
    public ICommand ImportCommand { get; private set; } = null!;

    private void SuggestProfileApplication(TargetWindowInfo window)
    {
        // Profiles select windows by stable properties (never by HWND): default to the process name.
        if (string.IsNullOrWhiteSpace(_profileProcessName) || _profileProcessName == _suggestedProcessName)
        {
            ProfileProcessName = window.ProcessName;
            _suggestedProcessName = window.ProcessName;
        }
        if (_profileName == "My profile" && window.ProcessName.Length > 0) ProfileName = window.ProcessName + " overlays";
    }

    private WindowSelector BuildProfileSelector()
    {
        var selector = string.IsNullOrWhiteSpace(_profileProcessName) ? new WindowSelector() : WindowSelector.ByProcessName(_profileProcessName.Trim());
        if (!string.IsNullOrWhiteSpace(_profileTitleContains)) selector = selector.WithTitle(_profileTitleContains.Trim());
        if (!string.IsNullOrWhiteSpace(_profileClassName)) selector = selector.WithClassName(_profileClassName.Trim());
        return selector;
    }

    private async Task ExportAsync()
    {
        if (_dialogs is null) return;
        var export = ProfileExporter.TryFromRules(_profileName.Trim(), BuildProfileSelector(), Rules.Select(r => r.Rule).ToArray());
        ReportDiagnostics("export", export.Diagnostics);
        if (export.Profile is null)
        {
            StatusText = "The profile cannot be exported; see the diagnostics panel.";
            return;
        }
        string json = ProfileSerializer.ToJson(export.Profile);
        var path = _dialogs.PickSaveProfile(SafeFileName(_profileName) + ".json");
        if (path is null) return;
        await File.WriteAllTextAsync(path, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log.Info("profile", $"Exported {export.Profile.Rules.Count} rules to {path}.");
        StatusText = $"Profile saved: {path}";
    }

    private async Task ImportAsync()
    {
        if (_dialogs?.PickOpenProfile() is not { } path) return;
        var load = await Task.Run(() => ProfileSerializer.LoadFile(path));
        ReportDiagnostics("profile", load.Diagnostics);
        if (load.Profile is not { } profile)
        {
            StatusText = "The profile could not be loaded; see the diagnostics panel.";
            return;
        }
        var bind = profile.ToRules(_engine.Appearances);
        ReportDiagnostics("profile", bind.Diagnostics);

        // Replace the current rules.
        foreach (var old in Rules) _preview?.RemoveRule(old.Id);
        Rules.Clear();
        foreach (var rule in bind.Rules)
        {
            var item = new RuleItemViewModel(rule, SetRuleEnabled);
            Rules.Add(item);
            ApplyToSession(item);
        }
        ProfileName = profile.Name;
        ProfileProcessName = profile.Application.ProcessName ?? "";
        ProfileTitleContains = profile.Application.TitleContains ?? "";
        ProfileClassName = profile.Application.ClassName ?? "";
        _suggestedProcessName = null;
        NewRule();
        int skipped = profile.Rules.Count - bind.Rules.Count;
        Log.Info("profile", $"Imported '{profile.Name}' from {path}: {bind.Rules.Count} rules{(skipped > 0 ? $", {skipped} skipped" : "")}.");
        StatusText = $"Imported {bind.Rules.Count} rules from '{profile.Name}'.";

        // Attach to a window matching the profile's application when the current target does not match.
        if (_target is null || !profile.Selector.Matches(_target))
        {
            var match = await Task.Run(() => _engine.FindWindows(profile.Selector).FirstOrDefault());
            if (match is not null)
            {
                Log.Info("profile", $"Found a window matching the profile: {match}.");
                await SetTargetAsync(match.Handle);
            }
            else
            {
                Log.Info("profile", $"No running window matches {profile.Selector}; choose a target to preview the rules.");
            }
        }
    }

    private void ReportDiagnostics(string source, IEnumerable<ProfileDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            var level = d.Severity switch
            {
                ProfileDiagnosticSeverity.Error => Diagnostics.LogLevel.Error,
                ProfileDiagnosticSeverity.Warning => Diagnostics.LogLevel.Warning,
                _ => Diagnostics.LogLevel.Info,
            };
            string where = d.LineNumber is { } line ? $" (line {line}, byte {d.BytePositionInLine ?? 0})" : "";
            Log.Write(level, source, $"{d.Severity} at {d.Path}{where}: {d.Message}");
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }

    // ================================================================ gallery

    /// <summary>Gallery tiles (native presets and registered WPF appearances).</summary>
    public ObservableCollection<GalleryItemViewModel> Gallery { get; } = [];

    /// <summary>Applies a gallery tile to the designer (parameter: <see cref="GalleryItemViewModel"/>).</summary>
    public ICommand UseGalleryItemCommand { get; private set; } = null!;

    /// <summary>
    /// Registers the built-in gallery. WPF items go through <see cref="Services.EditorAppearanceCatalog.Register(string, string, Func{UIElement})"/>
    /// so they are registered in <c>engine.Appearances</c> under stable ids and rules reference them with
    /// <see cref="RegisteredAppearance"/> (exportable). Add your own controls here.
    /// </summary>
    private void RegisterGallery()
    {
        _catalog.Register("privacy-badge", "Privacy badge", () => new PrivacyBadge { Text = "Hidden by policy" },
            "WindowVeil.Wpf.Controls.PrivacyBadge next to the element.", OverlayPlacement.Right(offsetX: 8), OverlayInputMode.PassThrough);
        _catalog.Register("custom-button", "Custom WPF button", CreateCustomButton,
            "A Button whose command logs to the editor's diagnostics (interactive).", OverlayPlacement.Right(offsetX: 8), OverlayInputMode.Interactive);
        _catalog.Register("sample-card", "Custom UserControl", () => new SampleCardOverlay(),
            "SampleCardOverlay, a XAML UserControl of the editor project.", OverlayPlacement.Below(offsetY: 6), OverlayInputMode.PassThrough);

        Gallery.Add(GalleryItemViewModel.ForPreset("solid-mask", "Solid mask", "Opaque fill drawn by the native (GDI) renderer.",
            AppearanceType.SolidMask, () => PreviewOnSampleText(new Border { Background = Brush(0xFF, 0x20, 0x20, 0x20) }), UseGalleryItemCommand));
        Gallery.Add(GalleryItemViewModel.ForPreset("transparent-mask", "Transparent mask", "Translucent fill; the content stays readable.",
            AppearanceType.TransparentMask, () => PreviewOnSampleText(new Border { Background = Brush(0x66, 0xE5, 0x39, 0x35) }), UseGalleryItemCommand));
        Gallery.Add(GalleryItemViewModel.ForPreset("highlight", "Highlight border", "A frame around the element; the inside stays click-through.",
            AppearanceType.Highlight, () => PreviewOnSampleText(new Border { BorderBrush = Brush(0xFF, 0xFF, 0xB3, 0x00), BorderThickness = new Thickness(3), Margin = new Thickness(-4) }), UseGalleryItemCommand));
        foreach (var entry in _catalog.Entries) Gallery.Add(GalleryItemViewModel.ForCatalog(entry, UseGalleryItemCommand));
    }

    private Button CreateCustomButton() => new()
    {
        Content = "Custom action",
        Padding = new Thickness(12, 4, 12, 4),
        // Runs on WindowVeil's WPF overlay thread when clicked in the target; the log is thread-safe.
        Command = new ActionCommand(_ => Log.Info("overlay", "Custom WPF button clicked — its command ran in the editor process.")),
    };

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Grid PreviewOnSampleText(UIElement overlay)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6) };
        grid.Children.Add(new TextBlock { Text = "Account 1234-5678", Margin = new Thickness(4, 2, 4, 2), FontSize = 13 });
        grid.Children.Add(overlay);
        return grid;
    }

    private void UseGalleryItem(GalleryItemViewModel? item)
    {
        if (item is null) return;
        Designer.UseGalleryItem(item);
        Log.Info("designer", item.IsWpf
            ? $"Appearance set to registered '{item.Id}' ({item.DisplayName}); exported as {{ \"type\": \"registered\", \"factory\": \"{item.Id}\" }}."
            : $"Appearance set to {item.DisplayName}.");
    }
}
