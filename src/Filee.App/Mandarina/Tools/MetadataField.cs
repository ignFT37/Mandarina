// Mandarina: one field of the METADATA panel (Author, Latitude, Title…). It knows the value the files have now (the common one,
// or "several values" when they differ), what the user has typed, whether the user removed it on purpose (the ✕), and whether
// that amounts to a change worth writing: an empty field that was left alone changes nothing, a field whose text was
// emptied or crossed out is removed from the files.

using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Filee.App.Mandarina.Tools;

/// <summary>How a field's text is checked before it is written.</summary>
public enum MetadataFieldType
{
    Text,
    Date,
    Latitude,
    Longitude,
    Track,
    Year,
    Iso6709,
}

public sealed partial class MetadataField : ObservableObject
{
    private readonly string _multipleText;
    private readonly string _removedText;
    private readonly string _hint;

    public MetadataField(string id, string label, MetadataFieldType type, int columns, string initial, bool differs, bool present,
        string multipleText, string removedText, string hint)
    {
        Id = id;
        Label = label;
        Type = type;
        Columns = columns;
        Initial = initial;
        Differs = differs;
        Present = present;
        _multipleText = multipleText;
        _removedText = removedText;
        _hint = hint;
        _text = initial;
    }

    public string Id { get; }

    public string Label { get; }

    public MetadataFieldType Type { get; }

    /// <summary>How many of the three columns of the grid the field takes.</summary>
    public int Columns { get; }

    /// <summary>The text the files have: the common value of all of them, or empty when none has it or they differ.</summary>
    public string Initial { get; }

    /// <summary>True when the selected files do not all have the same value (the box then shows "(several values)").</summary>
    public bool Differs { get; }

    /// <summary>True when at least one file has a value (so there is something to remove).</summary>
    public bool Present { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTouched))]
    [NotifyPropertyChangedFor(nameof(IsRemoval))]
    [NotifyPropertyChangedFor(nameof(NewValue))]
    [NotifyPropertyChangedFor(nameof(ErrorKey))]
    [NotifyPropertyChangedFor(nameof(CanClear))]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTouched))]
    [NotifyPropertyChangedFor(nameof(IsRemoval))]
    [NotifyPropertyChangedFor(nameof(NewValue))]
    [NotifyPropertyChangedFor(nameof(ErrorKey))]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    [NotifyPropertyChangedFor(nameof(CanEnterText))]
    [NotifyPropertyChangedFor(nameof(CanClear))]
    private bool _removed;

    /// <summary>Raised after the user changed the text or crossed the field out.</summary>
    public event EventHandler? Edited;

    partial void OnTextChanged(string value) => Edited?.Invoke(this, EventArgs.Empty);

    partial void OnRemovedChanged(bool value) => Edited?.Invoke(this, EventArgs.Empty);

    public string Placeholder => Removed ? _removedText : Differs ? _multipleText : _hint;

    public bool CanEnterText => !Removed;

    /// <summary>The ✕ is there while the field has something to remove (a value in a file, or text just typed) and is not crossed out yet.</summary>
    public bool CanClear => !Removed && (Present || Text.Trim().Length > 0);

    /// <summary>True when writing the files would change this field.</summary>
    public bool IsTouched => Removed || (Differs ? Text.Trim().Length > 0 : Text.Trim() != Initial);

    /// <summary>True when the field is to be removed: crossed out, or emptied when it had a value.</summary>
    public bool IsRemoval => Removed || (IsTouched && !Differs && Text.Trim().Length == 0);

    /// <summary>What to write: null when untouched, empty text to remove the field, else the new text.</summary>
    public string? NewValue => !IsTouched ? null : IsRemoval ? "" : Text.Trim();

    /// <summary>Crosses the field out (it will be removed from the files), or brings it back if it already was.</summary>
    [RelayCommand]
    public void ToggleRemoved()
    {
        if (Removed)
        {
            Removed = false;
            return;
        }
        if (Present)
        {
            Text = "";
            Removed = true;
        }
        else
        {
            Text = "";
        }
    }

    /// <summary>Puts the field back as the files have it.</summary>
    public void Restore()
    {
        Removed = false;
        Text = Initial;
    }

    /// <summary>The key of the message that says why the text is not valid; null when it is (or when nothing will be written).</summary>
    public string? ErrorKey
    {
        get
        {
            if (!IsTouched || IsRemoval)
                return null;
            var text = Text.Trim();
            return Type switch
            {
                MetadataFieldType.Date when !MetadataValues.TryParseDate(text, out _) => "panel.removeMetadata.bad_date",
                MetadataFieldType.Latitude when !MetadataValues.TryParseCoordinate(text, latitude: true, out _) => "panel.removeMetadata.bad_coordinate",
                MetadataFieldType.Longitude when !MetadataValues.TryParseCoordinate(text, latitude: false, out _) => "panel.removeMetadata.bad_coordinate",
                MetadataFieldType.Track when !TrackPattern().IsMatch(text) => "panel.removeMetadata.bad_track",
                MetadataFieldType.Year when !YearPattern().IsMatch(text) => "panel.removeMetadata.bad_year",
                MetadataFieldType.Iso6709 when !MetadataValues.IsIso6709(text) => "panel.removeMetadata.bad_iso6709",
                _ => null,
            };
        }
    }

    [GeneratedRegex(@"^\d{1,4}(/\d{1,4})?$")]
    private static partial Regex TrackPattern();

    [GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2}([T ]\d{2}:\d{2}(:\d{2})?)?)?)?$")]
    private static partial Regex YearPattern();
}

/// <summary>A card of the METADATA panel: some fields that belong together, and for the last card of a photo or video the position buttons.</summary>
public sealed class MetadataGroup(MetadataPanel panel, IReadOnlyList<MetadataField> fields, bool hasLocation)
{
    /// <summary>The panel, for the commands and texts that belong to the whole (remove the position, how many tags there are).</summary>
    public MetadataPanel Panel { get; } = panel;

    public IReadOnlyList<MetadataField> Fields { get; } = fields;

    /// <summary>True when the card ends with the "Remove location" button.</summary>
    public bool HasLocation { get; } = hasLocation;
}
