// Mandarina: Windows' share sheet takes files, not folders, so the Share action compresses every folder to a ZIP in a temporary folder of
// its own first. These tests make real ZIPs of real folders (checked with System.IO.Compression and with Windows' own tar) and look at
// what is left on disk: the originals untouched, one ZIP per folder, nothing at all after a failure, a cancel or the sheet closing.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Filee.App.Mandarina;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public sealed class MandarinaShareStagingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-staging").FullName;

    /// <summary>Where the ZIPs of a test go (instead of the real temp folder).</summary>
    private string Stage => Path.Combine(_root, "staging");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Folder(string name, string? parent = null) => Directory.CreateDirectory(Path.Combine(_root, parent ?? "src", name)).FullName;

    private static string Write(string folder, string relative, byte[] content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Write(string folder, string relative, string text) => Write(folder, relative, System.Text.Encoding.UTF8.GetBytes(text));

    private static byte[] Random(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Every file under <paramref name="folder"/> as "relative/path" → SHA-256, plus every empty folder as "relative/path/".</summary>
    private static Dictionary<string, string> Tree(string folder)
    {
        var tree = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            tree[Path.GetRelativePath(folder, file).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
            tree[Path.GetRelativePath(folder, directory).Replace('\\', '/') + "/"] = "";
        return tree;
    }

    /// <summary>What a ZIP holds, in the same shape as <see cref="Tree"/> (names without the folder's own name at the front).</summary>
    private static Dictionary<string, string> Contents(string zipPath, string folderName)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var contents = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            Assert.StartsWith(folderName + "/", entry.FullName);
            var name = entry.FullName[(folderName.Length + 1)..];
            if (entry.FullName.EndsWith('/'))
            {
                contents[name] = "";
                continue;
            }
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            contents[name] = Convert.ToHexString(SHA256.HashData(memory.ToArray()));
        }
        contents.Remove("");
        return contents;
    }

    /// <summary>The names of a ZIP's entries (the ZIP is closed again: a ZIP somebody holds cannot be deleted).</summary>
    private static List<string> EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(entry => entry.FullName).ToList();
    }

    private ShareStaging Prepare(params string[] paths) => ShareStaging.Prepare(paths, cancel: TestContext.Current.CancellationToken, root: Stage);

    private IEnumerable<string> Leftovers() => Directory.Exists(Stage) ? Directory.EnumerateFileSystemEntries(Stage, "*", SearchOption.AllDirectories) : [];

    // ───────── The ZIP ─────────

    [Fact]
    public void A_folder_becomes_a_zip_with_everything_in_it()
    {
        var folder = Folder("Fotos de verano");
        Write(folder, "leeme.txt", "hola, mundo");
        Write(folder, "sub/datos.bin", Random(300_000));
        Write(folder, "sub/más/ñandú ü ☕ [1] #2 100%.txt", "unicode en el nombre");
        Write(folder, "sub/más/pic.jpg", Random(20_000, 7));
        Write(folder, "vacio.txt", Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(folder, "sub", "carpeta vacía"));
        var before = Tree(folder);

        using var staged = Prepare(folder);

        var zip = Assert.Single(staged.Files);
        Assert.Equal(Path.Combine(staged.Folder!, "Fotos de verano.zip"), zip);
        Assert.Equal(before, Contents(zip, "Fotos de verano"));   // every file byte for byte, and the empty folder
        Assert.Equal(before, Tree(folder));                       // the original untouched
        Assert.Empty(staged.Skipped);
        Assert.True(File.Exists(zip) && new FileInfo(zip).Length > 0);
    }

    [Fact]
    public void The_zip_is_readable_by_windows_own_tools()
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(tar))
        {
            Assert.Skip("Windows' tar is not here.");
            return;
        }
        var folder = Folder("Documentos");
        Write(folder, "a.txt", "uno");
        Write(folder, "b/c/d.bin", Random(100_000, 3));
        Write(folder, "b/ñ.txt", "eñe");
        Directory.CreateDirectory(Path.Combine(folder, "vacía"));
        using var staged = Prepare(folder);

        // An independent reader unpacks it, names and bytes as they were.
        var unpacked = Directory.CreateDirectory(Path.Combine(_root, "desempaquetado")).FullName;
        using var process = Process.Start(new ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, ArgumentList = { "-xf", staged.Files[0], "-C", unpacked } })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(Tree(folder), Tree(Path.Combine(unpacked, "Documentos")));
    }

    [Fact]
    public void Text_is_compressed_and_formats_that_are_compressed_already_are_stored()
    {
        var folder = Folder("Mezcla");
        Write(folder, "texto.txt", string.Concat(Enumerable.Repeat("una línea que se repite\r\n", 4000)));
        Write(folder, "foto.jpg", Random(50_000, 5));
        Write(folder, "otro.docx", Random(50_000, 6));
        using var staged = Prepare(folder);

        using var zip = ZipFile.OpenRead(staged.Files[0]);
        var text = zip.GetEntry("Mezcla/texto.txt")!;
        Assert.True(text.CompressedLength < text.Length / 4, "text should shrink");
        foreach (var name in new[] { "Mezcla/foto.jpg", "Mezcla/otro.docx" })   // stored: no time lost deflating them again
        {
            var entry = zip.GetEntry(name)!;
            Assert.InRange(entry.CompressedLength, entry.Length, entry.Length + entry.Length / 100 + 64);
        }
    }

    [Fact]
    public void Files_keep_their_modification_time_in_the_zip()
    {
        var folder = Folder("Fechas");
        var file = Write(folder, "viejo.txt", "de otra época");
        var stamp = new DateTime(2019, 3, 14, 15, 9, 26, DateTimeKind.Local);
        File.SetLastWriteTime(file, stamp);
        var ancient = Write(folder, "antiguo.txt", "sin fecha");
        File.SetLastWriteTime(ancient, new DateTime(1601, 1, 2)); // outside what a ZIP can say: the nearest it can
        using var staged = Prepare(folder);

        using var zip = ZipFile.OpenRead(staged.Files[0]);
        Assert.InRange((zip.GetEntry("Fechas/viejo.txt")!.LastWriteTime.LocalDateTime - stamp).TotalSeconds, -2, 2);
        Assert.Equal(1980, zip.GetEntry("Fechas/antiguo.txt")!.LastWriteTime.Year);
    }

    [Fact]
    public void An_empty_folder_is_a_valid_zip()
    {
        var folder = Folder("Vacía");
        using var staged = Prepare(folder);

        using (var zip = ZipFile.OpenRead(staged.Files[0]))
            Assert.Equal(["Vacía/"], zip.Entries.Select(entry => entry.FullName));
        var unpacked = Path.Combine(_root, "fuera");
        ZipFile.ExtractToDirectory(staged.Files[0], unpacked);
        Assert.True(Directory.Exists(Path.Combine(unpacked, "Vacía")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(unpacked, "Vacía")));
    }

    [Fact]
    public void A_folder_with_many_small_files_keeps_them_all()
    {
        var folder = Folder("Muchos");
        for (var i = 0; i < 1500; i++)
            Write(folder, $"d{i % 30}/f{i}.txt", $"fichero {i}");
        using var staged = Prepare(folder);

        Assert.Equal(1500, EntryNames(staged.Files[0]).Count);
        Assert.Equal(Tree(folder), Contents(staged.Files[0], "Muchos"));
    }

    // ───────── What is shared ─────────

    [Fact]
    public void Files_and_folders_go_together_in_the_order_of_the_selection_and_files_are_not_copied()
    {
        var first = Write(Folder("sueltos"), "uno.png", Random(500, 1));
        var folder = Folder("Carpeta");
        Write(folder, "x.txt", "x");
        var last = Write(Folder("sueltos"), "dos.pdf", Random(500, 2));

        using var staged = Prepare(first, folder + "\\", last);

        Assert.Equal([first, Path.Combine(staged.Folder!, "Carpeta.zip"), last], staged.Files);
        Assert.Equal(["Carpeta.zip"], Directory.GetFileSystemEntries(staged.Folder!).Select(Path.GetFileName)); // the loose files stay where they are
    }

    [Fact]
    public void Only_files_need_no_temporary_folder()
    {
        var one = Write(Folder("a"), "uno.txt", "1");
        var two = Write(Folder("a"), "dos.txt", "2");

        using var staged = Prepare(one, two, one.ToUpperInvariant(), "");

        Assert.Equal([one, two], staged.Files);
        Assert.Null(staged.Folder);
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public void Things_that_are_gone_are_left_out()
    {
        var folder = Folder("Existe");
        Write(folder, "x.txt", "x");

        using (var staged = Prepare(Path.Combine(_root, "no existe.txt"), folder, Path.Combine(_root, "tampoco")))
            Assert.Equal(["Existe.zip"], staged.Files.Select(Path.GetFileName));
        using var nothing = Prepare(Path.Combine(_root, "no existe.txt"));
        Assert.Empty(nothing.Files);
        Assert.Null(nothing.Folder);
    }

    [Fact]
    public void Folders_with_the_same_name_get_a_number()
    {
        var one = Folder("Fotos", "uno");
        var two = Folder("Fotos", "dos");
        var three = Folder("fotos", "tres");
        Write(one, "a.txt", "uno");
        Write(two, "a.txt", "dos");
        Write(three, "a.txt", "tres");

        using var staged = Prepare(one, two, three);

        Assert.Equal(["Fotos.zip", "Fotos 2.zip", "fotos 3.zip"], staged.Files.Select(Path.GetFileName));
        Assert.Equal(3, staged.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Tree(one), Contents(staged.Files[0], "Fotos"));
        Assert.Equal(Tree(two), Contents(staged.Files[1], "Fotos"));
        Assert.Equal(Tree(three), Contents(staged.Files[2], "fotos"));
    }

    [Fact]
    public void Two_shares_never_meet_in_the_same_folder()
    {
        var folder = Folder("Carpeta");
        Write(folder, "x.txt", "x");

        using var first = Prepare(folder);
        using var second = Prepare(folder);

        Assert.NotEqual(first.Folder, second.Folder);
        Assert.Equal("Carpeta.zip", Path.GetFileName(second.Files[0])); // no " 2": each has a folder of its own
    }

    [Fact]
    public void A_drive_root_is_named_by_its_letter()
    {
        Assert.Equal("D", ShareStaging.FolderName(@"D:\"));
        Assert.Equal("share", ShareStaging.FolderName(@"\\srv\share"));
        Assert.Equal("Docs", ShareStaging.FolderName(@"C:\Users\Me\Docs\"));
    }

    // ───────── Names ─────────

    [Theory]
    [InlineData("Fotos", "Fotos.zip")]
    [InlineData("a:b*c?d", "a_b_c_d.zip")]               // a name from a place that allows what Windows does not
    [InlineData("punto.", "punto.zip")]                  // Windows drops a trailing dot or space
    [InlineData("espacio ", "espacio.zip")]
    [InlineData("aux", "_aux.zip")]                      // a device name, whatever the extension
    [InlineData("COM1.viejo", "_COM1.viejo.zip")]
    [InlineData("auxiliar", "auxiliar.zip")]
    [InlineData("...", "Folder.zip")]
    public void Zip_names_are_names_Windows_accepts(string folderName, string expected) =>
        Assert.Equal(expected, Path.GetFileName(ShareStaging.FreeZipPath(Folder("nombres"), folderName)));

    [Fact]
    public void A_long_folder_name_gives_a_zip_whose_whole_path_stays_under_the_share_sheets_limit()
    {
        var directory = Folder("nombres");
        var name = new string('n', 250);

        var first = ShareStaging.FreeZipPath(directory, name);
        File.WriteAllText(first, "ocupado");
        var second = ShareStaging.FreeZipPath(directory, name);

        Assert.InRange(first.Length, 1, 240);
        Assert.InRange(second.Length, 1, 240);
        Assert.NotEqual(first, second);
        Assert.EndsWith(" 2.zip", second);
    }

    [Fact]
    public void A_deep_folder_and_a_long_name_are_compressed_whatever_the_length_of_the_paths()
    {
        var name = "una carpeta con un nombre muy largo " + new string('x', 150);
        var folder = Folder(name);
        var deep = folder;
        for (var level = 0; level < 6; level++)
            deep = Path.Combine(deep, $"nivel {level} " + new string('d', 45));
        Write(deep, "hondo.txt", "muy dentro");
        Write(folder, "arriba.txt", "arriba");
        Assert.True(Path.Combine(deep, "hondo.txt").Length > 300);

        using var staged = Prepare(folder);

        Assert.InRange(staged.Files[0].Length, 1, 240);
        Assert.Equal(Tree(folder), Contents(staged.Files[0], name));
    }

    // ───────── Files other programs hold ─────────

    [Fact]
    public void A_file_another_program_is_writing_is_still_read()
    {
        var folder = Folder("Registro");
        var log = Write(folder, "app.log", "primera línea\r\n");
        Write(folder, "otro.txt", "otro");

        // The way a program keeps its log open: for writing, letting others read and write.
        using (var writer = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Seek(0, SeekOrigin.End);
            writer.Write(System.Text.Encoding.UTF8.GetBytes("segunda línea\r\n"));
            writer.Flush();
            using var staged = Prepare(folder);

            using var zip = ZipFile.OpenRead(staged.Files[0]);
            using var reader = new StreamReader(zip.GetEntry("Registro/app.log")!.Open());
            Assert.Equal("primera línea\r\nsegunda línea\r\n", reader.ReadToEnd());
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_stops_the_share_with_its_name_and_leaves_nothing_behind()
    {
        var fine = Folder("Bien");
        Write(fine, "x.txt", "x");
        var folder = Folder("Informes");
        Write(folder, "a.txt", "a");
        var locked = Write(folder, "trimestral/Informe final.docx", "abierto");
        Write(folder, "z.txt", "z");

        // Another program with the file open and refusing to share it (an exclusive lock).
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var failure = Assert.Throws<ShareStagingException>(() => Prepare(fine, folder));

        Assert.Equal(ShareProblem.InUse, failure.Problem);
        Assert.Equal("Informes", failure.Folder);
        Assert.Equal("Informe final.docx", failure.Item);
        Assert.Contains("Informes/trimestral/Informe final.docx", failure.Message);
        Assert.Empty(Leftovers());                   // not even the ZIP of "Bien", which had gone well
        Assert.True(File.Exists(Path.Combine(fine, "x.txt")) && File.Exists(Path.Combine(folder, "a.txt")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void A_file_the_user_may_not_read_stops_the_share_with_its_name()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACLs.");
            return;
        }
        var folder = Folder("Privado");
        Write(folder, "visible.txt", "v");
        var secret = Write(folder, "secreto.txt", "s");
        var me = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new FileInfo(secret);
        try
        {
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Could not deny access to the file: " + ex.Message);
            return;
        }

        try
        {
            var failure = Assert.Throws<ShareStagingException>(() => Prepare(folder));

            Assert.Equal(ShareProblem.NoAccess, failure.Problem);
            Assert.Equal("secreto.txt", failure.Item);
            Assert.Empty(Leftovers());
        }
        finally
        {
            var security = info.GetAccessControl();
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    [Theory]
    [InlineData(0x80070020, "InUse")]       // ERROR_SHARING_VIOLATION
    [InlineData(0x80070021, "InUse")]       // ERROR_LOCK_VIOLATION
    [InlineData(0x80070070, "NoSpace")]     // ERROR_DISK_FULL
    [InlineData(0x80070027, "NoSpace")]     // ERROR_HANDLE_DISK_FULL
    [InlineData(0x80070057, "Other")]
    public void What_went_wrong_is_told_from_the_error_code(uint code, string expected)
    {
        var failure = ShareStaging.Failed(new IOException("el mensaje de Windows", unchecked((int)code)), "Carpeta", "sub/archivo.txt");

        Assert.Equal(Enum.Parse<ShareProblem>(expected), failure.Problem);
        Assert.Equal("archivo.txt", failure.Item);
        Assert.Contains("el mensaje de Windows", failure.Message);
        Assert.Contains("sub/archivo.txt", failure.Message);
        Assert.Equal(ShareProblem.NoAccess, ShareStaging.Failed(new UnauthorizedAccessException("no"), "Carpeta", null).Problem);
        Assert.Null(ShareStaging.Failed(new IOException("x"), "Carpeta", null).Item);
    }

    // ───────── Links ─────────

    private static bool MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, ArgumentList = { "/c", "mklink", "/J", link, target } })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [Fact]
    public void Links_inside_a_folder_are_not_followed_so_loops_and_other_drives_stay_out()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows'.");
            return;
        }
        var folder = Folder("Con enlaces");
        Write(folder, "real.txt", "real");
        Write(folder, "sub/dentro.txt", "dentro");
        var outside = Folder("Fuera");
        Write(outside, "secreto.txt", "no es de la carpeta");
        var loop = Path.Combine(folder, "sub", "vuelta");
        var exit = Path.Combine(folder, "salida");
        if (!MakeJunction(loop, folder) || !MakeJunction(exit, outside))
        {
            Assert.Skip("Could not make junctions here.");
            return;
        }
        try
        {
            using var staged = Prepare(folder);

            var names = EntryNames(staged.Files[0]).Order().ToList();
            Assert.Equal(["Con enlaces/real.txt", "Con enlaces/sub/dentro.txt"], names);   // no round trip, no way out
            Assert.Equal(["Con enlaces/salida", "Con enlaces/sub/vuelta"], staged.Skipped.Order());
            Assert.True(File.Exists(Path.Combine(outside, "secreto.txt")));
        }
        finally
        {
            Directory.Delete(loop);   // the link, not what it points to
            Directory.Delete(exit);
        }
    }

    [Fact]
    public void A_folder_that_is_itself_a_link_is_followed_because_the_user_picked_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are Windows'.");
            return;
        }
        var real = Folder("Verdadera");
        Write(real, "x.txt", "x");
        var link = Path.Combine(_root, "src", "Atajo");
        if (!MakeJunction(link, real))
        {
            Assert.Skip("Could not make a junction here.");
            return;
        }
        try
        {
            using var staged = Prepare(link);

            Assert.Equal(["Atajo/x.txt"], EntryNames(staged.Files[0]));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void The_temporary_folder_is_not_zipped_into_the_folder_it_sits_in()
    {
        var folder = Folder("Perfil");
        Write(folder, "x.txt", "x");

        using var staged = ShareStaging.Prepare([folder], cancel: TestContext.Current.CancellationToken, root: Path.Combine(folder, "Temp", "Mandarina"));

        var names = EntryNames(staged.Files[0]);
        Assert.Contains("Perfil/x.txt", names);
        Assert.DoesNotContain(names, name => name.Contains("share-") || name.EndsWith(".zip")); // only the empty Temp\Mandarina is left of it
        Assert.Single(staged.Skipped);
    }

    // ───────── Progress and cancelling ─────────

    private sealed class Recorder(Action<double>? each = null) : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            Values.Add(value);
            each?.Invoke(value);
        }
    }

    [Fact]
    public void Progress_runs_from_nothing_to_everything_without_going_back()
    {
        var folder = Folder("Grande");
        Write(folder, "uno.bin", Random(2_000_000, 1));
        Write(folder, "dos.bin", Random(1_000_000, 2));
        var progress = new Recorder();

        using var staged = ShareStaging.Prepare([folder], progress, TestContext.Current.CancellationToken, Stage);

        Assert.True(progress.Values.Count >= 10, "a bar that moves");
        Assert.Equal(progress.Values.Order(), progress.Values);
        Assert.InRange(progress.Values[0], 0, 0.5);
        Assert.Equal(1, progress.Values[^1]);
        Assert.All(progress.Values, value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void A_cancel_before_starting_does_nothing()
    {
        var folder = Folder("Cancelada");
        Write(folder, "x.txt", "x");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ShareStaging.Prepare([folder], null, cancel.Token, Stage));

        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_cancel_halfway_stops_and_deletes_what_was_made()
    {
        var done = Folder("Hecha");
        Write(done, "x.txt", "x");
        var big = Folder("Grande");
        Write(big, "uno.bin", Random(3_000_000, 1));
        Write(big, "dos.bin", Random(3_000_000, 2));
        using var cancel = new CancellationTokenSource();
        var progress = new Recorder(value =>
        {
            if (value > 0.25)
                cancel.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => ShareStaging.Prepare([done, big], progress, cancel.Token, Stage));

        Assert.Empty(Leftovers());
        Assert.Equal(2, Directory.GetFiles(big).Length); // the originals are where they were
    }

    // ───────── Cleaning up ─────────

    [Fact]
    public void Dispose_deletes_the_temporary_folder_and_the_originals_stay()
    {
        var folder = Folder("Efímera");
        Write(folder, "x.txt", "x");
        var staged = Prepare(folder);
        var temporary = staged.Folder!;
        Assert.True(Directory.Exists(temporary));

        staged.Dispose();
        staged.Dispose(); // twice is fine

        Assert.False(Directory.Exists(temporary));
        Assert.Null(staged.Folder);
        Assert.True(File.Exists(Path.Combine(folder, "x.txt")));
    }

    [Fact]
    public async Task The_temporary_folder_goes_in_a_while_and_waits_for_a_program_that_still_holds_the_zip()
    {
        var folder = Folder("Subida");
        Write(folder, "x.txt", "x");
        var staged = Prepare(folder);
        var temporary = staged.Folder!;

        // A share target uploading the ZIP: it has it open without allowing deletes.
        var upload = new FileStream(staged.Files[0], FileMode.Open, FileAccess.Read, FileShare.Read);
        staged.ReleaseSoon(TimeSpan.Zero, TimeSpan.FromMilliseconds(50), attempts: 200);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(temporary), "deleted a ZIP somebody was reading");

        upload.Dispose();
        for (var i = 0; i < 100 && Directory.Exists(temporary); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(temporary));
    }

    [Fact]
    public async Task Nothing_is_deleted_before_the_delay_is_up()
    {
        var folder = Folder("Pronto");
        Write(folder, "x.txt", "x");
        var staged = Prepare(folder);

        staged.ReleaseSoon(TimeSpan.FromSeconds(30));
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(staged.Folder));
        staged.Dispose();
    }

    [Fact]
    public void The_sweep_removes_old_leftovers_and_nothing_else()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "Mandarina")).FullName;
        string Leftover(string name, int daysOld)
        {
            var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            File.WriteAllText(Path.Combine(path, "viejo.zip"), "zip");
            Directory.SetCreationTime(path, DateTime.Now.AddDays(-daysOld));
            Directory.SetLastWriteTime(path, DateTime.Now.AddDays(-daysOld));
            return path;
        }
        var old = Leftover("share-0123456789abcdef0123456789abcdef", 3);
        var yesterdayAndABit = Leftover("share-fedcba9876543210fedcba9876543210", 2);
        var recent = Leftover("share-00000000000000000000000000000000", 0);
        var notOurs = new[]
        {
            Leftover("share-9876543210ABCDEF9876543210abcdef", 9),  // not a name this class makes (it writes lower-case hex)
            Leftover("share-zzz", 9),
            Leftover("share-0123456789abcdef0123456789abcdef0", 9),
            Leftover("panels", 9),                                    // other things the app keeps in %TEMP%\Mandarina
            Leftover("0a1b2c3d4e5f", 9),
        };
        File.WriteAllText(Path.Combine(root, "share-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), "a file, not a folder");

        Assert.Equal(2, ShareStaging.Sweep(TimeSpan.FromDays(1), root));

        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(yesterdayAndABit));
        Assert.True(Directory.Exists(recent));
        Assert.All(notOurs, path => Assert.True(Directory.Exists(path), path));
        Assert.True(File.Exists(Path.Combine(root, "share-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        Assert.Equal(0, ShareStaging.Sweep(TimeSpan.FromDays(1), root));
        Assert.Equal(0, ShareStaging.Sweep(TimeSpan.FromDays(1), Path.Combine(_root, "no hay"))); // no folder at all
    }

    [Fact]
    public void The_sweep_of_the_real_temp_folder_runs_and_leaves_what_is_recent()
    {
        var folder = Folder("Reciente");
        Write(folder, "x.txt", "x");
        using var staged = ShareStaging.Prepare([folder], cancel: TestContext.Current.CancellationToken); // in the real %TEMP%\Mandarina

        FileActions.SweepLeftovers();

        Assert.True(Directory.Exists(staged.Folder));
    }

    // ───────── The Share action ─────────

    private sealed class Texts : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key + ": " + string.Join(", ", args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    private sealed class SheetPlatform(bool opens = true) : IPlatformServices
    {
        public List<(string[] Paths, bool AllThere)> Shared { get; } = [];
        public Action? SheetClosed { get; private set; }

        public bool ShareFiles(IReadOnlyList<string> paths, nint ownerWindow, Action? closed = null)
        {
            Shared.Add(([.. paths], paths.All(File.Exists)));
            if (opens)
                SheetClosed = closed;
            return opens;
        }

        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }

    private FileActions Actions(IPlatformServices platform) =>
        new(platform, new Texts(), NullLogger<FileActions>.Instance) { ShareCleanupDelay = TimeSpan.Zero, StagingRoot = Stage };

    [Fact]
    public async Task Share_offers_the_sheet_the_files_and_a_zip_for_each_folder_and_tidies_up_when_it_closes()
    {
        var photo = Write(Folder("sueltos"), "foto.jpg", Random(2000, 4));
        var folder = Folder("Vacaciones");
        Write(folder, "a.txt", "a");
        var platform = new SheetPlatform();
        var progress = new Recorder();

        var result = await Actions(platform).ShareAsync(0, [photo, folder], progress, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Problem);
        var shared = Assert.Single(platform.Shared);
        Assert.True(shared.AllThere, "the sheet was given something that is not there");
        Assert.Equal([photo, "Vacaciones.zip"], shared.Paths.Select(path => path == photo ? path : Path.GetFileName(path)));
        Assert.Equal(1, progress.Values[^1]);
        var temporary = Path.GetDirectoryName(shared.Paths[1])!;
        Assert.True(Directory.Exists(temporary));   // the sheet is open: the ZIP has to be there

        platform.SheetClosed!();                    // the sheet closes
        for (var i = 0; i < 100 && Directory.Exists(temporary); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(temporary));
        Assert.True(File.Exists(photo));
    }

    [Fact]
    public async Task Share_of_files_alone_is_what_it_always_was()
    {
        var one = Write(Folder("sueltos"), "uno.txt", "1");
        var platform = new SheetPlatform();

        var result = await Actions(platform).ShareAsync(0, [one], null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([one], Assert.Single(platform.Shared).Paths);
        Assert.False(Directory.Exists(Stage));
    }

    [Fact]
    public async Task Share_that_the_system_will_not_open_deletes_the_zips_at_once()
    {
        var folder = Folder("Carpeta");
        Write(folder, "a.txt", "a");
        var platform = new SheetPlatform(opens: false);

        var result = await Actions(platform).ShareAsync(0, [folder], null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Problem);                // nothing to tell: the sheet just did not come up
        Assert.Single(platform.Shared);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Share_of_something_that_is_gone_does_not_open_the_sheet()
    {
        var platform = new SheetPlatform();

        var result = await Actions(platform).ShareAsync(0, [Path.Combine(_root, "ya no está")], null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(platform.Shared);
    }

    [Fact]
    public async Task Share_with_a_file_in_use_says_which_one_and_does_not_open_the_sheet()
    {
        var fine = Folder("Bien");
        Write(fine, "x.txt", "x");
        var folder = Folder("Informes");
        var locked = Write(folder, "Informe final.docx", "abierto");
        var platform = new SheetPlatform();
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await Actions(platform).ShareAsync(0, [fine, folder], null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("wheel.share.failed_inuse: Informes, Informe final.docx", result.Problem);
        Assert.Empty(platform.Shared);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Share_cancelled_while_compressing_is_just_cancelled()
    {
        var big = Folder("Grande");
        Write(big, "uno.bin", Random(3_000_000, 1));
        Write(big, "dos.bin", Random(3_000_000, 2));
        using var cancel = new CancellationTokenSource();
        var progress = new Recorder(value =>
        {
            if (value > 0.25)
                cancel.Cancel();
        });
        var platform = new SheetPlatform();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Actions(platform).ShareAsync(0, [big], progress, cancel.Token));

        Assert.Empty(platform.Shared);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void The_ring_starts_empty_when_the_action_has_something_to_wait_for_and_full_when_it_only_opens_something()
    {
        var folder = Folder("Carpeta");
        var file = Write(Folder("sueltos"), "a.txt", "a");

        Assert.Equal(0, FileActions.StartProgress("move", [file]));              // the folder picker
        Assert.Equal(0, FileActions.StartProgress("share", [file, folder]));     // compressing the folder
        Assert.Equal(1, FileActions.StartProgress("share", [file]));             // only the sheet to open
        Assert.Equal(1, FileActions.StartProgress("copy", [folder]));
        Assert.Equal(1, FileActions.StartProgress("terminal", [folder]));
    }
}
