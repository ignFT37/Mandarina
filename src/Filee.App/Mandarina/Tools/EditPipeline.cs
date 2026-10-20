// Mandarina: the picture work of the EDIT tool, with no interface in it. EditSettings holds every control of the
// panel and EditPipeline.Apply paints them onto a MagickImage. The panel runs this very same Apply on the reduced preview
// copy and on the full photo, so what you see is what is saved.
//
// Magick.NET is single-threaded in this build (one Gaussian blur of a 1200 px photo already takes ~300 ms), so the pixels
// are read once, edited by ONE parallel pass (the three neighbourhood filters, the tone curve, colour and effects) and
// written back once. A control left at its factory value is not even looked at, so factory settings give back the very
// same pixels, and so does any control at zero when the others are moved.

using System.Runtime.CompilerServices;
using ImageMagick;

namespace Filee.App.Mandarina.Tools;

/// <summary>Every control of the EDIT panel. The default value of each one changes nothing.</summary>
public sealed record EditSettings
{
    /// <summary>The settings of an untouched photo.</summary>
    public static EditSettings Default { get; } = new();

    // ───────── Light ─────────

    /// <summary>Exposure in stops (-2..2): +1 doubles the light, -1 halves it.</summary>
    public double Exposure { get; init; }

    /// <summary>-100..100: lifts (or lowers) the midtones; black and white stay where they are.</summary>
    public double Brightness { get; init; }

    /// <summary>-100..100: an S curve around the middle grey (negative flattens it).</summary>
    public double Contrast { get; init; }

    /// <summary>-100..100: brightens (+) or darkens (-) the bright tones, not the dark ones.</summary>
    public double Highlights { get; init; }

    /// <summary>-100..100: brightens (+) or darkens (-) the dark tones, not the bright ones.</summary>
    public double Shadows { get; init; }

    /// <summary>-100..100: the very brightest tones (+ brighter, - duller).</summary>
    public double Whites { get; init; }

    /// <summary>-100..100: the very darkest tones (+ lifts them, - crushes them to black).</summary>
    public double Blacks { get; init; }

    /// <summary>0.3..3: above 1 brightens the midtones and below 1 darkens them (Magick.NET's convention).</summary>
    public double Gamma { get; init; } = 1;

    // ───────── Colour ─────────

    /// <summary>-100..100: -100 is grey, +100 doubles the colour.</summary>
    public double Saturation { get; init; }

    /// <summary>-100..100: like saturation, but it acts on the dull colours first and leaves the vivid ones alone.</summary>
    public double Vibrance { get; init; }

    /// <summary>-100..100: negative is cooler (bluer), positive warmer (redder).</summary>
    public double Temperature { get; init; }

    /// <summary>-100..100: negative is greener, positive more magenta.</summary>
    public double Tint { get; init; }

    /// <summary>-180..180 degrees around the colour wheel (red → yellow → green for positive values).</summary>
    public double Hue { get; init; }

    // ───────── Detail ─────────

    /// <summary>0..100: unsharp mask on the brightness (no colour fringes).</summary>
    public double Sharpness { get; init; }

    /// <summary>-100..100: local contrast in the midtones (negative softens).</summary>
    public double Clarity { get; init; }

    /// <summary>0..100: smooths the flat areas and keeps the edges.</summary>
    public double NoiseReduction { get; init; }

    // ───────── Effects ─────────

    public bool BlackAndWhite { get; init; }

    /// <summary>0..100: how far the colours move to a sepia toning.</summary>
    public double Sepia { get; init; }

    /// <summary>-100..100: positive darkens the edges of the frame, negative lightens them.</summary>
    public double Vignette { get; init; }

    /// <summary>0..100: film grain.</summary>
    public double Grain { get; init; }

    // ───────── Automatic ─────────

    /// <summary>Stretches the levels of the photo and corrects its gamma before anything else is applied.</summary>
    public bool Auto { get; init; }

    /// <summary>True when nothing is changed.</summary>
    public bool IsDefault => this == Default;
}

public static class EditPipeline
{
    /// <summary>
    /// The longest side the sizes of the filters are tuned for: the preview's. A bigger photo gets proportionally bigger
    /// radii (and grain), so its saved result looks like the preview that was on screen.
    /// </summary>
    public const int ReferenceSide = 1200;

    private const int LutSize = 4096;
    private const float Inv255 = 1f / 255f;

    // Luma weights of an encoded colour: the same as the CSS/SVG saturate and hue-rotate matrices use.
    private const float LumaR = 0.213f;
    private const float LumaG = 0.715f;
    private const float LumaB = 0.072f;

    /// <summary>
    /// Applies <paramref name="settings"/> to <paramref name="image"/> in place. Alpha, profiles and metadata are kept.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="cancel"/> is cancelled.
    /// </summary>
    public static void Apply(MagickImage image, EditSettings settings, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.IsDefault)
            return;
        cancel.ThrowIfCancellationRequested();

        EnsureRgb(image);
        var width = (int)image.Width;
        var height = (int)image.Height;
        var channels = image.HasAlpha ? 4 : 3;
        if ((long)width * height * channels > Array.MaxLength)
            throw new NotSupportedException("This picture is too large to edit.");
        var mapping = channels == 4 ? PixelMapping.RGBA : PixelMapping.RGB;
        byte[]? pixels;
        using (var collection = image.GetPixels())
            pixels = collection.ToByteArray(mapping);
        if (pixels is null)
            throw new InvalidOperationException("The picture has no pixels.");

        var pass = new Pass(pixels, width, height, channels, settings, cancel);
        if (!pass.IsEmpty)
        {
            pass.Run(cancel);
            image.ImportPixels(pixels, new PixelImportSettings((uint)width, (uint)height, StorageType.Char, mapping));
        }
    }

    /// <summary>
    /// Brings gray, CMYK and the like to sRGB so the pass can work on red, green and blue. Pictures with an embedded CMYK
    /// profile go through it; the others use Magick's plain conversion.
    /// </summary>
    internal static void EnsureRgb(MagickImage image)
    {
        if (image.ColorSpace == ColorSpace.CMYK && image.HasProfile("icc"))
            image.TransformColorSpace(ColorProfiles.SRGB);
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Undefined))
            image.ColorSpace = ColorSpace.sRGB;
    }

    private static double Limit(double value, double min, double max) => double.IsNaN(value) ? 0 : Math.Clamp(value, min, max);

    private static float Smooth(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lookup(float[] lut, float value) => lut[(int)(Math.Clamp(value, 0f, 1f) * (LutSize - 1) + 0.5f)];

    private static byte ToByte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);

    // ───────── The pass ─────────

    /// <summary>One run over the pixels: everything it needs, worked out once from the settings.</summary>
    private sealed class Pass
    {
        private readonly byte[] _px;
        private readonly int _width;
        private readonly int _height;
        private readonly int _channels;

        // Detail: filters that look at the neighbours. Their blurred references are measured on the untouched pixels.
        private readonly Field? _fine;      // RGB, for the noise reduction
        private readonly Field? _sharp;     // brightness, a pixel or so
        private readonly Field? _wide;      // brightness, a few percent of the picture
        private readonly float _denoise;
        private readonly float _denoiseLow;
        private readonly float _denoiseHigh;
        private readonly float _sharpen;
        private readonly float _clarity;

        // Tone: one curve per channel (exposure, white balance, light controls, contrast).
        private readonly float[]? _lutR;
        private readonly float[]? _lutG;
        private readonly float[]? _lutB;

        // Colour.
        private readonly bool _matrix;
        private readonly float _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;
        private readonly float _vibrance;
        private readonly bool _mono;
        private readonly float _sepia;

        // Effects.
        private readonly float _vignette;
        private readonly float[]? _dx2;
        private readonly float _grain;
        private readonly double _grainSize;
        private readonly int[]? _grainCell;      // only on photos bigger than the preview: the lattice cell of each column
        private readonly float[]? _grainFade;

        public Pass(byte[] pixels, int width, int height, int channels, EditSettings settings, CancellationToken cancel)
        {
            _px = pixels;
            _width = width;
            _height = height;
            _channels = channels;
            var scale = Math.Max(1.0, Math.Max(width, height) / (double)ReferenceSide);

            var exposure = Limit(settings.Exposure, -2, 2);
            var brightness = Limit(settings.Brightness, -100, 100) / 100;
            var contrast = Limit(settings.Contrast, -100, 100) / 100;
            var highlights = Limit(settings.Highlights, -100, 100) / 100;
            var shadows = Limit(settings.Shadows, -100, 100) / 100;
            var whites = Limit(settings.Whites, -100, 100) / 100;
            var blacks = Limit(settings.Blacks, -100, 100) / 100;
            var gamma = double.IsNaN(settings.Gamma) ? 1 : Math.Clamp(settings.Gamma, 0.3, 3);
            var saturation = Limit(settings.Saturation, -100, 100) / 100;
            var vibrance = Limit(settings.Vibrance, -100, 100) / 100;
            var temperature = Limit(settings.Temperature, -100, 100) / 100;
            var tint = Limit(settings.Tint, -100, 100) / 100;
            var hue = Limit(settings.Hue, -180, 180);
            var sharpness = Limit(settings.Sharpness, 0, 100) / 100;
            var clarity = Limit(settings.Clarity, -100, 100) / 100;
            var noise = Limit(settings.NoiseReduction, 0, 100) / 100;
            var sepia = Limit(settings.Sepia, 0, 100) / 100;
            var vignette = Limit(settings.Vignette, -100, 100) / 100;
            var grain = Limit(settings.Grain, 0, 100) / 100;

            cancel.ThrowIfCancellationRequested();

            // Detail. The sizes are the ones of a 1200 px preview times the photo's scale.
            if (noise > 0)
            {
                _denoise = (float)noise;
                _denoiseLow = (float)(0.012 + 0.02 * noise);
                _denoiseHigh = (float)(0.05 + 0.15 * noise);
                _fine = Field.Create(pixels, width, height, channels, luma: false, (1.0 + noise) * scale, cancel);
            }
            if (sharpness > 0)
            {
                _sharpen = (float)(1.5 * sharpness);
                _sharp = Field.Create(pixels, width, height, channels, luma: true, 1.0 * scale, cancel);
            }
            if (clarity != 0)
            {
                // Softening is gentler than sharpening: all of the local contrast would be a plain blur.
                _clarity = (float)(clarity > 0 ? 1.2 * clarity : 0.6 * clarity);
                _wide = Field.Create(pixels, width, height, channels, luma: true, 24.0 * scale, cancel);
            }

            // Tone.
            var auto = settings.Auto ? MeasureAutoLevels(pixels, width, height, channels) : null;
            var tone = new Tone(exposure, temperature, tint, brightness, contrast, highlights, shadows, whites, blacks, gamma, auto);
            if (!tone.IsIdentity)
                (_lutR, _lutG, _lutB) = tone.Build();

            // Colour. Hue then saturation as one matrix: with saturation at -100 the three rows are the very same, so the
            // three channels come out equal.
            _mono = settings.BlackAndWhite;
            if (!_mono)
            {
                if (saturation != 0 || hue != 0)
                {
                    _matrix = true;
                    BuildMatrix(1 + saturation, hue, out _m00, out _m01, out _m02, out _m10, out _m11, out _m12, out _m20, out _m21, out _m22);
                }
                _vibrance = (float)vibrance;
            }
            _sepia = (float)sepia;

            // Effects.
            _vignette = (float)vignette;
            if (vignette != 0)
            {
                _dx2 = new float[width];
                for (var x = 0; x < width; x++)
                {
                    var dx = (x + 0.5f - width * 0.5f) / (width * 0.5f);
                    _dx2[x] = dx * dx;
                }
            }
            _grain = (float)grain;
            _grainSize = scale;
            if (grain > 0 && scale > 1.0)
            {
                // The grain of a big photo is as many pixels wide as the photo is bigger than the preview.
                _grainCell = new int[width];
                _grainFade = new float[width];
                for (var x = 0; x < width; x++)
                {
                    var u = x / scale;
                    _grainCell[x] = (int)Math.Floor(u);
                    var t = (float)(u - Math.Floor(u));
                    _grainFade[x] = t * t * (3f - 2f * t);
                }
            }
        }

        public bool IsEmpty =>
            _fine is null && _sharp is null && _wide is null && _lutR is null && !_matrix && _vibrance == 0 && !_mono && _sepia == 0
            && _vignette == 0 && _grain == 0;

        public void Run(CancellationToken cancel)
        {
            const int RowsPerChunk = 16;
            var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) };
            Parallel.For(0, (_height + RowsPerChunk - 1) / RowsPerChunk, options, chunk =>
                Rows(chunk * RowsPerChunk, Math.Min(_height, (chunk + 1) * RowsPerChunk)));
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Rows(int firstRow, int endRow)
        {
            var px = _px;
            var width = _width;
            var channels = _channels;

            var grainCell = _grainCell;
            var grainFade = _grainFade;

            for (var y = firstRow; y < endRow; y++)
            {
                var fineRow = _fine?.RowAt(y) ?? default;
                var sharpRow = _sharp?.RowAt(y) ?? default;
                var wideRow = _wide?.RowAt(y) ?? default;
                var dy2 = 0f;
                if (_vignette != 0)
                {
                    var dy = (y + 0.5f - _height * 0.5f) / (_height * 0.5f);
                    dy2 = dy * dy;
                }
                var gridY = 0;
                var fadeY = 0f;
                if (grainCell is not null)
                {
                    var v = y / _grainSize;
                    gridY = (int)Math.Floor(v);
                    var t = (float)(v - Math.Floor(v));
                    fadeY = t * t * (3f - 2f * t);
                }

                var i = y * width * channels;
                for (var x = 0; x < width; x++, i += channels)
                {
                    var r = px[i] * Inv255;
                    var g = px[i + 1] * Inv255;
                    var b = px[i + 2] * Inv255;

                    // ── Detail ──
                    if (_fine is not null)
                    {
                        _fine.Sample3(fineRow, x, out var br, out var bg, out var bb);
                        var difference = Math.Max(Math.Abs(r - br), Math.Max(Math.Abs(g - bg), Math.Abs(b - bb)));
                        // Flat areas (small differences from their surroundings) become their surroundings; edges stay.
                        var weight = _denoise * (1f - Smooth(_denoiseLow, _denoiseHigh, difference));
                        r += (br - r) * weight;
                        g += (bg - g) * weight;
                        b += (bb - b) * weight;
                    }
                    if (_wide is not null || _sharp is not null)
                    {
                        var luma = LumaR * r + LumaG * g + LumaB * b;
                        var delta = 0f;
                        if (_wide is not null)
                            delta += _clarity * (4f * luma * (1f - luma)) * (luma - _wide.Sample1(wideRow, x));
                        if (_sharp is not null)
                            delta += _sharpen * (luma - _sharp.Sample1(sharpRow, x));
                        r += delta;
                        g += delta;
                        b += delta;
                    }

                    // ── Tone ──
                    if (_lutR is not null)
                    {
                        r = Lookup(_lutR, r);
                        g = Lookup(_lutG!, g);
                        b = Lookup(_lutB!, b);
                    }

                    // ── Colour ──
                    if (_matrix)
                    {
                        var mr = _m00 * r + _m01 * g + _m02 * b;
                        var mg = _m10 * r + _m11 * g + _m12 * b;
                        var mb = _m20 * r + _m21 * g + _m22 * b;
                        r = Math.Clamp(mr, 0f, 1f);
                        g = Math.Clamp(mg, 0f, 1f);
                        b = Math.Clamp(mb, 0f, 1f);
                    }
                    if (_vibrance != 0)
                    {
                        var top = Math.Max(r, Math.Max(g, b));
                        var saturation = top > 1e-4f ? (top - Math.Min(r, Math.Min(g, b))) / top : 0f;
                        // Dull colours move most when adding, vivid ones when removing.
                        var factor = 1f + _vibrance * (_vibrance > 0 ? 1f - saturation : saturation);
                        var luma = LumaR * r + LumaG * g + LumaB * b;
                        r = Math.Clamp(luma + (r - luma) * factor, 0f, 1f);
                        g = Math.Clamp(luma + (g - luma) * factor, 0f, 1f);
                        b = Math.Clamp(luma + (b - luma) * factor, 0f, 1f);
                    }
                    if (_mono)
                    {
                        r = g = b = Math.Clamp(LumaR * r + LumaG * g + LumaB * b, 0f, 1f);
                    }
                    if (_sepia > 0)
                    {
                        var sr = Math.Min(1f, 0.393f * r + 0.769f * g + 0.189f * b);
                        var sg = Math.Min(1f, 0.349f * r + 0.686f * g + 0.168f * b);
                        var sb = Math.Min(1f, 0.272f * r + 0.534f * g + 0.131f * b);
                        r += (sr - r) * _sepia;
                        g += (sg - g) * _sepia;
                        b += (sb - b) * _sepia;
                    }

                    // ── Effects ──
                    if (_vignette != 0)
                    {
                        var distance = MathF.Sqrt(_dx2![x] + dy2);
                        var amount = Smooth(0.45f, 1.4f, distance);
                        if (_vignette > 0)
                        {
                            var factor = 1f - 0.9f * _vignette * amount;
                            r *= factor;
                            g *= factor;
                            b *= factor;
                        }
                        else
                        {
                            var lift = -0.85f * _vignette * amount;
                            r += (1f - r) * lift;
                            g += (1f - g) * lift;
                            b += (1f - b) * lift;
                        }
                    }
                    if (_grain > 0)
                    {
                        var noise = grainCell is null
                            ? Grain.At(x, y)
                            : Grain.Smooth(grainCell[x], gridY, grainFade![x], fadeY);
                        var luma = Math.Clamp(LumaR * r + LumaG * g + LumaB * b, 0f, 1f);
                        // Film grain shows most in the midtones.
                        var shown = noise * 0.085f * _grain * (0.4f + 2.4f * luma * (1f - luma));
                        r += shown;
                        g += shown;
                        b += shown;
                    }

                    px[i] = ToByte(r);
                    px[i + 1] = ToByte(g);
                    px[i + 2] = ToByte(b);
                }
            }
        }
    }

    /// <summary>
    /// Saturation then hue as one 3x3 matrix (the CSS/SVG ones): the hue matrix keeps the brightness of the colours and
    /// the saturation one scales what is left once the brightness is taken out.
    /// </summary>
    private static void BuildMatrix(double saturation, double hueDegrees, out float m00, out float m01, out float m02,
        out float m10, out float m11, out float m12, out float m20, out float m21, out float m22)
    {
        var angle = hueDegrees * Math.PI / 180;
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        double[,] hue =
        {
            { 0.213 + c * 0.787 - s * 0.213, 0.715 - c * 0.715 - s * 0.715, 0.072 - c * 0.072 + s * 0.928 },
            { 0.213 - c * 0.213 + s * 0.143, 0.715 + c * 0.285 + s * 0.140, 0.072 - c * 0.072 - s * 0.283 },
            { 0.213 - c * 0.213 - s * 0.787, 0.715 - c * 0.715 + s * 0.715, 0.072 + c * 0.928 + s * 0.072 },
        };
        var k = saturation;
        double[,] sat =
        {
            { 0.213 + 0.787 * k, 0.715 - 0.715 * k, 0.072 - 0.072 * k },
            { 0.213 - 0.213 * k, 0.715 + 0.285 * k, 0.072 - 0.072 * k },
            { 0.213 - 0.213 * k, 0.715 - 0.715 * k, 0.072 + 0.928 * k },
        };
        var product = new float[9];
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                double sum = 0;
                for (var n = 0; n < 3; n++)
                    sum += sat[row, n] * hue[n, col];
                product[row * 3 + col] = (float)sum;
            }
        }
        (m00, m01, m02, m10, m11, m12, m20, m21, m22) =
            (product[0], product[1], product[2], product[3], product[4], product[5], product[6], product[7], product[8]);
    }

    // ───────── Automatic levels ─────────

    /// <summary>The levels and gamma of "Auto": stretch the used range of the brightness and bring its average to the middle.</summary>
    private readonly record struct AutoLevels(double Low, double High, double Gamma);

    /// <summary>
    /// Measures the picture (every pixel of a small one, a sample of a big one): black and white are the 0.2 % and 99.8 %
    /// points of the brightness, the gamma moves the average of what is left towards 0.46. Null when the picture is flat.
    /// One stretch for the three channels, so colours are not shifted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static AutoLevels? MeasureAutoLevels(byte[] px, int width, int height, int channels)
    {
        var histogram = new long[256];
        long total = 0;
        var step = Math.Max(1, (int)Math.Sqrt((double)width * height / 1_000_000));
        for (var y = 0; y < height; y += step)
        {
            var i = y * width * channels;
            for (var x = 0; x < width; x += step, i += channels * step)
            {
                if (channels == 4 && px[i + 3] < 128)
                    continue;
                histogram[(int)(LumaR * px[i] + LumaG * px[i + 1] + LumaB * px[i + 2] + 0.5f)]++;
                total++;
            }
        }
        if (total == 0)
            return null;

        var low = -1;
        var high = 255;
        long seen = 0;
        for (var bin = 0; bin < 256; bin++)
        {
            seen += histogram[bin];
            if (low < 0 && seen >= total * 0.002)
                low = bin;
            if (seen >= total * 0.998)
            {
                high = bin;
                break;
            }
        }
        if (high - low < 24)
            return null;

        double black = low / 255.0;
        double white = high / 255.0;
        double mean = 0;
        for (var bin = 0; bin < 256; bin++)
            mean += histogram[bin] * Math.Clamp((bin / 255.0 - black) / (white - black), 0, 1);
        mean = Math.Clamp(mean / total, 0.02, 0.98);
        var gamma = Math.Clamp(Math.Log(mean) / Math.Log(0.46), 0.75, 1.45);
        if (Math.Abs(gamma - 1) < 0.03)
            gamma = 1;
        return new AutoLevels(black, white, gamma);
    }

    // ───────── The tone curves ─────────

    /// <summary>
    /// What the light controls do to one channel value v in 0..1 (encoded, like the file): auto levels, then the white
    /// balance and the exposure as gains in linear light, then the highlights, shadows, whites, blacks, brightness, gamma
    /// and contrast as one curve. The result is a lookup table per channel, monotonic so no tone is ever inverted.
    /// </summary>
    private sealed class Tone
    {
        private readonly double _brightness;
        private readonly double _contrast;
        private readonly double _highlights;
        private readonly double _shadows;
        private readonly double _whites;
        private readonly double _blacks;
        private readonly double _gamma;
        private readonly AutoLevels? _auto;
        private readonly double[] _gain = [1, 1, 1];
        private readonly bool _gains;

        public Tone(double exposure, double temperature, double tint, double brightness, double contrast, double highlights,
            double shadows, double whites, double blacks, double gamma, AutoLevels? auto)
        {
            _brightness = brightness;
            _contrast = contrast;
            _highlights = highlights;
            _shadows = shadows;
            _whites = whites;
            _blacks = blacks;
            _gamma = gamma;
            _auto = auto;

            // White balance: warm raises red and lowers blue; a magenta tint lowers green. The balance is scaled to keep
            // the brightness of a neutral grey (exposure alone changes that).
            if (temperature != 0 || tint != 0)
            {
                var red = Math.Pow(2, 0.45 * temperature) * Math.Pow(2, 0.12 * tint);
                var green = Math.Pow(2, -0.4 * tint);
                var blue = Math.Pow(2, -0.45 * temperature) * Math.Pow(2, 0.12 * tint);
                var keep = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                _gain = [red / keep, green / keep, blue / keep];
            }
            var stops = Math.Pow(2, exposure);
            for (var c = 0; c < 3; c++)
                _gain[c] *= stops;
            _gains = exposure != 0 || temperature != 0 || tint != 0;
        }

        public bool IsIdentity =>
            !_gains && _auto is null && _brightness == 0 && _contrast == 0 && _highlights == 0 && _shadows == 0 && _whites == 0
            && _blacks == 0 && _gamma == 1;

        public (float[] R, float[] G, float[] B) Build()
        {
            var luts = new float[3][];
            for (var c = 0; c < 3; c++)
            {
                var lut = new float[LutSize];
                var previous = 0.0;
                for (var n = 0; n < LutSize; n++)
                {
                    var v = n / (double)(LutSize - 1);
                    if (_auto is { } levels)
                    {
                        v = Math.Clamp((v - levels.Low) / (levels.High - levels.Low), 0, 1);
                        if (levels.Gamma != 1)
                            v = Math.Pow(v, 1 / levels.Gamma);
                    }
                    if (_gains)
                        v = Encode(Math.Min(1, Decode(v) * _gain[c]));
                    v = Curve(v);
                    if (v < previous)
                        v = previous;
                    lut[n] = (float)(previous = v);
                }
                luts[c] = lut;
            }
            return (luts[0], luts[1], luts[2]);
        }

        private double Curve(double v)
        {
            // Each light control raises (+) or lowers (-) a band of tones by up to a fifth of the scale.
            if (_highlights != 0 || _shadows != 0 || _whites != 0 || _blacks != 0)
            {
                v += 0.2 * (_blacks * (1 - Rise(v, 0, 0.32)) + _shadows * Band(v, 0.22, 0.38) + _highlights * Band(v, 0.78, 0.38)
                            + _whites * Rise(v, 0.68, 1.0));
                v = Math.Clamp(v, 0, 1);
            }
            if (_brightness != 0)
                v = Math.Clamp(v + 0.22 * _brightness * 4 * v * (1 - v), 0, 1);
            if (_gamma != 1)
                v = Math.Pow(v, 1 / _gamma);
            if (_contrast != 0)
                v = Sigmoid(v, _contrast * 5);
            return Math.Clamp(v, 0, 1);
        }

        /// <summary>0 below <paramref name="from"/>, 1 above <paramref name="to"/>, smooth between.</summary>
        private static double Rise(double v, double from, double to)
        {
            var t = Math.Clamp((v - from) / (to - from), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>A smooth bump: 1 at <paramref name="centre"/>, 0 farther than <paramref name="halfWidth"/>.</summary>
        private static double Band(double v, double centre, double halfWidth)
        {
            var t = 1 - Math.Abs(v - centre) / halfWidth;
            return t <= 0 ? 0 : t * t * (3 - 2 * t);
        }

        /// <summary>
        /// The logistic S curve through (0,0) and (1,1) for a positive strength; its inverse (a flatter middle) for a
        /// negative one. Both leave black and white alone.
        /// </summary>
        private static double Sigmoid(double v, double strength)
        {
            var a = Math.Abs(strength);
            var low = Logistic(-0.5 * a);
            var high = Logistic(0.5 * a);
            if (strength > 0)
                return (Logistic(a * (v - 0.5)) - low) / (high - low);
            var u = low + v * (high - low);
            return 0.5 + Math.Log(u / (1 - u)) / a;
        }

        private static double Logistic(double x) => 1 / (1 + Math.Exp(-x));

        private static double Decode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

        private static double Encode(double linear) => linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
    }

    // ───────── Blurred references ─────────

    /// <summary>
    /// A Gaussian-blurred copy of the picture to compare each pixel with (sharpening, clarity, noise reduction). Big
    /// blurs are computed on a reduced copy and read back with bilinear interpolation, so the cost does not grow with the
    /// radius or the size of the photo. Transparent pixels weigh nothing: they do not bleed their colour into the edges.
    /// </summary>
    private sealed class Field
    {
        private readonly float[] _data;
        private readonly int _width;
        private readonly int _height;
        private readonly int _planes;
        private readonly int _factor;
        private readonly int[] _x0;
        private readonly int[] _x1;
        private readonly float[] _tx;

        private Field(float[] data, int width, int height, int planes, int factor, int sourceWidth)
        {
            _data = data;
            _width = width;
            _height = height;
            _planes = planes;
            _factor = factor;
            _x0 = new int[sourceWidth];
            _x1 = new int[sourceWidth];
            _tx = new float[sourceWidth];
            for (var x = 0; x < sourceWidth; x++)
            {
                // The centre of source pixel x, in reduced pixels.
                var u = (x + 0.5) / factor - 0.5;
                var floor = (int)Math.Floor(u);
                _x0[x] = Math.Clamp(floor, 0, width - 1);
                _x1[x] = Math.Clamp(floor + 1, 0, width - 1);
                _tx[x] = (float)(u - floor);
            }
        }

        /// <summary>Where a row of the picture reads from: two rows of the field and how far between them.</summary>
        public readonly record struct Row(int Upper, int Lower, float Between);

        public static Field Create(byte[] px, int width, int height, int channels, bool luma, double sigma, CancellationToken cancel)
        {
            var factor = Math.Max(1, (int)(sigma / 1.5));
            var reducedWidth = (width + factor - 1) / factor;
            var reducedHeight = (height + factor - 1) / factor;
            var colourPlanes = luma ? 1 : 3;
            var weighted = channels == 4;
            var planes = colourPlanes + (weighted ? 1 : 0);
            var data = new float[reducedWidth * reducedHeight * planes];
            var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) };

            // 1. Average each factor × factor block (the colour weighted by alpha when there is one).
            Parallel.For(0, reducedHeight, options, ry => ReduceRow(px, width, height, channels, luma, factor, reducedWidth, planes, data, ry));
            cancel.ThrowIfCancellationRequested();

            // 2. Blur the reduced copy.
            Blur(data, reducedWidth, reducedHeight, planes, sigma / factor, options);

            // 3. With alpha the planes hold colour × weight and weight: divide them out.
            if (weighted)
            {
                for (var o = 0; o < data.Length; o += planes)
                {
                    var weight = data[o + colourPlanes];
                    for (var p = 0; p < colourPlanes; p++)
                        data[o + p] = weight > 1e-5f ? data[o + p] / weight : 0f;
                }
            }
            return new Field(data, reducedWidth, reducedHeight, planes, factor, width);
        }

        /// <summary>One row of the reduced copy: the average of the block of source pixels under each of its pixels.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ReduceRow(byte[] px, int width, int height, int channels, bool luma, int factor, int reducedWidth, int planes,
            float[] data, int ry)
        {
            var weighted = channels == 4;
            var colourPlanes = luma ? 1 : 3;
            var yStart = ry * factor;
            var yEnd = Math.Min(height, yStart + factor);
            for (var rx = 0; rx < reducedWidth; rx++)
            {
                var xStart = rx * factor;
                var xEnd = Math.Min(width, xStart + factor);
                float a = 0, b = 0, c = 0, weight = 0;
                for (var y = yStart; y < yEnd; y++)
                {
                    var i = (y * width + xStart) * channels;
                    for (var x = xStart; x < xEnd; x++, i += channels)
                    {
                        var w = weighted ? px[i + 3] * Inv255 : 1f;
                        if (luma)
                        {
                            a += w * (LumaR * px[i] + LumaG * px[i + 1] + LumaB * px[i + 2]);
                        }
                        else
                        {
                            a += w * px[i];
                            b += w * px[i + 1];
                            c += w * px[i + 2];
                        }
                        weight += w;
                    }
                }
                var o = (ry * reducedWidth + rx) * planes;
                if (luma)
                {
                    data[o] = a * Inv255;
                }
                else
                {
                    data[o] = a * Inv255;
                    data[o + 1] = b * Inv255;
                    data[o + 2] = c * Inv255;
                }
                if (weighted)
                    data[o + colourPlanes] = weight;
                else
                    for (var p = 0; p < colourPlanes; p++)
                        data[o + p] /= weight;
            }
        }

        private static void Blur(float[] data, int width, int height, int planes, double sigma, ParallelOptions options)
        {
            var radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
            var kernel = new float[radius + 1];
            double total = 0;
            for (var k = 0; k <= radius; k++)
            {
                var weight = Math.Exp(-0.5 * k * k / (sigma * sigma));
                kernel[k] = (float)weight;
                total += k == 0 ? weight : 2 * weight;
            }
            for (var k = 0; k <= radius; k++)
                kernel[k] = (float)(kernel[k] / total);

            var temp = new float[data.Length];
            Parallel.For(0, height, options, y => BlurRowAcross(data, temp, y, width, planes, kernel, radius));
            Parallel.For(0, height, options, y => BlurRowDown(temp, data, y, width, height, planes, kernel, radius));
        }

        /// <summary>The horizontal half of the Gaussian blur, for row <paramref name="y"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void BlurRowAcross(float[] source, float[] target, int y, int width, int planes, float[] kernel, int radius)
        {
            var row = y * width * planes;
            for (var x = 0; x < width; x++)
            {
                for (var p = 0; p < planes; p++)
                {
                    var sum = kernel[0] * source[row + x * planes + p];
                    for (var k = 1; k <= radius; k++)
                    {
                        var left = Math.Max(0, x - k);
                        var right = Math.Min(width - 1, x + k);
                        sum += kernel[k] * (source[row + left * planes + p] + source[row + right * planes + p]);
                    }
                    target[row + x * planes + p] = sum;
                }
            }
        }

        /// <summary>The vertical half, for row <paramref name="y"/>: a weighted sum of whole rows.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void BlurRowDown(float[] source, float[] target, int y, int width, int height, int planes, float[] kernel, int radius)
        {
            var stride = width * planes;
            var row = y * stride;
            for (var i = 0; i < stride; i++)
                target[row + i] = kernel[0] * source[row + i];
            for (var k = 1; k <= radius; k++)
            {
                var upper = Math.Max(0, y - k) * stride;
                var lower = Math.Min(height - 1, y + k) * stride;
                var weight = kernel[k];
                for (var i = 0; i < stride; i++)
                    target[row + i] += weight * (source[upper + i] + source[lower + i]);
            }
        }

        public Row RowAt(int y)
        {
            var v = (y + 0.5) / _factor - 0.5;
            var floor = (int)Math.Floor(v);
            return new Row(Math.Clamp(floor, 0, _height - 1) * _width * _planes, Math.Clamp(floor + 1, 0, _height - 1) * _width * _planes, (float)(v - floor));
        }

        /// <summary>The blurred brightness at column <paramref name="x"/> of the row (a luma field).</summary>
        public float Sample1(Row row, int x)
        {
            var left = _x0[x] * _planes;
            var right = _x1[x] * _planes;
            var t = _tx[x];
            var upper = _data[row.Upper + left] + (_data[row.Upper + right] - _data[row.Upper + left]) * t;
            var lower = _data[row.Lower + left] + (_data[row.Lower + right] - _data[row.Lower + left]) * t;
            return upper + (lower - upper) * row.Between;
        }

        /// <summary>The blurred colour at column <paramref name="x"/> of the row (an RGB field).</summary>
        public void Sample3(Row row, int x, out float r, out float g, out float b)
        {
            var left = _x0[x] * _planes;
            var right = _x1[x] * _planes;
            var t = _tx[x];
            var s = row.Between;
            r = Mix(left, right, t, s, row, 0);
            g = Mix(left, right, t, s, row, 1);
            b = Mix(left, right, t, s, row, 2);
        }

        private float Mix(int left, int right, float t, float s, Row row, int plane)
        {
            var upper = _data[row.Upper + left + plane] + (_data[row.Upper + right + plane] - _data[row.Upper + left + plane]) * t;
            var lower = _data[row.Lower + left + plane] + (_data[row.Lower + right + plane] - _data[row.Lower + left + plane]) * t;
            return upper + (lower - upper) * s;
        }
    }

    // ───────── Grain ─────────

    /// <summary>Repeatable noise: the same pixel always gets the same grain, so the preview does not boil while you drag.</summary>
    private static class Grain
    {
        /// <summary>Noise with an average of 0 and a spread of 1 for lattice point (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public static float At(int x, int y)
        {
            var h = unchecked((uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ 0x7f4a7c15u);
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            // Three 10-bit uniform values add up to something close to a bell curve (spread 0.5, so double it).
            var sum = ((h & 1023) + ((h >> 10) & 1023) + ((h >> 20) & 1023)) * (1f / 1023f);
            return (sum - 1.5f) * 2f;
        }

        /// <summary>
        /// Noise between four lattice points, for photos bigger than the preview: the grain gets as many pixels wide as
        /// the photo is bigger, so it looks the same at the same viewing size. Rescaled to keep the spread of 1.
        /// </summary>
        public static float Smooth(int cellX, int cellY, float fadeX, float fadeY)
        {
            var w00 = (1 - fadeX) * (1 - fadeY);
            var w10 = fadeX * (1 - fadeY);
            var w01 = (1 - fadeX) * fadeY;
            var w11 = fadeX * fadeY;
            var value = w00 * At(cellX, cellY) + w10 * At(cellX + 1, cellY) + w01 * At(cellX, cellY + 1) + w11 * At(cellX + 1, cellY + 1);
            return value / MathF.Sqrt(w00 * w00 + w10 * w10 + w01 * w01 + w11 * w11);
        }
    }
}
