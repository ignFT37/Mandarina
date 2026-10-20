// Mandarina: what the tools wheel and the actions wheel offer, choosing per file type and for
// one file or a selection. Some tools run straight from the wheel through the conversion pipeline (a preset that is
// not in the user's library: normalize, mute, PDF create/merge/split, extract); the others open a panel of their own
// (Mandarina/Tools, found by PanelRegistry) and are offered once their panel exists in this build.

using Avalonia.Media;
using Filee.App.Mandarina.Panels;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Mandarina;

/// <summary>A slice of the tools or actions wheel.</summary>
/// <param name="Id">The tool's id (compress, removeMetadata, …; AirDrop is "share").</param>
/// <param name="Preset">How a tool runs from the wheel. Null for actions and for tools that open a panel.</param>
/// <param name="OpensPanel">True when the petal opens the tool's panel (PanelRegistry) instead of converting.</param>
public sealed record WheelEntry(string Id, Geometry Icon, Preset? Preset = null, bool OpensPanel = false)
{
    /// <summary>Short title on the slice and in the centre ("Compress", shown upper-case).</summary>
    public string TitleKey => $"wheel.{Id}";
}

public static class WheelCatalog
{
    /// <summary>File actions, the same for any selection: files, folders or both.</summary>
    public static IReadOnlyList<WheelEntry> Actions { get; } =
    [
        new("share", WheelIcons.Share),
        new("float", WheelIcons.Float),
        new("copy", WheelIcons.Copy),
        new("move", WheelIcons.Move),
        new("copyPath", WheelIcons.CopyPath),
        new("terminal", WheelIcons.Terminal),
    ];

    private enum Kind { None, Photo, Svg, Gif, Audio, Video, Pdf, Archive }

    private static Kind KindOf(string format) => format switch
    {
        "jpg" or "png" or "webp" or "heic" or "tiff" or "avif" or "bmp" => Kind.Photo,
        "svg" => Kind.Svg,
        "gif" => Kind.Gif,
        "mp3" or "m4a" or "wav" or "flac" or "aiff" or "ogg" or "opus" => Kind.Audio,
        "mp4" or "mov" or "mkv" or "m4v" => Kind.Video,
        "pdf" => Kind.Pdf,
        "zip" or "tar" or "gz" or "tgz" or "rar" or "7z" => Kind.Archive,
        _ => Kind.None,
    };

    /// <summary>The tools for these files in wheel order; none for folders or files of different kinds.</summary>
    public static IReadOnlyList<WheelEntry> ToolsFor(IReadOnlyList<string> paths, ILocalizer loc)
    {
        if (paths.Count == 0 || paths.Any(Directory.Exists))
            return [];
        var formats = paths.Select(p => FormatRegistry.Detect(p)?.Id).ToList();
        if (formats.Any(f => f is null))
            return [];
        var kinds = formats.Select(f => KindOf(f!)).Distinct().ToList();
        if (kinds is not [var kind] || kind == Kind.None)
            return [];

        var many = paths.Count > 1;
        var allWav = formats.All(f => f == "wav");
        string Suffix(string id) => loc[$"wheel.{id}.suffix"];

        WheelEntry Tool(string id, Geometry icon, string target, Action<Preset> set, string? subfolder = null)
        {
            var preset = new Preset
            {
                Id = "mandarina-" + id,
                Name = loc[$"wheel.{id}"],
                TargetFormat = target,
                Output = subfolder is null
                    ? new OutputRule { FileNamePattern = "{name} " + Suffix(id) }
                    : new OutputRule { Location = OutputLocation.Subfolder, SubfolderName = "{name} " + subfolder },
            };
            set(preset);
            return new WheelEntry(id, icon, preset);
        }

        // A tool with a panel opens it; the petal only exists once the panel is part of the build.
        WheelEntry? Panel(string id, Geometry icon) => PanelRegistry.Has(id) ? new WheelEntry(id, icon, OpensPanel: true) : null;

        WheelEntry Compress() => Panel("compress", WheelIcons.Compress) ?? kind switch
        {
            Kind.Photo => Tool("compress", WheelIcons.Compress, BuiltInData.SameAsSource, p => p.Image.Quality = 70),
            Kind.Audio when allWav => Tool("compress", WheelIcons.Compress, "flac", _ => { }),
            _ => Tool("compress", WheelIcons.Compress, BuiltInData.SameAsSource, p => p.Media.Quality = MediaQuality.Small),
        };
        WheelEntry Metadata() => Panel("removeMetadata", WheelIcons.Metadata) ?? (kind is Kind.Photo or Kind.Gif
            ? Tool("removeMetadata", WheelIcons.Metadata, BuiltInData.SameAsSource, p =>
            {
                p.Image.KeepMetadata = false;
                p.Image.Quality = 95;
            })
            : Tool("removeMetadata", WheelIcons.Metadata, BuiltInData.SameAsSource, p =>
            {
                p.Media.StripMetadata = true;
                p.Media.CopyAudio = kind == Kind.Audio;
                p.Media.Quality = MediaQuality.High;
            }));
        WheelEntry Normalize() => Tool("normalizeAudio", WheelIcons.Normalize, BuiltInData.SameAsSource, p => p.Media.Normalize = true);
        WheelEntry Mute() => Tool("muteVideo", WheelIcons.Mute, BuiltInData.SameAsSource, p =>
        {
            p.Media.RemoveAudio = true;
            p.Media.Quality = MediaQuality.High;
        });
        WheelEntry CreatePdf() => Tool("createPDF", WheelIcons.CreatePdf, "pdf", p => p.Pdf.MergeIntoSingle = true);
        WheelEntry Merge() => Tool("mergePDF", WheelIcons.Merge, "pdf", p => p.Pdf.MergeIntoSingle = true);
        WheelEntry Split() => Tool("splitPDF", WheelIcons.Split, "pdf", p => p.Pdf.SplitPages = true, subfolder: Suffix("splitPDF"));
        WheelEntry Extract() => Tool("extractArchive", WheelIcons.Extract, FormatRegistry.Folder, _ => { });

        // Mandarina's own, after the common tools: transcribe the speech (one click) or choose how (language, formats, burn in).
        WheelEntry?[] Speech() => [Panel("transcribe", WheelIcons.Transcribe), Panel("subtitles", WheelIcons.Subtitles)];

        // The order, and the choice for one file or several (docs/mandarina/ESPECIFICACION.md).
        WheelEntry?[] entries = kind switch
        {
            Kind.Photo when many =>
                [Compress(), Metadata(), Panel("cropImage", WheelIcons.CropImage), CreatePdf(), Panel("createCollage", WheelIcons.Collage)],
            Kind.Photo =>
            [
                Compress(), Metadata(), Panel("editImage", WheelIcons.EditImage), Panel("annotateImage", WheelIcons.Annotate),
                Panel("frameImage", WheelIcons.FrameImage), Panel("cropImage", WheelIcons.CropImage), Panel("redactImage", WheelIcons.Redact),
            ],
            Kind.Svg => many ? [CreatePdf()] : [],
            Kind.Gif => [Metadata()],
            Kind.Audio when many => [Compress(), Metadata(), Normalize(), .. Speech()],
            Kind.Audio =>
            [
                Compress(), Metadata(), Normalize(), Panel("audioToVideo", WheelIcons.Visualize), Panel("trimAudio", WheelIcons.TrimAudio),
                Panel("audioChannels", WheelIcons.Channels), Panel("redactAudio", WheelIcons.Bleep), .. Speech(),
            ],
            Kind.Video when many => [Compress(), Metadata(), Mute(), Panel("joinVideos", WheelIcons.Join), .. Speech()],
            Kind.Video =>
            [
                Compress(), Metadata(), Mute(), Panel("trimVideo", WheelIcons.TrimVideo), Panel("cropVideo", WheelIcons.CropVideo),
                Panel("changeVideoSpeed", WheelIcons.Speed), Panel("videoSnapshots", WheelIcons.Snapshot),
                Panel("splitVideo", WheelIcons.SplitVideo), Panel("redactVideo", WheelIcons.Redact), .. Speech(),
            ],
            // A PDF has no preset for these two: they appear with their panels.
            Kind.Pdf when many =>
                [Panel("compress", WheelIcons.Compress), Panel("removeMetadata", WheelIcons.Metadata), Split(), Merge()],
            Kind.Pdf =>
                [Panel("compress", WheelIcons.Compress), Panel("removeMetadata", WheelIcons.Metadata), Split(), Panel("organizePDF", WheelIcons.Organize)],
            Kind.Archive => [Extract()],
            _ => [],
        };
        return entries.OfType<WheelEntry>().ToList();
    }
}
