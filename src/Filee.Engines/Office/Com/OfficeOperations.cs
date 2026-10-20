// Mandarina: what the engine does inside Word, Excel and PowerPoint. Always on a private copy of the file (OfficeFiles),
// opened read-only and without a window, with a password that can't be right (so a protected file fails at once instead of
// asking for one), saved in the format asked for and closed. Members are called by name (late binding), with the named
// arguments of the object model.

using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Pdf;
using SkiaSharp;

namespace Filee.Engines.Office.Com;

[SupportedOSPlatform("windows")]
internal static class OfficeOperations
{
    /// <summary>
    /// A password nobody has: opening a protected file with it fails at once, a plain file ignores it. At most 15
    /// characters: Excel refuses to open anything when the write-reservation password is longer (error 1004).
    /// </summary>
    private const string NoPassword = "Mnd-no-pwd-7d4";

    /// <summary>Saves the private copy at <paramref name="input"/> as <paramref name="to"/> (a format id) in <paramref name="output"/>.</summary>
    public static void Convert(OfficeApplication app, string to, string input, string output, bool pdfA)
    {
        switch (app.Kind)
        {
            case OfficeKind.Word:
                ConvertWord(app, to, input, output, pdfA);
                break;
            case OfficeKind.Excel:
                ConvertExcel(app, to, input, output, pdfA);
                break;
            default:
                ConvertPowerPoint(app, to, input, output, pdfA);
                break;
        }
    }

    private static void ConvertWord(OfficeApplication app, string to, string input, string output, bool pdfA)
    {
        using var documents = app.Root.Child("Documents");
        using var document = Open(() => documents.CallObject("Open",
            ("FileName", input), ("ConfirmConversions", false), ("ReadOnly", true), ("AddToRecentFiles", false),
            ("PasswordDocument", NoPassword), ("PasswordTemplate", NoPassword),
            ("WritePasswordDocument", NoPassword), ("WritePasswordTemplate", NoPassword),
            ("Visible", false), ("OpenAndRepair", false), ("NoEncodingDialog", true)));
        try
        {
            Save(() => to switch
            {
                "pdf" => document.CallNamed("ExportAsFixedFormat",
                    ("OutputFileName", output), ("ExportFormat", 17), ("OpenAfterExport", false), ("OptimizeFor", 0),
                    ("Range", 0), ("Item", 0), ("IncludeDocProps", true), ("KeepIRM", true), ("CreateBookmarks", 1),
                    ("DocStructureTags", true), ("BitmapMissingFonts", true), ("UseISO19005_1", pdfA)),
                "docx" => document.CallNamed("SaveAs2", ("FileName", output), ("FileFormat", 16), ("AddToRecentFiles", false)),
                "doc" => document.CallNamed("SaveAs2", ("FileName", output), ("FileFormat", 0), ("AddToRecentFiles", false)),
                _ => throw Unsupported(app, to),
            });
        }
        finally
        {
            TryClose(() => document.CallNamed("Close", ("SaveChanges", 0)));
        }
    }

    private static void ConvertExcel(OfficeApplication app, string to, string input, string output, bool pdfA)
    {
        // Excel's PDF export has no PDF/A option: say so rather than write a PDF that isn't what was asked for.
        if (pdfA && to == "pdf")
            throw new ConversionFailureException("office.error.pdfa_excel");

        using var workbooks = app.Root.Child("Workbooks");
        using var workbook = Open(() => workbooks.CallObject("Open",
            ("Filename", input), ("UpdateLinks", 0), ("ReadOnly", true), ("Password", NoPassword), ("WriteResPassword", NoPassword),
            ("IgnoreReadOnlyRecommended", true), ("Notify", false), ("AddToMru", false)));
        try
        {
            Save(() => to switch
            {
                "pdf" => workbook.CallNamed("ExportAsFixedFormat",
                    ("Type", 0), ("Filename", output), ("Quality", 0), ("IncludeDocProperties", true), ("IgnorePrintAreas", false),
                    ("OpenAfterPublish", false)),
                "xlsx" => workbook.CallNamed("SaveAs", ("Filename", output), ("FileFormat", 51)),
                "xls" => workbook.CallNamed("SaveAs", ("Filename", output), ("FileFormat", 56)),
                _ => throw Unsupported(app, to),
            });
        }
        finally
        {
            TryClose(() => workbook.CallNamed("Close", ("SaveChanges", false)));
        }
    }

    private static void ConvertPowerPoint(OfficeApplication app, string to, string input, string output, bool pdfA)
    {
        using var presentations = app.Root.Child("Presentations");
        using var presentation = Open(() => presentations.CallObject("Open",
            ("FileName", input), ("ReadOnly", -1), ("Untitled", 0), ("WithWindow", 0)));
        try
        {
            Save(() =>
            {
                switch (to)
                {
                    case "pdf" when pdfA:
                        return presentation.CallNamed("ExportAsFixedFormat",
                            ("Path", output), ("FixedFormatType", 2), ("Intent", 2), ("UseISO19005_1", -1));
                    case "pdf":
                        return presentation.CallNamed("SaveAs", ("FileName", output), ("FileFormat", 32)); // ppSaveAsPDF
                    case "pptx":
                        return presentation.CallNamed("SaveCopyAs", ("FileName", output), ("FileFormat", 24)); // ppSaveAsOpenXMLPresentation
                    case "ppt":
                        return presentation.CallNamed("SaveCopyAs", ("FileName", output), ("FileFormat", 1)); // ppSaveAsPresentation
                    default:
                        throw Unsupported(app, to);
                }
            });
        }
        finally
        {
            TryClose(() => presentation.Call("Close"));
        }
    }

    // ───────────────────────── Self-test ─────────────────────────

    /// <summary>
    /// Converts a small file of the engine's own to PDF, the cheapest proof that this Office can open and save: it throws
    /// what the conversion throws, or <c>office.error.no_output</c> when no PDF comes out.
    /// </summary>
    public static void SelfTest(OfficeApplication app, string folder)
    {
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "self-test" + OfficeSamples.Extension(app.Kind));
        var output = Path.Combine(folder, "self-test.pdf");
        OfficeSamples.Write(app.Kind, input);
        Convert(app, "pdf", input, output, false);
        if (!OfficeFiles.IsPdf(output))
            throw new ConversionFailureException("office.error.no_output");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static ComObject Open(Func<ComObject> open)
    {
        try
        {
            return open();
        }
        catch (OfficeComException ex) when (!ex.IsBusy && !ex.IsServerGone)
        {
            throw LooksLikePassword(ex.Message)
                ? new ConversionFailureException("office.error.password", null, ex)
                : new ConversionFailureException("office.error.open", Short(ex.Message), ex);
        }
    }

    private static void Save(Func<object?> save)
    {
        try
        {
            save();
        }
        catch (OfficeComException ex) when (!ex.IsBusy && !ex.IsServerGone)
        {
            throw new ConversionFailureException("office.error.save", Short(ex.Message), ex);
        }
    }

    /// <summary>Closing the copy must never hide the real result: if it fails the copy is thrown away with the job.</summary>
    private static void TryClose(Action close)
    {
        try
        {
            close();
        }
        catch (OfficeComException)
        {
            // the copy goes with the job's folder
        }
    }

    private static ConversionFailureException Unsupported(OfficeApplication app, string to) =>
        new("office.error.unsupported", $"{app.Kind.DisplayName()} → {to}");

    private static readonly string[] PasswordWords =
    [
        "password", "contraseña", "contrasena", "kennwort", "mot de passe", "senha", "wachtwoord", "hasło", "пароль",
        "パスワード", "密码", "密碼", "암호", "비밀번호",
    ];

    internal static bool LooksLikePassword(string message) =>
        PasswordWords.Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string Short(string message)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 200 ? message : message[..200] + "…";
    }
}

/// <summary>Tiny files of each kind, made by hand, for the self-test.</summary>
[SupportedOSPlatform("windows")]
internal static class OfficeSamples
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static string Extension(OfficeKind kind) => kind switch
    {
        OfficeKind.Word => ".docx",
        OfficeKind.Excel => ".xlsx",
        _ => ".pptx",
    };

    public static void Write(OfficeKind kind, string path)
    {
        switch (kind)
        {
            case OfficeKind.Word:
                WriteWord(path);
                break;
            case OfficeKind.Excel:
                WriteExcel(path);
                break;
            default:
                WritePowerPoint(path);
                break;
        }
    }

    private static void WriteWord(string path) => WritePackage(path, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
        ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
        ["word/document.xml"] = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            "<w:p><w:r><w:t>Mandarina</w:t></w:r></w:p></w:body></w:document>",
    });

    private static void WriteExcel(string path) => WritePackage(path, new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>",
        ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
        ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
        ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Mandarina</t></is></c><c r=\"B1\"><v>1</v></c></row></sheetData></worksheet>",
    });

    /// <summary>A one-slide deck with the picture deck writer PDF → PPTX uses (PowerPoint opens what it writes).</summary>
    private static void WritePowerPoint(string path)
    {
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(SKColors.White);
        using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        var (width, height) = PictureDeckWriter.SlideSize(720, 540);
        using var deck = new PictureDeckWriter(path, width, height);
        deck.AddSlide(jpeg.AsSpan(), 720, 540, "Self-test", null);
        deck.Save();
    }

    private static void WritePackage(string path, Dictionary<string, string> parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(Declaration + xml);
        }
    }
}
