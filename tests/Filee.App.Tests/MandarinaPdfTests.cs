// Mandarina: tests of the PDF ORGANIZE panel. The editing model (move, duplicate, remove, turn, undo / redo) runs without
// any window; the panel is rendered to a PNG with real PDFium thumbnails (mandarina-panel-organizepdf*.png in the
// screenshot folder, to review by eye); Apply runs against the real PdfSharp and the pages of the result are read back by
// rendering them with PDFium (MakePdf gives every page its own colour), plus the failures: corrupt, protected, vanished or
// changed files.

using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Mandarina;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using Filee.App.Services;
using Filee.Core.Localization;
using PDFtoImage;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using static Filee.App.Tests.MandarinaTestKit;

// PDFtoImage marks its API for the platforms PDFium ships for; the tests only run on desktop platforms.
#pragma warning disable CA1416

namespace Filee.App.Tests;

public sealed class MandarinaPdfModelTests
{
    private static string Order(PageEditor editor) => string.Join(" ", editor.Pages.Select(page => page.Source + 1));

    /// <summary>Makes exactly the pages at <paramref name="indexes"/> the selection.</summary>
    private static PageEditor Select(PageEditor editor, params int[] indexes)
    {
        editor.ClearSelection();
        foreach (var index in indexes)
            editor.Toggle(index);
        return editor;
    }

    private static int[] Selected(PageEditor editor) => Enumerable.Range(0, editor.Count).Where(editor.IsSelected).ToArray();

    [Fact]
    public void A_new_editor_is_the_original()
    {
        var editor = new PageEditor(4);
        Assert.Equal("1 2 3 4", Order(editor));
        Assert.Equal(4, editor.OriginalCount);
        Assert.False(editor.HasChanges);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.All(editor.Pages, page => Assert.Equal(0, page.Turn));
        Assert.All(editor.OriginMarks(), mark => Assert.False(mark));
    }

    [Fact]
    public void With_nothing_selected_no_edit_does_anything()
    {
        var editor = new PageEditor(3);
        Assert.False(editor.Rotate(1));
        Assert.False(editor.Duplicate());
        Assert.False(editor.Remove());
        Assert.False(editor.Move(-1));
        Assert.False(editor.Move(1));
        Assert.False(editor.MoveTo(0));
        Assert.False(editor.Reset());
        Assert.False(editor.Undo());
        Assert.False(editor.Redo());
        Assert.False(editor.CanUndo);
        Assert.False(editor.HasChanges);
        Assert.Equal("1 2 3", Order(editor));
    }

    [Fact]
    public void An_empty_pdf_gives_an_empty_editor_that_survives_every_call()
    {
        var editor = new PageEditor(0);
        editor.SelectAll();
        editor.SelectRange(0);
        editor.Toggle(0);
        editor.SelectOnly(0);
        Assert.False(editor.Remove());
        Assert.False(editor.Move(1));
        Assert.Equal(0, editor.Count);
        Assert.False(editor.HasChanges);
        Assert.Empty(editor.OriginMarks());
    }

    [Fact]
    public void Turning_is_clockwise_or_back_and_four_turns_cancel_out()
    {
        var editor = Select(new PageEditor(3), 1);
        Assert.True(editor.Rotate(1));
        Assert.Equal(90, editor.Pages[1].Turn);
        Assert.True(editor.Rotate(1));
        Assert.Equal(180, editor.Pages[1].Turn);
        editor.Rotate(-1);
        editor.Rotate(-1);
        Assert.Equal(0, editor.Pages[1].Turn);
        Assert.False(editor.HasChanges); // turned and turned back: the same as the original
        Assert.True(editor.Rotate(-1));
        Assert.Equal(270, editor.Pages[1].Turn);
        Assert.False(editor.Rotate(4)); // a whole turn is no turn
        Assert.Equal(0, editor.Pages[0].Turn); // only the selected page turned
        Assert.Equal(0, editor.Pages[2].Turn);
    }

    [Fact]
    public void The_first_page_cannot_go_back_and_the_last_cannot_go_forward()
    {
        var editor = Select(new PageEditor(4), 0);
        Assert.False(editor.Move(-1));
        Assert.False(editor.CanUndo); // nothing happened, so nothing to undo
        Assert.Equal("1 2 3 4", Order(editor));

        Select(editor, 3);
        Assert.False(editor.Move(1));
        Assert.False(editor.CanUndo);
        Assert.True(editor.Move(-1));
        Assert.Equal("1 2 4 3", Order(editor));
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public void Several_pages_move_together_and_the_ones_at_the_edge_stay()
    {
        var editor = Select(new PageEditor(6), 2, 3);
        Assert.True(editor.Move(-1));
        Assert.Equal("1 3 4 2 5 6", Order(editor));
        Assert.True(editor.Move(1));
        Assert.True(editor.Move(1));
        Assert.Equal("1 2 5 3 4 6", Order(editor));

        // A block that touches the edge cannot go further...
        editor = Select(new PageEditor(5), 0, 1);
        Assert.False(editor.Move(-1));
        // ...and in a scattered selection the others still move on.
        Select(editor, 0, 3);
        Assert.True(editor.Move(-1));
        Assert.Equal("1 2 4 3 5", Order(editor));
    }

    [Fact]
    public void Dropping_on_a_gap_puts_the_selection_there()
    {
        var editor = Select(new PageEditor(5), 1); // page 2
        Assert.False(editor.MoveTo(1)); // the gap before it
        Assert.False(editor.MoveTo(2)); // the gap after it: the same place
        Assert.True(editor.MoveTo(5)); // the very end
        Assert.Equal("1 3 4 5 2", Order(editor));
        Assert.True(editor.MoveTo(0));
        Assert.Equal("2 1 3 4 5", Order(editor));

        Select(editor, 0, 3); // pages 2 and 4, which keep their order
        Assert.True(editor.MoveTo(99)); // beyond the end counts as the end
        Assert.Equal("1 3 5 2 4", Order(editor));
        Assert.True(editor.MoveTo(0));
        Assert.Equal("2 4 1 3 5", Order(editor));
        Assert.True(editor.MoveTo(3)); // before "3": between 1 and 3
        Assert.Equal("1 2 4 3 5", Order(editor));
    }

    [Fact]
    public void A_copy_goes_right_behind_its_page_and_is_the_one_selected()
    {
        var editor = Select(new PageEditor(5), 1, 3);
        editor.Rotate(1);
        Assert.True(editor.Duplicate());
        Assert.Equal("1 2 2 3 4 4 5", Order(editor));
        Assert.Equal(2, editor.SelectedCount);
        Assert.False(editor.IsSelected(1)); // the originals are not
        Assert.True(editor.IsSelected(2)); // the copy of 2
        Assert.True(editor.IsSelected(5)); // the copy of 4
        Assert.NotEqual(editor.Pages[1].Id, editor.Pages[2].Id);
        Assert.Equal(90, editor.Pages[2].Turn); // a copy is turned like its page
        Assert.True(editor.HasChanges);
    }

    [Fact]
    public void Removing_takes_the_selected_pages_out_and_all_of_them_is_allowed()
    {
        var editor = Select(new PageEditor(5), 0, 4);
        Assert.True(editor.Remove());
        Assert.Equal("2 3 4", Order(editor));
        Assert.Equal(0, editor.SelectedCount);
        Assert.True(editor.HasChanges);

        editor.SelectAll();
        Assert.True(editor.Remove());
        Assert.Equal(0, editor.Count);
        Assert.True(editor.HasChanges);
        Assert.True(editor.Undo());
        Assert.Equal("2 3 4", Order(editor));
        Assert.Equal(3, editor.SelectedCount); // what was removed comes back selected
    }

    [Fact]
    public void Undo_and_redo_walk_the_history_and_a_new_edit_forgets_the_redo()
    {
        var editor = Select(new PageEditor(4), 1);
        editor.Rotate(1);
        editor.Duplicate();
        Assert.Equal("1 2 2 3 4", Order(editor));

        Assert.True(editor.Undo());
        Assert.Equal("1 2 3 4", Order(editor));
        Assert.Equal(90, editor.Pages[1].Turn);
        Assert.Equal(new[] { 1 }, Selected(editor)); // the page that was selected when it was copied
        Assert.True(editor.Undo());
        Assert.Equal(0, editor.Pages[1].Turn);
        Assert.False(editor.Undo());

        Assert.True(editor.Redo());
        Assert.True(editor.Redo());
        Assert.Equal("1 2 2 3 4", Order(editor));
        Assert.False(editor.Redo());

        Assert.True(editor.Undo());
        Assert.True(editor.CanRedo);
        Assert.True(editor.Remove()); // a new edit
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void Selecting_is_not_an_edit()
    {
        var editor = new PageEditor(5);
        editor.SelectOnly(1);
        editor.Toggle(2);
        editor.SelectAll();
        editor.ClearSelection();
        Assert.False(editor.CanUndo);
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public void Reset_goes_back_to_the_original_and_can_be_undone()
    {
        var editor = Select(new PageEditor(4), 0, 1);
        editor.Rotate(1);
        editor.Move(1);
        editor.Duplicate();
        Assert.True(editor.HasChanges);

        Assert.True(editor.Reset());
        Assert.Equal("1 2 3 4", Order(editor));
        Assert.False(editor.HasChanges);
        Assert.False(editor.Reset()); // already the original
        Assert.True(editor.Undo());
        Assert.True(editor.HasChanges);
    }

    [Fact]
    public void Clicks_select_one_toggle_or_extend_from_the_page_last_clicked()
    {
        var editor = new PageEditor(6);
        editor.SelectOnly(1);
        editor.SelectRange(3);
        Assert.Equal(new[] { 1, 2, 3 }, Selected(editor));
        editor.Toggle(5);
        Assert.Equal(new[] { 1, 2, 3, 5 }, Selected(editor));
        editor.Toggle(2);
        Assert.Equal(new[] { 1, 3, 5 }, Selected(editor));
        editor.SelectRange(0); // from the last page clicked (2) to 0
        Assert.Equal(new[] { 0, 1, 2 }, Selected(editor));
        editor.SelectOnly(99); // nothing there
        Assert.Empty(Selected(editor));
        editor.SelectAll();
        Assert.Equal(6, editor.SelectedCount);

        // The page last clicked is gone: Shift+click is a plain click.
        editor.SelectOnly(0);
        editor.Remove();
        editor.SelectRange(2);
        Assert.Equal(new[] { 2 }, Selected(editor));
    }

    [Fact]
    public void Changes_are_measured_against_the_original_not_against_the_history()
    {
        var editor = Select(new PageEditor(4), 1);
        editor.Move(1);
        Assert.True(editor.HasChanges);
        editor.Move(-1);
        Assert.False(editor.HasChanges); // back in place

        editor.Duplicate();
        Assert.True(editor.HasChanges);
        editor.Remove(); // the copy is the selected page
        Assert.False(editor.HasChanges);
        Assert.Equal("1 2 3 4", Order(editor));
    }

    [Fact]
    public void Only_moved_or_copied_pages_say_where_they_came_from()
    {
        var editor = Select(new PageEditor(5), 0);
        editor.Remove(); // 2 3 4 5: they shifted, they did not move
        Assert.All(editor.OriginMarks(), mark => Assert.False(mark));

        editor = Select(new PageEditor(5), 2);
        editor.Move(1); // 1 2 4 3 5: the page moved says so, not the one it passed
        Assert.Equal(new[] { false, false, false, true, false }, editor.OriginMarks());
        editor.Move(-1); // back where it was: nothing to say
        Assert.All(editor.OriginMarks(), mark => Assert.False(mark));

        editor = Select(new PageEditor(5), 4);
        editor.MoveTo(0); // 5 1 2 3 4: only the page moved far, not every page it passed
        Assert.Equal(new[] { true, false, false, false, false }, editor.OriginMarks());
        editor.Undo();
        Assert.All(editor.OriginMarks(), mark => Assert.False(mark));
        editor.Redo();
        editor.MoveTo(5); // and sent back to the end
        Assert.All(editor.OriginMarks(), mark => Assert.False(mark));

        editor = Select(new PageEditor(5), 2);
        editor.Duplicate(); // 1 2 3 3 4 5: both threes
        Assert.Equal(new[] { false, false, true, true, false, false }, editor.OriginMarks());
        editor.Move(1); // the copy goes past the 4: 1 2 3 4 3 5
        Assert.Equal(new[] { false, false, true, false, true, false }, editor.OriginMarks());
    }
}

public sealed class MandarinaPdfTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-pdf").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────── Helpers ─────────

    private string Pdf(string name, int pages) => MakePdf(Path.Combine(_root, name), pages);

    /// <summary>A MakePdf PDF where the given pages (1-based) already carry a /Rotate.</summary>
    private string RotatedPdf(string name, int pages, params (int Page, int Rotate)[] rotations)
    {
        var path = Pdf(name, pages);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            foreach (var (page, rotate) in rotations)
                document.Pages[page - 1].Elements.SetInteger("/Rotate", rotate); // PdfSharp's Rotate refuses -90
            document.Save(path);
        }
        return path;
    }

    private static async Task<OrganizePdfPanel> OpenAsync(string path, bool thumbnails = false, string language = "es")
    {
        var panel = new OrganizePdfPanel(ContextIn(language, path));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        if (thumbnails)
            await WaitAsync(() => panel.Items.All(cell => cell.Thumbnail is not null), 30000);
        return panel;
    }

    /// <summary>The colour at the middle of every page of a PDF, rendered small: how the pages of MakePdf tell themselves apart.</summary>
    private static SKColor[] Colours(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var options = new RenderOptions(Dpi: 20, BackgroundColor: SKColors.White);
        var colours = new List<SKColor>();
        lock (PdfPageOrganizer.PdfiumLock)
        {
            foreach (var bitmap in Conversion.ToImages(bytes, null, options))
            {
                using (bitmap)
                    colours.Add(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
            }
        }
        return [.. colours];
    }

    private static int[] Rotations(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var rotations = new int[document.PageCount];
        for (var i = 0; i < rotations.Length; i++)
            rotations[i] = document.Pages[i].Rotate;
        return rotations;
    }

    private static int PageCount(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The pages of the original that the pages of <paramref name="result"/> are, by their colours (1-based).</summary>
    private static int[] WhichPages(string original, string result)
    {
        var source = Colours(original);
        return Colours(result).Select(colour => Array.IndexOf(source, colour) + 1).ToArray();
    }

    /// <summary>Puts the last page first, then the new last page second, … until the order is reversed (what dragging does).</summary>
    private static void Reverse(OrganizePdfPanel panel)
    {
        var count = panel.Items.Count;
        for (var i = 0; i < count - 1; i++)
        {
            panel.SelectOnly(panel.Items[count - 1]);
            panel.MoveSelectionTo(i);
        }
    }

    private static int[] SelectedOrders(OrganizePdfPanel panel) => panel.Items.Where(cell => cell.IsSelected).Select(cell => cell.Order).ToArray();

    private static int[] Sources(OrganizePdfPanel panel) => panel.Items.Select(cell => cell.Source + 1).ToArray();

    // ───────── Render ─────────

    [AvaloniaFact]
    public async Task The_organize_panel_renders_with_real_thumbnails()
    {
        // 8 pages, the 4th already turned in the file. Edits: page 2 turned right, 5 left, 3 copied (the copy turned too),
        // 7 removed, 8 sent to the front; two pages left selected.
        var file = RotatedPdf("informe.pdf", 8, (4, 90));
        using var panel = await OpenAsync(file, thumbnails: true);
        Assert.Equal(8, panel.Items.Count);
        Assert.Equal("informe.pdf", panel.FileName);

        panel.SelectOnly(panel.Items[1]);
        panel.RotateRight();
        panel.SelectOnly(panel.Items[4]);
        panel.RotateLeft();
        panel.SelectOnly(panel.Items[2]);
        panel.Duplicate();
        panel.RotateRight();
        panel.SelectOnly(panel.Items[8]); // page 8 (the copy made the list one longer)
        panel.MoveSelectionTo(0);
        panel.SelectOnly(panel.Items[8]); // page 7 is the last now
        panel.Remove();
        panel.SelectOnly(panel.Items[3]);
        panel.Toggle(panel.Items[6]);

        Assert.Equal(new[] { 8, 1, 2, 3, 3, 4, 5, 6 }, Sources(panel));
        Assert.Equal(new[] { 4, 7 }, SelectedOrders(panel));
        Assert.True(panel.CanApply);

        var screenshot = Render(panel, "organizepdf");
        Assert.True(File.Exists(screenshot));
        Assert.Equal("Organizar", panel.Title);
        Assert.Equal("8 páginas → 8", panel.CountText);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_organize_panel_fits_in_every_language(string language)
    {
        var file = Pdf("language.pdf", 6);
        using var panel = await OpenAsync(file, thumbnails: true, language);
        panel.SelectOnly(panel.Items[2]);
        panel.Duplicate();
        panel.MoveBack();
        Render(panel, "organizepdf-" + language);
        Assert.Contains("6", panel.CountText);
    }

    [AvaloniaFact]
    public async Task The_organize_panel_renders_its_empty_and_waiting_states()
    {
        var file = Pdf("vacio.pdf", 4);
        using var panel = await OpenAsync(file, thumbnails: true);
        panel.SelectAll();
        panel.Remove();
        Assert.True(panel.NoPagesLeft);
        Assert.False(panel.CanApply);
        Render(panel, "organizepdf-empty");

        // Before the thumbnails arrive the cells are grey stand-ins of the right shape.
        var waiting = new OrganizePdfPanel(Context(file));
        try
        {
            await WaitAsync(() => !waiting.IsLoading);
            foreach (var cell in waiting.Items)
                cell.Thumbnail = null;
            waiting.Dispose(); // no more pictures will come
            Render(waiting, "organizepdf-waiting");
        }
        finally
        {
            waiting.Dispose();
        }
    }

    // ───────── Apply ─────────

    [AvaloniaFact]
    public async Task Reversing_five_pages_saves_them_in_the_opposite_order()
    {
        var file = Pdf("cinco.pdf", 5);
        var before = Hash(file);
        using var panel = await OpenAsync(file);
        Assert.False(panel.CanApply); // nothing changed yet
        Reverse(panel);
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, Sources(panel));
        Assert.True(panel.CanApply);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(100, panel.Progress);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "cinco organizado.pdf"), output);
        Assert.Equal(5, PageCount(output));
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, WhichPages(file, output)); // page i of the result is page n-i+1 of the original
        Assert.Equal(before, Hash(file)); // the original is untouched
    }

    [AvaloniaFact]
    public async Task Turning_pages_adds_to_the_rotation_they_already_have()
    {
        // Page 3 is already turned 90° in the file; page 4 carries -90 (some scanners write that).
        var file = RotatedPdf("giros.pdf", 4, (3, 90), (4, -90));
        using var panel = await OpenAsync(file);
        panel.SelectOnly(panel.Items[1]);
        panel.RotateRight(); // 0 → 90
        panel.SelectOnly(panel.Items[2]);
        panel.RotateRight(); // 90 → 180
        panel.SelectOnly(panel.Items[3]);
        panel.RotateRight(); // -90 → 0 (a multiple of 90 from 0 to 270, as PDF wants)
        panel.SelectOnly(panel.Items[0]);
        panel.RotateLeft(); // 0 → 270

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(new[] { 270, 90, 180, 0 }, Rotations(Assert.Single(panel.Outputs)));
        Assert.Equal(new[] { 0, 0, 90, -90 }, Rotations(file)); // the original keeps what it had
    }

    [AvaloniaFact]
    public async Task A_turn_adds_to_a_rotation_the_page_tree_passes_down()
    {
        // The /Pages node says "rotate 90" and the pages inherit it; page 2 has its own 180.
        var file = Path.Combine(_root, "heredado.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.AddPage().Rotate = 180;
            document.Internals.Catalog.Elements.GetDictionary("/Pages")!.Elements.SetInteger("/Rotate", 90);
            document.Save(file);
        }
        using var panel = await OpenAsync(file);
        panel.SelectAll();
        panel.RotateRight();

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(new[] { 180, 270 }, Rotations(Assert.Single(panel.Outputs)));
    }

    [AvaloniaFact]
    public async Task Duplicating_the_third_page_gives_six_pages_and_the_copies_turn_on_their_own()
    {
        var file = Pdf("seis.pdf", 5);
        using var panel = await OpenAsync(file);
        panel.SelectOnly(panel.Items[2]);
        panel.Duplicate(); // the copy is the selection now
        Assert.Equal(new[] { 1, 2, 3, 3, 4, 5 }, Sources(panel));
        panel.RotateRight(); // turns only the copy

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(6, PageCount(output));
        Assert.Equal(new[] { 1, 2, 3, 3, 4, 5 }, WhichPages(file, output));
        Assert.Equal(new[] { 0, 0, 0, 90, 0, 0 }, Rotations(output));
    }

    [AvaloniaFact]
    public async Task Removing_the_first_and_fifth_pages_leaves_the_other_three()
    {
        var file = Pdf("quita.pdf", 5);
        using var panel = await OpenAsync(file);
        panel.SelectOnly(panel.Items[0]);
        panel.Toggle(panel.Items[4]);
        panel.Remove();
        Assert.Equal("5 páginas → 3", panel.CountText);

        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(3, PageCount(output));
        Assert.Equal(new[] { 2, 3, 4 }, WhichPages(file, output));
    }

    [AvaloniaFact]
    public async Task Without_changes_or_without_pages_there_is_nothing_to_apply()
    {
        var file = Pdf("nada.pdf", 4);
        using var panel = await OpenAsync(file);
        Assert.False(panel.CanApply);
        Assert.False(panel.CanRun);

        // Turned all the way round, moved and put back, copied and the copy removed: still the original.
        panel.SelectOnly(panel.Items[1]);
        for (var i = 0; i < 4; i++)
            panel.RotateRight();
        panel.MoveForward();
        panel.MoveBack();
        panel.Duplicate();
        panel.Remove();
        Assert.False(panel.HasChanges);
        Assert.False(panel.CanApply);

        panel.SelectOnly(panel.Items[0]);
        panel.RotateRight();
        Assert.True(panel.CanApply);

        panel.SelectAll();
        panel.Remove();
        Assert.True(panel.NoPagesLeft);
        Assert.False(panel.CanApply);
        Assert.False(panel.HasPages);
        await panel.RunAsync(); // the button is off, and pressing it anyway does nothing
        Assert.False(panel.IsDone);
        Assert.Empty(Directory.GetFiles(_root, "* organizado*"));

        panel.Undo(); // the pages come back (the turned one still turned)
        Assert.False(panel.NoPagesLeft);
        Assert.True(panel.CanApply);
        panel.Reset();
        Assert.False(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task The_result_goes_beside_the_original_and_never_over_an_earlier_result()
    {
        var file = Pdf("doc.pdf", 3);
        var before = Hash(file);

        using var first = await OpenAsync(file);
        first.SelectOnly(first.Items[0]);
        first.MoveForward();
        await first.RunAsync();
        Assert.True(first.IsDone, first.Status);
        var one = Assert.Single(first.Outputs);
        Assert.Equal(Path.Combine(_root, "doc organizado.pdf"), one);
        Assert.Equal(new[] { 2, 1, 3 }, WhichPages(file, one));

        using var second = await OpenAsync(file);
        second.SelectOnly(second.Items[2]);
        second.MoveBack();
        await second.RunAsync();
        Assert.True(second.IsDone, second.Status);
        var two = Assert.Single(second.Outputs);
        Assert.Equal(Path.Combine(_root, "doc organizado 2.pdf"), two);
        Assert.Equal(new[] { 1, 3, 2 }, WhichPages(file, two));

        Assert.Equal(new[] { 2, 1, 3 }, WhichPages(file, one)); // the first result was not touched
        Assert.Equal(before, Hash(file));
    }

    [AvaloniaFact]
    public async Task The_documents_information_is_kept()
    {
        var file = Path.Combine(_root, "info.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.AddPage();
            document.Info.Title = "Informe anual ñandú";
            document.Info.Author = "Autor01";
            document.Info.Subject = "Cuentas";
            document.Info.Keywords = "pdf, cuentas";
            document.Save(file);
        }
        using var panel = await OpenAsync(file);
        panel.SelectOnly(panel.Items[1]);
        panel.MoveBack();
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var result = PdfReader.Open(Assert.Single(panel.Outputs), PdfDocumentOpenMode.Import);
        Assert.Equal("Informe anual ñandú", result.Info.Title);
        Assert.Equal("Autor01", result.Info.Author);
        Assert.Equal("Cuentas", result.Info.Subject);
        Assert.Equal("pdf, cuentas", result.Info.Keywords);
    }

    [AvaloniaFact]
    public async Task A_pdf_that_changed_after_it_was_opened_is_not_written_over_the_wrong_pages()
    {
        var file = Pdf("cambia.pdf", 5);
        using var panel = await OpenAsync(file);
        panel.SelectOnly(panel.Items[0]);
        panel.Remove();

        Pdf("cambia.pdf", 3); // somebody saves a shorter PDF meanwhile
        await panel.RunAsync();

        Assert.False(panel.IsDone);
        Assert.True(panel.HasError);
        Assert.Contains("ha cambiado", panel.Status);
        Assert.Empty(Directory.GetFiles(_root, "* organizado*"));
    }

    [Fact]
    public void Writing_never_overwrites_and_a_cancelled_write_leaves_nothing()
    {
        var file = Path.Combine(_root, "origen.pdf");
        MakePdf(file, 3);
        PageEdit[] pages = [new(0, 0, 0), new(1, 1, 0)];

        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var gone = Path.Combine(_root, "cancelado.pdf");
        Assert.ThrowsAny<OperationCanceledException>(() => PdfPageOrganizer.Write(file, gone, pages, 3, null, cancelled.Token));
        Assert.False(File.Exists(gone));

        var taken = Path.Combine(_root, "ocupado.pdf");
        File.WriteAllText(taken, "not mine");
        Assert.ThrowsAny<IOException>(() => PdfPageOrganizer.Write(file, taken, pages, 3, null, CancellationToken.None));
        Assert.Equal("not mine", File.ReadAllText(taken)); // somebody else's file is neither overwritten nor deleted

        Assert.Throws<PdfChangedException>(() => PdfPageOrganizer.Write(file, gone, pages, 4, null, CancellationToken.None));
        Assert.False(File.Exists(gone));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageOrganizer.Write(file, gone, [new(0, 7, 0)], 3, null, CancellationToken.None));
        Assert.False(File.Exists(gone));
    }

    // ───────── Big and broken files ─────────

    [AvaloniaFact]
    public async Task A_pdf_of_120_pages_opens_at_once_and_its_thumbnails_come_in_the_background()
    {
        var file = Pdf("ciento veinte.pdf", 120);

        var context = Context(file); // the first context of a run starts the app's services: not what is measured here
        var clock = Stopwatch.StartNew();
        using var panel = new OrganizePdfPanel(context);
        var constructed = clock.ElapsedMilliseconds;
        Assert.True(panel.IsLoading, "the constructor must return before the PDF is read");
        Assert.True(constructed < 500, $"the constructor took {constructed} ms");

        await WaitAsync(() => !panel.IsLoading);
        var loaded = clock.ElapsedMilliseconds;
        Assert.Null(panel.LoadError);
        Assert.Equal(120, panel.Items.Count);
        Assert.True(loaded < 5000, $"reading 120 pages took {loaded} ms");
        Assert.Equal("120 páginas → 120", panel.CountText);

        await WaitAsync(() => panel.Items.All(cell => cell.Thumbnail is not null), 60000);
        Assert.True(clock.ElapsedMilliseconds < 30000, $"120 thumbnails took {clock.ElapsedMilliseconds} ms");
    }

    [AvaloniaFact]
    public async Task Three_hundred_pages_open_in_small_steps_that_let_the_interface_breathe()
    {
        // A small PDF first: the first window of a run pays for loading the styles and compiling the templates.
        using (var warm = await OpenAsync(Pdf("calentar.pdf", 3), thumbnails: true))
            Render(warm, "organizepdf-warm");

        var file = Pdf("trescientas.pdf", 300);
        var context = Context(file);
        using var panel = new OrganizePdfPanel(context);
        var window = new ToolPanelWindow(panel);
        window.Show();
        try
        {
            // The grid fills a few cells per turn of the interface and gives way to input and drawing in between. A probe
            // posted at normal priority whenever the grid grows runs before the next step (which waits at background
            // priority) and so sees the grid as it was left; had all 300 cells gone in at once, every probe would see 300.
            var seen = new List<int>();
            panel.Items.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => seen.Add(panel.Items.Count), DispatcherPriority.Normal);

            await WaitAsync(() => !panel.IsLoading && panel.Items.All(cell => cell.Thumbnail is not null), 60000);

            Assert.Null(panel.LoadError);
            Assert.Equal(300, panel.Items.Count);
            Assert.Equal(300, seen.Max());
            Assert.True(seen.Distinct().Count() > 5, $"the grid filled in {seen.Distinct().Count()} step(s)");
            Assert.True(seen.Min() < 100, $"the first cells appeared with {seen.Min()} of 300 already there");
            Assert.Equal(Enumerable.Range(1, 300), panel.Items.Select(cell => cell.Order)); // numbered right while it filled
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_visible_pages_get_their_thumbnails_first()
    {
        var file = Pdf("prioridad.pdf", 60);
        var bytes = File.ReadAllBytes(file);
        var order = new List<int>();
        using var loader = new PageThumbnailLoader(bytes, 60, (page, _) => order.Add(page));
        loader.Prioritize([40, 41, 42]);

        await WaitAsync(() => order.Count == 60, 30000);

        Assert.Equal(60, order.Distinct().Count());
        Assert.All(new[] { 40, 41, 42 }, page => Assert.True(order.IndexOf(page) < 10, $"page {page} came at {order.IndexOf(page)}"));
    }

    [AvaloniaFact]
    public async Task A_corrupt_missing_or_protected_pdf_says_why_and_cannot_be_applied()
    {
        var garbage = Path.Combine(_root, "roto.pdf");
        File.WriteAllText(garbage, "this is not a PDF");
        var empty = Path.Combine(_root, "vacio.pdf");
        File.WriteAllBytes(empty, []);
        var missing = Path.Combine(_root, "no existe.pdf");
        var locked = Path.Combine(_root, "protegido.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.UserPassword = "user";
            document.SecuritySettings.OwnerPassword = "owner";
            document.Save(locked);
        }

        foreach (var path in new[] { garbage, empty, missing, locked })
        {
            using var panel = new OrganizePdfPanel(Context(path));
            await WaitAsync(() => !panel.IsLoading);
            Assert.NotNull(panel.LoadError);
            Assert.False(panel.CanApply, path);
            Assert.False(panel.CanRun, path);
            Assert.Empty(panel.Items);
            panel.SelectAll(); // the buttons have nothing to work on, and none of this throws
            panel.Remove();
            await panel.RunAsync();
            Assert.False(panel.IsDone);
        }

        using (var broken = new OrganizePdfPanel(Context(garbage)))
        {
            await WaitAsync(() => !broken.IsLoading);
            Assert.StartsWith("No se puede leer este PDF", broken.LoadError);
            Render(broken, "organizepdf-unreadable");
        }
        using (var protectedPanel = new OrganizePdfPanel(Context(locked)))
        {
            await WaitAsync(() => !protectedPanel.IsLoading);
            Assert.StartsWith("PDF protegido:", protectedPanel.LoadError);
            Render(protectedPanel, "organizepdf-protected");
        }
    }

    [AvaloniaFact]
    public async Task A_pdf_with_only_an_owner_password_opens_like_any_other()
    {
        var file = Path.Combine(_root, "dueno.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.AddPage();
            document.SecuritySettings.OwnerPassword = "owner";
            document.Save(file);
        }
        using var panel = await OpenAsync(file, thumbnails: true);
        Assert.Equal(2, panel.Items.Count);
        panel.SelectOnly(panel.Items[1]);
        panel.MoveBack();
        await panel.RunAsync();
        Assert.True(panel.IsDone, panel.Status);
        Assert.Equal(2, PageCount(Assert.Single(panel.Outputs)));
    }

    // ───────── The real view: mouse and keys ─────────

    private static List<Border> Cells(Window window) =>
        window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("pcell") && border.DataContext is OrganizePage)
            .OrderBy(border => ((OrganizePage)border.DataContext!).Order)
            .ToList();

    private static Point CentreOf(Window window, int index)
    {
        var cell = Cells(window)[index];
        return cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)!.Value;
    }

    private static void Click(Window window, Point at, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        Pump();
    }

    private static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Pump();
    }

    [AvaloniaFact]
    public async Task The_view_selects_with_the_mouse_and_obeys_the_keys()
    {
        var file = Pdf("teclas.pdf", 6);
        using var panel = await OpenAsync(file, thumbnails: true);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            Assert.Equal(6, Cells(window).Count);

            Click(window, CentreOf(window, 1)); // click: that page only
            Assert.Equal(new[] { 2 }, SelectedOrders(panel));
            Click(window, CentreOf(window, 3), RawInputModifiers.Control); // Ctrl+click: adds
            Assert.Equal(new[] { 2, 4 }, SelectedOrders(panel));
            Click(window, CentreOf(window, 1), RawInputModifiers.Control); // Ctrl+click on a selected page: takes it out
            Assert.Equal(new[] { 4 }, SelectedOrders(panel));
            Click(window, CentreOf(window, 0));
            Click(window, CentreOf(window, 2), RawInputModifiers.Shift); // Shift+click: the range
            Assert.Equal(new[] { 1, 2, 3 }, SelectedOrders(panel));

            Press(window, Key.A, PhysicalKey.A, RawInputModifiers.Control);
            Assert.Equal(6, SelectedOrders(panel).Length);
            Click(window, CentreOf(window, 4)); // pressing and releasing on a page of a selection narrows it to that page
            Assert.Equal(new[] { 5 }, SelectedOrders(panel));

            Press(window, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.Alt); // Alt+arrow: moves the selection
            Assert.Equal(new[] { 1, 2, 3, 5, 4, 6 }, Sources(panel));
            Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Alt);
            Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Alt);
            Assert.Equal(new[] { 1, 2, 3, 4, 6, 5 }, Sources(panel));

            Press(window, Key.Delete, PhysicalKey.Delete); // Del removes
            Assert.Equal(new[] { 1, 2, 3, 4, 6 }, Sources(panel));
            Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control); // Ctrl+Z undoes it
            Assert.Equal(new[] { 1, 2, 3, 4, 6, 5 }, Sources(panel));
            Press(window, Key.Y, PhysicalKey.Y, RawInputModifiers.Control); // Ctrl+Y does it again
            Assert.Equal(new[] { 1, 2, 3, 4, 6 }, Sources(panel));
            Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
            Assert.Equal(new[] { 1, 2, 3, 4, 6, 5 }, Sources(panel));
            Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift); // Ctrl+Shift+Z too
            Assert.Equal(new[] { 1, 2, 3, 4, 6 }, Sources(panel));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Dragging_a_page_to_a_gap_moves_it_there()
    {
        var file = Pdf("arrastra.pdf", 6);
        using var panel = await OpenAsync(file, thumbnails: true);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        try
        {
            // Page 1, dropped on the right half of page 3: it lands between 3 and 4.
            var from = CentreOf(window, 0);
            var target = CentreOf(window, 2) + new Point(Cells(window)[2].Bounds.Width / 4, 0);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(14, 0));
            window.MouseMove(target);
            Pump();
            Assert.True(panel.Items[0].IsGhost, "the dragged page is dimmed");
            window.MouseUp(target, MouseButton.Left);
            Pump();
            Assert.Equal(new[] { 2, 3, 1, 4, 5, 6 }, Sources(panel));
            Assert.All(panel.Items, cell => Assert.False(cell.IsGhost));

            // A group moves together: select 2 and 3 (now first and second), drag the first onto the left half of the last.
            Click(window, CentreOf(window, 0));
            Click(window, CentreOf(window, 1), RawInputModifiers.Control);
            var start = CentreOf(window, 0);
            var end = CentreOf(window, 5) - new Point(Cells(window)[5].Bounds.Width / 4, 0);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 16));
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Pump();
            Assert.Equal(new[] { 1, 4, 5, 2, 3, 6 }, Sources(panel));

            // Dropping a group where it already is changes nothing (and is no step of the history).
            var edit = panel.CanUndo;
            var before = Sources(panel);
            var here = CentreOf(window, 3);
            window.MouseDown(here, MouseButton.Left);
            window.MouseMove(here + new Point(10, 0));
            window.MouseUp(here + new Point(10, 0), MouseButton.Left);
            Pump();
            Assert.Equal(before, Sources(panel));
            Assert.Equal(edit, panel.CanUndo);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_tools_are_offered_on_the_wheel_for_a_pdf()
    {
        var file = Pdf("rueda.pdf", 2);
        TestServices.EnsureInitialized("es");
        Assert.True(PanelRegistry.Has("organizePDF"));
        var loc = AppHost.Get<ILocalizer>();
        Assert.Contains("organizePDF", WheelCatalog.ToolsFor([file], loc).Select(entry => entry.Id));
        Assert.DoesNotContain("organizePDF", WheelCatalog.ToolsFor([file, file], loc).Select(entry => entry.Id));
        Assert.Null(PanelRegistry.Unavailable("organizePDF", loc)); // no FFmpeg needed
        await Task.CompletedTask;
    }
}
