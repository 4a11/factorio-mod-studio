using System.IO.Compression;

namespace FactorioModStudio.Core;

/// <summary>
/// Reads the PNGs the game ships its icons in (8-bit RGB or RGBA, not interlaced) and recolours them: every pixel takes the hue
/// of the given colour while its brightness stays, so the shading of the original survives and grey metal gets coloured too.
/// Mipmaps to the right of the icon are recoloured with it, so the file is laid out exactly like the original.
/// </summary>
public static class IconRecolor
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>The recoloured PNG, or null when <paramref name="png"/> is not a PNG this reader understands.</summary>
    public static byte[]? Recolor(byte[] png, double red, double green, double blue)
    {
        if (Decode(png) is not { } image) return null;
        var (targetHue, _, _) = ToHsv(red, green, blue);
        var rgba = image.Rgba;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            var (_, s, v) = ToHsv(rgba[i] / 255.0, rgba[i + 1] / 255.0, rgba[i + 2] / 255.0);
            var (r, g, b) = FromHsv(targetHue, Math.Clamp(0.35 + 0.65 * s, 0, 1), v);
            rgba[i] = (byte)Math.Round(r * 255);
            rgba[i + 1] = (byte)Math.Round(g * 255);
            rgba[i + 2] = (byte)Math.Round(b * 255);
        }
        return PngEncoder.Encode(image.Width, image.Height, rgba);
    }

    // ------------------------------------------------------------------ PNG reading

    internal sealed record Image(int Width, int Height, byte[] Rgba);

    internal static Image? Decode(byte[] png) => Decode(png, out _);

    /// <summary>
    /// Any non-interlaced PNG: grey, RGB, palette (with tRNS transparency), grey + alpha or RGBA; 8 bits per channel, 16 (the low
    /// byte is dropped), or 1/2/4 for palettes and grey. Null with <paramref name="problem"/> saying why when it cannot be read.
    /// </summary>
    internal static Image? Decode(byte[] png, out string? problem)
    {
        problem = "This is not a PNG file.";
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int width = 0, height = 0, depth = 0, colour = -1;
        byte[]? palette = null, transparency = null;
        using var idat = new MemoryStream();
        var pos = 8;
        problem = "The PNG file is damaged.";
        while (pos + 8 <= png.Length)
        {
            var length = (int)ReadUInt32(png, pos);
            if (length < 0 || pos + 12 + length > png.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR")
            {
                width = (int)ReadUInt32(png, pos + 8);
                height = (int)ReadUInt32(png, pos + 12);
                depth = png[pos + 16];
                colour = png[pos + 17];
                var interlace = png[pos + 20];
                if (interlace != 0) { problem = "Interlaced PNGs are not supported; save it without interlacing."; return null; }
                var allowed = colour switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    2 or 4 or 6 => depth is 8 or 16,
                    _ => false
                };
                if (!allowed) { problem = $"PNG colour type {colour} with {depth}-bit channels is not supported; save it as 8-bit RGBA."; return null; }
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192) { problem = "The picture is larger than 8192 pixels."; return null; }
            }
            else if (type == "PLTE") palette = png[(pos + 8)..(pos + 8 + length)];
            else if (type == "tRNS") transparency = png[(pos + 8)..(pos + 8 + length)];
            else if (type == "IDAT") idat.Write(png, pos + 8, length);
            else if (type == "IEND") break;
            pos += 12 + length;
        }
        if (colour < 0) return null;
        if (colour == 3 && palette is null) return null;

        var channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bitsPerPixel = channels * depth;
        var bpp = Math.Max(1, bitsPerPixel / 8); // the byte distance the filters look back
        var stride = (int)(((long)width * bitsPerPixel + 7) / 8);
        var raw = new byte[(long)(stride + 1) * height];
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var read = 0;
            int n;
            while (read < raw.Length && (n = z.Read(raw, read, raw.Length - read)) > 0) read += n;
            if (read != raw.Length) return null;
        }
        catch (InvalidDataException) { return null; }

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var dst = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? pixels[dst + x - bpp] : 0;
                int b = y > 0 ? pixels[dst - stride + x] : 0;
                int c = x >= bpp && y > 0 ? pixels[dst - stride + x - bpp] : 0;
                int value = raw[src + x];
                value += filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => -1000
                };
                if (value < -255) return null; // unknown filter
                pixels[dst + x] = (byte)value;
            }
        }

        // Sample number `index` of row `row`, as stored (for 16 bits: the high byte).
        int Sample(int row, int index)
        {
            if (depth == 8) return pixels[row * stride + index];
            if (depth == 16) return pixels[row * stride + index * 2];
            var bit = index * depth;
            var shift = 8 - depth - bit % 8;
            return pixels[row * stride + bit / 8] >> shift & ((1 << depth) - 1);
        }
        int Grey(int sample) => depth >= 8 ? sample : sample * 255 / ((1 << depth) - 1);

        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var o = (y * width + x) * 4;
            switch (colour)
            {
                case 0:
                {
                    var sample = Sample(y, x);
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)Grey(sample);
                    var key = transparency is { Length: >= 2 } ? transparency[0] << 8 | transparency[1] : -1;
                    var value = depth == 16 ? pixels[y * stride + x * 2] << 8 | pixels[y * stride + x * 2 + 1] : sample;
                    rgba[o + 3] = value == key ? (byte)0 : (byte)255;
                    break;
                }
                case 3:
                {
                    var i = Sample(y, x);
                    if (i * 3 + 2 >= palette!.Length) return null;
                    rgba[o] = palette[i * 3];
                    rgba[o + 1] = palette[i * 3 + 1];
                    rgba[o + 2] = palette[i * 3 + 2];
                    rgba[o + 3] = transparency is not null && i < transparency.Length ? transparency[i] : (byte)255;
                    break;
                }
                case 4:
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)Sample(y, x * 2);
                    rgba[o + 3] = (byte)Sample(y, x * 2 + 1);
                    break;
                case 2:
                    rgba[o] = (byte)Sample(y, x * 3);
                    rgba[o + 1] = (byte)Sample(y, x * 3 + 1);
                    rgba[o + 2] = (byte)Sample(y, x * 3 + 2);
                    rgba[o + 3] = 255;
                    if (transparency is { Length: >= 6 } && depth == 8 && rgba[o] == transparency[1] && rgba[o + 1] == transparency[3] && rgba[o + 2] == transparency[5])
                        rgba[o + 3] = 0;
                    break;
                default:
                    rgba[o] = (byte)Sample(y, x * 4);
                    rgba[o + 1] = (byte)Sample(y, x * 4 + 1);
                    rgba[o + 2] = (byte)Sample(y, x * 4 + 2);
                    rgba[o + 3] = (byte)Sample(y, x * 4 + 3);
                    break;
            }
        }
        problem = null;
        return new Image(width, height, rgba);
    }

    /// <summary>
    /// A picture the player chose as an icon, made into what the blocks' icons are: <paramref name="size"/>×<paramref name="size"/>
    /// RGBA. A square picture is scaled; a wider one is taken as an icon with mipmaps to its right (the game's layout), so its left
    /// square is used. Null with <paramref name="problem"/> when it cannot be read or is taller than wide.
    /// </summary>
    public static byte[]? FitIcon(byte[] png, int size, out string? problem, out (int Width, int Height) original)
    {
        original = (0, 0);
        if (Decode(png, out problem) is not { } image) return null;
        original = (image.Width, image.Height);
        if (image.Height > image.Width)
        {
            problem = $"The picture is {image.Width}×{image.Height}: an icon must be square (or wider, with mipmaps to the right).";
            return null;
        }
        if (image.Height < 8) { problem = $"The picture is only {image.Width}×{image.Height}; an icon needs at least 8×8 pixels."; return null; }
        return PngEncoder.Encode(size, size, Resample(image, image.Height, size));
    }

    // The left side×side square of the image scaled to size×size: each target pixel averages the source area it covers (weighted by
    // alpha, so transparent pixels do not darken the edges); enlarging repeats the nearest pixel, which keeps pixel art crisp.
    private static byte[] Resample(Image image, int side, int size)
    {
        var result = new byte[size * size * 4];
        var scale = (double)side / size;
        for (var ty = 0; ty < size; ty++)
        for (var tx = 0; tx < size; tx++)
        {
            int x0 = (int)Math.Floor(tx * scale), y0 = (int)Math.Floor(ty * scale);
            var x1 = Math.Min(side, Math.Max(x0 + 1, (int)Math.Ceiling((tx + 1) * scale)));
            var y1 = Math.Min(side, Math.Max(y0 + 1, (int)Math.Ceiling((ty + 1) * scale)));
            double r = 0, g = 0, b = 0, a = 0;
            var count = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * image.Width + x) * 4;
                var alpha = image.Rgba[i + 3] / 255.0;
                r += image.Rgba[i] * alpha;
                g += image.Rgba[i + 1] * alpha;
                b += image.Rgba[i + 2] * alpha;
                a += alpha;
                count++;
            }
            var o = (ty * size + tx) * 4;
            if (a > 0)
            {
                result[o] = (byte)Math.Round(r / a);
                result[o + 1] = (byte)Math.Round(g / a);
                result[o + 2] = (byte)Math.Round(b / a);
            }
            result[o + 3] = (byte)Math.Round(a / count * 255);
        }
        return result;
    }

    private static uint ReadUInt32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ------------------------------------------------------------------ colour

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return (r + m, g + m, b + m);
    }
}
