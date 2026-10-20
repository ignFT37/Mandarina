// Mandarina: one area to cover in the REDACT tools. Its rectangle is in pixels of the original picture or video (not of
// the preview), so the same region works on the 1400 px preview and on the full-size file; for a video it also says
// when it is covered.

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Filee.App.Mandarina.Tools;

/// <summary>Where a region came from: drawn by the user, or found by one of the local detectors.</summary>
public enum RedactRegionSource
{
    Manual,
    Faces,
    Text,
}

public sealed partial class RedactRegion : ObservableObject
{
    public RedactRegion(Rect rect, RedactRegionSource source = RedactRegionSource.Manual)
    {
        _rect = rect;
        Source = source;
    }

    public RedactRegionSource Source { get; }

    /// <summary>True when a detector found it (a second detection of the same kind does not add it again).</summary>
    public bool IsAuto => Source != RedactRegionSource.Manual;

    /// <summary>Position and size in pixels of the original.</summary>
    [ObservableProperty] private Rect _rect;

    /// <summary>Second of the video where the cover starts; null = from the beginning. Photos ignore it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWholeVideo))]
    private double? _start;

    /// <summary>Second of the video where the cover ends; null = until the end.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWholeVideo))]
    private double? _end;

    /// <summary>True when the region covers the video from start to end.</summary>
    public bool IsWholeVideo => Start is null && End is null;

    /// <summary>True when the region is covered at <paramref name="seconds"/> of the video.</summary>
    public bool IsActiveAt(double seconds) => (Start is not { } start || seconds >= start) && (End is not { } end || seconds <= end);
}
