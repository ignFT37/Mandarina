// Mandarina: one proportion pill of the crop panels ("Free", "Original", "16:9", …).

using CommunityToolkit.Mvvm.ComponentModel;

namespace Filee.App.Mandarina.Tools;

public sealed partial class CropRatio : ObservableObject
{
    public CropRatio(string key, string label, double value, bool isVisible = true)
    {
        Key = key;
        _label = label;
        Value = value;
        _isVisible = isVisible;
    }

    /// <summary>"free", "original", "1:1", "4:3", … or "custom" (the pill that shows a turned proportion nobody listed).</summary>
    public string Key { get; }

    /// <summary>Width / height; 0 for the free box. The "original" pill gets the picture's own proportion once it is known.</summary>
    public double Value { get; internal set; }

    [ObservableProperty] private string _label;

    [ObservableProperty] private bool _isSelected;

    /// <summary>False for the custom pill until a turned proportion needs it.</summary>
    [ObservableProperty] private bool _isVisible;

    public bool IsFree => Key == "free";

    public bool IsOriginal => Key == "original";

    public bool IsCustom => Key == "custom";
}
