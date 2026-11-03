using System.IO.Compression;
using System.Text;

namespace FactorioModStudio.Core;

/// <summary>Minimal PNG writer (8-bit RGBA, no interlacing). Output is deterministic for the same pixels.</summary>
internal static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteBigEndian(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        WriteBigEndian(s, (uint)data.Length);
        s.Write(typeBytes);
        s.Write(data);
        WriteBigEndian(s, Crc32(typeBytes, data));
    }

    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("Pixel buffer has the wrong size.", nameof(rgba));

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        using (var header = new MemoryStream())
        {
            WriteBigEndian(header, (uint)width);
            WriteBigEndian(header, (uint)height);
            header.Write(new byte[] { 8, 6, 0, 0, 0 }); // 8 bit, RGBA, deflate, adaptive filtering, no interlace
            WriteChunk(output, "IHDR", header.ToArray());
        }

        using (var raw = new MemoryStream())
        {
            var stride = width * 4;
            for (var y = 0; y < height; y++)
            {
                raw.WriteByte(0); // filter: none
                raw.Write(rgba, y * stride, stride);
            }

            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw.GetBuffer(), 0, (int)raw.Length);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }
}

/// <summary>Tiny anti-aliased raster canvas used to draw the generated sprites. No external assets.</summary>
internal sealed class Canvas
{
    public delegate (double R, double G, double B) Paint(double x, double y, double distance);

    private readonly double[] _px; // r, g, b (0-255, straight) and alpha (0-1)

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        _px = new double[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Paints every pixel whose signed distance (negative = inside) is covered; the edge is anti-aliased.</summary>
    public void Fill(Func<double, double, double> distance, Paint paint, double alpha = 1)
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            double px = x + 0.5, py = y + 0.5;
            var d = distance(px, py);
            var coverage = Math.Clamp(0.5 - d, 0, 1) * alpha;
            if (coverage <= 0) continue;
            var (r, g, b) = paint(px, py, d);
            var i = (y * Width + x) * 4;
            var dstA = _px[i + 3];
            var outA = coverage + dstA * (1 - coverage);
            _px[i] = (r * coverage + _px[i] * dstA * (1 - coverage)) / outA;
            _px[i + 1] = (g * coverage + _px[i + 1] * dstA * (1 - coverage)) / outA;
            _px[i + 2] = (b * coverage + _px[i + 2] * dstA * (1 - coverage)) / outA;
            _px[i + 3] = outA;
        }
    }

    public byte[] ToPng()
    {
        var bytes = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            var a = _px[i * 4 + 3];
            if (a <= 0) continue;
            for (var c = 0; c < 3; c++) bytes[i * 4 + c] = (byte)Math.Clamp(Math.Round(_px[i * 4 + c]), 0, 255);
            bytes[i * 4 + 3] = (byte)Math.Clamp(Math.Round(a * 255), 0, 255);
        }
        return PngEncoder.Encode(Width, Height, bytes);
    }

    // ---- shapes (signed distance functions)

    public static double RoundedBox(double x, double y, double cx, double cy, double hx, double hy, double radius)
    {
        var qx = Math.Abs(x - cx) - (hx - radius);
        var qy = Math.Abs(y - cy) - (hy - radius);
        var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    public static double Circle(double x, double y, double cx, double cy, double r) =>
        Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

    public static double Ring(double x, double y, double cx, double cy, double r, double thickness) =>
        Math.Abs(Circle(x, y, cx, cy, r)) - thickness / 2;

    public static double RotatedBox(double x, double y, double cx, double cy, double angle, double hx, double hy, double radius)
    {
        double dx = x - cx, dy = y - cy;
        double cos = Math.Cos(-angle), sin = Math.Sin(-angle);
        return RoundedBox(dx * cos - dy * sin, dx * sin + dy * cos, 0, 0, hx, hy, radius);
    }
}

/// <summary>Deterministic generated artwork for the scaffolded mods. The colour scheme derives from the mod name.</summary>
internal static class ScaffoldArt
{
    private static (double R, double G, double B) Hsv(double hue, double s, double v)
    {
        hue = ((hue % 360) + 360) % 360 / 60;
        var c = v * s;
        var x = c * (1 - Math.Abs(hue % 2 - 1));
        var (r, g, b) = (int)hue switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        var m = v - c;
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }

    private static (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    /// <summary>FNV-1a over the UTF-8 bytes of the name, mapped to a hue.</summary>
    public static double HueFor(string name)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(name)) hash = (hash ^ b) * 16777619u;
        return hash % 360;
    }

    private static double Gear(double x, double y, double cx, double cy, double radius)
    {
        var d = Canvas.Ring(x, y, cx, cy, radius, radius * 0.36);
        d = Math.Min(d, Canvas.Circle(x, y, cx, cy, radius * 0.3));
        for (var k = 0; k < 8; k++)
        {
            var a = k * Math.PI / 4;
            d = Math.Min(d, Canvas.RotatedBox(x, y, cx + Math.Cos(a) * radius * 1.12, cy + Math.Sin(a) * radius * 1.12, a,
                radius * 0.2, radius * 0.17, radius * 0.06));
        }
        return d;
    }

    /// <summary>144x144 mod portal thumbnail: dark tinted tile with a light gear.</summary>
    public static byte[] Thumbnail(string name)
    {
        var hue = HueFor(name);
        var canvas = new Canvas(144, 144);
        var top = Hsv(hue, 0.50, 0.36);
        var bottom = Hsv(hue, 0.65, 0.16);
        canvas.Fill((_, _) => -1, (_, y, _) => Mix(top, bottom, y / 144));

        var tileEdge = Hsv(hue, 0.45, 0.65);
        var tileTop = Hsv(hue, 0.40, 0.30);
        var tileBottom = Hsv(hue, 0.55, 0.20);
        canvas.Fill((x, y) => Canvas.RoundedBox(x, y, 72, 72, 56, 56, 16),
            (_, y, d) => d > -2.5 ? tileEdge : Mix(tileTop, tileBottom, (y - 16) / 112));

        var gearLight = Hsv(hue, 0.18, 0.97);
        var gearDark = Hsv(hue, 0.35, 0.78);
        canvas.Fill((x, y) => Gear(x, y, 72, 72, 26), (_, y, _) => Mix(gearLight, gearDark, (y - 30) / 84));
        return canvas.ToPng();
    }

    /// <summary>64x64 item icon: a bevelled ingot.</summary>
    public static byte[] Ingot(string name)
    {
        var hue = HueFor(name);
        var canvas = new Canvas(64, 64);
        var outline = Hsv(hue, 0.75, 0.30);
        var light = Hsv(hue, 0.30, 0.98);
        var dark = Hsv(hue, 0.65, 0.70);
        canvas.Fill((x, y) => Canvas.RoundedBox(x, y, 32, 36, 24, 15, 6),
            (_, y, d) => d > -2 ? outline : Mix(light, dark, (y - 21) / 30));
        canvas.Fill((x, y) => Canvas.RoundedBox(x, y, 32, 31, 15, 5, 3),
            (_, _, _) => (255, 255, 255), 0.35);
        return canvas.ToPng();
    }

    private static void Chest(Canvas canvas, double cx, double cy, double s, double hue)
    {
        var outline = Hsv(hue, 0.70, 0.28);
        var bodyTop = Hsv(hue, 0.45, 0.85);
        var bodyBottom = Hsv(hue, 0.65, 0.58);
        var lid = Hsv(hue, 0.55, 0.50);
        var strap = Hsv(hue, 0.30, 0.40);
        var latch = Hsv(hue, 0.15, 0.97);

        canvas.Fill((x, y) => Canvas.RoundedBox(x, y, cx, cy, 22 * s, 17 * s, 3 * s), (x, y, d) =>
        {
            if (d > -1.4 * s) return outline;
            if (y < cy - 17 * s + 12 * s) return Mix(lid, Mix(lid, bodyTop, 0.3), (y - (cy - 17 * s)) / (12 * s));
            if (Math.Abs(Math.Abs(x - cx) - 13 * s) < 1.8 * s) return strap;
            return Mix(bodyTop, bodyBottom, (y - cy) / (17 * s) / 2 + 0.5);
        });
        canvas.Fill((x, y) => Canvas.RoundedBox(x, y, cx, cy - 4.5 * s, 3.2 * s, 4.2 * s, 1 * s),
            (_, _, d) => d > -0.9 * s ? outline : latch);
    }

    /// <summary>64x64 item icon of the chest.</summary>
    public static byte[] ChestIcon(string name)
    {
        var canvas = new Canvas(64, 64);
        Chest(canvas, 32, 32, 1.3, HueFor(name));
        return canvas.ToPng();
    }

    /// <summary>64x64 entity picture of the chest (drawn at scale 0.5 in game): slightly smaller, with a soft shadow.</summary>
    public static byte[] ChestPicture(string name)
    {
        var canvas = new Canvas(64, 64);
        canvas.Fill((x, y) => Canvas.RoundedBox(x, y, 32, 54, 24, 5, 5), (_, _, _) => (0, 0, 0), 0.28);
        Chest(canvas, 32, 31, 1.2, HueFor(name));
        return canvas.ToPng();
    }

    /// <summary>256x256 technology icon: a disc with a ring around the chest.</summary>
    public static byte[] TechnologyIcon(string name)
    {
        var hue = HueFor(name);
        var canvas = new Canvas(256, 256);
        var discTop = Hsv(hue, 0.45, 0.34);
        var discBottom = Hsv(hue, 0.60, 0.16);
        canvas.Fill((x, y) => Canvas.Circle(x, y, 128, 128, 118), (_, y, _) => Mix(discTop, discBottom, y / 256));
        var ring = Hsv(hue, 0.35, 0.92);
        canvas.Fill((x, y) => Canvas.Ring(x, y, 128, 128, 108, 8), (_, _, _) => ring);
        Chest(canvas, 128, 126, 3.6, hue);
        return canvas.ToPng();
    }
}
