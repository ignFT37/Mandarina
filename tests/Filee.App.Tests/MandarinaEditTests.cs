// Mandarina: EDIT for photos. EditPipeline (the picture work) is checked on small pictures whose pixels are
// known, control by control and in the direction its name says; the panel is rendered to a PNG with a real photo and run
// against Magick.NET (the copy it saves, the original it leaves alone, the profiles and the transparency it keeps).

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Filee.App.Mandarina.Panels;
using Filee.App.Mandarina.Tools;
using ImageMagick;
using static Filee.App.Tests.MandarinaTestKit;

namespace Filee.App.Tests;

public sealed class MandarinaEditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mandarina-edit").FullName;

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

    // ───────── Small pictures with known pixels ─────────

    private static MagickImage Make(int width, int height, Func<int, int, (int R, int G, int B)> pixel)
    {
        var data = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = (y * width + x) * 3;
                data[i] = (byte)r;
                data[i + 1] = (byte)g;
                data[i + 2] = (byte)b;
            }
        }
        var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        return image;
    }

    private static MagickImage Flat(int value, int width = 64, int height = 48) => Make(width, height, (_, _) => (value, value, value));

    private static MagickImage Colour(int r, int g, int b, int width = 16, int height = 16) => Make(width, height, (_, _) => (r, g, b));

    private static byte[] Rgb(MagickImage image)
    {
        using var pixels = image.GetPixels();
        return pixels.ToByteArray(PixelMapping.RGB)!;
    }

    private static (int R, int G, int B) At(byte[] rgb, int width, int x, int y)
    {
        var i = (y * width + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    private static (double R, double G, double B) Mean(MagickImage image)
    {
        var rgb = Rgb(image);
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < rgb.Length; i += 3)
        {
            r += rgb[i];
            g += rgb[i + 1];
            b += rgb[i + 2];
        }
        var n = rgb.Length / 3.0;
        return (r / n, g / n, b / n);
    }

    private static double Brightness(MagickImage image)
    {
        var (r, g, b) = Mean(image);
        return (r + g + b) / 3;
    }

    private static double StdDev(byte[] rgb, int width, int x0, int x1, int y0, int y1)
    {
        double sum = 0, sumSquares = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                double v = At(rgb, width, x, y).G;
                sum += v;
                sumSquares += v * v;
                n++;
            }
        }
        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSquares / n - mean * mean));
    }

    /// <summary>A copy of <paramref name="image"/> with <paramref name="settings"/> applied.</summary>
    private static MagickImage Edit(MagickImage image, EditSettings settings)
    {
        var copy = (MagickImage)image.Clone();
        EditPipeline.Apply(copy, settings);
        return copy;
    }

    private static readonly EditSettings Factory = new();

    // ───────── EditPipeline: factory values change nothing ─────────

    [Fact]
    public void Factory_settings_leave_every_pixel_alone()
    {
        using var photo = new MagickImage("plasma:orange-purple", 200, 120);
        var before = Rgb(photo);

        Assert.True(Factory.IsDefault);
        Assert.True(EditSettings.Default.IsDefault);
        Assert.False((Factory with { Exposure = 0.01 }).IsDefault);
        Assert.False((Factory with { Auto = true }).IsDefault);
        Assert.False((Factory with { Gamma = 1.01 }).IsDefault);
        Assert.False((Factory with { BlackAndWhite = true }).IsDefault);

        using var same = Edit(photo, Factory);
        Assert.Equal(before, Rgb(same));
    }

    [Fact]
    public void A_control_at_zero_changes_nothing_while_the_others_move()
    {
        using var photo = new MagickImage("plasma:orange-purple", 160, 100);
        using var moved = Edit(photo, Factory with { Exposure = 0.5, Contrast = 20 });
        // Every other control spelled out at its factory value gives the very same picture.
        using var spelledOut = Edit(photo, Factory with
        {
            Exposure = 0.5, Contrast = 20, Brightness = 0, Highlights = 0, Shadows = 0, Whites = 0, Blacks = 0, Gamma = 1,
            Saturation = 0, Vibrance = 0, Temperature = 0, Tint = 0, Hue = 0, Sharpness = 0, Clarity = 0, NoiseReduction = 0,
            BlackAndWhite = false, Sepia = 0, Vignette = 0, Grain = 0, Auto = false,
        });
        Assert.Equal(Rgb(moved), Rgb(spelledOut));
        Assert.NotEqual(Rgb(photo), Rgb(moved));

        // A control that is moved and put back leaves the other's result alone: only the exposure and contrast act here.
        using var onlyExposure = Edit(photo, Factory with { Exposure = 0.5 });
        Assert.NotEqual(Rgb(onlyExposure), Rgb(moved));
    }

    [Fact]
    public void Zero_values_of_detail_colour_and_effects_do_not_touch_the_pixels()
    {
        // Each of these, on its own, is a control at zero: nothing may move, not even by one level.
        using var photo = new MagickImage("plasma:orange-purple", 120, 80);
        var before = Rgb(photo);
        foreach (var settings in new[]
        {
            Factory with { Sharpness = 0 }, Factory with { Clarity = 0 }, Factory with { NoiseReduction = 0 }, Factory with { Sepia = 0 },
            Factory with { Vignette = 0 }, Factory with { Grain = 0 }, Factory with { Saturation = 0, Hue = 0, Vibrance = 0 },
            Factory with { Temperature = 0, Tint = 0 }, Factory with { Gamma = 1 }, Factory with { Highlights = 0, Shadows = 0 },
        })
        {
            using var same = Edit(photo, settings);
            Assert.Equal(before, Rgb(same));
        }
    }

    // ───────── EditPipeline: light ─────────

    [Fact]
    public void Exposure_plus_one_brightens_and_minus_one_darkens()
    {
        using var grey = Flat(128);
        using var up = Edit(grey, Factory with { Exposure = 1 });
        using var down = Edit(grey, Factory with { Exposure = -1 });
        // One stop doubles (or halves) the light, not the encoded value: 128 → about 176 and 92.
        Assert.InRange(Brightness(up), 170, 182);
        Assert.InRange(Brightness(down), 86, 98);
        Assert.True(Brightness(up) > Brightness(grey) && Brightness(grey) > Brightness(down));
    }

    [Fact]
    public void Brightness_lifts_the_midtones_more_than_the_ends()
    {
        int[] levels = [10, 64, 128, 192, 245];
        using var ramp = Make(5, 1, (x, _) => (levels[x], levels[x], levels[x]));
        using var up = Edit(ramp, Factory with { Brightness = 100 });
        using var down = Edit(ramp, Factory with { Brightness = -100 });
        var (before, lighter, darker) = (Rgb(ramp), Rgb(up), Rgb(down));
        Assert.True(lighter[6] > before[6] + 15);       // 128 → clearly brighter
        Assert.True(darker[6] < before[6] - 15);
        Assert.InRange(lighter[0] - before[0], 0, 14);  // the ends hardly move
        Assert.InRange(lighter[12] - before[12], 0, 14);
        Assert.InRange(before[0] - darker[0], 0, 14);
    }

    [Fact]
    public void Contrast_pushes_the_tones_apart_and_negative_contrast_pulls_them_together()
    {
        using var halves = Make(32, 8, (x, _) => x < 16 ? (80, 80, 80) : (180, 180, 180));
        using var more = Edit(halves, Factory with { Contrast = 50 });
        using var less = Edit(halves, Factory with { Contrast = -50 });
        var (m, l) = (Rgb(more), Rgb(less));
        Assert.True(At(m, 32, 2, 2).R < 80);
        Assert.True(At(m, 32, 30, 2).R > 180);
        Assert.True(At(l, 32, 2, 2).R > 80);
        Assert.True(At(l, 32, 30, 2).R < 180);
    }

    [Fact]
    public void Highlights_shadows_whites_and_blacks_each_act_on_their_own_tones()
    {
        // Five patches from dark to bright.
        int[] levels = [10, 64, 128, 192, 245];
        using var ramp = Make(5, 1, (x, _) => (levels[x], levels[x], levels[x]));
        var before = Rgb(ramp);
        int Level(EditSettings settings, int patch)
        {
            using var edited = Edit(ramp, settings);
            return Rgb(edited)[patch * 3];
        }

        // Highlights: the bright patch moves, the dark ones stay.
        Assert.True(Level(Factory with { Highlights = -100 }, 3) < before[9] - 15);
        Assert.True(Level(Factory with { Highlights = 100 }, 3) > before[9] + 8);
        Assert.InRange(Level(Factory with { Highlights = -100 }, 0), before[0] - 2, before[0] + 2);
        Assert.InRange(Level(Factory with { Highlights = -100 }, 1), before[3] - 2, before[3] + 2);

        // Shadows: the dark patch moves, the bright ones stay.
        Assert.True(Level(Factory with { Shadows = 100 }, 1) > before[3] + 15);
        Assert.True(Level(Factory with { Shadows = -100 }, 1) < before[3] - 15);
        Assert.InRange(Level(Factory with { Shadows = 100 }, 4), before[12] - 3, before[12] + 3);

        // Whites: the brightest patch (more is brighter), the dark ones stay.
        Assert.True(Level(Factory with { Whites = 100 }, 4) > before[12]);
        Assert.True(Level(Factory with { Whites = -100 }, 4) < before[12] - 15);
        Assert.InRange(Level(Factory with { Whites = 100 }, 1), before[3] - 2, before[3] + 2);

        // Blacks: the darkest patch (more lifts it, less crushes it), the bright ones stay.
        Assert.True(Level(Factory with { Blacks = 100 }, 0) > before[0] + 8);
        Assert.True(Level(Factory with { Blacks = -100 }, 0) < before[0]);
        Assert.InRange(Level(Factory with { Blacks = 100 }, 3), before[9] - 2, before[9] + 2);
    }

    [Fact]
    public void Gamma_above_one_brightens_and_below_one_darkens()
    {
        using var grey = Flat(128);
        using var up = Edit(grey, Factory with { Gamma = 2 });
        using var down = Edit(grey, Factory with { Gamma = 0.5 });
        Assert.True(Brightness(up) > 170);
        Assert.True(Brightness(down) < 80);
    }

    // ───────── EditPipeline: colour ─────────

    [Fact]
    public void Saturation_minus_100_makes_the_three_channels_equal_and_plus_makes_colour_stronger()
    {
        using var photo = new MagickImage("plasma:orange-purple", 80, 60);
        using var grey = Edit(photo, Factory with { Saturation = -100 });
        var rgb = Rgb(grey);
        for (var i = 0; i < rgb.Length; i += 3)
        {
            Assert.Equal(rgb[i], rgb[i + 1]);
            Assert.Equal(rgb[i], rgb[i + 2]);
        }

        static int Chroma((int R, int G, int B) c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        using var muted = Colour(180, 130, 110);
        using var vivid = Edit(muted, Factory with { Saturation = 50 });
        using var dull = Edit(muted, Factory with { Saturation = -50 });
        var (a, b, c) = (At(Rgb(muted), 16, 3, 3), At(Rgb(vivid), 16, 3, 3), At(Rgb(dull), 16, 3, 3));
        Assert.True(Chroma(b) > Chroma(a));
        Assert.True(Chroma(c) < Chroma(a));
    }

    [Fact]
    public void Vibrance_helps_dull_colours_more_than_vivid_ones()
    {
        static int Chroma((int R, int G, int B) c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        using var dull = Colour(130, 110, 110);
        using var vivid = Colour(230, 30, 30);
        var (dullBefore, vividBefore) = (Chroma(At(Rgb(dull), 16, 1, 1)), Chroma(At(Rgb(vivid), 16, 1, 1)));
        using var dullAfter = Edit(dull, Factory with { Vibrance = 100 });
        using var vividAfter = Edit(vivid, Factory with { Vibrance = 100 });
        var dullGain = Chroma(At(Rgb(dullAfter), 16, 1, 1)) / (double)dullBefore;
        var vividGain = Chroma(At(Rgb(vividAfter), 16, 1, 1)) / (double)vividBefore;
        Assert.True(dullGain > 1.4);
        Assert.True(dullGain > vividGain + 0.3);
        // And negative vibrance takes colour out, the vivid ones first.
        using var vividLess = Edit(vivid, Factory with { Vibrance = -100 });
        Assert.True(Chroma(At(Rgb(vividLess), 16, 1, 1)) < vividBefore);
    }

    [Fact]
    public void Temperature_warms_with_plus_and_cools_with_minus()
    {
        using var photo = new MagickImage("plasma:orange-purple", 80, 60);
        var (r0, _, b0) = Mean(photo);
        using var warm = Edit(photo, Factory with { Temperature = 50 });
        using var cold = Edit(photo, Factory with { Temperature = -50 });
        var (rw, _, bw) = Mean(warm);
        var (rc, _, bc) = Mean(cold);
        Assert.True(rw - bw > r0 - b0 + 10);   // more red than blue than before
        Assert.True(rc - bc < r0 - b0 - 10);   // and the other way round

        // On a neutral grey the cast is plain to see.
        using var grey = Flat(128);
        var (rg, _, bg) = At(Rgb(Edit(grey, Factory with { Temperature = 50 })), 64, 5, 5);
        Assert.True(rg > bg + 8);
    }

    [Fact]
    public void Tint_goes_from_green_to_magenta()
    {
        using var grey = Flat(128);
        var (r1, g1, b1) = At(Rgb(Edit(grey, Factory with { Tint = 50 })), 64, 5, 5);
        var (r2, g2, b2) = At(Rgb(Edit(grey, Factory with { Tint = -50 })), 64, 5, 5);
        Assert.True(g1 < (r1 + b1) / 2.0 - 5);   // magenta: green is the one that drops
        Assert.True(g2 > (r2 + b2) / 2.0 + 5);   // green
    }

    [Fact]
    public void Hue_turns_the_colours_round_the_wheel()
    {
        using var red = Colour(200, 40, 40);
        var (r1, g1, b1) = At(Rgb(Edit(red, Factory with { Hue = 120 })), 16, 2, 2);
        Assert.True(g1 > r1 && g1 > b1);          // red → green
        var (r2, g2, b2) = At(Rgb(Edit(red, Factory with { Hue = -120 })), 16, 2, 2);
        Assert.True(b2 > r2 && b2 > g2);          // red → blue
        var (r3, g3, b3) = At(Rgb(Edit(red, Factory with { Hue = 180 })), 16, 2, 2);
        Assert.True(g3 > r3 && b3 > r3);          // red → cyan
    }

    // ───────── EditPipeline: detail ─────────

    [Fact]
    public void Sharpness_exaggerates_an_edge_and_leaves_flat_areas_alone()
    {
        using var edge = Make(64, 32, (x, _) => x < 32 ? (90, 90, 90) : (170, 170, 170));
        using var sharp = Edit(edge, Factory with { Sharpness = 100 });
        using var gentle = Edit(edge, Factory with { Sharpness = 30 });
        var (s, g) = (Rgb(sharp), Rgb(gentle));
        Assert.True(At(s, 64, 31, 10).R < 90);      // darker on the dark side of the edge …
        Assert.True(At(s, 64, 32, 10).R > 170);     // … brighter on the bright side
        Assert.Equal(90, At(s, 64, 5, 10).R);       // far from the edge nothing changes
        Assert.Equal(170, At(s, 64, 60, 10).R);
        Assert.True(At(g, 64, 32, 10).R > 170 && At(g, 64, 32, 10).R < At(s, 64, 32, 10).R);
    }

    [Fact]
    public void Clarity_adds_local_contrast_in_the_midtones_and_negative_clarity_softens()
    {
        using var step = Make(200, 64, (x, _) => x < 100 ? (110, 110, 110) : (150, 150, 150));
        using var clear = Edit(step, Factory with { Clarity = 100 });
        using var soft = Edit(step, Factory with { Clarity = -100 });
        var (c, s) = (Rgb(clear), Rgb(soft));
        Assert.True(At(c, 200, 95, 30).R < 110 - 8);    // the dark side near the step gets darker
        Assert.True(At(c, 200, 105, 30).R > 150 + 8);   // the bright side brighter
        Assert.True(At(s, 200, 95, 30).R > 110 + 3);    // softening goes the other way
        Assert.True(At(s, 200, 105, 30).R < 150 - 3);
    }

    [Fact]
    public void Noise_reduction_smooths_flat_areas_and_keeps_the_edges()
    {
        var random = new Random(1234);
        double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        using var noisy = Make(96, 64, (x, _) =>
        {
            var v = (x < 48 ? 60 : 190) + (int)Math.Round(6 * Gaussian());
            return (v, v, v);
        });
        using var clean = Edit(noisy, Factory with { NoiseReduction = 100 });
        using var half = Edit(noisy, Factory with { NoiseReduction = 40 });
        var (before, after, middle) = (Rgb(noisy), Rgb(clean), Rgb(half));
        var spread = StdDev(before, 96, 8, 40, 8, 56);
        Assert.True(StdDev(after, 96, 8, 40, 8, 56) < spread * 0.5);
        Assert.True(StdDev(middle, 96, 8, 40, 8, 56) < spread);
        Assert.True(StdDev(after, 96, 8, 40, 8, 56) < StdDev(middle, 96, 8, 40, 8, 56));
        // The step between the two halves is still a step.
        Assert.True(At(after, 96, 49, 30).G - At(after, 96, 46, 30).G > 100);
    }

    // ───────── EditPipeline: effects ─────────

    [Fact]
    public void Black_and_white_makes_every_pixel_grey()
    {
        using var photo = new MagickImage("plasma:orange-purple", 80, 60);
        using var mono = Edit(photo, Factory with { BlackAndWhite = true });
        var rgb = Rgb(mono);
        for (var i = 0; i < rgb.Length; i += 3)
        {
            Assert.Equal(rgb[i], rgb[i + 1]);
            Assert.Equal(rgb[i], rgb[i + 2]);
        }
        Assert.NotEqual(Rgb(photo), rgb);
    }

    [Fact]
    public void Sepia_tones_towards_brown()
    {
        using var grey = Flat(128);
        var (r, g, b) = At(Rgb(Edit(grey, Factory with { Sepia = 100 })), 64, 5, 5);
        Assert.True(r > g && g > b);
        Assert.True(r - b > 30);
        var (r2, _, b2) = At(Rgb(Edit(grey, Factory with { Sepia = 30 })), 64, 5, 5);
        Assert.True(r2 - b2 is > 0 && r2 - b2 < r - b);   // half-way is half as brown
    }

    [Fact]
    public void Vignette_darkens_the_corners_and_a_negative_one_lightens_them()
    {
        using var grey = Flat(128, 80, 60);
        var (dark, light) = (Rgb(Edit(grey, Factory with { Vignette = 100 })), Rgb(Edit(grey, Factory with { Vignette = -100 })));
        Assert.True(At(dark, 80, 0, 0).R < 128 - 60);   // the corner
        Assert.True(At(dark, 80, 79, 59).R < 128 - 60);
        Assert.Equal(128, At(dark, 80, 40, 30).R);      // the middle is not touched
        Assert.True(At(light, 80, 0, 0).R > 128 + 60);
        Assert.Equal(128, At(light, 80, 40, 30).R);
        Assert.True(At(dark, 80, 0, 0).R < At(dark, 80, 20, 15).R && At(dark, 80, 20, 15).R < At(dark, 80, 40, 30).R);   // falls off smoothly
    }

    [Fact]
    public void Grain_adds_noise_that_comes_out_the_same_every_time()
    {
        using var grey = Flat(128, 64, 64);
        using var first = Edit(grey, Factory with { Grain = 50 });
        using var second = Edit(grey, Factory with { Grain = 50 });
        var rgb = Rgb(first);
        Assert.Equal(rgb, Rgb(second));                     // repeatable, so the preview does not boil
        Assert.True(StdDev(rgb, 64, 0, 64, 0, 64) > 5);     // it changes pixels
        Assert.InRange(Mean(first).G, 125, 131);            // without making the picture lighter or darker
        using var strong = Edit(grey, Factory with { Grain = 100 });
        Assert.True(StdDev(Rgb(strong), 64, 0, 64, 0, 64) > StdDev(rgb, 64, 0, 64, 0, 64));
    }

    // ───────── EditPipeline: automatic ─────────

    [Fact]
    public void Auto_stretches_a_flat_picture_and_leaves_a_single_colour_alone()
    {
        // A dull picture: 90 .. 160 only.
        using var dull = Make(256, 8, (x, _) => (90 + x * 70 / 255, 90 + x * 70 / 255, 90 + x * 70 / 255));
        using var auto = Edit(dull, Factory with { Auto = true });
        var rgb = Rgb(auto);
        int min = 255, max = 0;
        for (var i = 0; i < rgb.Length; i += 3)
        {
            min = Math.Min(min, rgb[i]);
            max = Math.Max(max, rgb[i]);
        }
        Assert.True(max - min > 230, $"range after Auto: {min}..{max}");

        // A picture of one colour has nothing to stretch.
        using var flat = Flat(120);
        using var same = Edit(flat, Factory with { Auto = true });
        Assert.Equal(Rgb(flat), Rgb(same));
    }

    // ───────── EditPipeline: pictures of every kind ─────────

    [Fact]
    public void Transparency_is_kept_and_only_the_colour_changes()
    {
        const int width = 40, height = 20;
        var data = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var x = i % width;
            data[i * 4] = 200;
            data[i * 4 + 1] = 100;
            data[i * 4 + 2] = 50;
            data[i * 4 + 3] = (byte)(x < 20 ? 255 : 90 + x);
        }
        using var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGBA));

        EditPipeline.Apply(image, Factory with { Exposure = 0.5, Vignette = 40, Sharpness = 50, NoiseReduction = 30, Grain = 20 }, TestContext.Current.CancellationToken);

        Assert.True(image.HasAlpha);
        using var pixels = image.GetPixels();
        var after = pixels.ToByteArray(PixelMapping.RGBA)!;
        for (var i = 0; i < width * height; i++)
            Assert.Equal(data[i * 4 + 3], after[i * 4 + 3]);
        Assert.True(after[0] != data[0] || after[1] != data[1]);   // the colour did change
    }

    [Fact]
    public void Transparent_pixels_do_not_bleed_their_colour_into_the_edges()
    {
        // A white square on a fully transparent black background: sharpening and noise reduction must see only the square.
        const int size = 40;
        var data = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var inside = x >= 10 && x < 30 && y >= 10 && y < 30;
                var i = (y * size + x) * 4;
                data[i] = data[i + 1] = data[i + 2] = (byte)(inside ? 230 : 0);
                data[i + 3] = (byte)(inside ? 255 : 0);
            }
        }
        using var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings(size, size, StorageType.Char, PixelMapping.RGBA));
        EditPipeline.Apply(image, Factory with { NoiseReduction = 100, Sharpness = 50 }, TestContext.Current.CancellationToken);
        using var pixels = image.GetPixels();
        var after = pixels.ToByteArray(PixelMapping.RGBA)!;
        // The square's own pixels keep their colour, edge pixels included (a black halo would dim them).
        foreach (var (x, y) in new[] { (10, 10), (10, 20), (20, 10), (29, 29), (15, 15) })
            Assert.InRange(after[(y * size + x) * 4], 225, 255);
    }

    [Fact]
    public void A_grey_picture_is_taken_to_colour_before_it_is_edited()
    {
        using var grey = new MagickImage("gradient:black-white", 64, 48);
        grey.ColorType = ColorType.Grayscale;
        EditPipeline.Apply(grey, Factory with { Sepia = 100 }, TestContext.Current.CancellationToken);
        Assert.Equal(ColorSpace.sRGB, grey.ColorSpace);
        var (r, _, b) = At(Rgb(grey), 64, 32, 24);
        Assert.True(r > b + 20);
    }

    [Fact]
    public void A_cmyk_picture_is_taken_to_srgb_before_it_is_edited()
    {
        using var rgb = new MagickImage("plasma:orange-purple", 64, 48);
        using var cmyk = (MagickImage)rgb.Clone();
        cmyk.ColorSpace = ColorSpace.CMYK;
        Assert.Equal(ColorSpace.CMYK, cmyk.ColorSpace);

        var settings = Factory with { Exposure = 0.3 };
        EditPipeline.Apply(cmyk, settings, TestContext.Current.CancellationToken);
        using var expected = Edit(rgb, settings);

        Assert.Equal(ColorSpace.sRGB, cmyk.ColorSpace);
        // The same photo as the RGB one, not C, M and Y read as R, G and B.
        var (a, b) = (Rgb(cmyk), Rgb(expected));
        double difference = 0;
        for (var i = 0; i < a.Length; i++)
            difference += Math.Abs(a[i] - b[i]);
        Assert.True(difference / a.Length < 12, $"average difference {difference / a.Length:0.0} levels");
    }

    [Fact]
    public void Tiny_pictures_survive_every_control()
    {
        var everything = Factory with
        {
            Exposure = 0.3, Brightness = 10, Contrast = 20, Highlights = -30, Shadows = 30, Whites = 10, Blacks = -10, Gamma = 1.1,
            Saturation = 10, Vibrance = 20, Temperature = 10, Tint = -5, Hue = 5, Sharpness = 40, Clarity = 30, NoiseReduction = 30,
            Sepia = 10, Vignette = 30, Grain = 20, Auto = true,
        };
        foreach (var (width, height) in new[] { (1, 1), (3, 2), (2, 40), (40, 2), (17, 13) })
        {
            using var image = Make(width, height, (x, y) => (x * 50 % 256, y * 80 % 256, 120));
            EditPipeline.Apply(image, everything, TestContext.Current.CancellationToken);
            Assert.Equal(((uint)width, (uint)height), (image.Width, image.Height));
        }
    }

    [Fact]
    public void A_cancelled_edit_throws_and_leaves_the_picture_alone()
    {
        using var photo = new MagickImage("plasma:orange-purple", 120, 80);
        var before = Rgb(photo);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => EditPipeline.Apply(photo, Factory with { Exposure = 1, Clarity = 50 }, cancel.Token));
        Assert.Equal(before, Rgb(photo));
    }

    [Fact]
    public void A_photo_edited_at_two_sizes_looks_the_same()
    {
        // The preview works on a 1200 px copy and Apply on the full photo: the radii (clarity, sharpness, grain) grow with the
        // photo so both look alike once the big one is shrunk.
        using var big = new MagickImage("plasma:orange-purple", 2400, 1600);
        using var small = (MagickImage)big.Clone();
        small.Resize(1200, 800);
        var settings = Factory with
        {
            Contrast = 30, Saturation = 20, Temperature = 15, Vignette = 40, Clarity = 40, Highlights = -20, Shadows = 20, Exposure = 0.2,
            Sharpness = 40, NoiseReduction = 30,
        };

        using var editedSmall = Edit(small, settings);
        using var editedBig = Edit(big, settings);
        editedBig.Resize(1200, 800);

        var (a, b) = (Rgb(editedSmall), Rgb(editedBig));
        double difference = 0;
        for (var i = 0; i < a.Length; i++)
            difference += Math.Abs(a[i] - b[i]);
        Assert.True(difference / a.Length < 3, $"average difference {difference / a.Length:0.00} levels");
    }

    [Fact]
    public void The_preview_of_a_1200_px_photo_is_fast_enough_to_follow_a_slider()
    {
        using var photo = new MagickImage("plasma:orange-purple", 1200, 800);
        var everything = Factory with
        {
            Exposure = 0.3, Brightness = 10, Contrast = 20, Highlights = -30, Shadows = 30, Whites = 10, Blacks = -10, Gamma = 1.1,
            Saturation = 10, Vibrance = 20, Temperature = 10, Tint = -5, Hue = 5, Sharpness = 40, Clarity = 30, NoiseReduction = 30,
            Sepia = 10, Vignette = 30, Grain = 20,
        };
        using (var warmUp = Edit(photo, everything))
        {
        }
        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var clock = Stopwatch.StartNew();
            using var edited = Edit(photo, everything);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }
        TestContext.Current.SendDiagnosticMessage($"EditPipeline.Apply, every control on, 1200x800: {best:0} ms");
        Assert.True(best < 2000, $"{best:0} ms");   // a wide bound: Debug builds are several times slower than the app's Release
    }

    // ───────── The panel ─────────

    private static async Task<EditImagePanel> OpenAsync(string photo, string language = "es")
    {
        var panel = new EditImagePanel(ContextIn(language, photo));
        await WaitAsync(() => !panel.IsLoading);
        Assert.Null(panel.LoadError);
        return panel;
    }

    private static Task WaitForPreviewAsync(EditImagePanel panel) => WaitAsync(() => !panel.IsRendering);

    [AvaloniaFact]
    public async Task The_edit_panel_renders_with_a_photo_and_the_light_controls()
    {
        var photo = MakePhoto(Path.Combine(_root, "paisaje.jpg"));
        var panel = await OpenAsync(photo);
        panel.SetSettings(Factory with
        {
            Exposure = 0.35, Brightness = 10, Contrast = 25, Highlights = -40, Shadows = 30, Whites = 12, Blacks = -15, Gamma = 1.1,
        });
        await WaitForPreviewAsync(panel);
        Assert.NotNull(panel.Edited);

        Render(panel, "editimage");
        Assert.Equal("Editar", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_edit_panel_renders_the_colour_detail_and_effects_groups()
    {
        var photo = MakePhoto(Path.Combine(_root, "paisaje.jpg"));

        var colour = await OpenAsync(photo);
        colour.IsColorTab = true;
        colour.SetSettings(Factory with { Saturation = 25, Vibrance = 30, Temperature = 20, Tint = -10, Hue = 15 });
        await WaitForPreviewAsync(colour);
        Render(colour, "editimage-color");

        var detail = await OpenAsync(photo);
        detail.IsDetailTab = true;
        detail.SetSettings(Factory with { Sharpness = 40, Clarity = 30, NoiseReduction = 20, Auto = true });
        await WaitForPreviewAsync(detail);
        Render(detail, "editimage-detail");

        var effects = await OpenAsync(photo);
        effects.IsEffectsTab = true;
        effects.SetSettings(Factory with { Sepia = 40, Vignette = 35, Grain = 20 });
        await WaitForPreviewAsync(effects);
        Render(effects, "editimage-effects");
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    public async Task The_edit_panel_fits_its_labels_in_every_language(string language)
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo, language);
        panel.SetSettings(Factory with { Exposure = -0.4, Contrast = 15, Shadows = 20, Highlights = -25 });
        await WaitForPreviewAsync(panel);
        Render(panel, $"editimage-{language}");
        Assert.NotEqual("wheel.editImage", panel.Title);
    }

    [AvaloniaFact]
    public async Task The_preview_follows_the_sliders_and_compare_shows_the_original()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        Assert.NotNull(panel.Original);
        Assert.Null(panel.Edited);
        Assert.Same(panel.Original, panel.Shown);       // nothing changed: the photo itself
        Assert.False(panel.CanApply);

        panel.Exposure = 0.5;
        Assert.True(panel.IsRendering);
        await WaitForPreviewAsync(panel);
        Assert.NotNull(panel.Edited);
        Assert.Same(panel.Edited, panel.Shown);
        Assert.Equal(panel.Original!.PixelSize, panel.Edited!.PixelSize);
        Assert.Equal(panel.Settings, panel.PreviewSettings);

        panel.HoldCompare(true);                        // while the button is held …
        Assert.Same(panel.Original, panel.Shown);
        panel.HoldCompare(false);                       // … and back when it is let go
        Assert.Same(panel.Edited, panel.Shown);

        panel.ResetAll();
        Assert.False(panel.IsEdited);
        Assert.Null(panel.Edited);
        Assert.Same(panel.Original, panel.Shown);
        Assert.False(panel.CanApply);
        Assert.Equal(EditSettings.Default, panel.Settings);
        Assert.Equal(1, panel.Gamma);
    }

    [AvaloniaFact]
    public async Task A_burst_of_slider_moves_ends_in_a_preview_of_the_last_one()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        var rendered = 0;
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditImagePanel.Edited))
                rendered++;
        };
        for (var step = 1; step <= 20; step++)
            panel.Exposure = step * 0.05;               // a drag: twenty moves in no time
        await WaitForPreviewAsync(panel);

        Assert.Equal(1.0, panel.Settings.Exposure);
        Assert.Equal(panel.Settings, panel.PreviewSettings);
        Assert.True(rendered < 20, $"{rendered} previews for 20 moves: the old ones should have been cancelled");
    }

    [AvaloniaFact]
    public async Task The_preview_keeps_up_with_a_drag_instead_of_waiting_for_the_hand_to_stop()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"), 1200, 800);
        var panel = await OpenAsync(photo);
        var previews = 0;
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditImagePanel.Edited))
                previews++;
        };

        // A drag: a new value every 15 ms for a second, with no pause as long as the preview's own delay.
        var finish = DateTime.UtcNow.AddMilliseconds(1000);
        for (var step = 1; DateTime.UtcNow < finish; step++)
        {
            panel.Exposure = Math.Sin(step * 0.1);
            var next = DateTime.UtcNow.AddMilliseconds(15);
            while (DateTime.UtcNow < next)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(3, TestContext.Current.CancellationToken);
            }
        }
        var duringTheDrag = previews;
        await WaitForPreviewAsync(panel);

        Assert.True(duringTheDrag >= 2, $"{duringTheDrag} previews while the slider was moving");
        Assert.Equal(panel.Settings, panel.PreviewSettings);       // and the last one is of the last value
    }

    [AvaloniaFact]
    public async Task A_reset_while_the_preview_is_being_worked_leaves_the_photo_untouched()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"), 1200, 800);
        var panel = await OpenAsync(photo);
        panel.SetSettings(Factory with { Clarity = 50, NoiseReduction = 50, Sharpness = 50 });
        await Task.Delay(120, TestContext.Current.CancellationToken);
        panel.ResetAll();                                          // while the first preview is on its way
        await WaitForPreviewAsync(panel);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(panel.Edited);
        Assert.Same(panel.Original, panel.Shown);
        Assert.Null(panel.PreviewSettings);
        Assert.False(panel.IsRendering);
    }

    [AvaloniaFact]
    public async Task Sliders_are_read_as_what_they_show_so_nothing_edited_means_nothing_edited()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        panel.Brightness = 0.4;        // a drag never lands exactly on zero
        panel.Exposure = 0.004;
        panel.Gamma = 1.003;
        panel.Hue = -0.3;
        Assert.False(panel.IsEdited);
        Assert.False(panel.CanApply);
        Assert.True(panel.Settings.IsDefault);

        panel.Brightness = 0.6;
        Assert.True(panel.IsEdited);
        Assert.Equal(1, panel.Settings.Brightness);
        Assert.True(panel.CanApply);
    }

    [AvaloniaFact]
    public async Task The_group_buttons_behave_like_tabs()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(panel.IsLightTab);

        panel.IsColorTab = true;
        Assert.Equal(1, panel.Tab);
        Assert.True(panel.IsColorTab);
        Assert.False(panel.IsLightTab);

        changed.Clear();
        panel.IsColorTab = false;                       // pressing the open tab again must not close it
        Assert.Equal(1, panel.Tab);
        Assert.Contains(nameof(EditImagePanel.IsColorTab), changed);   // so the button is told to stay on
        panel.IsEffectsTab = true;
        Assert.True(panel.IsEffectsTab);
        Assert.False(panel.IsColorTab);
    }

    [AvaloniaFact]
    public async Task Apply_saves_an_edited_copy_beside_the_original()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var original = File.ReadAllBytes(photo);
        var panel = await OpenAsync(photo);
        Assert.False(panel.CanApply);                   // nothing to save yet
        await panel.RunAsync();
        Assert.False(panel.IsDone);
        Assert.Empty(Directory.GetFiles(_root, "* editado*"));

        panel.Exposure = 0.7;
        panel.Contrast = 20;
        Assert.True(panel.CanApply);
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "foto editado.jpg"), output);
        using (var before = new MagickImage(photo))
        using (var after = new MagickImage(output))
        {
            Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
            Assert.True(Brightness(after) > Brightness(before) + 10, $"{Brightness(before):0} → {Brightness(after):0}");
        }
        Assert.Equal(original, File.ReadAllBytes(photo));   // the original is untouched

        // Another run does not overwrite: "foto editado 2.jpg".
        var again = await OpenAsync(photo);
        again.Saturation = -100;
        await again.RunAsync();
        Assert.Equal(Path.Combine(_root, "foto editado 2.jpg"), Assert.Single(again.Outputs));
        Assert.True(File.Exists(output));
    }

    [AvaloniaTheory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".tiff")]
    [InlineData(".bmp")]
    [InlineData(".avif")]
    public async Task Apply_saves_every_format_the_tool_reads_in_its_own_format(string extension)
    {
        var photo = MakePhoto(Path.Combine(_root, "foto" + extension), 320, 200);
        var panel = await OpenAsync(photo);
        panel.Exposure = 0.6;
        panel.Saturation = 20;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal("foto editado" + extension, Path.GetFileName(output));
        using var before = new MagickImage(photo);
        using var after = new MagickImage(output);
        Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
        Assert.True(Brightness(after) > Brightness(before) + 5, $"{Brightness(before):0} -> {Brightness(after):0}");
    }

    [AvaloniaFact]
    public async Task Apply_works_on_the_whole_photo_not_on_the_preview_copy()
    {
        // 2400 px wide: the preview copy is 1200 px, the saved photo must keep all of it.
        var photo = MakePhoto(Path.Combine(_root, "grande.png"), 2400, 1500);
        var panel = await OpenAsync(photo);
        Assert.Equal(1200, (int)panel.Original!.PixelSize.Width);
        panel.Vignette = 50;
        panel.Sharpness = 30;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var saved = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal((2400u, 1500u), (saved.Width, saved.Height));
        Assert.Equal("grande editado.png", Path.GetFileName(panel.Outputs[0]));
    }

    [AvaloniaFact]
    public async Task Apply_keeps_the_exif_and_the_icc_profile()
    {
        var path = Path.Combine(_root, "camara.jpg");
        using (var photo = new MagickImage("plasma:orange-purple", 400, 300))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Autor01");
            exif.SetValue(ExifTag.Make, "Mandarina");
            photo.SetProfile(exif);
            photo.SetProfile(ColorProfiles.SRGB);
            photo.Write(path);
        }
        var panel = await OpenAsync(path);
        panel.Temperature = 20;
        panel.Clarity = 20;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        using var saved = new MagickImage(Assert.Single(panel.Outputs));
        Assert.Equal("Autor01", saved.GetExifProfile()?.GetValue(ExifTag.Artist)?.Value);
        Assert.Equal("Mandarina", saved.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
        Assert.NotNull(saved.GetColorProfile());
    }

    [AvaloniaFact]
    public async Task Apply_keeps_the_transparency_of_a_png()
    {
        const int width = 200, height = 100;
        var data = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var x = i % width;
            data[i * 4] = 200;
            data[i * 4 + 1] = 120;
            data[i * 4 + 2] = 60;
            data[i * 4 + 3] = (byte)(x < 100 ? 255 : 128);   // the right half is half transparent
        }
        var path = Path.Combine(_root, "logo.png");
        using (var logo = new MagickImage())
        {
            logo.ReadPixels(data, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGBA));
            logo.Write(path);
        }

        var panel = await OpenAsync(path);
        panel.Exposure = 0.5;
        panel.Vignette = 30;
        await panel.RunAsync();

        Assert.True(panel.IsDone, panel.Status);
        var output = Assert.Single(panel.Outputs);
        Assert.Equal(Path.Combine(_root, "logo editado.png"), output);
        using var saved = new MagickImage(output);
        Assert.True(saved.HasAlpha);
        using var pixels = saved.GetPixels();
        var rgba = pixels.ToByteArray(PixelMapping.RGBA)!;
        Assert.Equal(255, rgba[(50 * width + 20) * 4 + 3]);
        Assert.Equal(128, rgba[(50 * width + 150) * 4 + 3]);
        Assert.True(rgba[(50 * width + 100) * 4] > 200);     // lighter than the 200 it was (exposure), mid-height centre
    }

    [AvaloniaFact]
    public async Task An_unreadable_file_shows_an_error_and_cannot_be_applied()
    {
        var path = Path.Combine(_root, "rota.jpg");
        File.WriteAllText(path, "this is not a photo");
        var panel = new EditImagePanel(Context(path));
        await WaitAsync(() => !panel.IsLoading);

        Assert.Equal(panel.Context["panel.editImage.cannot_open"], panel.LoadError);   // not Magick's file-and-line message
        Assert.Equal("No se puede abrir esta foto.", panel.LoadError);
        panel.Exposure = 0.5;
        Assert.False(panel.CanApply);
        Render(panel, "editimage-error");
    }

    [AvaloniaFact]
    public async Task A_photo_that_vanishes_before_apply_fails_cleanly_and_leaves_nothing_behind()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        panel.Exposure = 0.5;
        File.Delete(photo);

        await panel.RunAsync();

        Assert.True(panel.HasError, panel.Status);
        Assert.False(panel.IsDone);
        Assert.Empty(panel.Outputs);
        Assert.Empty(Directory.GetFiles(_root));
    }

    private static (byte B, byte G, byte R, byte A) BgraAt(Bitmap bitmap, int x, int y)
    {
        using var frame = Assert.IsType<WriteableBitmap>(bitmap).Lock();
        var bytes = new byte[4];
        Marshal.Copy(frame.Address + y * frame.RowBytes + x * 4, bytes, 0, 4);
        return (bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    [AvaloniaFact]
    public async Task The_preview_shows_the_real_colours_and_the_transparency_of_the_photo()
    {
        var path = Path.Combine(_root, "naranja.png");
        var data = new byte[60 * 40 * 4];
        for (var i = 0; i < 60 * 40; i++)
        {
            data[i * 4] = 255;
            data[i * 4 + 1] = 128;
            data[i * 4 + 2] = 0;
            data[i * 4 + 3] = (byte)(i % 60 < 30 ? 255 : 128);
        }
        using (var image = new MagickImage())
        {
            image.ReadPixels(data, new PixelReadSettings(60, 40, StorageType.Char, PixelMapping.RGBA));
            image.Write(path);
        }

        var panel = await OpenAsync(path);
        Assert.Equal((0, 128, 255, 255), BgraAt(panel.Original!, 10, 10));     // orange: blue 0, green 128, red 255, opaque
        Assert.Equal((0, 128, 255, 128), BgraAt(panel.Original!, 45, 10));     // the right half stays half transparent

        panel.Exposure = 1;
        await WaitForPreviewAsync(panel);
        var (b, g, r, a) = BgraAt(panel.Edited!, 10, 10);
        Assert.True(r == 255 && g > 128 && a == 255, $"{r},{g},{b},{a}");     // brighter, still opaque
        Assert.Equal(128, BgraAt(panel.Edited!, 45, 10).A);
    }

    [AvaloniaFact]
    public async Task A_cmyk_photo_is_shown_in_its_real_colours()
    {
        using var rgb = new MagickImage("plasma:orange-purple", 120, 90);
        var path = Path.Combine(_root, "imprenta.jpg");
        using (var cmyk = (MagickImage)rgb.Clone())
        {
            cmyk.ColorSpace = ColorSpace.CMYK;
            cmyk.Quality = 100;
            cmyk.Write(path);
        }
        var expected = At(Rgb(rgb), 120, 60, 45);

        var panel = await OpenAsync(path);
        var (_, g, r, a) = BgraAt(panel.Original!, 60, 45);
        Assert.Equal(255, a);
        Assert.InRange(r, expected.R - 40, expected.R + 40);   // not C, M and Y read as R, G and B
        Assert.InRange(g, expected.G - 40, expected.G + 40);
    }

    [AvaloniaFact]
    public async Task The_preview_of_a_big_photo_with_every_control_on_arrives_quickly()
    {
        var photo = MakePhoto(Path.Combine(_root, "enorme.jpg"), 3000, 2000);
        var panel = await OpenAsync(photo);
        Assert.Equal(1200, (int)panel.Original!.PixelSize.Width);                 // the preview works on a 1200 px copy

        var clock = Stopwatch.StartNew();
        panel.SetSettings(Factory with
        {
            Exposure = 0.3, Brightness = 10, Contrast = 20, Highlights = -30, Shadows = 30, Whites = 10, Blacks = -10, Gamma = 1.1,
            Saturation = 10, Vibrance = 20, Temperature = 10, Tint = -5, Hue = 5, Sharpness = 40, Clarity = 30, NoiseReduction = 30,
            Sepia = 10, Vignette = 30, Grain = 20,
        });
        await WaitForPreviewAsync(panel);
        TestContext.Current.SendDiagnosticMessage($"Panel preview, every control on, 3000x2000 photo: {clock.ElapsedMilliseconds} ms");
        Assert.NotNull(panel.Edited);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");   // a wide bound: Debug builds are slower
    }

    private static Point CentreOf(Control control, Visual window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [AvaloniaFact]
    public async Task The_compare_button_shows_the_original_while_the_mouse_is_held_down()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        panel.Exposure = 0.5;
        await WaitForPreviewAsync(panel);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CompareButton");
        var centre = CentreOf(button, window);

        Assert.Same(panel.Edited, panel.Shown);
        window.MouseDown(centre, MouseButton.Left);
        Assert.True(panel.ShowOriginal);
        Assert.Same(panel.Original, panel.Shown);
        window.MouseUp(centre, MouseButton.Left);
        Assert.False(panel.ShowOriginal);
        Assert.Same(panel.Edited, panel.Shown);

        // Let go somewhere else: the hold still ends.
        var away = new Point(centre.X + 300, centre.Y - 200);
        window.MouseDown(centre, MouseButton.Left);
        Assert.True(panel.ShowOriginal);
        window.MouseMove(away);
        window.MouseUp(away, MouseButton.Left);
        Assert.False(panel.ShowOriginal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_a_group_button_opens_its_controls_and_clicking_it_again_keeps_it_open()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        var window = new ToolPanelWindow(panel);
        window.Show();
        Pump();
        ToggleButton Tab(string key) => window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == panel.Context[key]);
        var (light, colour, effects) = (Tab("panel.editImage.tab_light"), Tab("panel.editImage.tab_color"), Tab("panel.editImage.tab_effects"));
        Assert.True(light.IsChecked);

        window.MouseDown(CentreOf(colour, window), MouseButton.Left);
        window.MouseUp(CentreOf(colour, window), MouseButton.Left);
        Pump();
        Assert.Equal(1, panel.Tab);
        Assert.True(colour.IsChecked);
        Assert.False(light.IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<SliderRow>(), row => row.Label == panel.Context["panel.editImage.temperature"] && row.IsEffectivelyVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<SliderRow>(), row => row.Label == panel.Context["panel.editImage.exposure"] && row.IsEffectivelyVisible);

        window.MouseDown(CentreOf(colour, window), MouseButton.Left);   // the open one, again
        window.MouseUp(CentreOf(colour, window), MouseButton.Left);
        Pump();
        Assert.Equal(1, panel.Tab);
        Assert.True(colour.IsChecked);

        window.MouseDown(CentreOf(effects, window), MouseButton.Left);
        window.MouseUp(CentreOf(effects, window), MouseButton.Left);
        Pump();
        Assert.Equal(3, panel.Tab);
        Assert.False(colour.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_the_panel_while_the_preview_works_is_harmless()
    {
        var photo = MakePhoto(Path.Combine(_root, "foto.jpg"));
        var panel = await OpenAsync(photo);
        panel.Clarity = 60;
        panel.Dispose();                                // the window closed with a preview still being worked
        await Task.Delay(300);
        Dispatcher_RunJobs();
        Assert.Null(panel.PreviewError);
    }

    private static void Dispatcher_RunJobs() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
