// State of the live donut toolbar: which files are being converted and which presets are offered, on the front and
// the back face of the donut (Tab flips between them, see DonutMenu.Flip.cs).

using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Controls;
using Filee.App.Mandarina;
using Panels = Filee.App.Mandarina.Panels;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.App.ViewModels;

public sealed partial class RadialViewModel(UserDataStore store, ILocalizer loc, PresetAvailability availability) : ObservableObject
{
    /// <summary>Mandarina: the blurred screen behind the wheel (frosted glass), captured just before it shows.</summary>
    [ObservableProperty] private IImage? _backdrop;

    /// <summary>Mandarina: which wheel is open. Tapping another wheel's key switches (<see cref="SetWheel"/>).</summary>
    public WheelMode Wheel { get; private set; } = WheelMode.Convert;

    /// <summary>Mandarina: the tools or actions in slice order (empty on the conversion wheel).</summary>
    public IReadOnlyList<WheelEntry> Entries { get; private set; } = [];

    /// <summary>What the centre pill says while no slice is under the pointer: the files' size.</summary>
    private string _idleCenter = "";

    [ObservableProperty] private IReadOnlyList<DonutItem> _items = [];
    [ObservableProperty] private string _centerTitle = "";
    [ObservableProperty] private string? _centerSubtitle;

    /// <summary>The job the progress ring shows, for the download badge above it; null when the ring shows none.</summary>
    [ObservableProperty] private JobViewModel? _progressJob;
    [ObservableProperty] private double _outerRadius = 150;
    [ObservableProperty] private double _holeRatio = 0.42;
    [ObservableProperty] private double _sliceOpacity = 0.96;

    /// <summary>The other face has a slice for these files: Tab flips to it.</summary>
    [ObservableProperty] private bool _canFlip;

    /// <summary>
    /// "Tab ↻ back" / "Tab ↻ front" in the hub (with the flip key of the settings) while <see cref="CanFlip"/> and a key
    /// or the wheel flips; else null.
    /// </summary>
    [ObservableProperty] private string? _flipHint;

    /// <summary>The hint shows a mouse instead of a key: only the wheel flips.</summary>
    [ObservableProperty] private bool _flipHintWheel;

    /// <summary>The key that flips the donut (settings name, "Tab"), or null for none.</summary>
    [ObservableProperty] private string? _flipKey;

    /// <summary>A turn of the mouse wheel flips the donut.</summary>
    [ObservableProperty] private bool _flipWithWheel;

    /// <summary>
    /// The window keeps room for the ring a group fans out. Dragging, the donut is placed before the files (and so
    /// the donut) are known: room when any donut has a group. In click mode only when this one does
    /// (<see cref="FitToThisDonut"/>), so other donuts stay as small, and as close to the cursor at a screen edge, as
    /// without groups.
    /// </summary>
    [ObservableProperty] private bool _reserveSubRing;

    /// <summary>
    /// 0 while a drag gesture waits for files, 1 otherwise. The gesture starts before anyone knows what is being
    /// dragged (a file, text, or nothing: Ctrl + a rubber-band selection), so the donut only appears when files enter.
    /// </summary>
    [ObservableProperty] private double _donutOpacity = 1;

    /// <summary>
    /// Background of the window while the donut is hidden: practically invisible, but not fully transparent, so
    /// Windows still delivers the drag to this window and the files can arrive.
    /// </summary>
    [ObservableProperty] private string _hitBackground = "Transparent";

    /// <summary>Files the donut will convert (empty while waiting for a drag to enter).</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>Presets in slice order.</summary>
    public IReadOnlyList<Preset> Presets { get; private set; } = [];

    /// <summary>For each slice, the presets its group fans out (empty for an ordinary preset).</summary>
    private IReadOnlyList<IReadOnlyList<Preset>> _subPresets = [];

    /// <summary>The slices of one face of the donut, for the files on it.</summary>
    private sealed record Face(IReadOnlyList<Preset> Presets, IReadOnlyList<IReadOnlyList<Preset>> SubPresets, IReadOnlyList<DonutItem> Items)
    {
        public static readonly Face Empty = new([], [], []);

        /// <summary>
        /// A slice converts these files. A face without one isn't flipped to: all of it greyed out would only be a
        /// dead end, and Tab doing nothing there is what it does on a donut with no back face.
        /// </summary>
        public bool IsUsable => Items.Any(item => item.IsEnabled);
    }

    private Face _front = Face.Empty;

    // The back face: its presets as the profile has them, and its slices for the files once they are checked (null
    // until then). Checking every preset against the files takes as long as for the front (a moment when engines are
    // missing), so the back is checked after the front is on screen, not before (see Load).
    private IReadOnlyList<string> _backIds = [];
    private IReadOnlyList<FileFormat> _formats = [];
    private Face? _back;
    private int _loads;

    /// <summary>The back face is showing (after a flip).</summary>
    public bool IsBackFace { get; private set; }

    /// <summary>
    /// The preset that slice <paramref name="index"/> converts with, or slice <paramref name="sub"/> of its group's ring;
    /// null when there is none or it can't convert these files.
    /// </summary>
    public Preset? PresetAt(int index, int sub)
    {
        if (index < 0 || index >= Items.Count || index >= Presets.Count) // Mandarina: Presets can be shorter (actions wheel)
            return null;
        if (sub < 0)
            return Items[index] is { IsEnabled: true, IsGroup: false } ? Presets[index] : null;
        return Items[index].Children is { } children && sub < children.Count && children[sub].IsEnabled
            ? _subPresets[index][sub]
            : null;
    }

    /// <summary>The preset slice <paramref name="index"/> shows, or slice <paramref name="sub"/> of its group's ring (to edit it).</summary>
    public Preset? PresetShownAt(int index, int sub) =>
        index < 0 || index >= Presets.Count ? null
        : sub < 0 ? Presets[index]
        : sub < _subPresets[index].Count ? _subPresets[index][sub] : null;

    /// <summary>True when opened with known files (keyboard, context menu, drop zone): slices are clicked.</summary>
    public bool IsClickMode { get; private set; }

    public bool HasFiles => Files.Count > 0;

    /// <summary>Drag mode before any file entered: an empty ring saying "Bring files here".</summary>
    public void ResetForDrag(WheelMode wheel = WheelMode.Convert) // Mandarina: wheel
    {
        Wheel = wheel;
        IsClickMode = false;
        Files = [];
        Entries = [];
        ApplyAppearance();
        ReserveSubRing = store.Profiles.Any(profile => HasGroups(profile.PresetIds.Concat(profile.BackPresetIds ?? [])));
        _loads++;
        (_front, _back, _backIds, _formats) = (Face.Empty, Face.Empty, [], []);
        ShowFace(back: false);
        CenterTitle = loc["donut.center_drag"];
        CenterSubtitle = null;
        DonutOpacity = 0;
        HitBackground = "#01000000";
    }

    /// <summary>Mandarina: shows another wheel for the same files. False when it is already showing.</summary>
    public bool SetWheel(WheelMode wheel)
    {
        if (wheel == Wheel)
            return false;
        Wheel = wheel;
        if (HasFiles)
            Load(Files, IsClickMode);
        return true;
    }

    /// <summary>
    /// Fills the donut for the given files (files and, for Mandarina's actions wheel, folders), on its front face, or on the back when <paramref name="backFace"/> and the
    /// back has a slice for them (the same files again after a preset was edited).
    /// </summary>
    public void Load(IReadOnlyList<string> files, bool clickMode, bool backFace = false)
    {
        IsClickMode = clickMode;
        Files = files;
        DonutOpacity = 1;
        HitBackground = "Transparent";
        ApplyAppearance();
        _idleCenter = SizeText(files);

        if (Wheel != WheelMode.Convert)
        {
            LoadEntries(files);
            return;
        }
        Entries = [];

        // Mandarina: the conversion wheel works on the files; folders only have actions.
        var convertible = files.Where(path => !Directory.Exists(path)).ToList();
        var profile = convertible.Count == 0 ? null : ProfileSelector.Select(store.Profiles, convertible);
        var formats = ProfileSelector.DetectFormats(convertible);
        // Both faces follow the same rule: a slice that can't convert these files is greyed out and says why.
        _front = BuildFace(profile?.PresetIds ?? [], formats);
        (_back, _backIds, _formats) = (null, [.. profile?.BackPresetIds ?? []], formats);
        var load = ++_loads;
        if (backFace)
        {
            ShowFace(BackFace().IsUsable);
        }
        else
        {
            ShowFace(back: false, checkBack: false);
            // Right after the donut is drawn: until then Tab has nothing to flip to, nor the hub a hint.
            if (_backIds.Count > 0)
                Dispatcher.UIThread.Post(() =>
                {
                    if (load == _loads && _back is null)
                        ShowFace(IsBackFace);
                }, DispatcherPriority.Background);
        }

        CenterTitle = _idleCenter; // Mandarina: the files' size, not "3 files · PNG"
        CenterSubtitle = Items.Count == 0 ? loc["wheel.empty_convert"] : clickMode ? loc["donut.close_hint"] : null;
    }

    /// <summary>Mandarina: the tools or actions wheel. It has no back face.</summary>
    private void LoadEntries(IReadOnlyList<string> files)
    {
        Entries = Wheel == WheelMode.Actions ? WheelCatalog.Actions : WheelCatalog.ToolsFor(files, loc);
        // One preset per slice keeps Presets aligned with the slices; a tool that opens a panel has a stand-in
        // (RadialController opens its panel instead of converting).
        var presets = Wheel == WheelMode.Actions
            ? []
            : Entries.Select(e => e.Preset ?? new Preset { Id = "mandarina-panel-" + e.Id, Name = loc[e.TitleKey] }).ToList();
        var subPresets = presets.Select(_ => (IReadOnlyList<Preset>)[]).ToList();
        var formats = ProfileSelector.DetectFormats(files.Where(path => !Directory.Exists(path)));
        var items = Entries.Select(entry =>
        {
            var reason = entry.OpensPanel ? Panels.PanelRegistry.Unavailable(entry.Id, loc)
                : entry.Preset is { } preset ? availability.Check(preset, formats) : null;
            return new DonutItem
            {
                Id = entry.Id,
                Label = loc[entry.TitleKey].ToUpper(CultureInfo.CurrentUICulture),
                Icon = entry.Icon,
                IsEnabled = reason is null,
                DisabledReason = reason,
            };
        }).ToList();
        _loads++;
        _front = new Face(presets, subPresets, items);
        (_back, _backIds, _formats) = (Face.Empty, [], formats);
        IsBackFace = false;
        Presets = presets;
        _subPresets = subPresets;
        Items = items;
        (CanFlip, FlipHint, FlipHintWheel) = (false, null, false);
        CenterTitle = _idleCenter;
        CenterSubtitle = Items.Count == 0 ? loc["wheel.empty_tools"] : null;
    }

    /// <summary>"2,2 MB" for the files; "3 elementos" when only folders were brought.</summary>
    private string SizeText(IReadOnlyList<string> paths)
    {
        long bytes = 0;
        var files = 0;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    bytes += new FileInfo(path).Length;
                    files++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (files == 0)
            return loc.Format("wheel.items", paths.Count);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1000 && unit < units.Length - 1)
        {
            size /= 1000;
            unit++;
        }
        return string.Format(CultureInfo.CurrentUICulture, unit == 0 ? "{0:0} {1}" : "{0:0.#} {1}", size, units[unit]);
    }

    /// <summary>
    /// Shows the other face (the donut is edge-on in its flip). Returns false, changing nothing, when it has no slice
    /// for these files (<see cref="CanFlip"/>).
    /// </summary>
    public bool ToggleFace()
    {
        if (_back is null)
            ShowFace(IsBackFace); // asked for before it was checked: check it now
        if (!CanFlip)
            return false;
        ShowFace(!IsBackFace);
        CenterSubtitle = IsClickMode ? loc["donut.close_hint"] : null;
        return true;
    }

    /// <summary>Shows a face; <paramref name="checkBack"/> false leaves an unchecked back face (and the flip) for later.</summary>
    private void ShowFace(bool back, bool checkBack = true)
    {
        var face = back ? BackFace() : _front;
        if (IsBackFace != back || !ReferenceEquals(Items, face.Items))
        {
            IsBackFace = back;
            Presets = face.Presets;
            _subPresets = face.SubPresets;
            Items = face.Items;
        }
        var other = back ? _front : checkBack ? BackFace() : _back;
        CanFlip = other?.IsUsable == true;
        var how = FlipKey is { } key ? KeyNames.Display(key) : "";
        FlipHint = CanFlip && (FlipKey is not null || FlipWithWheel)
            ? loc.Format(back ? "donut.flip_to_front" : "donut.flip_to_back", how).Trim()
            : null;
        FlipHintWheel = FlipHint is not null && FlipKey is null;
    }

    /// <summary>The back face's slices for the files, checked now if they weren't yet.</summary>
    private Face BackFace() => _back ??= BuildFace(_backIds, _formats);

    private bool HasGroups(IEnumerable<string> presetIds) => presetIds.Any(id => store.FindPreset(id)?.IsGroup == true);

    private Face BuildFace(IEnumerable<string> presetIds, IReadOnlyList<FileFormat> formats)
    {
        var presets = presetIds
            .Select(store.FindPreset)
            .OfType<Preset>()
            // Mandarina: a file isn't offered its own format ("PNG" on a PNG).
            .Where(p => !(formats.Count == 1 && p.Id == $"to-{formats[0].Id}"))
            .ToList();

        var subPresets = presets
            .Select(p => (IReadOnlyList<Preset>)(p.IsGroup ? p.SubPresetIds : [])
                .Select(store.FindPreset).OfType<Preset>().Where(c => !c.IsGroup).Take(Preset.MaxSubPresets).ToList())
            .ToList();

        DonutItem ItemOf(Preset p, bool caption = true)
        {
            var reason = availability.Check(p, formats);
            return new DonutItem
            {
                Id = p.Id,
                // On a group's ring the group's name gives the context: "Rotate" → Left / Right / 180°.
                Label = caption ? loc.DisplayName(p) : loc.RingName(p),
                Caption = !caption ? null
                    : p.TargetFormat == BuiltInData.SameAsSource
                        ? string.Join("/", formats.Select(f => f.DisplayName).Take(2))
                        : FormatLabels.NameOf(loc, p.TargetFormat),
                IsEnabled = reason is null,
                DisabledReason = reason,
            };
        }

        var items = presets.Select((p, i) =>
        {
            if (!p.IsGroup)
                return ItemOf(p);
            // A group: its own name and size, and its presets on the ring it fans out (labels only, they're small).
            var children = subPresets[i].Select(c => ItemOf(c, caption: false)).ToList();
            return new DonutItem
            {
                Id = p.Id,
                Label = loc.DisplayName(p),
                Caption = loc.Format("donut.group_caption", children.Count),
                IsEnabled = children.Any(c => c.IsEnabled),
                // Its presets were all deleted since it was made: say so rather than show a dead slice.
                DisabledReason = children.Count == 0 ? loc["donut.group_empty"]
                    : children.Select(c => c.DisabledReason).FirstOrDefault(r => r is not null),
                Children = children,
            };
        }).ToList();
        return new Face(presets, subPresets, items);
    }

    /// <summary>Updates the centre hint for the emphasised slice (and slice <paramref name="sub"/> of its group's ring).</summary>
    public void ShowHint(int index, int sub, bool centerHot)
    {
        // Mandarina: the pill in the hole names the slice under the pointer, else shows the files' size.
        if (Wheel != WheelMode.Convert || !Items.Any(i => i.IsGroup))
        {
            CenterTitle = index >= 0 && index < Items.Count ? Items[index].Label : _idleCenter;
            CenterSubtitle = index >= 0 && index < Items.Count && !Items[index].IsEnabled ? Items[index].DisabledReason
                : Items.Count == 0 ? CenterSubtitle : null;
            return;
        }
        if (centerHot)
            CenterSubtitle = loc["donut.cancel"];
        else if (index >= 0 && index < Items.Count && Items[index].Children is { Count: > 0 } children &&
                 (sub >= 0 || Items[index].IsEnabled))
            CenterSubtitle = sub >= 0 && sub < children.Count
                ? children[sub].IsEnabled
                    ? loc.Format(IsClickMode ? "donut.click_to" : "donut.drop_to", $"{Items[index].Label} · {children[sub].Label}")
                    : children[sub].DisabledReason
                : loc["donut.pick_in_group"];
        else if (index >= 0 && index < Items.Count)
            CenterSubtitle = Items[index].IsEnabled
                ? loc.Format(IsClickMode ? "donut.click_to" : "donut.drop_to", Items[index].Label)
                : Items[index].DisabledReason;
        else
            CenterSubtitle = IsClickMode ? loc["donut.close_hint"] : null;
    }

    /// <summary>
    /// Click mode, before the window is placed: room for a group's ring only if this donut has a group, on either face
    /// (the window must not grow, and the donut move, when it flips).
    /// </summary>
    public void FitToThisDonut() => ReserveSubRing = _front.Items.Any(item => item.IsGroup) || HasGroups(_backIds);

    private void ApplyAppearance()
    {
        OuterRadius = Math.Clamp(store.Settings.Donut.OuterRadius, 100, 240);
        HoleRatio = store.Settings.Donut.HoleRatio;
        SliceOpacity = store.Settings.Donut.Opacity;
        // Mandarina: the tools and actions wheels have no back face.
        FlipKey = Wheel == WheelMode.Convert ? store.Settings.Donut.EffectiveFlipKey(store.Settings.Triggers) : null;
        FlipWithWheel = Wheel == WheelMode.Convert && store.Settings.Donut.FlipWithWheel;
    }
}
