// Mandarina: line icons for the tools and actions wheels. 24×24 outlines, drawn with a round pen (DonutMenu scales
// them to 18 px above the slice's label).

using Avalonia.Media;

namespace Filee.App.Mandarina;

internal static class WheelIcons
{
    /// <summary>The design size of every icon.</summary>
    public const double Size = 24;

    public static Geometry Compress { get; } = Geometry.Parse("M14 10 L20 4 M14 10 L14 5.5 M14 10 L18.5 10 M10 14 L4 20 M10 14 L10 18.5 M10 14 L5.5 14");
    public static Geometry Metadata { get; } = Geometry.Parse("M3.5 12.5 L3.5 4.5 L4.5 3.5 L12.5 3.5 L20.5 11.5 L11.5 20.5 Z M8.5 7 L8.5 7.01");
    public static Geometry Normalize { get; } = Geometry.Parse("M4 10 L4 14 M8 7 L8 17 M12 4 L12 20 M16 7 L16 17 M20 10 L20 14");
    public static Geometry Mute { get; } = Geometry.Parse("M4 9 L7.5 9 L12 5 L12 19 L7.5 15 L4 15 Z M15.5 9.5 L20.5 14.5 M20.5 9.5 L15.5 14.5");
    public static Geometry Split { get; } = Geometry.Parse("M4 5 L10 5 L10 19 L4 19 Z M14 5 L20 5 L20 19 L14 19 Z");
    public static Geometry Merge { get; } = Geometry.Parse("M9 7 L9 3.5 L16 3.5 L20 7.5 L20 17 L16 17 M4 7 L11 7 L15 11 L15 20.5 L4 20.5 Z");
    public static Geometry CreatePdf { get; } = Geometry.Parse("M6 3.5 L13 3.5 L18 8.5 L18 20.5 L6 20.5 Z M13 3.5 L13 8.5 L18 8.5 M12 11.5 L12 17.5 M9 14.5 L15 14.5");
    public static Geometry Extract { get; } = Geometry.Parse("M3.5 5 L20.5 5 L20.5 9 L3.5 9 Z M5 9 L5 19.5 L19 19.5 L19 9 M10 13 L14 13");

    // The tools that open a panel.
    public static Geometry EditImage { get; } = Geometry.Parse("M4 7 L20 7 M4 17 L20 17 M9 4 L9 10 M15 14 L15 20");
    public static Geometry Annotate { get; } = Geometry.Parse("M4 20 L5 15.5 L16.5 4 L20 7.5 L8.5 19 L4 20 Z M14 6.5 L17.5 10");
    public static Geometry FrameImage { get; } = Geometry.Parse("M3.5 5.5 L20.5 5.5 L20.5 18.5 L3.5 18.5 Z M7.5 9.5 L16.5 9.5 L16.5 14.5 L7.5 14.5 Z");
    public static Geometry CropImage { get; } = Geometry.Parse("M7 3 L7 17 L21 17 M3 7 L17 7 L17 21");
    public static Geometry Redact { get; } = Geometry.Parse("M2.5 12 C5 7.5 8.5 5.5 12 5.5 C15.5 5.5 19 7.5 21.5 12 C19 16.5 15.5 18.5 12 18.5 C8.5 18.5 5 16.5 2.5 12 Z M12 9.5 A2.5 2.5 0 1 0 12 14.5 A2.5 2.5 0 1 0 12 9.5 Z M4 4 L20 20");
    public static Geometry Collage { get; } = Geometry.Parse("M3.5 3.5 L10.5 3.5 L10.5 10.5 L3.5 10.5 Z M13.5 3.5 L20.5 3.5 L20.5 10.5 L13.5 10.5 Z M3.5 13.5 L10.5 13.5 L10.5 20.5 L3.5 20.5 Z M13.5 13.5 L20.5 13.5 L20.5 20.5 L13.5 20.5 Z");
    public static Geometry TrimAudio { get; } = Geometry.Parse("M8 4 L4 4 L4 20 L8 20 M16 4 L20 4 L20 20 L16 20 M12 8 L12 16");
    public static Geometry Channels { get; } = Geometry.Parse("M3.5 12 A5 5 0 1 0 13.5 12 A5 5 0 1 0 3.5 12 Z M10.5 12 A5 5 0 1 0 20.5 12 A5 5 0 1 0 10.5 12 Z");
    public static Geometry Bleep { get; } = Geometry.Parse("M4 9.5 L7.5 9.5 L12 5.5 L12 18.5 L7.5 14.5 L4 14.5 Z M15.5 9 C16.8 10.5 16.8 13.5 15.5 15 M18 6.5 C20.7 9.5 20.7 14.5 18 17.5");
    public static Geometry Visualize { get; } = Geometry.Parse("M3.5 5.5 L20.5 5.5 L20.5 18.5 L3.5 18.5 Z M7 11 L7 13 M10 9 L10 15 M13 7.5 L13 16.5 M16 10 L16 14");
    public static Geometry TrimVideo { get; } = Geometry.Parse("M8 4 L4 4 L4 20 L8 20 M16 4 L20 4 L20 20 L16 20 M10 9 L10 15 L14.5 12 Z");
    public static Geometry CropVideo { get; } = Geometry.Parse("M7 3 L7 17 L21 17 M3 7 L17 7 L17 21 M10.5 9.5 L10.5 14.5 L14.5 12 Z");
    public static Geometry Speed { get; } = Geometry.Parse("M4 6 L12 12 L4 18 Z M12 6 L20 12 L12 18 Z");
    public static Geometry Snapshot { get; } = Geometry.Parse("M3.5 7.5 L7.5 7.5 L9 5 L15 5 L16.5 7.5 L20.5 7.5 L20.5 19 L3.5 19 Z M12 9.5 A3.5 3.5 0 1 0 12 16.5 A3.5 3.5 0 1 0 12 9.5 Z");
    public static Geometry SplitVideo { get; } = Geometry.Parse("M3.5 7 L20.5 7 L20.5 17 L3.5 17 Z M12 3.5 L12 20.5");
    public static Geometry Join { get; } = Geometry.Parse("M3.5 8 L11 8 L11 16 L3.5 16 Z M13 8 L20.5 8 L20.5 16 L13 16 Z M11 12 L13 12");
    // Mandarina's own: a speech bubble with its lines (transcribe) and a screen with a caption under it (subtitles).
    public static Geometry Transcribe { get; } = Geometry.Parse("M3.5 4.5 L20.5 4.5 L20.5 16.5 L11 16.5 L7 20.5 L7 16.5 L3.5 16.5 Z M7.5 9 L16.5 9 M7.5 12.5 L13.5 12.5");
    public static Geometry Subtitles { get; } = Geometry.Parse("M3.5 5 L20.5 5 L20.5 19 L3.5 19 Z M6.5 14.5 L10.5 14.5 M12.5 14.5 L17.5 14.5 M6.5 11 L9 11 M11 11 L17.5 11");
    public static Geometry Organize { get; } = Geometry.Parse("M8 4 L8 20 M4.5 7.5 L8 4 L11.5 7.5 M16 20 L16 4 M12.5 16.5 L16 20 L19.5 16.5");

    public static Geometry Share { get; } = Geometry.Parse("M14 4 L20 4 L20 10 M20 4 L11 13 M18 14 L18 20 L4 20 L4 6 L10 6");
    public static Geometry Float { get; } = Geometry.Parse("M9 3.5 L15 3.5 M10 3.5 L10 9 L7 13 L17 13 L14 9 L14 3.5 M12 13 L12 20.5");
    public static Geometry Copy { get; } = Geometry.Parse("M8 8 L20 8 L20 20 L8 20 Z M16 8 L16 4 L4 4 L4 16 L8 16");
    public static Geometry Move { get; } = Geometry.Parse("M3.5 5.5 L9.5 5.5 L11.5 7.5 L20.5 7.5 L20.5 19.5 L3.5 19.5 Z M9 13.5 L16 13.5 M13.5 11 L16 13.5 L13.5 16");
    public static Geometry CopyPath { get; } = Geometry.Parse("M6 3.5 L13 3.5 L18 8.5 L18 20.5 L6 20.5 Z M13 3.5 L13 8.5 L18 8.5 M9 12.5 L15 12.5 M9 15.5 L15 15.5 M9 18 L13 18");
    public static Geometry Terminal { get; } = Geometry.Parse("M3.5 5.5 L20.5 5.5 L20.5 18.5 L3.5 18.5 Z M7 10 L10 12.5 L7 15 M12 15 L16 15");
}
