// Mandarina: real HEIC files for the photo tests. Three sources, none of them a photo of anybody:
//  - the HEIF encoder of Windows (PowerShell + WinRT, as LocalDetector does): single pictures, an iPhone-like grid of 512 × 512
//    tiles for big ones, the orientation as an irot box and Exif/XMP from properties. Missing on a PC without the HEIF
//    extension, so tests that need it skip (TryEncode returns false);
//  - HeicBuilder, a small box writer for what that encoder never writes and phones do: irot together with an Exif orientation,
//    a thumbnail item, a depth map or an alpha plane as auxiliary pictures, a colour description in nclx, a clean-aperture crop,
//    a grid, a second top-level picture. The coded pictures inside are real HEVC (the checked-in fixture's, or x265's);
//  - Filee's own HEIF writer (Filee.Engines.Heic) with x265 through FFmpeg for the pictures whose pixels the test chooses (a flat
//    colour, to compare numbers) at 8 or 10 bits and with an ICC profile. Skips without FFmpeg.
// Every picture is the same synthetic one: a gradient with four coloured corners (red top left, green top right, blue bottom
// left, yellow bottom right), a big black F, a row of swatches and some text, so a turn, a flip or a colour shift shows.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Filee.Engines.Heic;
using ImageMagick;

namespace Filee.App.Tests;

internal enum HeicMetadata
{
    None,

    /// <summary>Camera make and model, an author and a comment (Exif and an XMP packet).</summary>
    Camera,

    /// <summary>The same, plus GPS 40°25'N 3°42'W.</summary>
    CameraAndGps,
}

internal static class HeicSamples
{
    /// <summary>The checked-in sample: 320 × 240, Windows' encoder, turned by irot (orientation 6, so it looks 240 × 320), with camera tags and GPS.</summary>
    public static string Fixture(string name = "synthetic-rotated.heic") => Path.Combine(AppContext.BaseDirectory, "Fixtures", "heic", name);

    /// <summary>The corner markers as drawn, upright, as (x, y, R, G, B) in the order top left, top right, bottom left, bottom right.</summary>
    public static readonly (double X, double Y, byte R, byte G, byte B)[] Corners =
    [
        (0.04, 0.04, 255, 0, 0),
        (0.96, 0.04, 0, 255, 0),
        (0.04, 0.96, 0, 0, 255),
        (0.96, 0.96, 255, 255, 0),
    ];

    // ───────── Windows' HEIF encoder ─────────

    private static readonly Lazy<bool> EncoderWorks = new(() =>
    {
        var probe = Path.Combine(Path.GetTempPath(), $"mandarina-heic-probe-{Guid.NewGuid():N}.heic");
        try
        {
            return Encode(probe, 64, 48, 0, HeicMetadata.None, 0.8, false, out _);
        }
        finally
        {
            TryDelete(probe);
        }
    });

    /// <summary>True when this PC can write HEIC (Windows' "HEIF Image Extensions" are installed).</summary>
    public static bool EncoderAvailable => OperatingSystem.IsWindows() && EncoderWorks.Value;

    /// <summary>
    /// Writes the synthetic picture to <paramref name="path"/> with Windows' encoder. <paramref name="orientation"/> is the Exif
    /// number (1 upright, 6 and 8 a quarter turn): Windows stores it as an irot box and applies nothing to the pixels, so the
    /// picture looks turned (<see cref="Clockwise"/>, <see cref="CounterClockwise"/>). With <paramref name="lookUpright"/> the
    /// pixels are stored turned the other way first, as a phone stores a portrait, so that it looks upright (width × height as
    /// asked) once the orientation is applied. False when there is no encoder.
    /// </summary>
    public static bool TryEncode(string path, int width, int height, int orientation = 0, HeicMetadata metadata = HeicMetadata.None, double quality = 0.85,
        bool lookUpright = false) =>
        EncoderAvailable && Encode(path, width, height, orientation, metadata, quality, lookUpright, out _);

    private static bool Encode(string path, int width, int height, int orientation, HeicMetadata metadata, double quality, bool lookUpright, out string output)
    {
        output = "";
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-InputFormat", "None", "-EncodedCommand", EncodedScript })
            info.ArgumentList.Add(argument);
        info.Environment["MANDARINA_HEIC_OUT"] = path;
        info.Environment["MANDARINA_HEIC_W"] = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["MANDARINA_HEIC_H"] = height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["MANDARINA_HEIC_ORI"] = orientation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["MANDARINA_HEIC_META"] = ((int)metadata).ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["MANDARINA_HEIC_Q"] = quality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["MANDARINA_HEIC_UPRIGHT"] = lookUpright ? "1" : "0";
        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return false;
            var text = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            output = text.GetAwaiter().GetResult();
            return process.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output = ex.Message;
            return false;
        }
    }

    private static string EncodedScript { get; } = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    /// <summary>Input: MANDARINA_HEIC_OUT, _W, _H, _ORI (0 = leave it), _META (0, 1, 2) and _Q (0..1). Output: "OK" and the size.</summary>
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
            $out = $env:MANDARINA_HEIC_OUT
            $w = [int]$env:MANDARINA_HEIC_W
            $h = [int]$env:MANDARINA_HEIC_H
            $ori = [int]$env:MANDARINA_HEIC_ORI
            $meta = [int]$env:MANDARINA_HEIC_META
            $quality = [single][double]::Parse($env:MANDARINA_HEIC_Q, [Globalization.CultureInfo]::InvariantCulture)
            $upright = [int]$env:MANDARINA_HEIC_UPRIGHT

            Add-Type -AssemblyName System.Runtime.WindowsRuntime, System.Drawing
            $null = [Windows.Graphics.Imaging.BitmapEncoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            $null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            $null = [Windows.Graphics.Imaging.BitmapPropertySet, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
            $null = [Windows.Graphics.Imaging.BitmapTypedValue, Windows.Graphics.Imaging, ContentType = WindowsRuntime]

            $asTaskOp = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
            } | Select-Object -First 1
            $asTaskAct = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
            } | Select-Object -First 1
            function Await($operation, [Type]$resultType) {
                $task = $asTaskOp.MakeGenericMethod($resultType).Invoke($null, @($operation))
                if (-not $task.Wait(120000)) { throw 'timeout' }
                $task.Result
            }
            function AwaitAction($action) {
                $task = $asTaskAct.Invoke($null, @($action))
                if (-not $task.Wait(120000)) { throw 'timeout' }
            }
            # A BitmapPropertySet implements IDictionary explicitly, which PowerShell does not call: go through the interface.
            $dictAdd = [System.Collections.Generic.IDictionary[string, Windows.Graphics.Imaging.BitmapTypedValue]].GetMethod('Add')
            function Add-Prop($set, [string]$name, $value, $type) {
                $null = $dictAdd.Invoke($set, @($name, [Windows.Graphics.Imaging.BitmapTypedValue]::new($value, $type)))
            }

            # The synthetic picture.
            $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'
            $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
            $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(20, 40, 120), [System.Drawing.Color]::FromArgb(255, 160, 40), 35.0)
            $g.FillRectangle($grad, $rect)
            $m = [Math]::Min($w, $h)
            $q = [int]($m * 0.14)
            $g.FillRectangle([System.Drawing.Brushes]::Red, 0, 0, $q, $q)
            $g.FillRectangle([System.Drawing.Brushes]::Lime, $w - $q, 0, $q, $q)
            $g.FillRectangle([System.Drawing.Brushes]::Blue, 0, $h - $q, $q, $q)
            $g.FillRectangle([System.Drawing.Brushes]::Yellow, $w - $q, $h - $q, $q, $q)
            $cols = @('Red', 'Lime', 'Blue', 'Cyan', 'Magenta', 'Yellow', 'Gray', 'White', 'Black')
            $sw = [int]($m * 0.07)
            $x0 = [int](($w - $sw * $cols.Count) / 2)
            for ($i = 0; $i -lt $cols.Count; $i++) {
                $b = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromName($cols[$i]))
                $g.FillRectangle($b, $x0 + $i * $sw, $h - $q - $sw, $sw, $sw)
            }
            $g.FillEllipse([System.Drawing.Brushes]::White, [int]($w * 0.55), [int]($h * 0.18), [int]($m * 0.3), [int]($m * 0.3))
            $pts = [System.Drawing.Point[]]@((New-Object System.Drawing.Point([int]($w * 0.15), [int]($h * 0.75))), (New-Object System.Drawing.Point([int]($w * 0.3), [int]($h * 0.45))), (New-Object System.Drawing.Point([int]($w * 0.45), [int]($h * 0.75))))
            $g.FillPolygon([System.Drawing.Brushes]::DarkGreen, $pts)
            $font = New-Object System.Drawing.Font('Arial', [single]($m * 0.4), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $g.DrawString('F', $font, [System.Drawing.Brushes]::Black, [single]($w * 0.30), [single]($h * 0.05))
            $font2 = New-Object System.Drawing.Font('Arial', [single]($m * 0.06), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $g.DrawString('MANDARINA HEIC TEST', $font2, [System.Drawing.Brushes]::White, [single]($w * 0.2), [single]($h * 0.55))
            $g.Dispose()
            # A portrait the way a phone stores it: sideways, with the orientation that turns it upright.
            if ($upright -eq 1) {
                if ($ori -eq 6) { $bmp.RotateFlip([System.Drawing.RotateFlipType]::Rotate270FlipNone) }
                elseif ($ori -eq 8) { $bmp.RotateFlip([System.Drawing.RotateFlipType]::Rotate90FlipNone) }
                elseif ($ori -eq 3) { $bmp.RotateFlip([System.Drawing.RotateFlipType]::Rotate180FlipNone) }
            }

            # Through a PNG in memory into a SoftwareBitmap, then into the HEIF encoder.
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            $ms.Position = 0
            $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync([System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($ms))) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Await ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore)) ([Windows.Graphics.Imaging.SoftwareBitmap])

            $file = [System.IO.File]::Open($out, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite)
            try {
                $options = New-Object Windows.Graphics.Imaging.BitmapPropertySet
                Add-Prop $options 'ImageQuality' $quality ([Windows.Foundation.PropertyType]::Single)
                $encoder = Await ([Windows.Graphics.Imaging.BitmapEncoder]::CreateAsync([Windows.Graphics.Imaging.BitmapEncoder]::HeifEncoderId, [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($file), $options)) ([Windows.Graphics.Imaging.BitmapEncoder])
                $encoder.SetSoftwareBitmap($bitmap)
                $props = New-Object Windows.Graphics.Imaging.BitmapPropertySet
                if ($ori -gt 0) { Add-Prop $props 'System.Photo.Orientation' ([uint16]$ori) ([Windows.Foundation.PropertyType]::UInt16) }
                if ($meta -ge 1) {
                    Add-Prop $props 'System.Photo.CameraManufacturer' 'Mandarina Test' ([Windows.Foundation.PropertyType]::String)
                    Add-Prop $props 'System.Photo.CameraModel' 'Synthetic Cam 1' ([Windows.Foundation.PropertyType]::String)
                    Add-Prop $props 'System.Author' ([string[]]@('Autor Test')) ([Windows.Foundation.PropertyType]::StringArray)
                    Add-Prop $props 'System.Comment' 'synthetic sample' ([Windows.Foundation.PropertyType]::String)
                }
                if ($meta -ge 2) {
                    Add-Prop $props 'System.GPS.LatitudeRef' 'N' ([Windows.Foundation.PropertyType]::String)
                    Add-Prop $props 'System.GPS.LatitudeNumerator' ([uint32[]]@(40, 25, 0)) ([Windows.Foundation.PropertyType]::UInt32Array)
                    Add-Prop $props 'System.GPS.LatitudeDenominator' ([uint32[]]@(1, 1, 1)) ([Windows.Foundation.PropertyType]::UInt32Array)
                    Add-Prop $props 'System.GPS.LongitudeRef' 'W' ([Windows.Foundation.PropertyType]::String)
                    Add-Prop $props 'System.GPS.LongitudeNumerator' ([uint32[]]@(3, 42, 0)) ([Windows.Foundation.PropertyType]::UInt32Array)
                    Add-Prop $props 'System.GPS.LongitudeDenominator' ([uint32[]]@(1, 1, 1)) ([Windows.Foundation.PropertyType]::UInt32Array)
                }
                if ($props.Count -gt 0) { AwaitAction ($encoder.BitmapProperties.SetPropertiesAsync($props)) }
                AwaitAction ($encoder.FlushAsync())
            } finally {
                $file.Dispose()
            }
            [Console]::Out.WriteLine('OK ' + (Get-Item $out).Length)
            exit 0
        } catch {
            [Console]::Out.WriteLine('FAIL ' + $_.Exception.Message)
            exit 1
        }
        """;

    // ───────── What a decoded picture should look like ─────────

    /// <summary>
    /// The four corner markers of <paramref name="image"/> in the order top left, top right, bottom left, bottom right, as the
    /// name of the colour each is closest to ("red", "green", "blue", "yellow"). HEVC and 4:2:0 move a channel by a few tens at the edge
    /// of a marker, never to another colour.
    /// </summary>
    public static string[] CornerColours(IMagickImage<byte> image)
    {
        using var pixels = image.GetPixels();
        var result = new string[Corners.Length];
        for (var i = 0; i < Corners.Length; i++)
        {
            var x = Math.Clamp((int)(Corners[i].X * image.Width), 0, (int)image.Width - 1);
            var y = Math.Clamp((int)(Corners[i].Y * image.Height), 0, (int)image.Height - 1);
            result[i] = NameOf(pixels.GetPixel(x, y).ToArray()!);
        }
        return result;
    }

    /// <summary>The corner markers of the synthetic picture seen upright, as <see cref="CornerColours"/> returns them.</summary>
    public static readonly string[] Upright = ["red", "green", "blue", "yellow"];

    /// <summary>The same picture after a quarter turn clockwise (Exif orientation 6): the left side became the top.</summary>
    public static readonly string[] Clockwise = ["blue", "red", "yellow", "green"];

    /// <summary>The same picture after a quarter turn counter-clockwise (Exif orientation 8): the right side became the top.</summary>
    public static readonly string[] CounterClockwise = ["green", "yellow", "red", "blue"];

    /// <summary>"red", "green", "blue" or "yellow" for the marker colour <paramref name="pixel"/> (R, G, B) is closest to.</summary>
    public static string NameOf(byte[] pixel)
    {
        var (r, g, b) = (pixel[0], pixel[1], pixel[2]);
        string[] names = ["red", "green", "blue", "yellow"];
        byte[][] references = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 0]];
        var best = 0;
        var distance = int.MaxValue;
        for (var i = 0; i < references.Length; i++)
        {
            var d = Math.Abs(references[i][0] - r) + Math.Abs(references[i][1] - g) + Math.Abs(references[i][2] - b);
            if (d < distance)
                (best, distance) = (i, d);
        }
        return distance <= 150 ? names[best] : $"other({r},{g},{b})";
    }

    // ───────── Reading what a HEIC holds ─────────

    /// <summary>A coded picture taken out of a HEIC: what a HEIF writer needs to store it again.</summary>
    public sealed record Coded(int Width, int Height, byte[] HvcC, byte[] Sample, int BitDepth);

    /// <summary>The primary picture of a one-picture HEIC (Windows' encoder writes one below about 1 MP), undone.</summary>
    public static Coded ReadCoded(byte[] heic)
    {
        var meta = Children(heic, 0, heic.Length).First(box => box.Type == "meta");
        var inside = Children(heic, meta.Body + 4, meta.End).ToList();
        var primary = BinaryPrimitives.ReadUInt16BigEndian(heic.AsSpan(inside.First(box => box.Type == "pitm").Body + 4));
        var properties = Children(heic, inside.First(box => box.Type == "iprp").Body, inside.First(box => box.Type == "iprp").End)
            .First(box => box.Type == "ipco");
        var all = Children(heic, properties.Body, properties.End).ToList();
        var hvcC = all.First(box => box.Type == "hvcC");
        var ispe = all.First(box => box.Type == "ispe");
        var pixi = all.FirstOrDefault(box => box.Type == "pixi");

        // iloc, version 0 or 1: absolute offsets, no base offset in the files this reads.
        var iloc = inside.First(box => box.Type == "iloc");
        var version = heic[iloc.Body];
        var sizes = heic[iloc.Body + 4];
        var baseSizes = heic[iloc.Body + 5];
        int offsetSize = sizes >> 4, lengthSize = sizes & 15, baseSize = baseSizes >> 4, indexSize = version is 1 or 2 ? baseSizes & 15 : 0;
        var at = iloc.Body + 6;
        var count = version < 2 ? BinaryPrimitives.ReadUInt16BigEndian(heic.AsSpan(at)) : (int)BinaryPrimitives.ReadUInt32BigEndian(heic.AsSpan(at));
        at += version < 2 ? 2 : 4;
        long Number(int size)
        {
            long value = 0;
            for (var i = 0; i < size; i++)
                value = (value << 8) | heic[at++];
            return value;
        }
        for (var i = 0; i < count; i++)
        {
            var id = version < 2 ? BinaryPrimitives.ReadUInt16BigEndian(heic.AsSpan(at)) : (int)BinaryPrimitives.ReadUInt32BigEndian(heic.AsSpan(at));
            at += version < 2 ? 2 : 4;
            if (version is 1 or 2)
                at += 2; // construction method
            at += 2; // data reference index
            var baseOffset = Number(baseSize);
            var extents = BinaryPrimitives.ReadUInt16BigEndian(heic.AsSpan(at));
            at += 2;
            var sample = new List<byte>();
            for (var e = 0; e < extents; e++)
            {
                if (indexSize > 0)
                    Number(indexSize);
                var offset = baseOffset + Number(offsetSize);
                var length = Number(lengthSize);
                if (id == primary)
                    sample.AddRange(heic.AsSpan((int)offset, (int)length));
            }
            if (id == primary)
            {
                var depth = pixi.Type is null ? 8 : heic[pixi.Body + 5];
                return new Coded((int)BinaryPrimitives.ReadUInt32BigEndian(heic.AsSpan(ispe.Body + 4)), (int)BinaryPrimitives.ReadUInt32BigEndian(heic.AsSpan(ispe.Body + 8)),
                    heic[hvcC.Body..hvcC.End], [.. sample], depth);
            }
        }
        throw new InvalidDataException("The primary picture is not in the file.");
    }

    private readonly record struct Box(string Type, int Body, int End);

    private static IEnumerable<Box> Children(byte[] data, int from, int to)
    {
        var at = from;
        while (at + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            var type = Encoding.Latin1.GetString(data, at + 4, 4);
            var header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = to - at;
            }
            var end = (int)Math.Min(to, at + size);
            yield return new Box(type, at + header, end);
            if (size <= 0)
                yield break;
            at = end;
        }
    }

    // ───────── Display P3 ─────────

    /// <summary>
    /// A matrix/TRC ICC profile (v2) for Display P3: the colorants adapted to D50, the sRGB curve as a table. The tests only need
    /// a real wide-gamut profile whose bytes they can find in a file again.
    /// </summary>
    public static byte[] DisplayP3()
    {
        static byte[] Fixed(double value) => BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((int)Math.Round(value * 65536)));
        static byte[] Xyz(double x, double y, double z) => [.. "XYZ \0\0\0\0"u8, .. Fixed(x), .. Fixed(y), .. Fixed(z)];
        static byte[] Text(string text) => [.. "text\0\0\0\0"u8, .. Encoding.ASCII.GetBytes(text), 0];

        var description = Encoding.ASCII.GetBytes("Display P3 (test)\0");
        byte[] desc = [.. "desc\0\0\0\0"u8, .. U32(description.Length), .. description, .. new byte[4 + 4 + 2 + 1 + 67]];
        var curve = new List<byte>([.. "curv\0\0\0\0"u8, .. U32(1024)]);
        for (var i = 0; i < 1024; i++)
        {
            var v = i / 1023.0;
            var linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            curve.AddRange(U16((int)Math.Round(linear * 65535)));
        }
        var curveBytes = curve.ToArray();

        // (signature, data); the three curves are one block, as profiles do.
        var blocks = new List<byte[]> { desc, Text("Generated by the Mandarina tests"), Xyz(0.9642, 1.0, 0.8249), Xyz(0.5151, 0.2412, -0.0011), Xyz(0.2920, 0.6922, 0.0419), Xyz(0.1571, 0.0666, 0.7841), curveBytes };
        (string Signature, int Block)[] tags = [("desc", 0), ("cprt", 1), ("wtpt", 2), ("rXYZ", 3), ("gXYZ", 4), ("bXYZ", 5), ("rTRC", 6), ("gTRC", 6), ("bTRC", 6)];
        var tableSize = 4 + tags.Length * 12;
        var offsets = new List<int>();
        var at = 128 + tableSize;
        foreach (var block in blocks)
        {
            offsets.Add(at);
            at += (block.Length + 3) & ~3;
        }

        using var profile = new MemoryStream();
        var header = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)at);
        header[8] = 2; header[9] = 0x10;                                       // version 2.1.0
        "mntr"u8.CopyTo(header.AsSpan(12));
        "RGB "u8.CopyTo(header.AsSpan(16));
        "XYZ "u8.CopyTo(header.AsSpan(20));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), 2024);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(28), 1);
        "acsp"u8.CopyTo(header.AsSpan(36));
        "APPL"u8.CopyTo(header.AsSpan(40));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(68), (int)Math.Round(0.9642 * 65536));   // the D50 illuminant
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(72), 65536);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(76), (int)Math.Round(0.8249 * 65536));
        profile.Write(header);
        profile.Write(U32(tags.Length));
        foreach (var (signature, block) in tags)
        {
            profile.Write(Encoding.ASCII.GetBytes(signature));
            profile.Write(U32(offsets[block]));
            profile.Write(U32(blocks[block].Length));
        }
        for (var i = 0; i < blocks.Count; i++)
        {
            profile.Write(blocks[i]);
            profile.Write(new byte[((blocks[i].Length + 3) & ~3) - blocks[i].Length]);
        }
        return profile.ToArray();
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(long value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    // ───────── A Mandarina-made Exif ─────────

    /// <summary>An Exif block (TIFF) like a phone writes: make, model, the orientation and a date taken.</summary>
    public static byte[] PhoneExif(ushort orientation, string make = "Mandarina Phone")
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, make);
        exif.SetValue(ExifTag.Model, "Synthetic 15");
        exif.SetValue(ExifTag.Orientation, orientation);
        exif.SetValue(ExifTag.DateTimeOriginal, "2024:05:17 10:20:30");
        return exif.ToByteArray()!;
    }

    // ───────── 8 and 10 bits, and profiles, through Filee's own writer ─────────

    /// <summary>
    /// A HEIC whose single picture is HEVC made by x265 through FFmpeg from <paramref name="png"/> at 8 or 10 bits (Samsung and
    /// Sony cameras write 10 bits in HDR mode), with an ICC profile and Exif if given. Full-range BT.601 like Filee's own writer,
    /// which HeifFile declares in its nclx, so flat colours come back as they went in. Null when there is no FFmpeg with x265.
    /// </summary>
    public static async Task<byte[]?> FromPngAsync(string png, int bitDepth = 8, byte[]? icc = null, byte[]? exif = null)
    {
        if (Filee.App.Mandarina.Panels.MediaTools.Locate() is not { } media)
            return null;
        var stream = Path.Combine(Path.GetTempPath(), $"mandarina-hevc-{Guid.NewGuid():N}.hevc");
        try
        {
            await media.RunAsync(
            [
                "-i", png, "-frames:v", "1",
                "-vf", $"scale=out_color_matrix=bt601:out_range=pc,format={(bitDepth == 10 ? "yuv420p10le" : "yuv420p")}",
                "-c:v", "libx265", "-preset", "ultrafast", "-crf", "10", "-force_key_frames", "expr:1", "-forced-idr", "1",
                "-x265-params", "bframes=0:open-gop=0:info=0:range=full:colormatrix=smpte170m:log-level=error",
                "-f", "hevc", stream,
            ]);
            using var image = new MagickImage(png);
            var hevc = HevcStream.Parse(await File.ReadAllBytesAsync(stream));
            return HeifFile.Write(new HeifImage((int)image.Width, (int)image.Height, hevc, (int)image.Width, (int)image.Height, 1, 1, icc, exif));
        }
        catch (Filee.App.Mandarina.Panels.PanelException)
        {
            return null; // this FFmpeg has no x265
        }
        finally
        {
            TryDelete(stream);
        }
    }

    // ───────── Samples made once per test run ─────────

    private static readonly string CacheFolder = Path.Combine(Path.GetTempPath(), $"mandarina-heic-samples-{Environment.ProcessId}");

    private static readonly Dictionary<string, string?> Cache = [];

    /// <summary>
    /// A sample file made by <paramref name="make"/> (given the path to write) the first time <paramref name="key"/> is asked for
    /// and reused by every test after: Windows' encoder takes a couple of seconds per picture. Null when it could not be made.
    /// Never write to the file; copy it. The folder goes away with the test process.
    /// </summary>
    public static string? Cached(string key, Func<string, bool> make)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var known))
                return known;
            Directory.CreateDirectory(CacheFolder);
            if (Cache.Count == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteFolder(CacheFolder);
            var path = Path.Combine(CacheFolder, key + ".heic");
            return Cache[key] = make(path) && File.Exists(path) ? path : null;
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
