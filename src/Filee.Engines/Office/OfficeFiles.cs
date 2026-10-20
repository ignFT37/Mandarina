// Mandarina: the files the Microsoft Office engine hands to Office. Office never opens the user's own file: it opens a
// private copy made from the bytes alone, so the copy has no "downloaded from the Internet" mark (no Protected View), a
// file the user has open in their Word is no problem, and PowerPoint (one instance per user) can't hand back the user's
// open presentation to be closed.

using Filee.Core.Conversion;

namespace Filee.Engines.Office;

internal static class OfficeFiles
{
    /// <summary>The Office Open XML formats: a ZIP package, unless a password turned it into an OLE container.</summary>
    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        "docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xltx", "pptx", "pptm", "ppsx", "potx",
    };

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] EncryptedPackage = "EncryptedPackage"u8.ToArray().SelectMany(b => new byte[] { b, 0 }).ToArray();

    /// <summary>
    /// Fails with <c>office.error.password</c> for a package file that was saved with "Encrypt with password" (Office
    /// stores it in an OLE container with an EncryptedPackage stream): Office would ask for the password in a window.
    /// Older formats (DOC, XLS, PPT) are told by Office itself, through the password the engine opens them with.
    /// </summary>
    public static void RejectPasswordProtected(string path, string format)
    {
        if (!Packages.Contains(format) || !IsEncryptedPackage(path))
            return;
        throw new ConversionFailureException("office.error.password");
    }

    internal static bool IsEncryptedPackage(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[512];
            if (file.Read(header, 0, header.Length) < header.Length || !header.AsSpan(0, 8).SequenceEqual(OleSignature))
                return false;
            // The directory of the OLE container lists the streams; an encrypted package has "EncryptedPackage".
            var sectorSize = 1 << BitConverter.ToUInt16(header, 0x1E);
            var directory = BitConverter.ToInt32(header, 0x30);
            if (sectorSize is < 512 or > 4096 || directory < 0)
                return false;
            file.Position = (directory + 1L) * sectorSize;
            var sectors = new byte[Math.Min(sectorSize * 2, 8192)];
            var read = file.Read(sectors, 0, sectors.Length);
            return sectors.AsSpan(0, read).IndexOf(EncryptedPackage) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // Office will say what is wrong with the file
        }
    }

    /// <summary>
    /// Copies <paramref name="input"/> into <paramref name="folder"/> keeping its name (a field like FILENAME in a header
    /// shows the right one) or, if the path would get too long for Office, a short one. Only the bytes of the file are
    /// copied, not its alternate streams (Zone.Identifier).
    /// </summary>
    public static string PrivateCopy(string input, string folder)
    {
        var name = Path.GetFileName(input);
        if (folder.Length + name.Length + 1 > 200)
            name = "document" + Path.GetExtension(input);
        var target = Path.Combine(folder, name);
        using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
        return target;
    }

    /// <summary>The file exists and starts like a PDF.</summary>
    public static bool IsPdf(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var start = new byte[5];
            return file.Read(start, 0, start.Length) == start.Length && start.AsSpan().SequenceEqual("%PDF-"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
