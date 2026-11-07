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

    internal static Image? Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var length = (int)ReadUInt32(png, pos);
            if (length < 0 || pos + 12 + length > png.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR")
            {
                width = (int)ReadUInt32(png, pos + 8);
                height = (int)ReadUInt32(png, pos + 12);
                var depth = png[pos + 16];
                var colour = png[pos + 17];
                var interlace = png[pos + 20];
                if (depth != 8 || interlace != 0 || colour is not (2 or 6)) return null;
                channels = colour == 6 ? 4 : 3;
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
            }
            else if (type == "IDAT") idat.Write(png, pos + 8, length);
            else if (type == "IEND") break;
            pos += 12 + length;
        }
        if (channels == 0) return null;

        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
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
                int a = x >= channels ? pixels[dst + x - channels] : 0;
                int b = y > 0 ? pixels[dst - stride + x] : 0;
                int c = x >= channels && y > 0 ? pixels[dst - stride + x - channels] : 0;
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

        if (channels == 4) return new Image(width, height, pixels);
        var rgba = new byte[width * height * 4];
        for (int i = 0, j = 0; i < pixels.Length; i += 3, j += 4)
        {
            rgba[j] = pixels[i];
            rgba[j + 1] = pixels[i + 1];
            rgba[j + 2] = pixels[i + 2];
            rgba[j + 3] = 255;
        }
        return new Image(width, height, rgba);
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
