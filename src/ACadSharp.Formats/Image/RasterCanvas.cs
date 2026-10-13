using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ACadSharp.Formats.Image;

/// <summary>
/// 纯托管软件光栅化器（零外部依赖，替代外部位图库）。
/// 提供：RGBA8 像素缓冲、扫描线填充（nonzero / even-odd + 边缘抗锯齿）、变宽折线描边（圆角连接/端点）、
/// 圆盘填充、线性/径向渐变填充、PNG 编码（ZLibStream + CRC32）。
/// 坐标系：像素，原点左上，+x 向右、+y 向下（与位图一致）。所有运算均为托管实现，无 Skia 依赖。
/// </summary>
internal sealed class RasterCanvas
{
    /// <summary>像素坐标点（浮点，画布系）。</summary>
    public readonly struct P
    {
        public readonly float X, Y;
        public P(float x, float y) { X = x; Y = y; }
    }

    /// <summary>不透明 RGB 颜色。</summary>
    public readonly struct Rgb
    {
        public readonly byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public static readonly Rgb White = new Rgb(255, 255, 255);
        public static readonly Rgb Black = new Rgb(0, 0, 0);
    }

    public int Width { get; }
    public int Height { get; }
    private readonly byte[] _px; // RGBA8，长度 = W*H*4

    public RasterCanvas(int w, int h)
    {
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        Width = w; Height = h;
        _px = new byte[w * h * 4];
    }

    // ===== 基础：清屏 / 像素混合（over 算子）=====

    /// <summary>用给定颜色（含 alpha）清屏。</summary>
    public void Clear(byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i < _px.Length; i += 4)
        {
            _px[i] = r; _px[i + 1] = g; _px[i + 2] = b; _px[i + 3] = a;
        }
    }

    private void Blend(int x, int y, byte r, byte g, byte b, float a)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || a <= 0f) return;
        int idx = (y * Width + x) * 4;
        if (a >= 1f)
        {
            _px[idx] = r; _px[idx + 1] = g; _px[idx + 2] = b; _px[idx + 3] = 255;
            return;
        }
        float sa = a, da = _px[idx + 3] / 255f;
        float oa = sa + da * (1f - sa);
        if (oa <= 0f) return;
        float inv = 1f / oa;
        _px[idx]     = (byte)((r * sa + _px[idx]     * da * (1f - sa)) * inv);
        _px[idx + 1] = (byte)((g * sa + _px[idx + 1] * da * (1f - sa)) * inv);
        _px[idx + 2] = (byte)((b * sa + _px[idx + 2] * da * (1f - sa)) * inv);
        _px[idx + 3] = (byte)(oa * 255f);
    }

    // ===== 贝塞尔扁平化（供 PolyBuilder 与字形轮廓复用）=====

    /// <summary>二次贝塞尔（起点为 last，控制点 cx/cy，终点 x1/y1）扁平化为折线追加到 outp。</summary>
    public static void FlattenQuad(float x0, float y0, float cx, float cy, float x1, float y1, List<P> outp)
    {
        const int N = 16;
        for (int i = 1; i <= N; i++)
        {
            float t = i / (float)N, u = 1 - t;
            float x = u * u * x0 + 2 * u * t * cx + t * t * x1;
            float y = u * u * y0 + 2 * u * t * cy + t * t * y1;
            outp.Add(new P(x, y));
        }
    }

    /// <summary>三次贝塞尔扁平化为折线追加到 outp。</summary>
    public static void FlattenCubic(float x0, float y0, float c1x, float c1y, float c2x, float c2y, float x1, float y1, List<P> outp)
    {
        const int N = 24;
        for (int i = 1; i <= N; i++)
        {
            float t = i / (float)N, u = 1 - t;
            float x = u * u * u * x0 + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x1;
            float y = u * u * u * y0 + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y1;
            outp.Add(new P(x, y));
        }
    }

    // ===== 填充 =====

    /// <summary>填充多个闭合轮廓（每个轮廓按闭合环处理）。evenOdd=true 用奇偶规则，否则非零环绕规则。</summary>
    public void Fill(List<List<P>> contours, bool evenOdd, Rgb color)
        => ScanFill(contours, evenOdd, (_, _) => color);

    /// <summary>渐变填充：t 沿 (gradA→gradB) 线性插值，或沿 (cx,cy) 半径 radial 插值，取 c1→c2。</summary>
    public void FillGradient(List<List<P>> contours, bool evenOdd, P gradA, P gradB, Rgb c1, Rgb c2, bool radial, float cx, float cy, float radius)
    {
        float dx = gradB.X - gradA.X, dy = gradB.Y - gradA.Y;
        float len2 = dx * dx + dy * dy;
        ScanFill(contours, evenOdd, (px, py) =>
        {
            float t;
            if (radial)
            {
                float ddx = px - cx, ddy = py - cy;
                t = radius > 1e-6f ? (float)Math.Sqrt(ddx * ddx + ddy * ddy) / radius : 0f;
            }
            else
            {
                t = len2 > 1e-9f ? ((px - gradA.X) * dx + (py - gradA.Y) * dy) / len2 : 0f;
            }
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return new Rgb(
                (byte)(c1.R + (c2.R - c1.R) * t),
                (byte)(c1.G + (c2.G - c1.G) * t),
                (byte)(c1.B + (c2.B - c1.B) * t));
        });
    }

    private void ScanFill(List<List<P>> contours, bool evenOdd, Func<int, int, Rgb> colorAt)
    {
        if (contours == null || contours.Count == 0) return;
        float ymin = float.MaxValue, ymax = float.MinValue;
        foreach (var c in contours) foreach (var p in c) { if (p.Y < ymin) ymin = p.Y; if (p.Y > ymax) ymax = p.Y; }
        if (ymin > ymax) return;
        int y0 = Math.Max(0, (int)Math.Floor(ymin));
        int y1 = Math.Min(Height - 1, (int)Math.Ceiling(ymax));
        if (y0 > y1) return;

        var xs = new List<float>(64);
        var ds = new List<int>(64);
        for (int py = y0; py <= y1; py++)
        {
            float y = py + 0.5f;
            xs.Clear(); ds.Clear();
            foreach (var c in contours)
            {
                int n = c.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = c[i];
                    var b = c[(i + 1) % n];
                    if ((a.Y > y) != (b.Y > y))
                    {
                        float t = (y - a.Y) / (b.Y - a.Y);
                        xs.Add(a.X + t * (b.X - a.X));
                        ds.Add(b.Y > a.Y ? 1 : -1);
                    }
                }
            }
            int m = xs.Count;
            if (m == 0) continue;

            // 按 x 排序，dirs 同步
            var order = new int[m];
            for (int i = 0; i < m; i++) order[i] = i;
            Array.Sort(order, (p, q) => xs[p].CompareTo(xs[q]));

            if (evenOdd)
            {
                for (int i = 0; i + 1 < m; i += 2)
                    FillScanSpan(xs[order[i]], xs[order[i + 1]], py, colorAt);
            }
            else
            {
                int winding = 0; bool inside = false; float startX = 0;
                for (int i = 0; i < m; i++)
                {
                    winding += ds[order[i]];
                    bool now = winding != 0;
                    if (now && !inside) { startX = xs[order[i]]; inside = true; }
                    else if (!now && inside) { FillScanSpan(startX, xs[order[i]], py, colorAt); inside = false; }
                }
            }
        }
    }

    private void FillScanSpan(float xa, float xb, int py, Func<int, int, Rgb> colorAt)
    {
        if (xb - xa < 1e-6f) return;
        int cx0 = (int)Math.Floor(xa);
        int cx1 = (int)Math.Floor(xb);
        for (int cx = cx0; cx <= cx1; cx++)
        {
            float cov = 1f;
            if (cx == cx0) cov = Math.Min(1f, (cx + 1) - xa);
            if (cx == cx1) cov = Math.Min(cov, xb - cx);
            if (cov <= 0f) continue;
            var col = colorAt(cx, py);
            Blend(cx, py, col.R, col.G, col.B, cov);
        }
    }

    // ===== 描边 =====

    /// <summary>变宽折线描边（数据单位坐标需先转像素）。圆角连接与端点（round join/cap）。</summary>
    public void StrokePolyline(List<P> pts, bool closed, float width, Rgb color)
    {
        if (pts == null || pts.Count < 2 || width <= 0) return;
        var polys = new List<List<P>>(pts.Count);
        float h = width / 2;
        int n = pts.Count;
        int segCount = closed ? n : n - 1;
        for (int i = 0; i < segCount; i++)
        {
            var p0 = pts[i];
            var p1 = pts[(i + 1) % n];
            float dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float nx, ny;
            if (len < 1e-9f) { nx = 0; ny = 0; }
            else { nx = -dy / len * h; ny = dx / len * h; }
            polys.Add(new List<P> {
                new P(p0.X + nx, p0.Y + ny),
                new P(p1.X + nx, p1.Y + ny),
                new P(p1.X - nx, p1.Y - ny),
                new P(p0.X - nx, p0.Y - ny) });
        }
        // 顶点圆盘：开放路径给两端点/内部点圆角连接，闭合路径给各顶点连接
        for (int i = 0; i < n; i++)
            polys.Add(Disc(pts[i].X, pts[i].Y, h, 14));
        ScanFill(polys, false, (_, _) => color);
    }

    /// <summary>实心圆盘填充（点标记、halo 等）。</summary>
    public void FillDisc(float cx, float cy, float r, Rgb color)
    {
        if (r <= 0) return;
        var poly = Disc(cx, cy, r, 22);
        ScanFill(new List<List<P>> { poly }, false, (_, _) => color);
    }

    private static List<P> Disc(float cx, float cy, float r, int seg)
    {
        var p = new List<P>(seg);
        for (int i = 0; i < seg; i++)
        {
            double a = i * 2 * Math.PI / seg;
            p.Add(new P(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r));
        }
        return p;
    }

    // ===== PNG 编码（零依赖：ZLibStream + CRC32）=====

    private static readonly byte[] _pngSig = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] _crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data, int off, int len)
    {
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < len; i++)
            c = _crcTable[(c ^ data[off + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        byte[] len = new byte[4];
        WriteU32(len, 0, (uint)data.Length);
        s.Write(len, 0, 4);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        byte[] crcBuf = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcBuf, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, crcBuf, typeBytes.Length, data.Length);
        byte[] crcBytes = new byte[4];
        WriteU32(crcBytes, 0, Crc32(crcBuf, 0, crcBuf.Length));
        s.Write(crcBytes, 0, 4);
    }

    /// <summary>将当前像素缓冲编码为 PNG 写出。失败返回 false。</summary>
    public bool SavePng(string path)
    {
        try
        {
            using var fs = File.Create(path);
            fs.Write(_pngSig, 0, _pngSig.Length);

            var ihdr = new byte[13];
            WriteU32(ihdr, 0, (uint)Width);
            WriteU32(ihdr, 4, (uint)Height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // color type RGBA
            WriteChunk(fs, "IHDR", ihdr);

            var idat = new MemoryStream();
            using (var zs = new ZLibStream(idat, CompressionLevel.Optimal, true))
            {
                for (int y = 0; y < Height; y++)
                {
                    zs.WriteByte(0); // filter: none
                    int row = y * Width * 4;
                    zs.Write(_px, row, Width * 4);
                }
            }
            WriteChunk(fs, "IDAT", idat.ToArray());
            WriteChunk(fs, "IEND", Array.Empty<byte>());
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// 路径构建器：以与路径命令同语义的 MoveTo/LineTo/QuadTo/CubicTo 记录命令，
/// 最终扁平化为像素折线（封闭轮廓或开放折线），交给 RasterCanvas 填充/描边。
/// 用于把原 Png 引擎中基于路径命令的几何构建逻辑无缝迁移到托管光栅化器。
/// </summary>
internal sealed class PolyBuilder
{
    private sealed class Sub
    {
        public readonly List<RasterCanvas.P> Pts = new();
    }

    private readonly List<Sub> _subs = new();
    private Sub _cur;

    public PolyBuilder()
    {
        _cur = new Sub();
        _subs.Add(_cur);
    }

    public bool IsEmpty
    {
        get
        {
            foreach (var s in _subs) if (s.Pts.Count >= 2) return false;
            return true;
        }
    }

    public void MoveTo(float x, float y)
    {
        if (_cur.Pts.Count > 0) { _cur = new Sub(); _subs.Add(_cur); }
        _cur.Pts.Add(new RasterCanvas.P(x, y));
    }

    public void LineTo(float x, float y)
    {
        _cur.Pts.Add(new RasterCanvas.P(x, y));
    }

    public void QuadTo(float cx, float cy, float x, float y)
    {
        if (_cur.Pts.Count == 0) { _cur.Pts.Add(new RasterCanvas.P(cx, cy)); }
        var s = _cur.Pts[_cur.Pts.Count - 1];
        RasterCanvas.FlattenQuad(s.X, s.Y, cx, cy, x, y, _cur.Pts);
    }

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        if (_cur.Pts.Count == 0) { _cur.Pts.Add(new RasterCanvas.P(c1x, c1y)); }
        var s = _cur.Pts[_cur.Pts.Count - 1];
        RasterCanvas.FlattenCubic(s.X, s.Y, c1x, c1y, c2x, c2y, x, y, _cur.Pts);
    }

    /// <summary>闭合轮廓（每个子路径首尾相连）用于填充。</summary>
    public List<List<RasterCanvas.P>> ToContours()
    {
        var res = new List<List<RasterCanvas.P>>();
        foreach (var s in _subs)
        {
            if (s.Pts.Count >= 3)
            {
                var c = new List<RasterCanvas.P>(s.Pts);
                c.Add(c[0]);
                res.Add(c);
            }
        }
        return res;
    }

    /// <summary>开放/闭合折线（closed 时各子路径首尾相连）用于描边。</summary>
    public List<List<RasterCanvas.P>> ToPolylines(bool closed)
    {
        var res = new List<List<RasterCanvas.P>>();
        foreach (var s in _subs)
        {
            if (s.Pts.Count >= 2)
            {
                var c = new List<RasterCanvas.P>(s.Pts);
                if (closed) c.Add(c[0]);
                res.Add(c);
            }
        }
        return res;
    }
}
