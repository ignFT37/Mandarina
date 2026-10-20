// Mandarina: one cell of the page grid of the ORGANIZE panel. It follows one entry of the PageEditor (by id) while the
// pages are edited, so the cell, its picture and its selection survive a move instead of being rebuilt.

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Filee.App.Mandarina.Tools;

public sealed partial class OrganizePage(int id, int source, double aspect, string origin) : ObservableObject
{
    /// <summary>The <see cref="PageEdit.Id"/> this cell shows.</summary>
    public int Id { get; } = id;

    /// <summary>Zero-based page of the original PDF.</summary>
    public int Source { get; } = source;

    /// <summary>Width ÷ height of the page as the PDF shows it, before the user's turn.</summary>
    public double Aspect { get; } = aspect;

    /// <summary>"origin: p. 3": where the page comes from, shown only when it was duplicated or moved.</summary>
    public string Origin { get; } = origin;

    [ObservableProperty] private int _turn;
    [ObservableProperty] private int _order;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isGhost;
    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>The page was duplicated or moved: the line under the number says where it came from.</summary>
    [ObservableProperty] private bool _showOrigin;
}
