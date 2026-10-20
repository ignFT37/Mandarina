// Mandarina: METADATA. Opens with one file or several of the same kind (photos, audio, video or PDF). "Edit" shows
// the tags that make sense for the kind — author, camera, location for a photo; title, artist, album for a song — each with the
// value the files have (or "(several values)"), and writes only the ones that were touched; "Remove all" saves a copy with
// no tags at all. Both save a new file beside the original ("name metadata.ext", "name no metadata.ext") and never touch it.
// Nothing is recoded: JPEG, PNG, WebP, GIF, TIFF, MP3 and PDF are rewritten block by block, the rest of the audio and video goes
// through FFmpeg "-c copy". The engines are PhotoMetadata, MediaMetadata and PdfMetadataEditor.

using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Mandarina.Panels;

namespace Filee.App.Mandarina.Tools;

/// <summary>The kind of file a METADATA panel was opened for.</summary>
public enum MetadataKind
{
    Photo,
    Audio,
    Video,
    Pdf,
}

[ToolPanel("removeMetadata")]
public sealed partial class MetadataPanel : ToolPanel
{
    private readonly CancellationTokenSource _loading = new();
    private readonly List<FileMeta> _metas = [];
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoveMode))]
    [NotifyPropertyChangedFor(nameof(ShowEdit))]
    [NotifyPropertyChangedFor(nameof(ShowRemove))]
    [NotifyPropertyChangedFor(nameof(Notices))]
    [NotifyPropertyChangedFor(nameof(HasNotices))]
    private bool _isEditMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyPropertyChangedFor(nameof(ShowEdit))]
    [NotifyPropertyChangedFor(nameof(ShowRemove))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyPropertyChangedFor(nameof(ShowEdit))]
    [NotifyPropertyChangedFor(nameof(ShowRemove))]
    private string? _loadError;

    [ObservableProperty] private IReadOnlyList<MetadataField> _fields = [];
    [ObservableProperty] private IReadOnlyList<MetadataGroup> _groups = [];
    [ObservableProperty] private bool _removeCover = true;

    public MetadataPanel(PanelContext context) : base(context)
    {
        Kind = KindOf(Files[0]);
        _ = LoadAsync();
    }

    public MetadataKind Kind { get; }

    public bool IsPhoto => Kind == MetadataKind.Photo;

    public bool IsAudio => Kind == MetadataKind.Audio;

    public bool IsVideo => Kind == MetadataKind.Video;

    public bool IsPdf => Kind == MetadataKind.Pdf;

    public bool IsMedia => Kind is MetadataKind.Audio or MetadataKind.Video;

    /// <summary>The "Remove all" pill: the opposite of "Edit" (the two buttons are one choice; pressing the one that is on does nothing).</summary>
    public bool IsRemoveMode
    {
        get => !IsEditMode;
        set
        {
            if (value)
                IsEditMode = false;
            else
                OnPropertyChanged();
        }
    }

    public bool ShowLoading => IsLoading;

    public bool ShowError => !IsLoading && LoadError is not null;

    public bool ShowEdit => !IsLoading && LoadError is null && IsEditMode;

    public bool ShowRemove => !IsLoading && LoadError is null && !IsEditMode;

    public string FileName => Files.Count == 1 ? Path.GetFileName(Files[0]) : Context.Format("panel.removeMetadata.files", Files.Count);

    public override Size PreferredSize => Kind switch
    {
        MetadataKind.Photo => new Size(780, 590),
        MetadataKind.Video => new Size(780, 530),
        MetadataKind.Audio => new Size(780, 520),
        _ => new Size(780, 460),
    };

    public override Control CreateView() => new MetadataView { DataContext = this };

    /// <summary>The field with this id ("Author", "Latitude"…); null when the kind has no such field.</summary>
    public MetadataField? Field(string id) => Fields.FirstOrDefault(f => f.Id == id);

    // ───────── What the files have ─────────

    /// <summary>One file's tags, in the terms of the panel (field id → text).</summary>
    private sealed record FileMeta(string Path, IReadOnlyDictionary<string, string> Values, int Tags, int OtherTags, bool CanEdit, bool HasCover,
        bool HasChapters, bool HasLocation, bool RecodesToJpeg, bool RecodesAvif, long AttachedBytes, int DroppedStreams, bool LosesCustomTags);

    /// <summary>True when some file's format cannot hold tags (BMP): "Edit" has nothing to write into.</summary>
    public bool EditUnavailable => _metas.Any(m => !m.CanEdit);

    /// <summary>The number of tags the photos carry in all.</summary>
    public int TotalTags => _metas.Sum(m => m.Tags);

    public bool HasCover => _metas.Any(m => m.HasCover);

    /// <summary>What "Remove all" leaves alone: the part of the file that is not a tag.</summary>
    public string KeepText => Context[Kind switch
    {
        MetadataKind.Photo => "panel.removeMetadata.keep_photo",
        MetadataKind.Pdf => "panel.removeMetadata.keep_pdf",
        _ => "panel.removeMetadata.keep_media",
    }];

    /// <summary>The line under "Remove all" that says how many tags there are now (photos only).</summary>
    public string? TagsText
    {
        get
        {
            if (!IsPhoto || IsLoading || LoadError is not null)
                return null;
            var total = TotalTags;
            var many = Files.Count > 1;
            var key = total switch { 0 => "none", 1 => "one", _ => "many" };
            return many
                ? Context.Format($"panel.removeMetadata.tags_all_{key}", total)
                : Context.Format($"panel.removeMetadata.tags_now_{key}", total);
        }
    }

    /// <summary>"+ N more tags": what the photos hold that the fields do not show.</summary>
    public string? MoreTagsText
    {
        get
        {
            if (!IsPhoto || IsLoading || LoadError is not null)
                return null;
            var other = _metas.Sum(m => m.OtherTags);
            return other switch { <= 0 => null, 1 => Context["panel.removeMetadata.more_tags_one"], _ => Context.Format("panel.removeMetadata.more_tags", other) };
        }
    }

    /// <summary>True when the kind has a position to remove (photos and videos) and a file has one.</summary>
    public bool CanRemoveLocation => (Field("Latitude") ?? Field("Location")) is { } position && !position.Removed && (position.Present || position.Text.Length > 0);

    public bool HasLocationFields => Field("Latitude") is not null || Field("Location") is not null;

    /// <summary>Crosses out the position: latitude and longitude of a photo, the location of a video.</summary>
    public void RemoveLocation()
    {
        foreach (var id in new[] { "Latitude", "Longitude", "Location" })
        {
            if (Field(id) is not { } field)
                continue;
            field.Removed = false;
            field.Text = "";
            field.Removed = field.Present;
        }
        OnPropertyChanged(nameof(CanRemoveLocation));
    }

    [RelayCommand]
    private void RemoveLocationNow() => RemoveLocation();

    /// <summary>Whether the panel can offer to take the cover picture out (audio and video that have one).</summary>
    public bool ShowCoverOption => IsMedia && HasCover;

    // ───────── Loading ─────────

    private async Task LoadAsync()
    {
        try
        {
            if (Files.Any(f => KindOf(f) != Kind))
                throw new PanelException(Context["panel.removeMetadata.mixed"]);
            var cancel = _loading.Token;
            var media = Context.Media;
            foreach (var file in Files)
            {
                cancel.ThrowIfCancellationRequested();
                _metas.Add(await ReadAsync(file, media, cancel));
            }
            if (!_disposed)
                BuildFields();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Fire-and-forget: whatever goes wrong must end up on screen, or the panel would say "loading" for ever.
            LoadError = Describe(ex);
        }
        IsLoading = false;
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(MoreTagsText));
        OnPropertyChanged(nameof(ShowCoverOption));
        OnPropertyChanged(nameof(HasLocationFields));
        OnPropertyChanged(nameof(Notices));
        OnPropertyChanged(nameof(HasNotices));
        NotifyCanApplyChanged();
    }

    private string Describe(Exception ex) => ex switch
    {
        PdfMetadataProtectedException => Context["panel.removeMetadata.protected"],
        // A picture Magick.NET cannot read (a damaged HEIC…) or a file that is no photo: the cause in the user's language.
        ImageMagick.MagickException or PanelException { Key: not null } => Context.Format("panel.removeMetadata.cannot_read", Context.Describe(ex)),
        PanelException panel => Context.Format("panel.removeMetadata.cannot_read", panel.Message),
        _ => Context.Format("panel.removeMetadata.cannot_read", ex.Message),
    };

    private async Task<FileMeta> ReadAsync(string file, MediaTools? media, CancellationToken cancel)
    {
        switch (Kind)
        {
            case MetadataKind.Photo:
            {
                var info = await Task.Run(() => PhotoMetadata.Read(file), cancel);
                var toJpeg = info.Kind == PhotoMetadataKind.Heif && PhotoMetadata.OutputExtension(file).Equals(".jpg", StringComparison.OrdinalIgnoreCase);
                return new FileMeta(file, info.Values.ToDictionary(p => p.Key.ToString(), p => p.Value), info.Tags, info.OtherTags, info.CanEdit,
                    HasCover: false, HasChapters: false, info.HasLocation, RecodesToJpeg: toJpeg, RecodesAvif: info.Kind == PhotoMetadataKind.Heif && !toJpeg,
                    info.AttachedBytes, 0, false);
            }
            case MetadataKind.Pdf:
            {
                var info = await Task.Run(() => PdfMetadataEditor.Read(file), cancel);
                return new FileMeta(file, info.Values.ToDictionary(p => p.Key.ToString(), p => p.Value), info.Tags, info.OtherTags, true, false, false, false, false, false, 0, 0, false);
            }
            default:
            {
                // Without FFmpeg there is nothing to read: the panel opens with a warning and Apply stays off.
                if (media is null && MediaMetadata.NeedsFfmpeg(file))
                    return new FileMeta(file, new Dictionary<string, string>(), 0, 0, true, false, false, false, false, false, 0, 0, false);
                var info = await MediaMetadata.ReadAsync(file, media, cancel);
                return new FileMeta(file, info.Values.ToDictionary(p => p.Key.ToString(), p => p.Value), 0, 0, true, info.HasCover, info.HasChapters,
                    info.Values.ContainsKey(MediaMetadataField.Location), false, false, 0, info.DroppedStreams, info.LosesCustomTags);
            }
        }
    }

    private void BuildFields()
    {
        // The fields of each card, with how many of the three columns each takes.
        (string Id, int Columns, MetadataFieldType Type)[][] cards = Kind switch
        {
            MetadataKind.Photo =>
            [
                [("Description", 3, MetadataFieldType.Text), ("Author", 1, MetadataFieldType.Text), ("Copyright", 1, MetadataFieldType.Text), ("Software", 1, MetadataFieldType.Text)],
                [("Make", 1, MetadataFieldType.Text), ("Model", 1, MetadataFieldType.Text), ("Lens", 1, MetadataFieldType.Text)],
                [("Date", 1, MetadataFieldType.Date), ("Latitude", 1, MetadataFieldType.Latitude), ("Longitude", 1, MetadataFieldType.Longitude)],
            ],
            MetadataKind.Audio =>
            [
                [("Title", 3, MetadataFieldType.Text), ("Artist", 1, MetadataFieldType.Text), ("Album", 1, MetadataFieldType.Text), ("AlbumArtist", 1, MetadataFieldType.Text)],
                [("Genre", 1, MetadataFieldType.Text), ("Date", 1, MetadataFieldType.Year), ("Track", 1, MetadataFieldType.Track), ("Comment", 3, MetadataFieldType.Text)],
            ],
            MetadataKind.Video =>
            [
                [("Title", 3, MetadataFieldType.Text), ("Author", 2, MetadataFieldType.Text), ("Created", 1, MetadataFieldType.Date), ("Comment", 3, MetadataFieldType.Text)],
                [("Location", 2, MetadataFieldType.Iso6709)],
            ],
            _ =>
            [
                [("Title", 3, MetadataFieldType.Text), ("Author", 1, MetadataFieldType.Text), ("Subject", 1, MetadataFieldType.Text), ("Keywords", 1, MetadataFieldType.Text)],
                [("Creator", 1, MetadataFieldType.Text), ("Producer", 1, MetadataFieldType.Text)],
            ],
        };

        var all = new List<MetadataField>();
        var groups = new List<MetadataGroup>();
        for (var card = 0; card < cards.Length; card++)
        {
            var fields = new List<MetadataField>();
            foreach (var (id, columns, type) in cards[card])
            {
                var values = _metas.Select(m => m.Values.GetValueOrDefault(id)).ToList();
                var present = values.Any(v => !string.IsNullOrEmpty(v));
                var common = values.All(v => v == values[0]);
                var field = new MetadataField(id, Context[$"panel.removeMetadata.f_{LabelKey(id, Kind)}"], type, columns,
                    initial: common ? values[0] ?? "" : "", differs: !common, present,
                    multipleText: Context["panel.removeMetadata.multiple"], removedText: Context["panel.removeMetadata.will_remove"], hint: HintFor(type));
                field.Edited += OnFieldEdited;
                fields.Add(field);
            }
            all.AddRange(fields);
            groups.Add(new MetadataGroup(this, fields, hasLocation: card == cards.Length - 1 && fields.Any(f => f.Id is "Latitude" or "Location")));
        }
        Groups = groups;
        Fields = all;
    }

    private static string LabelKey(string id, MetadataKind kind) => id switch
    {
        "AlbumArtist" => "album_artist",
        "Date" when kind == MetadataKind.Audio => "year",
        _ => id.ToLowerInvariant(),
    };

    private static string HintFor(MetadataFieldType type) => type switch
    {
        MetadataFieldType.Date => "2024-05-17 10:20:30",
        MetadataFieldType.Latitude => "40.4168",
        MetadataFieldType.Longitude => "-3.7038",
        MetadataFieldType.Iso6709 => "+40.4168-003.7038/",
        MetadataFieldType.Year => "2024-05-17",
        MetadataFieldType.Track => "3/12",
        _ => "",
    };

    // ───────── State the view shows ─────────

    private void OnFieldEdited(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanRemoveLocation));
        NotifyCanApplyChanged();
    }

    partial void OnIsEditModeChanged(bool value) => NotifyCanApplyChanged();

    partial void OnIsLoadingChanged(bool value) => NotifyCanApplyChanged();

    partial void OnLoadErrorChanged(string? value) => NotifyCanApplyChanged();

    partial void OnFieldsChanged(IReadOnlyList<MetadataField> value)
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanRemoveLocation));
    }

    /// <summary>The first thing wrong with what was typed, in words; null when all of it can be written.</summary>
    public string? ValidationMessage
    {
        get
        {
            foreach (var item in Fields)
                if (item.ErrorKey is { } key)
                    return Context[key];
            // A position needs both numbers.
            if (Field("Latitude") is { } latitude && Field("Longitude") is { } longitude && (latitude.IsTouched || longitude.IsTouched)
                && !latitude.IsRemoval && !longitude.IsRemoval && (PositionText(latitude) is null || PositionText(longitude) is null))
                return Context["panel.removeMetadata.bad_pair"];
            return null;
        }
    }

    /// <summary>The text a position field will be written with: what was typed, or what the files have when it was left alone.</summary>
    private static string? PositionText(MetadataField field)
    {
        if (field.IsTouched)
            return field.NewValue is { Length: > 0 } typed ? typed : null;
        return field.Differs || field.Initial.Length == 0 ? null : field.Initial;
    }

    /// <summary>Things to know before applying (formats that are recoded, tags that are not kept, a missing FFmpeg…).</summary>
    public IReadOnlyList<string> Notices
    {
        get
        {
            var notices = new List<string>();
            if (IsLoading || LoadError is not null)
                return notices;
            if (_metas.Any(m => m.RecodesToJpeg))
                notices.Add(Context["panel.removeMetadata.note_heic"]);
            if (_metas.Any(m => m.RecodesAvif))
                notices.Add(Context["panel.removeMetadata.note_avif"]);
            if (IsEditMode && EditUnavailable)
                notices.Add(Context["panel.removeMetadata.note_no_tags"]);
            if (NeedsFfmpeg && Context.Media is null)
                notices.Add(Context.Format("engine.reason.needs_package", "FFmpeg"));
            if (_metas.Any(m => m.DroppedStreams > 0))
                notices.Add(Context["panel.removeMetadata.note_data_streams"]);
            if (IsEditMode && _metas.Any(m => m.LosesCustomTags))
                notices.Add(Context["panel.removeMetadata.note_custom_tags"]);
            if (_metas.Any(m => m.AttachedBytes > 0))
                notices.Add(Context.Format(IsEditMode ? "panel.removeMetadata.note_attached_edit" : "panel.removeMetadata.note_attached", PanelFormat.Size(_metas.Sum(m => m.AttachedBytes))));
            return notices;
        }
    }

    public bool HasNotices => Notices.Count > 0;

    /// <summary>True when some file needs FFmpeg to be written (audio other than MP3, and video).</summary>
    private bool NeedsFfmpeg => IsMedia && _metas.Any(m => MediaMetadata.NeedsFfmpeg(m.Path));

    /// <summary>Values typed in the edit form (removing needs no typing, so there is nothing to lose).</summary>
    public override bool HasUnsavedWork => IsEditMode && Fields.Any(f => f.IsTouched);

    public override bool CanApply
    {
        get
        {
            if (IsLoading || LoadError is not null)
                return false;
            if (NeedsFfmpeg && Context.Media is null)
                return false;
            if (!IsEditMode)
                return true;
            return !EditUnavailable && Fields.Any(f => f.IsTouched) && ValidationMessage is null;
        }
    }

    // ───────── Apply ─────────

    public override async Task<IReadOnlyList<string>> ApplyAsync(IProgress<double> progress, CancellationToken cancel)
    {
        var outputs = new List<string>();
        var edit = IsEditMode;
        var cover = RemoveCover;
        var media = Context.Media;
        try
        {
            for (var i = 0; i < Files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var source = Files[i];
                var extension = Kind == MetadataKind.Photo ? PhotoMetadata.OutputExtension(source) : Path.GetExtension(source);
                var output = edit
                    ? OutputPaths.Beside(source, Context["panel.removeMetadata.suffix_edited"], extension)
                    : Context.OutputFor(source, "removeMetadata", extension);
                outputs.Add(output);
                var slice = new SliceProgress(progress, i, Files.Count);
                await WriteAsync(source, output, edit, cover, media, slice, cancel);
                progress.Report((i + 1.0) / Files.Count);
            }
        }
        catch (Exception ex)
        {
            // All or nothing: whatever was written in this run goes.
            foreach (var output in outputs)
                await DeleteAsync(output);
            if (ex is OperationCanceledException or PanelException)
                throw;
            throw new PanelException(ex is PdfMetadataProtectedException ? Context["panel.removeMetadata.protected"] : ex.Message, ex);
        }
        return outputs;
    }

    private async Task WriteAsync(string source, string output, bool edit, bool removeCover, MediaTools? media, IProgress<double> progress, CancellationToken cancel)
    {
        switch (Kind)
        {
            case MetadataKind.Photo:
                if (edit)
                {
                    var changes = PhotoChanges();
                    await Task.Run(() => PhotoMetadata.Edit(source, output, changes), cancel);
                }
                else
                {
                    await Task.Run(() => PhotoMetadata.Strip(source, output), cancel);
                }
                break;
            case MetadataKind.Pdf:
                if (edit)
                {
                    var changes = PdfChanges();
                    await Task.Run(() => PdfMetadataEditor.Edit(source, output, changes), cancel);
                }
                else
                {
                    await Task.Run(() => PdfMetadataEditor.Strip(source, output), cancel);
                }
                break;
            default:
                if (edit)
                    await MediaMetadata.EditAsync(source, output, MediaChanges(), media, progress, cancel);
                else
                    await MediaMetadata.StripAsync(source, output, removeCover, media, progress, cancel);
                break;
        }
    }

    private static async Task DeleteAsync(string path)
    {
        for (var attempt = 0; attempt < 40 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }

    private Dictionary<PhotoMetadataField, string?> PhotoChanges()
    {
        var changes = new Dictionary<PhotoMetadataField, string?>();
        foreach (var field in Fields.Where(f => f.IsTouched && f.Id is not ("Latitude" or "Longitude")))
            changes[Enum.Parse<PhotoMetadataField>(field.Id)] = field.NewValue;
        var latitude = Field("Latitude");
        var longitude = Field("Longitude");
        if (latitude is { IsTouched: true } || longitude is { IsTouched: true })
        {
            if (latitude!.IsRemoval || longitude!.IsRemoval)
            {
                changes[PhotoMetadataField.Latitude] = "";
                changes[PhotoMetadataField.Longitude] = "";
            }
            else
            {
                changes[PhotoMetadataField.Latitude] = PositionText(latitude);
                changes[PhotoMetadataField.Longitude] = PositionText(longitude!);
            }
        }
        return changes;
    }

    private Dictionary<PdfMetadataField, string?> PdfChanges() =>
        Fields.Where(f => f.IsTouched).ToDictionary(f => Enum.Parse<PdfMetadataField>(f.Id), f => f.NewValue);

    private Dictionary<MediaMetadataField, string?> MediaChanges() =>
        Fields.Where(f => f.IsTouched).ToDictionary(f => Enum.Parse<MediaMetadataField>(f.Id), f => f.NewValue);

    /// <summary>Reports one file's progress as its share of the whole job.</summary>
    private sealed class SliceProgress(IProgress<double> parent, int index, int count) : IProgress<double>
    {
        public void Report(double value) => parent.Report((index + Math.Clamp(value, 0, 1)) / count);
    }

    // ───────── Kinds ─────────

    /// <summary>The kind a file belongs to by its extension (what the wheel offers the tool for).</summary>
    internal static MetadataKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" or ".m4a" or ".wav" or ".flac" or ".aiff" or ".aif" or ".ogg" or ".oga" or ".opus" => MetadataKind.Audio,
        ".mp4" or ".mov" or ".mkv" or ".m4v" => MetadataKind.Video,
        ".pdf" => MetadataKind.Pdf,
        _ => MetadataKind.Photo,
    };

    public override void Dispose()
    {
        _disposed = true;
        _loading.Cancel();
        base.Dispose();
    }
}
