using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ACadSharp.Formats.Image;

/// <summary>
/// 纯托管（零外部依赖）的光栅图辅助工具：
/// <list type="bullet">
///   <item>JPEG 属性探测（尺寸/通道数，用于 PDF /DCTDecode 直嵌，无需解码）</item>
///   <item>PNG 解码（8-bit RGB/RGBA/Gray/GrayA/Palette → RGB(A) 字节，用于 PDF /FlateDecode+Predictor15）</item>
/// </list>
/// 所有实现均基于 PNG / JPEG 开放规范独立编写，不依赖 PDFsharp / SkiaSharp / System.Drawing，
/// 符合 Formats 零系统级依赖约定。
/// 外部 PDF 底图引用（PdfUnderlay）的矢量嵌入涉及完整 PDF 对象图克隆，按项目约定不引入第三方
/// PDF 库，渲染端降级为「引用框 + 信息」（见 EmitPdfUnderlayPdf），故此处无需 PDF 解析能力。
/// </summary>
internal static class RasterHelpers
{
    #region ===== JPEG 探测（不解码，直嵌 DCTDecode）=====
    public sealed class JpegInfo
    {
        public int Width;
        public int Height;
        public int Components; // 1=Gray 3=RGB 4=CMYK
    }

    /// <summary>探测 JPEG 文件尺寸与通道数。失败返回 null。</summary>
    public static JpegInfo ProbeJpeg(byte[] data)
    {
        if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }
            byte m = data[i + 1];
            // SOF0..SOF15 中排除 DHT(0xC4)/SOF8(0xC8)/SOF12(0xCC)
            if (m is >= 0xC0 and <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
            {
                int h = (data[i + 5] << 8) | data[i + 6];
                int w = (data[i + 7] << 8) | data[i + 8];
                int comp = data[i + 9];
                return new JpegInfo { Width = w, Height = h, Components = comp };
            }
            int len = (data[i + 2] << 8) | data[i + 3];
            i += 2 + len;
        }
        return null;
    }
    #endregion

    #region ===== PNG 解码（8-bit）=====
    /// <summary>解码 PNG 为像素字节。返回 channels: 3(RGB)/4(RGBA)。失败抛异常。</summary>
    public static (int width, int height, int channels, byte[] pixels) DecodePngToRgb(byte[] png)
    {
        if (png == null || png.Length < 8 || !(png[0] == 0x89 && png[1] == 0x50)) throw new Exception("非 PNG");
        int w = 0, h = 0, bitDepth = 0, colorType = 0;
        byte[] palette = null; byte[] trans = null;
        var idat = new List<byte>();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            string type = Encoding.ASCII.GetString(png, p + 4, 4);
            byte[] chunk = new byte[len];
            Array.Copy(png, p + 8, chunk, 0, len);
            if (type == "IHDR")
            {
                w = (chunk[0] << 24) | (chunk[1] << 16) | (chunk[2] << 8) | chunk[3];
                h = (chunk[4] << 24) | (chunk[5] << 16) | (chunk[6] << 8) | chunk[7];
                bitDepth = chunk[8]; colorType = chunk[9];
            }
            else if (type == "PLTE") palette = chunk;
            else if (type == "tRNS") trans = chunk;
            else if (type == "IDAT") idat.AddRange(chunk);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        if (w == 0 || h == 0) throw new Exception("PNG 无 IHDR");
        if (bitDepth != 8) throw new Exception("仅支持 8-bit PNG");

        // 解压 IDAT（zlib）
        var raw = InflateZlib(idat);
        int channels = colorType switch
        {
            0 => 1,  // Gray
            2 => 3,  // RGB
            3 => 3,  // Palette → 转 RGB
            4 => 2,  // Gray+Alpha
            6 => 4,  // RGBA
            _ => throw new Exception("不支持的颜色类型 " + colorType)
        };
        int stride = w * channels;
        byte[] outp = new byte[h * stride];
        // 逐行还原 filter
        int pos = 0;
        byte[] prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            byte ft = raw[pos++];
            byte[] cur = new byte[stride];
            for (int x = 0; x < stride; x++)
            {
                byte rawb = raw[pos++];
                byte a = x >= channels ? cur[x - channels] : (byte)0;
                byte b = prev[x];
                byte c = x >= channels ? prev[x - channels] : (byte)0;
                byte v = ft switch
                {
                    0 => rawb,
                    1 => (byte)(rawb + a),
                    2 => (byte)(rawb + b),
                    3 => (byte)(rawb + ((a + b) / 2)),
                    4 => (byte)(rawb + Paeth(a, b, c)),
                    _ => rawb
                };
                cur[x] = v;
            }
            Array.Copy(cur, 0, outp, y * stride, stride);
            prev = cur;
        }

        // 转 RGB(A)
        if (colorType == 2) return (w, h, 3, outp);
        if (colorType == 6) return (w, h, 4, outp);
        if (colorType == 4) // Gray+Alpha → RGBA
        {
            byte[] rgba = new byte[w * h * 4];
            for (int k = 0, j = 0; k < outp.Length; k += 2, j += 4)
            { rgba[j] = rgba[j + 1] = rgba[j + 2] = outp[k]; rgba[j + 3] = outp[k + 1]; }
            return (w, h, 4, rgba);
        }
        if (colorType == 0) // Gray → RGBA (alpha=255)
        {
            byte[] rgba = new byte[w * h * 4];
            for (int k = 0, j = 0; k < outp.Length; k++, j += 4)
            { rgba[j] = rgba[j + 1] = rgba[j + 2] = outp[k]; rgba[j + 3] = 255; }
            return (w, h, 4, rgba);
        }
        if (colorType == 3) // Palette → RGB(A)
        {
            byte[] rgba = new byte[w * h * 4];
            bool hasTrans = trans != null && trans.Length > 0;
            for (int k = 0, j = 0; k < outp.Length; k++, j += 4)
            {
                int idx = outp[k];
                rgba[j] = palette[idx * 3];
                rgba[j + 1] = palette[idx * 3 + 1];
                rgba[j + 2] = palette[idx * 3 + 2];
                rgba[j + 3] = (hasTrans && idx < trans.Length) ? trans[idx] : (byte)255;
            }
            return (w, h, 4, rgba);
        }
        throw new Exception("unreachable");
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int pp = a + b - c;
        int pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static byte[] InflateZlib(List<byte> idatChunks)
    {
        if (idatChunks == null || idatChunks.Count < 2) return Array.Empty<byte>();
        var all = idatChunks.ToArray();
        // 跳过 zlib 2 字节头
        using var ms = new MemoryStream(all, 2, all.Length - 2);
        using var def = new DeflateStream(ms, CompressionMode.Decompress);
        using var outms = new MemoryStream();
        def.CopyTo(outms);
        return outms.ToArray();
    }
    #endregion
}
