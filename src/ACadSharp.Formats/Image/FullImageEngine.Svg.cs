using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.Formats.Pdf;
using CSMath;
// SHX 形状/字体解析现由核心 ACadSharp.Text 自行实现（零外部依赖，替代原 Fonts.Lib）。
using ACadSharp.Text;

namespace ACadSharp.Formats.Image;

/// <summary>图像引擎：DxfImageExporter（netDxf 输入版）SVG+PDF 侧的 ACadSharp 移植（partial，SVG 部分在此文件、PDF 部分在 FullImageEngine.Pdf.cs）。</summary>
internal partial class ImageEngine
{
    private readonly CadDocument _doc;
    private readonly ImageConfiguration _opt;
    private readonly Action<string> _log;
    private readonly bool _whiteBg;
    private readonly bool _transparentBg; // 仅 PNG 光栅端使用：true=画布不画底色（全透明 alpha）
    private bool _useRealTextWidth;       // PNG 光栅端置 true：文字对齐锚点用真实字体度量算宽（修正居中/右对正漂移）
    private double _ltScale = 1.0;
    private double _scale = 1.0;
    private double _minX = double.MaxValue, _minY = double.MaxValue, _maxX = double.MinValue, _maxY = double.MinValue;
    private double _margin = 2.0, _w = 1.0, _h = 1.0;

    private StringBuilder _svgDefs;
    private int _hatchGradSeq;
    private readonly Dictionary<string, int> _skippedCounts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double[]> _ltypeDash;
    private Dictionary<string, LtpPattern> _ltypePattern;
    private readonly Dictionary<string, ShxFont?> _shxFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GlyphRec?> _glyphCache = new(StringComparer.OrdinalIgnoreCase);

    public ImageEngine(CadDocument doc, ImageConfiguration opt)
    {
        _doc = doc;
        _opt = opt;
        _log = opt.Log ?? (_ => { });
        _whiteBg = opt.WhiteBackground;
        _transparentBg = opt.TransparentBackground;
        try
        {
            double v = doc.Header.LineTypeScale;
            if (double.IsFinite(v) && v > 0) _ltScale = v;
        }
        catch { }
    }

    #region ===== 颜色：AutoCAD 标准 ACI 调色板 =====
    // 使用 ACadSharp fork 内置官方 ACI 色表（Color.GetIndexRGB，1-255 精确值；
    // 已程序化验证与 netDxf AciColor.IndexRgb 逐字节一致，ACI 颜色 parity 成立）。
    private static readonly string[] AciHex = BuildAciHex();

    private static string[] BuildAciHex()
    {
        var t = new string[256];
        for (int i = 0; i < 256; i++) t[i] = "#FFFFFF";
        for (int i = 1; i <= 255; i++)
        {
            ReadOnlySpan<byte> rgb = Color.GetIndexRGB((byte)i);
            t[i] = "#" + rgb[0].ToString("X2") + rgb[1].ToString("X2") + rgb[2].ToString("X2");
        }
        return t;
    }

    // ACI 索引 → 十六进制颜色。ACI 7 是 AutoCAD 的"白/黑"色：
    // 黑底显示白色，白底显示黑色（与 AutoCAD 模型空间切换布局背景的行为一致）。
    private string AciColor(int idx)
    {
        if (idx <= 0 || idx >= 256) idx = 7;
        if (_whiteBg && idx == 7) return "#000000";
        return AciHex[idx];
    }

    // netDxf 一致：AciColor 对真色（420）存储最近 ACI 索引（FromTrueColor → RgbToAci），
    // 而 fork Color.Index 对真色为 -1 —— 故普通实体按 Index 取色前必须把真色换算为最近 ACI
    // （netDxf RgbToAci：RGB 空间暴力最近邻，精确匹配优先）。
    private static short TrueColorToAci(byte r, byte g, byte b)
    {
        int prevDist = int.MaxValue;
        short index = 0;
        for (int i = 1; i < 256; i++)
        {
            ReadOnlySpan<byte> color = Color.GetIndexRGB((byte)i);
            int red = r - color[0];
            int green = g - color[1];
            int blue = b - color[2];
            int dist = red * red + green * green + blue * blue;
            if (dist == 0) return (short)i; // RGB 恰好是某索引色
            if (dist < prevDist) { prevDist = dist; index = (short)i; }
        }
        return index;
    }

    // 等价 netDxf EntityObject.Color.Index：真色 → 最近 ACI (1..255)；否则原索引（0=ByBlock / 256=ByLayer）
    private static short EntityAciIndex(Entity ent)
    {
        var c = ent.Color;
        if (c.IsTrueColor) return TrueColorToAci(c.R, c.G, c.B);
        return c.Index;
    }

    private string ColorOf(short aci, short layerAci, short byBlockAci)
    {
        int idx;
        if (aci >= 1 && aci <= 255) idx = aci;   // 独立颜色
        else if (aci == 0) idx = byBlockAci;      // 随块 -> INSERT 颜色
        else idx = layerAci;                      // 随图层 (256) / 非法 -> 图层色
        return AciColor(idx);
    }

    // 等价 netDxf Layer.Color.Index（真色图层同样 = 最近 ACI）；层缺失 → 7
    private static short LayerAciIndex(Layer layer)
    {
        if (layer == null) return 7;
        var lc = layer.Color;
        return lc.IsTrueColor ? TrueColorToAci(lc.R, lc.G, lc.B) : lc.Index;
    }

    // 实体的有效 ACI 颜色（用于把 INSERT 颜色作为其块内容的 ByBlock 颜色传递下去）：
    //   独立颜色 (1-255) -> 该颜色；ByLayer (256) -> 自身图层色；ByBlock (0) -> 继承上层 ByBlock 色 fallback。
    private static short EffectiveAci(Entity ent, short fallback)
    {
        short aci;
        try { aci = EntityAciIndex(ent); } catch { return fallback; }
        if (aci >= 1 && aci <= 255) return aci;
        if (aci == 256)
        {
            try
            {
                var layer = ent.Layer;
                if (layer == null) return fallback;
                var lc = layer.Color;
                return lc.IsTrueColor ? TrueColorToAci(lc.R, lc.G, lc.B) : lc.Index;
            }
            catch { return fallback; }
        }
        return fallback; // ByBlock (0) -> 上层 ByBlock 色
    }

    #endregion

    #region ===== 线型：虚线/点划线模式 =====
    // 从文档 LTYPE 表构建"线型名 -> dasharray"缓存。
    // DXF 段长语义：正=实线段(dash)、负=空隙(gap)、0=点(dot)。
    // 简单线型（无 SHX Shape 段）：合并连续同类型段后输出交替的 [dash, gap, ...]。
    // 复杂线型（含 SHX Shape 段）：另构建 LtpPattern 模式段序列，导出时沿实体路径逐周期走查。
    private void BuildLinetypeDash()
    {
        _ltypeDash = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        _ltypePattern = new Dictionary<string, LtpPattern>(StringComparer.OrdinalIgnoreCase);
        if (_doc?.LineTypes == null) return;
        const double dot = 0.1;
        const double shapeUnit = 4.0; // SHX 字形经验基准尺寸：点 ≈ 缩放 × 4
        foreach (var lt in _doc.LineTypes)
        {
            if (lt == null || string.IsNullOrEmpty(lt.Name)) continue;
            var seglist0 = lt.Segments?.ToList() ?? new List<LineType.Segment>();
            if (seglist0.Count == 0)
            {
                _ltypeDash[lt.Name] = null; // Continuous 等无段线型 = 实线
                continue;
            }

            // [T4.8] 与 netDxf（DxfReader 后处理 L883-895）逐条一致：shape 段的 NAME 按【编号】
            // 从 SHX 库解析（Style.ShapeName(number)，不是编号字符串——SHX 名表键是形状名，
            // "149" 这类数字串查不到 → 回退短划）；库中无此形状（或字体文件缺失）时
            // netDxf 把该段从线型中【删除】，再用剩余段重建图案。
            List<LineType.Segment> seglist;
            string[] resolvedShapeNames = null; // 与 seglist 同序；仅 shape 段非空
            if (lt.HasShapes)
            {
                seglist = new List<LineType.Segment>(seglist0.Count);
                resolvedShapeNames = new string[seglist0.Count];
                int w = 0;
                for (int j = 0; j < seglist0.Count; j++)
                {
                    var s0 = seglist0[j];
                    if (s0.IsShape)
                    {
                        int shapeNum = (int)s0.ShapeNumber;
                        if (shapeNum < 0) shapeNum += 65536; // short 负值 = 高位形状号
                        var font = GetShapeFont(s0.Style?.Filename ?? "");
                        string sname = font != null ? font.GetShapeName(shapeNum) : "";
                        if (string.IsNullOrEmpty(sname)) continue; // 库中无此形状 → 删除该段（netDxf 语义）
                        resolvedShapeNames[w] = sname;
                    }
                    seglist.Add(s0);
                    w++;
                }
            }
            else
            {
                seglist = seglist0;
            }

            // 复杂线型（含 SHX Shape 段）：构建"模式段序列"供沿路径真实渲染（严格遵循 LTYPE 定义）
            if (lt.HasShapes)
            {
                int n0 = seglist.Count;
                var pat = new LtpPattern();
                for (int j = 0; j < n0; j++)
                {
                    var s0 = seglist[j];
                    double l = s0?.Length ?? 0;
                    if (s0.IsShape)
                    {
                        // 旋转类型与 netDxf 同位判定（DXF 74 bit0：1=absolute，0=relative；upright 不可达）
                        int rotType = s0.Flags.HasFlag(LineTypeShapeFlags.RotationIsAbsolute) ? 2 : 1;
                        pat.Segs.Add(new LtpSeg
                        {
                            Kind = LtpSeg.Shape,
                            Len = Math.Abs(l),
                            InGap = l <= 1e-9,
                            ShapeName = resolvedShapeNames[j],
                            ShapeFile = s0.Style?.Filename ?? "",
                            ShapeScale = s0.Scale > 0 ? s0.Scale : 1.0,
                            RotType = rotType,
                            ShapeRot = s0.Rotation * 180.0 / Math.PI, // fork 存弧度 → 转度（与参考实现单位一致）
                            OX = s0.Offset.X,
                            OY = s0.Offset.Y,
                        });
                    }
                    else if (s0.IsText)
                    {
                        // 文字段：不绘制文字本身，线在文字处断开 → 空隙
                        pat.Segs.Add(new LtpSeg { Kind = LtpSeg.Gap, Len = Math.Abs(l) });
                    }
                    else if (l > 1e-9) pat.Segs.Add(new LtpSeg { Kind = LtpSeg.Dash, Len = l });
                    else if (l < -1e-9) pat.Segs.Add(new LtpSeg { Kind = LtpSeg.Gap, Len = -l });
                    else
                    {
                        // 零长简单段 = 点；相邻形状段已有字形 → 跳过（避免双点）
                        var pv = seglist[(j - 1 + n0) % n0];
                        var nx2 = seglist[(j + 1) % n0];
                        if (!pv.IsShape && !nx2.IsShape)
                            pat.Segs.Add(new LtpSeg { Kind = LtpSeg.Dash, Len = dot });
                    }
                }
                // 合并连续同类型段（形状段永不合并——每个都是独立放置）
                var pm = new List<LtpSeg>();
                foreach (var s0 in pat.Segs)
                {
                    if (pm.Count > 0 && s0.Kind != LtpSeg.Shape && pm[pm.Count - 1].Kind == s0.Kind)
                        pm[pm.Count - 1].Len += s0.Len;
                    else pm.Add(s0);
                }
                pat.Segs.Clear();
                pat.Segs.AddRange(pm);
                // 形状放置位置（相对段起点）：
                //  - 笔落型（段长>0，符号落在实线上）：居中于自身段；
                //  - 间隙型（段长≤0，符号在空隙内，如 914C 的"点"）：居中于"总空隙"（AutoCAD 一致）。
                int pn = pat.Segs.Count;
                for (int j = 0; j < pn; j++)
                {
                    var s0 = pat.Segs[j];
                    if (s0.Kind != LtpSeg.Shape) continue;
                    if (!s0.InGap) { s0.CenterOffset = s0.Len / 2; continue; }
                    double gapBefore = pat.Segs[(j - 1 + pn) % pn].Kind == LtpSeg.Gap ? pat.Segs[(j - 1 + pn) % pn].Len : 0;
                    double gapAfter = pat.Segs[(j + 1) % pn].Kind == LtpSeg.Gap ? pat.Segs[(j + 1) % pn].Len : 0;
                    s0.CenterOffset = (s0.Len + gapAfter - gapBefore) / 2;
                }
                double per = 0;
                foreach (var s0 in pat.Segs) per += s0.Len;
                if (per > 1e-9)
                {
                    pat.Period = per;
                    _ltypePattern[lt.Name] = pat;
                }
            }

            int n = seglist.Count;
            var segs = new List<(bool IsDash, double Len)>();
            double pendingShrink = 0;   // 待施加到"下一个 gap 段"的缩减（零长形状点跨边界吃掉的 d/2）
            double deferredShrink = 0;  // 前面没有 gap 可吃时，延后施加到图案尾部 gap 的缩减
            void AddGap(double g)
            {
                g -= pendingShrink;
                pendingShrink = 0;
                if (g > 1e-6) segs.Add((false, g));
            }
            void AddDash(double d)
            {
                d += pendingShrink; // 跨边界的点与紧邻笔落段视觉融合（加长）
                pendingShrink = 0;
                if (d > 1e-6) segs.Add((true, d));
            }
            void ShrinkPrevGap(double v)
            {
                for (int m = segs.Count - 1; m >= 0; m--)
                {
                    if (!segs[m].IsDash)
                    {
                        var g = segs[m].Len - v;
                        if (g > 1e-6) segs[m] = (false, g); else segs.RemoveAt(m);
                        return;
                    }
                }
                deferredShrink += v; // 尚无 gap，延后到尾部
            }
            for (int i = 0; i < n; i++)
            {
                var seg = seglist[i];
                double len = seg?.Length ?? 0;
                var prev = seglist[(i - 1 + n) % n];
                var next = seglist[(i + 1) % n];
                if (seg.IsShape)
                {
                    double s = seg.Scale > 0 ? seg.Scale : 1.0;
                    if (len > 1e-9 || len < -1e-9)
                    {
                        // 形状占用明确段长（笔落段或空隙段）：点居中于该段
                        double span = Math.Abs(len);
                        double d = Math.Min(s * shapeUnit, 0.9 * span);
                        if (d < 0.05) d = Math.Min(0.05, span);
                        double side = (span - d) / 2;
                        if (side > 1e-9) AddGap(side);
                        AddDash(d);
                        if (side > 1e-9) AddGap(side);
                    }
                    else
                    {
                        // 零长形状（符号位于图案边界）：点跨边界，前后空隙各吃 d/2
                        double prevGap = prev != null && prev.Length < -1e-9 ? -prev.Length : 0;
                        double nextGap = next != null && next.Length < -1e-9 ? -next.Length : 0;
                        double d = Math.Min(s * shapeUnit, 0.85 * (prevGap + nextGap));
                        if (d < 0.05) d = 0.05;
                        AddDash(d);
                        pendingShrink += d / 2;
                        ShrinkPrevGap(d / 2);
                    }
                    continue;
                }
                if (seg.IsText)
                {
                    // 文字段：线在文字处断开 → 空隙
                    if (Math.Abs(len) > 1e-9) AddGap(Math.Abs(len));
                    else AddDash(dot);
                    continue;
                }
                if (len > 1e-9) AddDash(len);
                else if (len < -1e-9) AddGap(-len);
                else
                {
                    // 零长简单段 = 点；相邻形状段的点已代表该位置 → 跳过（避免多余小点）
                    bool nearShape = prev.IsShape || next.IsShape;
                    if (!nearShape) AddDash(dot);
                }
            }
            if (pendingShrink > 0) ShrinkPrevGap(pendingShrink); // 图案末尾仍待施加 → 落到尾部 gap
            if (deferredShrink > 0) ShrinkPrevGap(deferredShrink);

            // 合并连续同类型
            var merged = new List<(bool IsDash, double Len)>();
            foreach (var s in segs)
            {
                if (merged.Count > 0 && merged[merged.Count - 1].IsDash == s.IsDash)
                    merged[merged.Count - 1] = (s.IsDash, merged[merged.Count - 1].Len + s.Len);
                else merged.Add(s);
            }
            // 保证以 dash 开头（若以 gap 开头，把首个 gap 移到末尾，形成闭环等效模式）
            if (merged.Count > 0 && !merged[0].IsDash)
            {
                var first = merged[0];
                merged.RemoveAt(0);
                merged.Add(first);
            }
            if (merged.Count == 0)
            {
                _ltypeDash[lt.Name] = null; // 极端退化（全被吃掉）→ 实线兜底
                continue;
            }
            bool anyDash = merged.Any(m => m.IsDash);
            var arr = anyDash
                ? merged.Select(m => m.Len).ToArray()
                : new double[] { 1.0, 1.0 };
            _ltypeDash[lt.Name] = arr;
        }
    }

    // 实体的有效虚线模式（已乘有效比例 = $LTSCALE × 实体线型比例48 × 块插入比例）；
    // null = 实线。
    private double[] ResolveDash(Entity ent, double[] byBlockDash, double ltMul)
    {
        if (ent == null) return null;
        string name = null;
        try { name = ent.LineType?.Name; } catch { }
        if (string.IsNullOrEmpty(name) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
        { try { name = ent.Layer?.LineType?.Name; } catch { } }
        if (string.IsNullOrEmpty(name) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
            return byBlockDash; // 随块 -> 继承上层
        if (name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return null;
        if (_ltypeDash == null || !_ltypeDash.TryGetValue(name, out var dash) || dash == null) return null;
        double scale = (double.IsFinite(ltMul) && ltMul > 0) ? ltMul : 1.0;
        try { var s = ent.LineTypeScale; if (double.IsFinite(s) && s > 0) scale *= s; } catch { }
        if (Math.Abs(scale - 1.0) < 1e-9) return dash;
        var scaled = new double[dash.Length];
        for (int i = 0; i < dash.Length; i++) scaled[i] = dash[i] * scale;
        return scaled;
    }

    private static string DashAttr(double[] dash)
    {
        if (dash == null || dash.Length == 0) return "";
        return string.Join(" ", dash.Select(v => F(v)));
    }

    // 解析实体的有效复杂线型模式（已乘有效比例）。null = 非复杂线型（走 dasharray/实线）。
    private LtpPattern ResolvePattern(Entity ent, LtpPattern byBlockPattern, double ltMul)
    {
        if (ent == null) return null;
        string name = null;
        try { name = ent.LineType?.Name; } catch { }
        if (string.IsNullOrEmpty(name) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
        { try { name = ent.Layer?.LineType?.Name; } catch { } }
        if (string.IsNullOrEmpty(name) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
            return byBlockPattern; // 随块 -> 继承上层
        if (name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return null;
        if (_ltypePattern == null || !_ltypePattern.TryGetValue(name, out var pat) || pat == null) return null;
        double scale = (double.IsFinite(ltMul) && ltMul > 0) ? ltMul : 1.0;
        try { var s = ent.LineTypeScale; if (double.IsFinite(s) && s > 0) scale *= s; } catch { }
        return Math.Abs(scale - 1.0) < 1e-9 ? pat : pat.Scaled(scale);
    }

    // 块插入的线型比例贡献（CAD 语义：块内实体图案比例乘块插入的 X 比例）。
    private static double BlockScaleOf(Insert ins)
    {
        try
        {
            double s = ins.XScale;
            if (double.IsFinite(s) && s > 0) return s;
        }
        catch { }
        return 1.0;
    }

    // 经 Fonts.Lib 加载 SHX 形状库（AutoCAD-86 shapes 格式），按文件名缓存。
    // 文件路径由 ImageConfiguration.FileProvider 解析；失败返回 null（调用方回退"点"）。
    private ShxFont? GetShapeFont(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (_shxFonts.TryGetValue(fileName, out var cached)) return cached;
        ShxFont? font = null;
        try
        {
            string path = "";
            try { path = _opt.FileProvider?.Invoke(fileName) ?? ""; } catch { }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                _log($"⚠️  SHX 形状库未找到：{fileName}（复杂线型形状回退为「点」渲染）");
            else
                font = ShxFont.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            _log($"⚠️  SHX 形状库加载失败 {fileName}：{ex.Message}");
        }
        _shxFonts[fileName] = font;
        return font;
    }

    // 取形状字形（按"文件名|shape编号"缓存）。SHX shapes 库中 code 即 shape 编号。
    private GlyphRec? GetGlyph(ShxFont font, string fileName, int num)
    {
        string key = fileName + "|" + num;
        if (_glyphCache.TryGetValue(key, out var g)) return g;
        GlyphRec? rec = null;
        try
        {
            var r = new GlyphRec();
            if (font.TryGetGlyph(num, r) && !r.Empty) rec = r;
        }
        catch { }
        _glyphCache[key] = rec;
        return rec;
    }

    // 累计弧长表（数据单位）
    private static double[] BuildCum(List<(double X, double Y)> pts)
    {
        var cum = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        return cum;
    }

    // 取路径上弧长 s 处的点与切向单位向量（s 钳制到 [0, L]）
    private static void PathPointAt(List<(double X, double Y)> pts, double[] cum, double s,
        out double x, out double y, out double tx, out double ty)
    {
        int n = pts.Count;
        double L = cum[n - 1];
        if (L <= 1e-12)
        {
            x = pts[0].X; y = pts[0].Y; tx = 1; ty = 0; return;
        }
        if (s < 0) s = 0; else if (s > L) s = L;
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cum[mid] >= s) hi = mid; else lo = mid + 1;
        }
        int i = Math.Max(1, lo);
        double segLen = cum[i] - cum[i - 1];
        double t = segLen > 1e-12 ? (s - cum[i - 1]) / segLen : 0;
        x = pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t;
        y = pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t;
        tx = pts[i].X - pts[i - 1].X;
        ty = pts[i].Y - pts[i - 1].Y;
        double tl = Math.Sqrt(tx * tx + ty * ty);
        if (tl < 1e-12) { tx = 1; ty = 0; } else { tx /= tl; ty /= tl; }
    }

    // 把弧长区间 [s0, s1] 的子路径追加到 d（端点精确插值 + 区间内原始采样顶点）。
    // pdf=false 为 SVG（M/L），true 为 PDF（m/l）；fx/fy=数据坐标→输出坐标映射。
    private static void AppendSpanPath(StringBuilder d, List<(double X, double Y)> pts, double[] cum,
        double s0, double s1, Func<double, double> fx, Func<double, double> fy, bool pdf)
    {
        if (s1 - s0 < 1e-9) return;
        int n = pts.Count;
        double L = cum[n - 1];
        if (s0 < 0) s0 = 0;
        if (s1 > L) s1 = L;
        if (s1 - s0 < 1e-9) return;

        PathPointAt(pts, cum, s0, out double ax, out double ay, out _, out _);
        double m0x = fx(ax), m0y = fy(ay);
        PathPointAt(pts, cum, s1, out double bx, out double by, out _, out _);
        double m1x = fx(bx), m1y = fy(by);

        if (pdf) d.Append(F(m0x)).Append(' ').Append(F(m0y)).Append(" m ");
        else d.Append("M").Append(F(m0x)).Append(',').Append(F(m0y));
        for (int i = 1; i < n; i++)
        {
            double c = cum[i];
            if (c <= s0 + 1e-9 || c >= s1 - 1e-9) continue;
            double mx = fx(pts[i].X), my = fy(pts[i].Y);
            if (pdf) d.Append(F(mx)).Append(' ').Append(F(my)).Append(" l ");
            else d.Append("L").Append(F(mx)).Append(',').Append(F(my));
        }
        double dx = m1x - m0x, dy = m1y - m0y;
        if (dx * dx + dy * dy > 1e-18)
        {
            if (pdf) d.Append(F(m1x)).Append(' ').Append(F(m1y)).Append(" l ");
            else d.Append("L").Append(F(m1x)).Append(',').Append(F(m1y));
        }
    }

    // 沿路径走查模式：dash 段→线子路径；gap 段→留空；shape 段→段中点处真实 SHX 字形（沿切向）。
    private void WalkPattern(StringBuilder d, List<(double X, double Y)> pts, double[] cum, LtpPattern pat,
        Func<double, double> fx, Func<double, double> fy, bool pdf)
    {
        double L = cum[cum.Length - 1];
        var segs = pat.Segs;
        int n = segs.Count;
        if (L <= 1e-9 || n == 0) return;
        double pos = 0;
        int i = 0, guard = 0;
        while (pos < L - 1e-9 && guard++ < 50000000)
        {
            var seg = segs[i % n];
            double span = seg.Len;
            if (seg.Kind == LtpSeg.Dash)
            {
                AppendSpanPath(d, pts, cum, pos, Math.Min(pos + span, L), fx, fy, pdf);
            }
            else if (seg.Kind == LtpSeg.Shape)
            {
                // 形状中心位置：正跨度=自身段中点；间隙型=总空隙中点（构建时算好，见 LtpSeg.CenterOffset）
                double at = pos + seg.CenterOffset;
                PathPointAt(pts, cum, at, out double px, out double py, out double tx, out double ty);
                EmitShapeGlyph(d, pts, cum, at, px, py, tx, ty, seg, fx, fy, pdf);
            }
            pos += span;
            i++;
        }
    }

    // 在路径点 (px,py) 绘制真实 SHX 字形（追加到 d）：字形包围盒中心对齐路径点，
    // X 轴沿切向（upright=0° / relative=切向 / absolute=指定角），缩放=ShapeScale。
    // SHX 库/形状不可用时回退为沿切向居中的"点"（短实线），保持图案节奏。
    private void EmitShapeGlyph(StringBuilder d, List<(double X, double Y)> pts, double[] cum, double at,
                                double px, double py, double tx, double ty, LtpSeg seg,
                                Func<double, double> fx, Func<double, double> fy, bool pdf)
    {
        GlyphRec? rec = null;
        if (!string.IsNullOrEmpty(seg.ShapeFile))
        {
            var font = GetShapeFont(seg.ShapeFile);
            if (font != null && font.TryGetShapeNumber(seg.ShapeName, out int num))
                rec = GetGlyph(font, seg.ShapeFile, num);
        }
        if (rec == null)
        {
            double dd = seg.Len > 1e-9 ? Math.Min(seg.ShapeScale * 4.0, 0.9 * seg.Len) : 0.1;
            if (dd < 0.05) dd = 0.05;
            AppendSpanPath(d, pts, cum, at - dd / 2, at + dd / 2, fx, fy, pdf);
            return;
        }
        double S = seg.ShapeScale > 1e-9 ? seg.ShapeScale : 1.0;
        double ang;
        switch (seg.RotType)
        {
            case 1: ang = Math.Atan2(ty, tx); break;   // relative：字形 X 轴沿路径切向
            case 2: ang = seg.ShapeRot * Math.PI / 180.0; break; // absolute
            default: ang = 0; break;                   // upright
        }
        double ca = Math.Cos(ang), sa = Math.Sin(ang);
        double ox = seg.OX * S, oy = seg.OY * S;
        // 字形点(字体单位) → 输出坐标：平移(包围盒中心→路径点)+旋转+缩放，再经 fx/fy 映射
        void Xf(float gx, float gy, out double mx, out double my)
        {
            double lx = (gx - rec.CX) * S + ox;
            double ly = (gy - rec.CY) * S + oy;
            double wx = px + lx * ca - ly * sa;
            double wy = py + lx * sa + ly * ca;
            mx = fx(wx); my = fy(wy);
        }
        double curX = 0, curY = 0; bool haveCur = false;
        for (int k = 0; k < rec.Type.Count; k++)
        {
            var p = rec.Pts[k];
            switch (rec.Type[k])
            {
                case 0:
                {
                    Xf(p[0], p[1], out double mx, out double my);
                    if (pdf) d.Append(F(mx)).Append(' ').Append(F(my)).Append(" m ");
                    else d.Append("M").Append(F(mx)).Append(',').Append(F(my));
                    curX = mx; curY = my; haveCur = true;
                    break;
                }
                case 1:
                {
                    Xf(p[0], p[1], out double mx, out double my);
                    if (pdf) d.Append(F(mx)).Append(' ').Append(F(my)).Append(" l ");
                    else d.Append("L").Append(F(mx)).Append(',').Append(F(my));
                    curX = mx; curY = my; haveCur = true;
                    break;
                }
                case 2:
                {
                    Xf(p[0], p[1], out double qx, out double qy);
                    Xf(p[2], p[3], out double ex, out double ey);
                    if (pdf)
                    {
                        // PDF 无二次贝塞尔 → 转三次：c1=p+2/3(c-p)、c2=e+2/3(c-e)
                        double p0x = haveCur ? curX : qx, p0y = haveCur ? curY : qy;
                        double c1x = p0x + 2.0 / 3.0 * (qx - p0x), c1y = p0y + 2.0 / 3.0 * (qy - p0y);
                        double c2x = ex + 2.0 / 3.0 * (qx - ex), c2y = ey + 2.0 / 3.0 * (qy - ey);
                        d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ')
                         .Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                    }
                    else d.Append("Q").Append(F(qx)).Append(',').Append(F(qy)).Append(' ').Append(F(ex)).Append(',').Append(F(ey));
                    curX = ex; curY = ey; haveCur = true;
                    break;
                }
                case 3:
                {
                    Xf(p[0], p[1], out double c1x, out double c1y);
                    Xf(p[2], p[3], out double c2x, out double c2y);
                    Xf(p[4], p[5], out double ex, out double ey);
                    if (pdf) d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ')
                        .Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                    else d.Append("C").Append(F(c1x)).Append(',').Append(F(c1y)).Append(' ')
                        .Append(F(c2x)).Append(',').Append(F(c2y)).Append(' ').Append(F(ex)).Append(',').Append(F(ey));
                    curX = ex; curY = ey; haveCur = true;
                    break;
                }
            }
        }
    }

    // 实体几何 → 折线点列（复杂线型走查用；返回 false = 无法折线化，退回 dasharray）。
    // 与参考实现 DxfImageExporter.TryGetPathPoints 一致（采样密度按 0.5 数据单位弧长）。
    // 注意：fork 中 Arc : Circle（netDxf 中两者为兄弟类），故 Arc 判断必须先于 Circle。
    private static bool TryGetPathPoints(Entity ent, Transform2D tr, out List<(double X, double Y)> pts)
    {
        pts = null;
        void Apply(double x, double y, out double rx, out double ry)
        {
            if (tr == null) { rx = x; ry = y; }
            else { var a = tr.Apply(x, y); rx = a.Item1; ry = a.Item2; }
        }
        if (ent is Line ln)
        {
            Apply(ln.StartPoint.X, ln.StartPoint.Y, out double x1, out double y1);
            Apply(ln.EndPoint.X, ln.EndPoint.Y, out double x2, out double y2);
            pts = new List<(double X, double Y)> { (x1, y1), (x2, y2) };
            return true;
        }
        if (ent is LwPolyline p2)
        {
            // 变宽填充折线回退（ConstantWidth = DWG 往返后的等宽形式，语义相同）
            if ((p2.Vertices.FirstOrDefault()?.StartWidth ?? 0) > 0 || p2.ConstantWidth > 0) return false;
            var l = new List<(double X, double Y)>(p2.Vertices.Count);
            foreach (var v in p2.Vertices) { Apply(v.Location.X, v.Location.Y, out double x, out double y); l.Add((x, y)); }
            if (p2.IsClosed && l.Count >= 3) l.Add(l[0]); // 补闭合段，模式可走完整个环
            pts = l;
            return l.Count >= 2;
        }
        if (ent is Polyline3D p3)
        {
            var l3 = new List<(double X, double Y)>(p3.Vertices.Count);
            foreach (var v in p3.Vertices) { Apply(v.Location.X, v.Location.Y, out double x, out double y); l3.Add((x, y)); }
            if (p3.IsClosed && l3.Count >= 3) l3.Add(l3[0]);
            pts = l3;
            return l3.Count >= 2;
        }
        if (ent is Arc a) // 必须先于 Circle（fork 继承关系）
        {
            Apply(a.Center.X, a.Center.Y, out double cx, out double cy);
            double r = a.Radius * (tr?.Scale ?? 1.0);
            if (r <= 0) return false;
            double s = a.StartAngle; // fork 弧度（参考实现度→弧度）
            double e = a.EndAngle;
            double span = e - s;
            while (span < 0) span += 2 * Math.PI;
            while (span >= 2 * Math.PI) span -= 2 * Math.PI;
            if (span < 1e-6) span = 2 * Math.PI;
            int m = (int)Math.Clamp(Math.Ceiling(r * span / 0.5), 32, 100000);
            var la = new List<(double X, double Y)>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double ang = s + span * i / m;
                la.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
            pts = la;
            return true;
        }
        if (ent is Circle c)
        {
            Apply(c.Center.X, c.Center.Y, out double cx, out double cy);
            double r = c.Radius * (tr?.Scale ?? 1.0);
            if (r <= 0) return false;
            int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * r / 0.5), 64, 100000);
            var lc = new List<(double X, double Y)>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double ang = 2 * Math.PI * i / m; // CAD 线型从 0°（3 点钟方向）逆时针起
                lc.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
            pts = lc;
            return true;
        }
        if (ent is Ellipse el)
        {
            Apply(el.Center.X, el.Center.Y, out double cx, out double cy);
            double rx = el.MajorAxis * (tr?.Scale ?? 1.0), ry = el.MinorAxis * (tr?.Scale ?? 1.0);
            if (rx <= 0 || ry <= 0) return false;
            double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度（与参考实现单位一致）
            double ca = Math.Cos(rot), sa = Math.Sin(rot);
            int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * Math.Max(rx, ry) / 0.5), 64, 100000);
            var le = new List<(double X, double Y)>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double th = 2 * Math.PI * i / m; // 从长轴端点起（与 CAD 图案起点一致）
                double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
                le.Add((cx + ex * ca - ey * sa, cy + ex * sa + ey * ca));
            }
            pts = le;
            return true;
        }
        if (ent is Spline sp)
        {
            var sp2 = SplineSample(sp, tr, 16);
            if (sp2.Count < 2) return false;
            var ls = new List<(double X, double Y)>(sp2.Count);
            foreach (var p in sp2) ls.Add((p.X, p.Y));
            pts = ls;
            return true;
        }
        return false;
    }

    // 复杂线型（SVG）：沿路径走查模式生成单个 <path>（dash 子路径 + 字形子路径）。
    // 返回 false = 不支持/空（调用方回退旧 dasharray 逻辑）。
    private bool TryEmitComplexSvg(StringBuilder sb, Entity ent, Transform2D tr, LtpPattern pat, string col, double w)
    {
        if (pat == null) return false;
        if (!TryGetPathPoints(ent, tr, out var pts) || pts.Count < 2) return false;
        var cum = BuildCum(pts);
        if (cum[cum.Length - 1] <= 1e-9) return false;
        var d = new StringBuilder();
        WalkPattern(d, pts, cum, pat, Sx, Sy, false);
        if (d.Length == 0) return false;
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(w)}\" stroke-linejoin=\"round\"/>");
        return true;
    }

    // 复杂线型模式段（Dash/Gap/Shape 三态；Shape 段独立放置，永不合并）
    private class LtpSeg
    {
        public const int Dash = 0, Gap = 1, Shape = 2;
        public int Kind;
        public double Len;        // 段长（数据单位，正）
        public bool InGap;        // Shape：位于空隙内（间隙型）
        public double CenterOffset; // Shape：字形中心相对段起点的偏移
        public string ShapeName;
        public string ShapeFile;
        public double ShapeScale;
        public int RotType;       // 0=upright 1=relative 2=absolute
        public double ShapeRot;   // 绝对旋转（度）
        public double OX, OY;     // 形状偏移（字体单位）
    }

    private class LtpPattern
    {
        public List<LtpSeg> Segs = new();
        public double Period;
        public LtpPattern Scaled(double s)
        {
            var p = new LtpPattern();
            foreach (var seg in this.Segs)
            {
                var c = new LtpSeg
                {
                    Kind = seg.Kind, Len = seg.Len * s, InGap = seg.InGap,
                    CenterOffset = seg.CenterOffset * s, ShapeName = seg.ShapeName,
                    ShapeFile = seg.ShapeFile, ShapeScale = seg.ShapeScale * s,
                    RotType = seg.RotType, ShapeRot = seg.ShapeRot, OX = seg.OX * s, OY = seg.OY * s,
                };
                p.Segs.Add(c);
            }
            p.Period = this.Period * s;
            return p;
        }
    }

    // 记录 SHX 字形的笔路径（M/L/Q/C 指令 + 包围盒，SHX 字体单位；基线 y=0）
    private sealed class GlyphRec : IGlyphSink
    {
        public readonly List<int> Type = new();     // 0=M 1=L 2=Q 3=C
        public readonly List<float[]> Pts = new();
        public float MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue;
        public bool Empty { get { return Pts.Count == 0; } }
        public float CX { get { return (MinX + MaxX) / 2; } }
        public float CY { get { return (MinY + MaxY) / 2; } }
        private void Pt(float x, float y)
        {
            if (x < MinX) MinX = x; if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
        }
        public void MoveTo(float x, float y) { Type.Add(0); Pts.Add(new[] { x, y }); Pt(x, y); }
        public void LineTo(float x, float y) { Type.Add(1); Pts.Add(new[] { x, y }); Pt(x, y); }
        public void QuadTo(float cx, float cy, float x, float y) { Type.Add(2); Pts.Add(new[] { cx, cy, x, y }); Pt(cx, cy); Pt(x, y); }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) { Type.Add(3); Pts.Add(new[] { c1x, c1y, c2x, c2y, x, y }); Pt(c1x, c1y); Pt(c2x, c2y); Pt(x, y); }
        public void Close() { } // SHX 笔路径为开放路径，无闭合轮廓
    }

    #endregion

    #region ===== 坐标映射与范围 =====

    // SVG：y 向下坐标系（原点在左上角），需 Y 翻转
    private double Sx(double x) => _margin + (x - _minX) * _scale;
    private double Sy(double y) => _h - (_margin + (y - _minY) * _scale); // Y 翻转
    // PDF：y 向上坐标系（原点在左下角），与 CAD 同向，不翻转
    private double Py(double y) => _margin + (y - _minY) * _scale;

    // XLine/Ray 裁剪：把无限线（XLine）/射线（Ray）在"画布矩形"（数据坐标，含边距）内取可见段。
    // 入参均为世界系（调用点先用 tr 把起点/方向转到世界系）：p0 起点、(dx,dy) 方向。
    // 参数 t 与画布边界求交 → t∈[tmin,tmax]。XLine：t∈[-∞,+∞]；Ray：t∈[0,+∞]（仅起点之后）。
    // 返回世界系两端点；与画布不相交返回 false（不发射）。
    private bool ClipInfiniteLineToCanvas(double p0x, double p0y, double dx, double dy, bool isXLine,
        out double x1, out double y1, out double x2, out double y2)
    {
        x1 = y1 = x2 = y2 = 0;
        double cxmin = _minX - _margin, cxmax = _maxX + _margin;
        double cymin = _minY - _margin, cymax = _maxY + _margin;
        double tmin = isXLine ? double.NegativeInfinity : 0.0;
        double tmax = double.PositiveInfinity;
        bool ok = true;
        // x 边界
        if (Math.Abs(dx) > 1e-12)
        {
            double ta = (cxmin - p0x) / dx, tb = (cxmax - p0x) / dx;
            if (ta > tb) { var tmp = ta; ta = tb; tb = tmp; }
            if (ta > tmin) tmin = ta;
            if (tb < tmax) tmax = tb;
        }
        else if (p0x < cxmin || p0x > cxmax) { ok = false; } // 垂直线但起点在画布 x 外
        // y 边界
        if (ok && Math.Abs(dy) > 1e-12)
        {
            double ta = (cymin - p0y) / dy, tb = (cymax - p0y) / dy;
            if (ta > tb) { var tmp = ta; ta = tb; tb = tmp; }
            if (ta > tmin) tmin = ta;
            if (tb < tmax) tmax = tb;
        }
        else if (p0y < cymin || p0y > cymax) { ok = false; } // 水平线但起点在画布 y 外
        if (!ok || !(tmax > tmin)) return false;
        x1 = p0x + dx * tmin; y1 = p0y + dy * tmin;
        x2 = p0x + dx * tmax; y2 = p0y + dy * tmax;
        return true;
    }

    // XLine/Ray 的方向向量经块变换（只旋转+缩放、不平移）：取原点与原点+方向两点之差。
    private static (double dx, double dy) TransformDirection(double dx, double dy, Transform2D tr)
    {
        if (tr == null) return (dx, dy);
        var a = tr.Apply(0, 0);
        var b = tr.Apply(dx, dy);
        return (b.Item1 - a.Item1, b.Item2 - a.Item2);
    }

    // MLine 渲染几何（三引擎共享）：把 MLine 按 MLineStyle.Elements 的各 Offset 偏移到世界系折线。
    // 每个 Element 生成一条平行折线（沿角平分法向偏移），返回 (偏移后世界点列, 该 Element 的 ACI)。
    // 顶点先经 tr 世界化，再在世界系算段方向/法向（tr 为相似变换，法向方向一致）；
    // 偏移量乘块缩放（tr.Scale）。样式无定义时退化为画中线。
    private static List<(List<(double X, double Y)> Poly, short Aci)> MLineOffsetPolys(MLine ml, Transform2D tr)
    {
        var result = new List<(List<(double X, double Y)>, short)>();
        if (ml?.Vertices == null || ml.Vertices.Count < 2) return result;
        // 顶点世界化
        var wp = new List<(double X, double Y)>(ml.Vertices.Count);
        foreach (var v in ml.Vertices)
        {
            var p = tr == null ? (v.Position.X, v.Position.Y) : tr.Apply(v.Position.X, v.Position.Y);
            wp.Add(p);
        }
        // 每顶点法向（角平分法向，近似 miter 拐角）
        int n = wp.Count;
        var normals = new (double X, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            double dx, dy;
            if (i == 0) { dx = wp[1].X - wp[0].X; dy = wp[1].Y - wp[0].Y; }
            else if (i == n - 1) { dx = wp[n - 1].X - wp[n - 2].X; dy = wp[n - 1].Y - wp[n - 2].Y; }
            else { dx = (wp[i].X - wp[i - 1].X) + (wp[i + 1].X - wp[i].X); dy = (wp[i].Y - wp[i - 1].Y) + (wp[i + 1].Y - wp[i].Y); }
            double len = Math.Sqrt(dx * dx + dy * dy);
            normals[i] = len < 1e-12 ? (0, 0) : (-dy / len, dx / len);
        }
        double offScale = tr?.Scale ?? 1.0;
        var style = ml.Style ?? ACadSharp.Objects.MLineStyle.Default;
        var elems = style.Elements.ToList();
        if (elems.Count == 0)
        {
            result.Add((wp, EntityAciIndex(ml))); // 无样式定义 → 中线
            return result;
        }
        foreach (var el in elems)
        {
            double off = el.Offset * offScale;
            var poly = new List<(double X, double Y)>(n);
            for (int i = 0; i < n; i++)
                poly.Add((wp[i].X + normals[i].X * off, wp[i].Y + normals[i].Y * off));
            short aci;
            try { aci = el.Color.IsTrueColor ? TrueColorToAci(el.Color.R, el.Color.G, el.Color.B) : el.Color.Index; }
            catch { aci = 256; }
            result.Add((poly, aci));
        }
        return result;
    }

    // PolyfaceMesh 线框（三引擎共享）：旧式 3D 多面体网格。每个 Face（VertexFaceRecord.Index1-4，
    // 1-based，0=无效）把有效顶点索引连成一条线串；3 点成三角、4 点成四边形。
    // 顶点（VertexFaceMesh.Location）经 tr 世界化，返回世界系线段列表 (a, b)。
    private static List<((double AX, double AY), (double BX, double BY))> PolyfaceMeshWire(PolyfaceMesh pf, Transform2D tr)
    {
        var segs = new List<((double AX, double AY), (double BX, double BY))>();
        var verts = pf?.Vertices;
        var faces = pf?.Faces;
        if (verts == null || verts.Count == 0 || faces == null) return segs;
        // 顶点世界化（Location 为 XYZ）
        var wp = new (double X, double Y)[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            var p = tr == null ? (verts[i].Location.X, verts[i].Location.Y) : tr.Apply(verts[i].Location.X, verts[i].Location.Y);
            wp[i] = p;
        }
        foreach (var f in faces)
        {
            short[] idx = { f.Index1, f.Index2, f.Index3, f.Index4 };
            var valid = new List<(double X, double Y)>();
            foreach (var ix in idx)
            {
                int i0 = ix - 1;
                if (i0 < 0 || i0 >= wp.Length) continue; // 0 或越界 → 无效索引
                valid.Add(wp[i0]);
            }
            // 依次连线（首尾不重复闭合；三角 3 点、四边形 4 点）
            for (int k = 1; k < valid.Count; k++)
                segs.Add(((valid[k - 1].X, valid[k - 1].Y), (valid[k].X, valid[k].Y)));
        }
        return segs;
    }

    // PolygonMesh 线框（三引擎共享）：M×N 多边形网格（顶点行优先，index = r*n + c）。
    // MVertexCount/NVertexCount 有效且乘积 = 顶点数时画网格线（行线 + 列线）；
    // 否则退化为按顺序顶点折线。顶点（Location）经 tr 世界化，返回世界系线段列表。
    private static List<((double AX, double AY), (double BX, double BY))> PolygonMeshWire(PolygonMesh pm, Transform2D tr)
    {
        var segs = new List<((double AX, double AY), (double BX, double BY))>();
        var verts = pm?.Vertices;
        if (verts == null || verts.Count == 0) return segs;
        var wp = new (double X, double Y)[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            var p = tr == null ? (verts[i].Location.X, verts[i].Location.Y) : tr.Apply(verts[i].Location.X, verts[i].Location.Y);
            wp[i] = p;
        }
        int m = pm.MVertexCount, n = pm.NVertexCount;
        int total = verts.Count;
        if (m > 0 && n > 0 && m * n == total)
        {
            // 行线（固定 r，沿 c）
            for (int r = 0; r < m; r++)
                for (int c = 0; c < n - 1; c++)
                { int i1 = r * n + c, i2 = r * n + c + 1; segs.Add(((wp[i1].X, wp[i1].Y), (wp[i2].X, wp[i2].Y))); }
            // 列线（固定 c，沿 r）
            for (int r = 0; r < m - 1; r++)
                for (int c = 0; c < n; c++)
                { int i1 = r * n + c, i2 = (r + 1) * n + c; segs.Add(((wp[i1].X, wp[i1].Y), (wp[i2].X, wp[i2].Y))); }
        }
        else
        {
            for (int i = 0; i + 1 < total; i++)
                segs.Add(((wp[i].X, wp[i].Y), (wp[i + 1].X, wp[i + 1].Y)));
        }
        return segs;
    }

    // Wipeout 世界坐标多边形（三引擎共享）：擦除多边形，世界点 = InsertPoint + X·UVector + Y·VVector
    //（ClipBoundaryVertices 为 OCS 平面内每像素偏移，U/V 为每像素方向向量，均取 XY 分量做 2D 渲染）。
    // tr 非空时 InsertPoint 整体世界化、U/V 只取线性部分（旋转+缩放、不平移）。
    // 顶点 <2 或全退化返回空列表。
    private static List<(double X, double Y)> WipeoutWorldPoly(Wipeout wo, Transform2D tr)
    {
        var pts = new List<(double X, double Y)>();
        var clip = wo?.ClipBoundaryVertices;
        if (clip == null || clip.Count < 2) return pts;
        double ix, iy, ux, uy, vx, vy;
        if (tr == null)
        {
            ix = wo.InsertPoint.X; iy = wo.InsertPoint.Y;
            ux = wo.UVector.X; uy = wo.UVector.Y;
            vx = wo.VVector.X; vy = wo.VVector.Y;
        }
        else
        {
            var i = tr.Apply(wo.InsertPoint.X, wo.InsertPoint.Y);
            ix = i.Item1; iy = i.Item2;
            (ux, uy) = TransformDirection(wo.UVector.X, wo.UVector.Y, tr);
            (vx, vy) = TransformDirection(wo.VVector.X, wo.VVector.Y, tr);
        }
        foreach (var c in clip)
        {
            double wx = ix + c.X * ux + c.Y * vx;
            double wy = iy + c.X * uy + c.Y * vy;
            pts.Add((wx, wy));
        }
        return pts;
    }

    // Leader 箭头两翼（三引擎共享）：尖端 pts[0]，沿 pts[0]→pts[1] 反方向张开 ±angle 的两翼。
    // 返回 (左翼世界点, 右翼世界点)；顶点不足或退化返回 false。箭头长 = TextHeight（>0 时）否则线宽倍数。
    private static bool LeaderArrowWings(Leader ld, List<(double X, double Y)> pts, double arrowLen,
        out double w1x, out double w1y, out double w2x, out double w2y)
    {
        w1x = w1y = w2x = w2y = 0;
        if (pts == null || pts.Count < 2) return false;
        double ax = pts[0].X, ay = pts[0].Y;
        double dx = ax - pts[1].X, dy = ay - pts[1].Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return false;
        double baseAng = Math.Atan2(dy, dx);
        double a1 = baseAng + 0.5236; // +30°
        double a2 = baseAng - 0.5236; // -30°
        w1x = ax - arrowLen * Math.Cos(a1); w1y = ay - arrowLen * Math.Sin(a1);
        w2x = ax - arrowLen * Math.Cos(a2); w2y = ay - arrowLen * Math.Sin(a2);
        return true;
    }

    private IEnumerable<Entity> AllEntities()
    {
        foreach (var e in _doc.ModelSpace.Entities)
            yield return e;
    }

    // 计算数据范围（含 Insert 展开后的块内实体）
    private bool ComputeExtents()
    {
        bool any = false;
        _minX = _minY = double.MaxValue;
        _maxX = _maxY = double.MinValue;

        void Expand(double x, double y)
        {
            if (x < _minX) _minX = x; if (y < _minY) _minY = y;
            if (x > _maxX) _maxX = x; if (y > _maxY) _maxY = y;
            any = true;
        }

        void ExpandCircle(double cx, double cy, double r)
        { Expand(cx - r, cy - r); Expand(cx + r, cy + r); }

        if (_doc == null || _doc.ModelSpace?.Entities == null) return false;

        foreach (var ent in AllEntities())
        {
            switch (ent)
            {
                case Point pt: Expand(pt.Location.X, pt.Location.Y); break;
                case Line ln: Expand(ln.StartPoint.X, ln.StartPoint.Y); Expand(ln.EndPoint.X, ln.EndPoint.Y); break;
                case LwPolyline p2: foreach (var v in p2.Vertices) Expand(v.Location.X, v.Location.Y); break;
                case Polyline3D p3: foreach (var v in p3.Vertices) Expand(v.Location.X, v.Location.Y); break;
                case Polyline2D p2d: foreach (var v in p2d.Vertices) Expand(v.Location.X, v.Location.Y); break;
                case Mesh mesh: if (mesh.Vertices != null) foreach (var v in mesh.Vertices) Expand(v.X, v.Y); break;
                case PolyfaceMesh pf: if (pf.Vertices != null) foreach (var v in pf.Vertices) Expand(v.Location.X, v.Location.Y); break;
                case PolygonMesh pm: if (pm.Vertices != null) foreach (var v in pm.Vertices) Expand(v.Location.X, v.Location.Y); break;
                case Wipeout wo: foreach (var p in WipeoutWorldPoly(wo, null)) Expand(p.X, p.Y); break;
                case Arc a: ExpandCircle(a.Center.X, a.Center.Y, a.Radius); break; // 必须先于 Circle（fork 继承）
                case Circle c: ExpandCircle(c.Center.X, c.Center.Y, c.Radius); break;
                case Ellipse el: ExpandCircle(el.Center.X, el.Center.Y, Math.Max(el.MajorAxis, el.MinorAxis)); break;
                case TextEntity t: Expand(t.InsertPoint.X, t.InsertPoint.Y); break;
                case MText mt: Expand(mt.InsertPoint.X, mt.InsertPoint.Y); break;
                case Solid sd:
                    Expand(sd.FirstCorner.X, sd.FirstCorner.Y); Expand(sd.SecondCorner.X, sd.SecondCorner.Y);
                    Expand(sd.ThirdCorner.X, sd.ThirdCorner.Y); Expand(sd.FourthCorner.X, sd.FourthCorner.Y); break;
                case Spline sp:
                    foreach (var p in SplineSample(sp, null, 4)) Expand(p.X, p.Y); break;
                case Hatch ht:
                    foreach (var loop in BuildHatchLoops(ht, null))
                        foreach (var p in loop) Expand(p.X, p.Y); break;
                case Insert ins: ExpandInsert(ins, Expand); break;
                default: break;
            }
        }
        return any;
    }

    // 展开 Insert（含嵌套），用于计算范围
    private void ExpandInsert(Insert ins, Action<double, double> expand)
    {
        if (ins?.Block == null) return;
        double sx = ins.XScale, sy = ins.YScale, rot = ins.Rotation; // fork 弧度（参考实现：度→弧度）
        double ca = Math.Cos(rot), sa = Math.Sin(rot);
        double px = ins.InsertPoint.X, py = ins.InsertPoint.Y;
        foreach (var e in ins.Block.Entities)
        {
            if (e is Insert nested)
            {
                // 嵌套块：把嵌套块位置先变换到本块坐标，再递归（近似：仅展开一层位置，深层用其自身展开）
                ExpandInsertTransformed(nested, ca, sa, sx, sy, px, py, expand);
                continue;
            }
            foreach (var p in EntityLocalPoints(e))
            {
                double lx = p.X * sx, ly = p.Y * sy;
                double wx = px + lx * ca - ly * sa;
                double wy = py + lx * sa + ly * ca;
                expand(wx, wy);
            }
        }
    }

    private void ExpandInsertTransformed(Insert nested, double ca, double sa, double sx, double sy, double px, double py, Action<double, double> expand)
    {
        if (nested?.Block == null) return;
        double nsx = sx * nested.XScale, nsy = sy * nested.YScale;
        double nrot = (nested.Rotation * 180.0 / Math.PI) + Math.Atan2(sa, ca) * 180 / Math.PI; // fork：嵌套旋转弧度→度（参考实现：度）
        double nca = Math.Cos(nrot * Math.PI / 180), nsa = Math.Sin(nrot * Math.PI / 180);
        double bx = px + (nested.InsertPoint.X * sx) * ca - (nested.InsertPoint.Y * sy) * sa;
        double by = py + (nested.InsertPoint.X * sx) * sa + (nested.InsertPoint.Y * sy) * ca;
        foreach (var e in nested.Block.Entities)
        {
            foreach (var p in EntityLocalPoints(e))
            {
                double lx = p.X * nsx, ly = p.Y * nsy;
                expand(bx + lx * nca - ly * nsa, by + lx * nsa + ly * nca);
            }
        }
    }

    // 取实体在局部坐标系下的关键点（用于范围/插入展开）
    private static IEnumerable<XY> EntityLocalPoints(Entity e)
    {
        if (e is Point pt) yield return new XY(pt.Location.X, pt.Location.Y);
        else if (e is Line ln) { yield return new XY(ln.StartPoint.X, ln.StartPoint.Y); yield return new XY(ln.EndPoint.X, ln.EndPoint.Y); }
        else if (e is LwPolyline p2) foreach (var v in p2.Vertices) yield return new XY(v.Location.X, v.Location.Y);
        else if (e is Polyline3D p3) foreach (var v in p3.Vertices) yield return new XY(v.Location.X, v.Location.Y);
        else if (e is Circle c) { yield return new XY(c.Center.X - c.Radius, c.Center.Y - c.Radius); yield return new XY(c.Center.X + c.Radius, c.Center.Y + c.Radius); }
        else if (e is Arc a) { yield return new XY(a.Center.X - a.Radius, a.Center.Y - a.Radius); yield return new XY(a.Center.X + a.Radius, a.Center.Y + a.Radius); }
        else if (e is Ellipse el) { double r = Math.Max(el.MajorAxis, el.MinorAxis); yield return new XY(el.Center.X - r, el.Center.Y - r); yield return new XY(el.Center.X + r, el.Center.Y + r); }
        else if (e is TextEntity t) yield return new XY(t.InsertPoint.X, t.InsertPoint.Y);
        else if (e is MText mt) yield return new XY(mt.InsertPoint.X, mt.InsertPoint.Y);
        else if (e is Solid so)
        {
            yield return new XY(so.FirstCorner.X, so.FirstCorner.Y);
            yield return new XY(so.SecondCorner.X, so.SecondCorner.Y);
            yield return new XY(so.ThirdCorner.X, so.ThirdCorner.Y);
            yield return new XY(so.FourthCorner.X, so.FourthCorner.Y);
        }
        else if (e is Spline sp2)
            foreach (var p in SplineSample(sp2, null, 4)) yield return new XY(p.X, p.Y);
        else if (e is Hatch ht2)
            foreach (var loop in BuildHatchLoops(ht2, null))
                foreach (var p in loop) yield return new XY(p.X, p.Y);
    }

    #endregion

    #region ===== 导出 SVG =====
    public ImageExportResult WriteSvg(string outPath)
    {
        if (!ComputeExtents())
        {
            _log("⚠️  SVG 无可用实体，跳过");
            return new ImageExportResult { Success = false };
        }

        _scale = 1.0; // 1:1 实际尺寸
        double dataW = _maxX - _minX;
        double dataH = _maxY - _minY;
        _margin = Math.Max(dataW, dataH) * 0.02 + 1.0; // 少量边距（实际尺寸）
        if (!double.IsFinite(_margin) || _margin <= 0) _margin = 2.0;
        _w = dataW + 2 * _margin;
        _h = dataH + 2 * _margin;

        BuildLinetypeDash();

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        // 画布 = 1:1 内容尺寸 × OutputZoom（viewBox 保持 1:1 坐标，浏览器统一放大）
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(_w * _opt.OutputZoom)}\" height=\"{F(_h * _opt.OutputZoom)}\" viewBox=\"0 0 {F(_w)} {F(_h)}\">");
        // 背景（默认黑色；WhiteBackground=true 时白色；TransparentBackground=true 时省略背景矩形=透明）
        if (!_transparentBg)
            sb.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{F(_w)}\" height=\"{F(_h)}\" fill=\"{(_whiteBg ? "#FFFFFF" : "#000000")}\"/>");

        _svgDefs = new StringBuilder();
        _hatchGradSeq = 0;
        _skippedCounts.Clear();
        foreach (var ent in AllEntities())
            EmitEntitySvg(sb, ent, null, 1.0, 7, null, null, _ltScale); // 顶层无 INSERT 上下文，ByBlock 兜底白/实线

        if (_svgDefs.Length > 0)
        {
            sb.AppendLine("  <defs>");
            sb.Append(_svgDefs);
            sb.AppendLine("  </defs>");
        }
        sb.AppendLine("</svg>");
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        _log($"✅  SVG 已生成：{outPath}（画板 {F(_w * _opt.OutputZoom)}×{F(_h * _opt.OutputZoom)}，1:1 内容放大 {F(_opt.OutputZoom)} 倍）");
        LogSkipped("SVG");
        return new ImageExportResult
        {
            Success = true,
            CanvasPixelsW = (int)Math.Ceiling(_w * _opt.OutputZoom),
            CanvasPixelsH = (int)Math.Ceiling(_h * _opt.OutputZoom),
            DataX0 = _minX - _margin,
            DataX1 = _maxX + _margin,
            DataY0 = _minY - _margin,
            DataY1 = _maxY + _margin,
        };
    }

    // 发射单个实体（含 Insert 展开）。tr 为累积变换（用于嵌套块），scaleMul 为线宽/文字高度倍率，
    // byBlockAci 为"随块"实体应继承的颜色（= 当前 INSERT 的有效颜色，顶层为 7），
    // byBlockDash 为"随块"实体应继承的虚线模式（= 当前 INSERT 的有效线型，顶层为 null=实线），
    // ltMul 为线型比例乘数（= $LTSCALE × 外层块插入比例之积；顶层 = _ltScale），
    // byLayerOverride 为"随图层"实体应继承的图层色（= 当前 INSERT 所在图层色；顶层为 null=用实体自身图层）。
    private void EmitEntitySvg(StringBuilder sb, Entity ent, Transform2D tr, double scaleMul, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul, short? byLayerOverride = null)
    {
        // 块内 ByLayer 图元按 CAD 语义随"插入点块所在图层"（byLayerOverride），
        // 顶层实体则用自身图层色。
        short layerAci;
        if (byLayerOverride.HasValue) layerAci = byLayerOverride.Value;
        else { layerAci = 7; try { layerAci = LayerAciIndex(ent.Layer); } catch { } }
        double[] dash = ResolveDash(ent, byBlockDash, ltMul);
        LtpPattern pat = ResolvePattern(ent, byBlockPat, ltMul); // 复杂线型（含 SHX 段）→ 沿路径真实渲染

        switch (ent)
        {
            case Insert ins: EmitInsertSvg(sb, ins, tr, byBlockAci, dash, pat, ltMul); break;

            case Line ln:
                {
                    var (x1, y1) = tr == null ? (ln.StartPoint.X, ln.StartPoint.Y) : tr.Apply(ln.StartPoint.X, ln.StartPoint.Y);
                    var (x2, y2) = tr == null ? (ln.EndPoint.X, ln.EndPoint.Y) : tr.Apply(ln.EndPoint.X, ln.EndPoint.Y);
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (!TryEmitComplexSvg(sb, ent, tr, pat, col, w))
                        EmitLine(sb, x1, y1, x2, y2, col, w, dash);
                }
                break;

            case LwPolyline pl:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = pl.Vertices.Select(v => tr == null ? (v.Location.X, v.Location.Y) : tr.Apply(v.Location.X, v.Location.Y)).ToList();
                    // 几何宽度（组码43，数据单位，真实加粗）：>0 时按 CAD 语义渲染为"填充多边形"；否则按线宽(370)描边。
                    // DWG 往返：全顶点等宽被 DwgStreamExporter/AcadDwgOperator 归一为 ConstantWidth（逐顶点置 0）——
                    // 先物化为逐顶点宽度，再走与 OFF 路由（netDxf 逐顶点 StartWidth）完全相同的 tapered 逻辑。
                    double sw = pl.Vertices.FirstOrDefault()?.StartWidth ?? 0;
                    if (sw <= 0 && pl.ConstantWidth > 0)
                    {
                        foreach (var v in pl.Vertices) { v.StartWidth = pl.ConstantWidth; v.EndWidth = pl.ConstantWidth; }
                        sw = pl.ConstantWidth;
                    }
                    if (sw > 0 && pts.Count >= 2)
                    {
                        EmitTaperedPolyline(sb, pl, tr, col, scaleMul, dash);
                        break;
                    }
                    double w = StrokeWidth(ent, scaleMul);
                    EmitPolyline(sb, pts, col, w, pl.IsClosed, dash);
                }
                break;

            case Polyline3D p3:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = p3.Vertices.Select(v => tr == null ? (v.Location.X, v.Location.Y) : tr.Apply(v.Location.X, v.Location.Y)).ToList();
                    // 闭合 3D 折线需补闭合段（旧版硬编码 false 会漏最后一段）
                    EmitPolyline(sb, pts, col, StrokeWidth(ent, scaleMul), p3.IsClosed, dash);
                }
                break;

            case Polyline2D p2d:
                {
                    // 2D 多段线（老式 POLYLINE+VERTEX）：顶点折线折线（bulge 弧段暂按直线弦，后续可采样为弧）
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = new List<(double X, double Y)>(p2d.Vertices.Count);
                    foreach (var v in p2d.Vertices)
                    {
                        var p = tr == null ? (v.Location.X, v.Location.Y) : tr.Apply(v.Location.X, v.Location.Y);
                        pts.Add(p);
                    }
                    EmitPolyline(sb, pts, col, StrokeWidth(ent, scaleMul), p2d.IsClosed, dash);
                }
                break;

            case Mesh mesh:
                {
                    // 3D 网格（现代 MESH）：渲染边线框（Edges 逐边画线段），与 AutoCAD 线框显示一致
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (mesh.Edges != null && mesh.Vertices != null && mesh.Vertices.Count > 0)
                    {
                        foreach (var e in mesh.Edges)
                        {
                            if (e.Start < 0 || e.End < 0 || e.Start >= mesh.Vertices.Count || e.End >= mesh.Vertices.Count) continue;
                            var va = mesh.Vertices[e.Start];
                            var vb = mesh.Vertices[e.End];
                            var (mx1, my1) = tr == null ? (va.X, va.Y) : tr.Apply(va.X, va.Y);
                            var (mx2, my2) = tr == null ? (vb.X, vb.Y) : tr.Apply(vb.X, vb.Y);
                            EmitLine(sb, mx1, my1, mx2, my2, col, w, null);
                        }
                    }
                }
                break;

            case PolyfaceMesh pf:
                {
                    // 旧式 3D 多面体网格：按 Face 索引连线成线框（三角/四边形）
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolyfaceMeshWire(pf, tr))
                        EmitLine(sb, a.AX, a.AY, b.BX, b.BY, col, w, null);
                }
                break;

            case PolygonMesh pm:
                {
                    // M×N 多边形网格：网格线框（行线+列线），参数无效退化为顶点折线
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolygonMeshWire(pm, tr))
                        EmitLine(sb, a.AX, a.AY, b.BX, b.BY, col, w, null);
                }
                break;

            case Wipeout wo:
                {
                    // 擦除多边形：用背景色填充（模拟 AutoCAD 擦除底层图元）；透明背景无底色可擦则跳过
                    if (!_transparentBg)
                    {
                        var wpoly = WipeoutWorldPoly(wo, tr);
                        EmitQuadFill(sb, wpoly, _whiteBg ? "#FFFFFF" : "#000000");
                    }
                }
                break;

            case Arc a: // 必须先于 Circle（fork 继承关系）
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (a.Center.X, a.Center.Y) : tr.Apply(a.Center.X, a.Center.Y);
                    double r = a.Radius * (tr?.Scale ?? 1.0);
                    // fork 角度为弧度 → 转度（参考实现入参为度）
                    EmitArcAsPolyline(sb, cx, cy, r, a.StartAngle * 180.0 / Math.PI, a.EndAngle * 180.0 / Math.PI, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Circle c:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (c.Center.X, c.Center.Y) : tr.Apply(c.Center.X, c.Center.Y);
                    double r = c.Radius * (tr?.Scale ?? 1.0);
                    EmitCircle(sb, cx, cy, r, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Ellipse el:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (el.Center.X, el.Center.Y) : tr.Apply(el.Center.X, el.Center.Y);
                    // 注意：与参考实现一致——netDxf MajorAxis=全长(2a)，此处 fork MajorAxis 同为全长（像素 parity）
                    double rx = el.MajorAxis * (tr?.Scale ?? 1.0);
                    double ry = el.MinorAxis * (tr?.Scale ?? 1.0);
                    double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度
                    EmitEllipse(sb, cx, cy, rx, ry, rot, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Point pt:
                {
                    var (cx, cy) = tr == null ? (pt.Location.X, pt.Location.Y) : tr.Apply(pt.Location.X, pt.Location.Y);
                    // 点标记颜色随实体/图层（与 CAD 一致）；大小与线宽保持一致（直径 = 3× 当前线宽）
                    EmitPoint(sb, cx, cy, ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul));
                }
                break;

            case TextEntity t:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    // SHX 样式（txt.shx/simpex.shx+Hztxt.shx 等）：用真实 SHX 笔迹路径渲染（与 AutoCAD 一致的字宽/字形）。
                    // 非 SHX 样式或字体缺失 → 回退原系统字体（SimSun）路径，行为逐字节不变。
                    var shx = TryBuildShxTextWorld(t, tr, scaleMul);
                    if (shx != null)
                    {
                        var d = new StringBuilder();
                        for (int k = 0; k < shx.Value.Type.Count; k++)
                        {
                            var p = shx.Value.Pts[k];
                            switch (shx.Value.Type[k])
                            {
                                case 0: d.Append("M").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))); break;
                                case 1: d.Append("L").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))); break;
                                case 2: d.Append("Q").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))).Append(' ').Append(F(Sx(p[2]))).Append(',').Append(F(Sy(p[3]))); break;
                                case 3: d.Append("C").Append(F(Sx(p[0]))).Append(',').Append(F(Sy(p[1]))).Append(' ').Append(F(Sx(p[2]))).Append(',').Append(F(Sy(p[3]))).Append(' ').Append(F(Sx(p[4]))).Append(',').Append(F(Sy(p[5]))); break;
                            }
                        }
                        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(shx.Value.PenWidth)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
                        break;
                    }
                    // TTF 样式：原系统字体路径（不变）
                    var (bx, by, brot, bscale) = TextBaseline(t, tr);
                    double fs = t.Height * scaleMul * bscale;
                    string cssFont = SvgFontFamily(ResolveFontFamily(GetStyleFont(t.Style)));
                    EmitText(sb, bx, by, t.Value ?? "", Math.Max(fs, 0.5), col, -brot, cssFont, t.WidthFactor);
                }
                break;

            case MText mt:
                // 多行文字：解析格式码（\P 换行/\H 分段字号/\f 字体）+ 参考列宽换行 + 附着点定位
                EmitMTextSvg(sb, mt, tr, scaleMul, layerAci, byBlockAci, GetStyleFont(mt.Style));
                break;

            case Solid so:
                EmitQuadFill(sb, QuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, tr), ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Face3D fd:
                EmitQuadFill(sb, QuadPts(fd.FirstCorner, fd.SecondCorner, fd.ThirdCorner, fd.FourthCorner, tr), ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Hatch ht:
                // HATCH 填充（实心=实体色/真色；450=1 渐变=SVG linear/radialGradient；图案 T4.5）
                EmitHatchSvg(sb, ht, tr, scaleMul, layerAci, byBlockAci);
                break;

            case Spline sp:
                {
                    // SPLINE（控制点+节矢 → de Boor NURBS 采样；仅拟合点 → 拟合折线）
                    string col = ColorOf(EntityAciIndex(sp), layerAci, byBlockAci);
                    if (TryEmitComplexSvg(sb, ent, tr, pat, col, StrokeWidth(sp, scaleMul))) break;
                    EmitSplineSvg(sb, sp, tr, scaleMul, col, dash);
                }
                break;

            case XLine xl:
                {
                    // 无限线：裁剪到画布矩形后渲染（与 AutoCAD 视口显示一致）
                    var (px, py) = tr == null ? (xl.FirstPoint.X, xl.FirstPoint.Y) : tr.Apply(xl.FirstPoint.X, xl.FirstPoint.Y);
                    var (dx, dy) = TransformDirection(xl.Direction.X, xl.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, true, out var x1, out var y1, out var x2, out var y2))
                        EmitLine(sb, x1, y1, x2, y2, ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Ray ray:
                {
                    // 射线：仅起点之后，裁剪到画布矩形后渲染
                    var (px, py) = tr == null ? (ray.StartPoint.X, ray.StartPoint.Y) : tr.Apply(ray.StartPoint.X, ray.StartPoint.Y);
                    var (dx, dy) = TransformDirection(ray.Direction.X, ray.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, false, out var x1, out var y1, out var x2, out var y2))
                        EmitLine(sb, x1, y1, x2, y2, ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case MLine ml:
                {
                    // 多线：按 MLineStyle 各 Offset 偏移到世界系平行折线，逐条描边
                    double w = StrokeWidth(ent, scaleMul);
                    foreach (var (poly, aci) in MLineOffsetPolys(ml, tr))
                    {
                        string col = ColorOf(aci, layerAci, byBlockAci);
                        var pts = new List<(double X, double Y)>(poly.Count);
                        foreach (var p in poly) pts.Add(p);
                        EmitPolyline(sb, pts, col, w, false, dash);
                    }
                }
                break;

            case Leader ld:
                {
                    // 引线：折线 + 尖端箭头（AutoCAD 默认实心/开放箭头，此处用开放 V 形）
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    var pts = new List<(double X, double Y)>(ld.Vertices?.Count ?? 0);
                    if (ld.Vertices != null)
                        foreach (var v in ld.Vertices)
                        {
                            var p = tr == null ? (v.X, v.Y) : tr.Apply(v.X, v.Y);
                            pts.Add(p);
                        }
                    if (pts.Count >= 2)
                    {
                        EmitPolyline(sb, pts, col, w, false, dash);
                        // 箭头（仅首段，尖端 pts[0]）
                        double alen = ld.TextHeight > 1e-9 ? ld.TextHeight * scaleMul : Math.Max(w * 6.0, 4.0 * (tr?.Scale ?? 1.0));
                        if (LeaderArrowWings(ld, pts, alen, out var w1x, out var w1y, out var w2x, out var w2y))
                        {
                            EmitLine(sb, w1x, w1y, pts[0].X, pts[0].Y, col, w, null);
                            EmitLine(sb, w2x, w2y, pts[0].X, pts[0].Y, col, w, null);
                        }
                    }
                }
                break;

            default:
                RecordSkip(ent.GetType().Name);
                break;
        }
    }

    private void EmitInsertSvg(StringBuilder sb, Insert ins, Transform2D parent, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul)
    {
        if (ins?.Block == null) return;
        var tr = Transform2D.Compose(ins, parent);
        // 块内容的"随块"颜色/线型 = 该 INSERT 的有效值（独立 > 图层；嵌套 ByBlock 继承上层）
        short insAci = EffectiveAci(ins, byBlockAci);
        double[] insDash = ResolveDash(ins, byBlockDash, ltMul);
        LtpPattern insPat = ResolvePattern(ins, byBlockPat, ltMul);
        // 块内"随图层"图元 = 该 INSERT 所在图层色（CAD 语义：块内 ByLayer 随插入点块图层）
        short insLayerAci = 7;
        try { insLayerAci = LayerAciIndex(ins.Layer); } catch { }
        // 块内实体的线型比例额外乘块插入比例（CAD 语义）
        double childLtMul = ltMul * BlockScaleOf(ins);
        foreach (var e in ins.Block.Entities)
            EmitEntitySvg(sb, e, tr, tr.Scale, insAci, insDash, insPat, childLtMul, insLayerAci);
    }

    // 按 TEXT 对齐方式（DXF 72/73 组码）计算基线起点、旋转与缩放。
    // fork：HorizontalAlignment(72: Left/Center/Right/Aligned/Middle/Fit) + VerticalAlignment(73: Baseline/Bottom/Middle/Top)。
    // R17：完整对齐方式，语义以 AutoCAD 2021 DXF 官方规范（帮助 "TEXT (DXF)" 组码表）为准：
    //   ① 72=0 且 73=0（基线/左对正）：组 11 无意义，基线起点 = 组 10（InsertPoint）。
    //   ② Aligned(72=3)/Fit(72=5)：基线沿 10→11 方向（附加旋转 = 方向角）；Fit 额外按 |11−10|/W 缩放字高。
    //   ③ 其余全部组合（72∈{1,2,4} 或 73∈{1,2,3}）：锚点 = 组 11（AlignmentPoint），
    //      基线起点 = 锚点 + R(rot)·(dx,dy)：
    //        dx：72 Left=0 / Center=−W/2 / Middle=−W/2 / Right=−W；
    //        dy：73 Baseline=Bottom=0 / Middle=−H/2 / Top=−H（文本框模型 [基线, 基线+H]）；
    //        y 锚点：R20——73=Baseline 时取组 10y（用户 ZWCAD 2021 实测：基线过设计者真实基线，
    //        组 11 仅水平锚）；73∈{1,2,3} 取组 11y（组 10 被忽略）。
    //        W = 应用文字样式后的字符串长度 = 估算宽度 × 宽度因子(41)（CJK 全角 1.0em × WF 与 SimSun 实际字宽一致）。
    // R16 的锚点规则（72=Left 一律取 10）对 72=0/73≠0 违规：图框数值单元格（如"武汉钢铁有限公司"72=0/73=2，
    // 11=10+(0,H/2)）被渲染在 10−H/2 处，整体偏低 H/2；且未乘宽度因子 41，72=4 居中标签水平漂移 (W_est−W_actual)/2。
    // widthOverride：PNG 光栅端传入"真实字体度量宽度（数据单位，含宽度因子）"，
    // 修正估算宽度与真实字宽（CJK 实测 1.167em vs 估算 1.0em）不一致导致的居中/右对正漂移。
    // null（SVG/PDF 端）= 沿用 EstimateTextWidth 估算（行为逐字节不变）。
    private (double X, double Y, double Rot, double Scale) TextBaseline(TextEntity t, Transform2D tr, double? widthOverride = null)
    {
        double H = t.Height;
        double W = widthOverride ?? (EstimateTextWidth(t.Value ?? "", H) * Math.Max(t.WidthFactor, 1e-9));
        double rot = t.Rotation * 180.0 / Math.PI; // fork 弧度 → 度
        XYZ origin;
        double extraRot = 0.0, fitScale = 1.0;
        bool baselineLeft = t.HorizontalAlignment == TextHorizontalAlignment.Left
                            && t.VerticalAlignment == TextVerticalAlignmentType.Baseline;
        if (baselineLeft)
        {
            origin = t.InsertPoint; // ① 基线起点 = 组 10
        }
        else if (t.HorizontalAlignment == TextHorizontalAlignment.Aligned
                 || t.HorizontalAlignment == TextHorizontalAlignment.Fit)
        {
            // ② 基线沿 10→11
            origin = t.InsertPoint;
            double ddx = t.AlignmentPoint.X - t.InsertPoint.X;
            double ddy = t.AlignmentPoint.Y - t.InsertPoint.Y;
            if (ddx * ddx + ddy * ddy > 1e-18)
            {
                extraRot = Math.Atan2(ddy, ddx) * 180.0 / Math.PI;
                if (t.HorizontalAlignment == TextHorizontalAlignment.Fit && W > 1e-9)
                    fitScale = Math.Sqrt(ddx * ddx + ddy * ddy) / W;
            }
        }
        else
        {
            // ③ 锚点 = 组 11 + 72/73 偏移（局部偏移旋转到世界系）
            double dx = t.HorizontalAlignment switch
            {
                TextHorizontalAlignment.Center => -W / 2,
                TextHorizontalAlignment.Middle => -W / 2,
                TextHorizontalAlignment.Right => -W,
                _ => 0
            };
            // R21（用户反馈"图框字体坐标偏移"，实测 test1234 图框数值 武汉钢铁有限公司 V=Middle 偏低 1.35px）：
            // V=Middle 用字体真实 em-box 中心，而非旧 H-box 模型。旧模型假设字形全在基线上方
            // （ascent=H, descent=0）→ 基线=11y−H/2；但实测 SimSun/SimHei ascent=0.859、descent=0.141，
            // em-box 中心=基线+(asc−desc)/2=0.359H，故 V=Middle 文本整体偏低 0.141H（=H/2−0.359H）。
            // SHX 样式 family=null → centerFrac=0.5（保持 SHX 路径原行为，不劣化）；V=Baseline 仍 dy=0。
            double vcf = GetEmBoxCenterFraction(ResolveFontFamily(GetStyleFont(t.Style)));
            double dy = t.VerticalAlignment switch
            {
                TextVerticalAlignmentType.Middle => -vcf * H,
                TextVerticalAlignmentType.Top => -H,
                _ => 0 // Baseline/Bottom（保持旧行为，避免影响其他文字）
            };
            // R20（用户 ZWCAD 2021 实测，r20_base_cmp.json）：73=0（Baseline）时基线 y 过组 10
            // （设计者真实基线，如"测绘制图单位"10y=3.4805=实测 3.484），组 11 只用于水平锚（72）；
            // 73≠0 取组 11y（垂直偏移量见上方 R21 em-box 中心修正）。
            double anchorY = t.VerticalAlignment == TextVerticalAlignmentType.Baseline
                ? t.InsertPoint.Y
                : t.AlignmentPoint.Y;
            double r = rot * Math.PI / 180.0;
            double ca = Math.Cos(r), sa = Math.Sin(r);
            origin = new XYZ(
                t.AlignmentPoint.X + dx * ca - dy * sa,
                anchorY + dx * sa + dy * ca,
                t.AlignmentPoint.Z);
        }
        double totalRot = rot + extraRot;
        if (tr != null)
        {
            var applied = tr.Apply(origin.X, origin.Y);
            return (applied.Item1, applied.Item2, totalRot + (tr?.Rotation ?? 0.0), fitScale);
        }
        return (origin.X, origin.Y, totalRot, fitScale);
    }

    // 估算文本宽度（数据单位）：CJK 全角≈1.0×字高，半角≈0.55×字高，再乘宽度因子。
    private static double EstimateTextWidth(string s, double H)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double w = 0;
        foreach (char ch in s)
        {
            bool cjk = IsCjkChar(ch);
            w += cjk ? 1.0 : 0.55;
        }
        return w * H;
    }

    // ===== 文字样式 → 字体族解析 =====
    private static string ResolveFontFamily(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)) return null;
        string f = fontName.Trim();
        string fam = f;
        int dot = f.LastIndexOf('.');
        if (dot > 0) fam = f.Substring(0, dot);
        string lower = fam.ToLowerInvariant();
        // SHX（AutoCAD 形状字体，系统无 TTF 等价）→ 默认
        if (lower.EndsWith("shx") || lower == "txt" || lower == "simplex" || lower == "hztxt"
            || lower == "romans" || lower == "gothic" || lower == "arial" || lower == "aaa")
            return null;
        // 方正字体（FZ*）：含 DengXian/等线 → DengXian；其余 → 默认
        if (lower.StartsWith("fz"))
            return (lower.Contains("dengxian") || lower.Contains("等线")) ? "DengXian" : null;
        switch (lower)
        {
            case "simhei": case "黑体": return "SimHei";
            case "simsun": case "宋体": return "SimSun";
            case "dengxian": case "等线": return "DengXian";
            case "kaiti": case "楷体": case "楷体_gb2312": return "KaiTi";
            case "fangsong": case "仿宋": case "仿宋_gb2312": return "FangSong";
            case "simli": case "隶书": return "SimLi";
            case "microsoft yahei": case "微软雅黑": return "Microsoft YaHei";
            default: return fam;
        }
    }

    // 标准族名 → CSS font-family 列表（SVG）。null/默认 → 宋体。
    private static string SvgFontFamily(string family)
    {
        if (string.IsNullOrEmpty(family)) return "SimSun, 宋体, serif";
        switch (family)
        {
            case "SimHei": return "SimHei, 黑体, sans-serif";
            case "SimSun": return "SimSun, 宋体, serif";
            case "DengXian": return "DengXian, 等线, sans-serif";
            case "KaiTi": return "KaiTi, 楷体, serif";
            case "FangSong": return "FangSong, 仿宋, serif";
            case "SimLi": return "SimLi, 隶书, serif";
            case "Microsoft YaHei": return "Microsoft YaHei, 微软雅黑, sans-serif";
            default: return family + ", SimSun, 宋体, serif";
        }
    }

    // 实体样式 → 原始字体名（fork TextStyle：Filename(3) 优先，其次 BigFontFilename(4)）
    private static string GetStyleFont(TextStyle style)
    {
        if (style == null) return null;
        try
        {
            string fam = style.Filename;
            if (!string.IsNullOrWhiteSpace(fam)) return fam;
            string ff = style.BigFontFilename;
            if (!string.IsNullOrWhiteSpace(ff)) return ff;
        }
        catch { }
        return null;
    }

    // R21：字体 em-box 中心占字高 H 的比例（TTF 用真实度量，SHX/未知回退 0.5）。
    // V=Middle 垂直居中需把基线放在 11y − centerFrac·H，使 em-box 中心落在锚点。
    // 实测 SimSun/SimHei/DengXian 等：ascent≈0.86、descent≈0.14（DengXian 0.78/0.22）→ centerFrac≈0.36/0.39，
    // 而非旧 H-box 模型假设的 0.5 → 修正 V=Middle 文本偏低 0.14H 的问题。
    // family 为 ResolveFontFamily 返回的 CSS 族列表（如 "SimHei, 黑体, sans-serif"），取首个可解析族测度量。
    private static readonly Dictionary<string, double> _emCenterCache = new(StringComparer.OrdinalIgnoreCase);
    private static double GetEmBoxCenterFraction(string family)
    {
        if (string.IsNullOrEmpty(family)) return 0.5; // SHX（family=null）→ 0.5，保持 SHX 路径原行为
        string first = family.Split(',')[0].Trim();
        if (first.Length == 0) return 0.5;
        if (_emCenterCache.TryGetValue(first, out double cached)) return cached;
        double frac = 0.5;
        try
        {
            // 纯托管（零外部依赖）：族名→TTF/TTC 文件→TrueTypeFont 度量。
            string? path = PdfFontManager.FindTtf(first);
            if (!string.IsNullOrEmpty(path))
            {
                var tf = TrueTypeFont.LoadFromFile(path!);
                if (tf != null)
                {
                    // em-box = [−descent, ascent]（descent 取绝对值，兼容不同字体存储符号约定）
                    double top = Math.Abs(tf.Ascent);
                    double bottom = Math.Abs(tf.Descent);
                    double em = top + bottom;
                    if (em > 1e-6)
                    {
                        // em-box 中心相对基线 = (top − bottom)/2 → 占 em 比例
                        frac = (top - bottom) / (2.0 * em);
                        if (frac < 0) frac = 0;
                        if (frac > 1) frac = 1;
                    }
                }
            }
        }
        catch { frac = 0.5; } // 度量失败 → 回退 0.5（不劣化）
        _emCenterCache[first] = frac;
        return frac;
    }

    #endregion

    #region ===== SHX 真实字形文字（与 AutoCAD 一致：unifont 拉丁 + bigfont CJK 笔迹路径）=====

    // 根因：主文字样式为 SHX 字体（Standard=txt.shx+Hztxt.shx、HZTXT=simplex.shx+Hztxt.shx 等），
    // 旧 ResolveFontFamily 把所有 .shx 映射 null→SimSun（比例字体），而 txt.shx 是等宽（数字 0.75em）、
    // SimSun 数字仅 0.5em → 拉丁/数字标签比 AutoCAD 短 25~45%，居中/右对齐文字锚点漂移。
    // 修法：SHX 样式改用真实 SHX 笔迹路径（ShxFont 字形 + advance）渲染，与 AutoCAD 完全一致。
    // unifont（txt.shx）按字符码取字形；bigfont（Hztxt.shx）按 GBK 双字节取字形；em=UnitsPerEm，
    // 字高 H 对应 em 盒 → 缩放 s=H/em，基线 y=0 与数据系同向。TTF 样式（.ttf）仍走原系统字体路径。

    // GBK 编码（bigfont CJK → 双字节码）；CodePages 包不可用时返回 null（调用方回退 TTF 路径，不劣化）。
    private static System.Text.Encoding _shxGbk;
    private static bool _shxGbkTried;
    private static System.Text.Encoding ShxGbkEncoding()
    {
        if (_shxGbkTried) return _shxGbk;
        _shxGbkTried = true;
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            _shxGbk = System.Text.Encoding.GetEncoding(936); // GBK
        }
        catch { _shxGbk = null; }
        return _shxGbk;
    }

    // 解析文字样式的 SHX 文本面：latin=unifont/bigfont 拉丁面（按 Filename），cjk=bigfont CJK 面（按 BigFontFilename）。
    // 非 .shx 样式、字体文件缺失、或格式不符 → 返回 false（调用方走原 TTF 路径）。
    private bool TryGetShxTextFaces(TextEntity t, out ShxFont latin, out ShxFont cjk)
    {
        latin = null; cjk = null;
        string fname = null, bfname = null;
        try { fname = t.Style?.Filename; bfname = t.Style?.BigFontFilename; } catch { }
        if (string.IsNullOrWhiteSpace(fname)) return false;
        string low = fname.Trim().ToLowerInvariant();
        if (!low.EndsWith(".shx", StringComparison.Ordinal)) return false; // 仅 SHX 走本路径，.ttf 走 TTF
        var lat = GetShapeFont(fname); // 复用按文件名缓存的 ShxFont 加载（FileProvider 解析路径）
        if (lat == null) return false;
        if (lat.Format != ShxFormat.Unifont && lat.Format != ShxFormat.BigFont) return false;
        latin = lat;
        if (!string.IsNullOrWhiteSpace(bfname))
        {
            var big = GetShapeFont(bfname);
            if (big != null && big.Format == ShxFormat.BigFont) cjk = big;
        }
        return true;
    }

    // SHX 文字 → 世界坐标（数据单位，y 向上）笔路径。返回 null = 该文字不适合 SHX 路径
    // （非 SHX 样式/字体缺失/CJK 无法定位字形），调用方回退 TTF 路径。
    //   out Type  : 逐指令类型（0=Move 1=Line 2=Quad 3=Cubic），与 GlyphRec.Type 同序
    //   out Pts   : 与 Type 一一对应的控制点数组（[x,y] / [cx,cy,ex,ey] / [c1x,c1y,c2x,c2y,ex,ey]），世界坐标（数据单位）
    //   out PenWidth : 笔画宽度（数据单位，三端描边用）
    // 算法：① 逐字符取 unifont/bigront 字形（局部：基线 y=0、+x 右，字体单位）→ 数据单位局部点（缩放 s=H/em、宽因子 wf 只乘一次）；
    //       ② 局部总宽 W 作 widthOverride 传给 TextBaseline 得基线左端 (bx,by) 与总旋转 brot（含块变换）；
    //       ③ 局部点绕 (bx,by) 旋转 brot 得世界点。
    private (List<int> Type, List<double[]> Pts, double PenWidth)? TryBuildShxTextWorld(TextEntity t, Transform2D tr, double scaleMul)
    {
        string text = t.Value ?? "";
        if (text.Length == 0) return null;
        if (!TryGetShxTextFaces(t, out ShxFont latin, out ShxFont cjk)) return null;
        System.Text.Encoding gbk = null; bool hasCjk = false;
        foreach (var ch in text) if (ch >= 0x2E80) { hasCjk = true; break; }
        if (hasCjk)
        {
            if (cjk == null) return null; // 含 CJK 但无 bigfont → 无法定位字形 → 回退 TTF（避免缺字/错字）
            gbk = ShxGbkEncoding();
            if (gbk == null) return null;  // GBK 不可用 → 回退 TTF
        }

        double H = t.Height * scaleMul;                 // 字高（数据单位，em 盒高）
        if (!(H > 1e-9)) return null;
        double wf = Math.Max(t.WidthFactor, 1e-9);      // 宽因子（只作用水平方向）

        // ① 局部路径（基线左端为原点，+x 右、+y 上，数据单位）+ 总宽 W。
        // 各面 em 不同（txt.shx=8 / Hztxt.shx=11），advance 与字形点统一换算到数据单位（s=H/em_该面）。
        var ltype = new List<int>(text.Length * 4);
        var lpts = new List<double[]>(text.Length * 4);
        double penX = 0.0; // 累计 advance（数据单位，未乘 wf）
        foreach (var ch in text)
        {
            ShxFont face; int code;
            if (ch < 0x80) { face = latin; code = ch; }
            else
            {
                byte[] gb = gbk.GetBytes(new char[] { ch });
                if (gb.Length != 2) continue;
                face = cjk; code = (gb[0] << 8) | gb[1];
            }
            int em = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1;
            double s = H / em;                          // 数据单位 / 该面字体单位
            bool hasAdv = face.TryGetAdvance(code, out float advF);
            double advData = (hasAdv ? advF : 0.7 * em) * s; // 该字符 advance（数据单位）
            var g = new GlyphRec();
            bool ok = face.TryGetGlyph(code, g, ShxTextOrientation.Horizontal);
            if (ok && !g.Empty)
                for (int k = 0; k < g.Type.Count; k++)
                {
                    var p = g.Pts[k];
                    switch (g.Type[k])
                    {
                        case 0:
                        case 1:
                            ltype.Add(g.Type[k]);
                            lpts.Add(new double[] { (penX + p[0] * s) * wf, p[1] * s });
                            break;
                        case 2:
                            ltype.Add(2);
                            lpts.Add(new double[] { (penX + p[0] * s) * wf, p[1] * s, (penX + p[2] * s) * wf, p[3] * s });
                            break;
                        case 3:
                            ltype.Add(3);
                            lpts.Add(new double[] { (penX + p[0] * s) * wf, p[1] * s, (penX + p[2] * s) * wf, p[3] * s, (penX + p[4] * s) * wf, p[5] * s });
                            break;
                    }
                }
            penX += advData;
        }
        if (ltype.Count == 0) return null;              // 无任何字形（全缺）→ 回退 TTF
        double W = penX * wf;                           // 局部总宽（数据单位）

        // ② 基线左端 (bx,by) 与总旋转 brot、Fit 缩放 bscale（含块变换）；W 作 widthOverride 修正居中/右对齐锚点
        var (bx, by, brot, bscale) = TextBaseline(t, tr, W);
        double br = brot * Math.PI / 180.0;
        double ca = Math.Cos(br), sa = Math.Sin(br);
        if (!(bscale > 0)) bscale = 1.0;

        // ③ 局部 → 世界（绕基线左端缩放 bscale[Fit] 再旋转 brot）
        var wtype = ltype;
        var wpts = new List<double[]>(lpts.Count);
        foreach (var lp in lpts)
        {
            int n = lp.Length;
            var wp = new double[n];
            for (int i = 0; i < n; i += 2)
            {
                double lx = lp[i] * bscale, ly = lp[i + 1] * bscale;
                wp[i] = bx + lx * ca - ly * sa;
                wp[i + 1] = by + lx * sa + ly * ca;
            }
            wpts.Add(wp);
        }
        // 笔画宽度 = 文字实体线宽（CAD 语义：SHX 文字按实体 370/ByLayer 线宽描边，与其余图元及
        // 复杂线型 SHX 字形一致），Fit 时随 bscale 等比缩放。线宽缺失/为 0 时 StrokeWidth 兜底最小 0.1。
        double penW = StrokeWidth(t, scaleMul) * bscale;
        return (wtype, wpts, penW);
    }

    #endregion

    #region ===== MTEXT 多行文字 =====

    // MTEXT 段（字号/宽比/颜色相同的连续文本）
    private struct MTextRun
    {
        public string Text;
        public double Height;
        public double WidthFactor;
        public int? Color;      // \C 颜色（ACI）；null=随实体
        public bool Underline;  // 下划线（\U）
        public bool Overline;   // 上划线（\O/\o）
        public bool Bold;       // 加粗（\f 规格 |b1|）
        public bool Italic;     // 斜体（\f 规格 |i1|）
        public double Oblique;  // 倾斜角（\Q<deg>;，度，0=无）
        public string Font;     // 字体族名（\f 内联）；null=随实体样式
    }

    // MTEXT 一行
    private class MTextLine
    {
        public List<MTextRun> Runs = new();
        public double MaxHeight;
        public double Width;
        public void Recalc()
        {
            MaxHeight = 0; Width = 0;
            foreach (var r in Runs)
            {
                MaxHeight = Math.Max(MaxHeight, r.Height);
                Width += MTextRunWidth(r);
            }
        }
    }

    private static double MTextRunWidth(MTextRun r)
        => EstimateTextWidth(r.Text, r.Height) * (r.WidthFactor > 0 ? r.WidthFactor : 1.0);

    private static bool IsCjkChar(char ch)
        => (ch >= 0x2E80 && ch <= 0x9FFF) || (ch >= 0xFF00 && ch <= 0xFFEF)
           || (ch >= 0x3000 && ch <= 0x303F) || (ch >= 0x20000 && ch <= 0x2FA1F);

    private static bool IsHexChar(char ch)
        => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

    // 解析 MTEXT 原始文本（含格式码）为行/段
    private MTextLayout ParseMText(string raw, double baseHeight)
    {
        var layout = new MTextLayout();
        MTextLine line = new MTextLine();
        layout.Lines.Add(line);
        var stack = new Stack<(double H, double WF, int? C, bool U, bool OV, bool B, bool I, double OB, string F)>();
        double curH = baseHeight > 0 ? baseHeight : 1.0;
        double curWF = 1.0;
        int? curC = null;
        bool curU = false;
        bool curOV = false;
        bool curB = false;
        bool curI = false;
        double curOB = 0.0;
        string curFont = null;
        var curText = new StringBuilder();

        void Flush()
        {
            if (curText.Length > 0)
                line.Runs.Add(new MTextRun
                {
                    Text = curText.ToString(), Height = curH, WidthFactor = curWF, Color = curC,
                    Underline = curU, Overline = curOV, Bold = curB, Italic = curI, Oblique = curOB, Font = curFont
                });
            curText.Length = 0;
        }
        void NewLine()
        {
            Flush();
            line = new MTextLine();
            layout.Lines.Add(line);
        }
        int SkipCode(int i)
        {
            int end = raw.IndexOf(';', i + 2);
            return end < 0 ? raw.Length - 1 : end;
        }

        if (!string.IsNullOrEmpty(raw))
        {
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch == '{')
                {
                    Flush();
                    stack.Push((curH, curWF, curC, curU, curOV, curB, curI, curOB, curFont));
                }
                else if (ch == '}')
                {
                    Flush();
                    if (stack.Count > 0) (curH, curWF, curC, curU, curOV, curB, curI, curOB, curFont) = stack.Pop();
                }
                else if (ch == '~' || ch == '\t')
                {
                    NewLine();
                }
                else if (ch == '\\')
                {
                    if (i + 1 >= raw.Length) break;
                    char code = raw[i + 1];
                    switch (code)
                    {
                        case 'P': NewLine(); i++; break;
                        case '\\': curText.Append('\\'); i++; break;
                        case '{': curText.Append('{'); i++; break;
                        case '}': curText.Append('}'); i++; break;
                        case 'H':
                        case 'h':
                            {
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                bool rel = v.EndsWith("x") || v.EndsWith("X");
                                if (rel) v = v.Substring(0, v.Length - 1).Trim();
                                if (double.TryParse(v, NumberStyles.Float, Ci, out double h) && h > 0)
                                {
                                    // \H 只影响其后的文本：先以旧高度落段，再改高度
                                    // 相对高度（x 后缀）= 改码前的“当前高度”× 倍数（AutoCAD 语义，不是基准高度）
                                    double oldH = curH;
                                    Flush();
                                    curH = rel ? (oldH > 0 ? oldH * h : h) : h;
                                }
                                i = end;
                            }
                            break;
                        case 'W':
                        case 'w':
                            {
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                if (double.TryParse(v, NumberStyles.Float, Ci, out double wf) && wf > 0)
                                {
                                    Flush(); // \W 只影响其后的文本
                                    curWF = wf;
                                }
                                i = end;
                            }
                            break;
                        case 'C':
                        case 'c':
                            {
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                if (int.TryParse(v, out int c) && c >= 1 && c <= 255)
                                {
                                    Flush(); // \C 只影响其后的文本
                                    curC = c;
                                }
                                i = end;
                            }
                            break;
                        case 'L': Flush(); curU = true; i++; break;
                        case 'l': Flush(); curU = false; i++; break;
                        case 'f':
                        case 'F':
                            {
                                // \f<fontname>|b<bold>|i<italic>|c<charset>|p<pitch>;
                                // 字体名 = 第一个 '|' 之前；|b1|/|i1| 分别表示加粗/斜体
                                int end = SkipCode(i);
                                string arg = raw.Substring(i + 2, end - i - 2);
                                int pipe = arg.IndexOf('|');
                                string fontName = (pipe >= 0 ? arg.Substring(0, pipe) : arg).Trim();
                                bool specB = false, specI = false;
                                if (pipe >= 0)
                                {
                                    foreach (var part in arg.Substring(pipe + 1).Split('|'))
                                    {
                                        if (part.Length < 2) continue;
                                        char key = char.ToLowerInvariant(part[0]);
                                        string val = part.Substring(1).Trim();
                                        if (key == 'b') specB = (val == "1");
                                        else if (key == 'i') specI = (val == "1");
                                    }
                                }
                                // 兼容“字体名直接带 Bold/Italic”的写法
                                if (!specB && fontName.EndsWith("Bold", StringComparison.OrdinalIgnoreCase)) specB = true;
                                if (!specI && fontName.EndsWith("Italic", StringComparison.OrdinalIgnoreCase)) specI = true;
                                Flush(); // \f 只影响其后的文本
                                curFont = fontName.Length > 0 ? fontName : null;
                                curB = specB;
                                curI = specI;
                                i = end;
                            }
                            break;
                        case 'O': Flush(); curOV = true; i++; break;   // 上划线开
                        case 'o': Flush(); curOV = false; i++; break;  // 上划线关
                        case 'Q':
                            {
                                // \Q<倾斜角>; 倾斜角（度），影响其后的文本
                                int end = SkipCode(i);
                                string v = raw.Substring(i + 2, end - i - 2).Trim();
                                if (double.TryParse(v, NumberStyles.Float, Ci, out double ob))
                                {
                                    Flush();
                                    curOB = ob;
                                }
                                i = end;
                            }
                            break;
                        case 'R':
                        case 'A':
                        case 'B':
                        case 'E':
                        case 'I':
                        case 'K':
                        case 'T':
                        case 'X':
                        case 'Y':
                            i = SkipCode(i); // 对齐/字距/其他——消费，不渲染
                            break;
                        case 'k':
                            i++; // 删除线关（无参数，暂不渲染）
                            break;
                        case 'U':
                            if (i + 2 < raw.Length && raw[i + 2] == '+')
                            {
                                int end = i + 3;
                                while (end < raw.Length && end < i + 7 && IsHexChar(raw[end])) end++;
                                if (end > i + 3 && int.TryParse(raw.Substring(i + 3, end - i - 3), NumberStyles.HexNumber, Ci, out int cp))
                                    curText.Append((char)cp);
                                i = end;
                            }
                            else i = SkipCode(i); // \U<on|off>; 下划线
                            break;
                        case 'S':
                            {
                                int end = SkipCode(i);
                                curText.Append(raw.Substring(i + 2, end - i - 2).Replace("^B", ""));
                                i = end;
                            }
                            break;
                        default:
                            if (char.IsLetter(code)) i = SkipCode(i); // 未知码（如 \pt7;）——消费
                            else curText.Append(ch);
                            break;
                    }
                }
                else
                {
                    curText.Append(ch);
                }
            }
        }
        Flush();

        foreach (var ln in layout.Lines) ln.Recalc();
        FinishMTextLayout(layout);
        return layout;
    }

    // MTEXT 排版结果
    private class MTextLayout
    {
        public List<MTextLine> Lines = new();
        public double MaxLineWidth;
    }

    private static void FinishMTextLayout(MTextLayout layout)
    {
        layout.MaxLineWidth = 0;
        foreach (var ln in layout.Lines)
            layout.MaxLineWidth = Math.Max(layout.MaxLineWidth, ln.Width);
    }

    // 行距（基线到基线）：AutoCAD 默认≈1.5×行高；
    // Exact=直接用组码44值，AtLeast=max(44值, 1.5×行高)
    private static double MTextLineGap(MTextLine prev, MTextLine cur, LineSpacingStyleType style, double factor)
    {
        double maxH = Math.Max(Math.Max(prev.MaxHeight, cur.MaxHeight), 0.1);
        double def = 1.5 * maxH;
        switch (style)
        {
            case LineSpacingStyleType.Exact:
                return factor > 0 ? factor : def;
            case LineSpacingStyleType.AtLeast:
                return Math.Max(factor > 0 ? factor : 0.0, def);
            default:
                return def;
        }
    }

    // 按参考列宽（组码41）换行：CJK 逐字可断，拉丁按词（含尾随空格）断
    private MTextLayout WrapMText(MTextLayout layout, double refWidth)
    {
        if (refWidth <= 0) return layout;
        bool needWrap = false;
        foreach (var line in layout.Lines)
            if (line.Width > refWidth && line.Runs.Count > 0) { needWrap = true; break; }
        if (!needWrap) return layout;

        var result = new MTextLayout();
        foreach (var line in layout.Lines)
        {
            if (line.Width <= refWidth || line.Runs.Count == 0)
            {
                result.Lines.Add(line);
                continue;
            }
            // 分词：CJK 单字 / 连续空白 / 拉丁词+尾随空白
            var frags = new List<(string Text, MTextRun Run)>();
            foreach (var r in line.Runs)
            {
                int i = 0;
                while (i < r.Text.Length)
                {
                    char ch = r.Text[i];
                    int j = i + 1;
                    if (IsCjkChar(ch))
                    {
                        frags.Add((r.Text.Substring(i, 1), r));
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                        while (j < r.Text.Length && char.IsWhiteSpace(r.Text[j])) j++;
                        frags.Add((r.Text.Substring(i, j - i), r));
                    }
                    else
                    {
                        while (j < r.Text.Length && !IsCjkChar(r.Text[j]) && !char.IsWhiteSpace(r.Text[j])) j++;
                        int k = j;
                        while (k < r.Text.Length && char.IsWhiteSpace(r.Text[k])) k++;
                        frags.Add((r.Text.Substring(i, k - i), r));
                        j = k;
                    }
                    i = j;
                }
            }
            // 贪心填充
            var cur = new List<(string Text, MTextRun Run)>();
            double curW = 0;
            bool hasText = false;
            void FlushCur()
            {
                if (cur.Count > 0)
                {
                    var ln = new MTextLine();
                    foreach (var (t, run) in cur)
                    {
                        if (ln.Runs.Count > 0)
                        {
                            var last = ln.Runs[ln.Runs.Count - 1];
                            if (last.Height == run.Height && last.WidthFactor == run.WidthFactor
                                && last.Color == run.Color && last.Underline == run.Underline
                                && (last.Font ?? "") == (run.Font ?? ""))
                            {
                                last.Text += t;
                                ln.Runs[ln.Runs.Count - 1] = last;
                                continue;
                            }
                        }
                        ln.Runs.Add(new MTextRun { Text = t, Height = run.Height, WidthFactor = run.WidthFactor, Color = run.Color, Underline = run.Underline, Font = run.Font });
                    }
                    ln.Recalc();
                    result.Lines.Add(ln);
                }
                cur.Clear(); curW = 0; hasText = false;
            }
            foreach (var (t, run) in frags)
            {
                double fw = EstimateTextWidth(t, run.Height) * (run.WidthFactor > 0 ? run.WidthFactor : 1.0);
                if (hasText && curW + fw > refWidth) FlushCur();
                if (!string.IsNullOrWhiteSpace(t)) hasText = true;
                cur.Add((t, run));
                curW += fw;
            }
            FlushCur();
        }
        FinishMTextLayout(result);
        return result;
    }

    // MTEXT 局部排版：各行基线（第0行=0，y 向上）+ 附着点锚（局部坐标，数据单位）
    private static (double[] Baselines, double AnchorX, double AnchorY) MTextLocalLayout(MTextLayout layout, MText mt)
    {
        int n = layout.Lines.Count;
        var baselines = new double[n];
        // 局部坐标系 y 向上（与 CAD 一致）：第 0 行基线在 0，其后各行向下（基线 y 递减），
        // 行距 = 基线到基线距离。旧版误用“+”使基线递增 → 多行上下颠倒、相互重叠。
        for (int i = 1; i < n; i++)
            baselines[i] = baselines[i - 1] - MTextLineGap(layout.Lines[i - 1], layout.Lines[i], mt.LineSpacingStyle, mt.LineSpacing);
        // 文本框：顶=首行基线+0.8×行高（上升部），底=末行基线-0.2×行高（下降部）
        double top = 0.8 * layout.Lines[0].MaxHeight;
        double bottom = baselines[n - 1] - 0.2 * layout.Lines[n - 1].MaxHeight;
        double boxH = top - bottom;
        double anchorX = 0, anchorY = top;
        switch (mt.AttachmentPoint)
        {
            case AttachmentPointType.TopCenter:
            case AttachmentPointType.MiddleCenter:
            case AttachmentPointType.BottomCenter: anchorX = layout.MaxLineWidth / 2; break;
            case AttachmentPointType.TopRight:
            case AttachmentPointType.MiddleRight:
            case AttachmentPointType.BottomRight: anchorX = layout.MaxLineWidth; break;
        }
        switch (mt.AttachmentPoint)
        {
            case AttachmentPointType.MiddleLeft:
            case AttachmentPointType.MiddleCenter:
            case AttachmentPointType.MiddleRight: anchorY = top - boxH / 2; break;
            case AttachmentPointType.BottomLeft:
            case AttachmentPointType.BottomCenter:
            case AttachmentPointType.BottomRight: anchorY = bottom; break;
        }
        return (baselines, anchorX, anchorY);
    }

    // MTEXT → SVG：每行每段独立 <text>（各自字号/颜色），基线按附着点+旋转定位
    private void EmitMTextSvg(StringBuilder sb, MText mt, Transform2D tr, double scaleMul, short layerAci, short byBlockAci, string styleFont)
    {
        string baseCol = ColorOf(EntityAciIndex(mt), layerAci, byBlockAci);
        var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height), mt.RectangleWidth);
        if (layout.Lines.Count == 0) return;
        var (baselines, anchorX, anchorY) = MTextLocalLayout(layout, mt);

        double rot = mt.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度（MText.Rotation 由 AlignmentPoint 推导）
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double px = mt.InsertPoint.X, py = mt.InsertPoint.Y;
        if (tr != null) { var ap = tr.Apply(px, py); px = ap.Item1; py = ap.Item2; }

        // 局部点→世界：P + R·(local − anchor)（局部偏移乘 scaleMul）
        void ToWorld(double lx, double ly, out double wx, out double wy)
        {
            double dx = (lx - anchorX) * scaleMul, dy = (ly - anchorY) * scaleMul;
            wx = px + dx * ca - dy * sa;
            wy = py + dx * sa + dy * ca;
        }

        for (int li = 0; li < layout.Lines.Count; li++)
        {
            double x = 0;
            foreach (var run in layout.Lines[li].Runs)
            {
                double rw = MTextRunWidth(run) * scaleMul;
                if (!string.IsNullOrEmpty(run.Text))
                {
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    double fs = Math.Max(run.Height * scaleMul, 0.2);
                    string cssFont = SvgFontFamily(ResolveFontFamily(!string.IsNullOrEmpty(run.Font) ? run.Font : styleFont));
                    ToWorld(x, baselines[li], out double wx, out double wy);
                    double sx = Sx(wx), sy = Sy(wy);

                    // 富文本属性：字体族/字号/颜色/加粗/斜体/下划线+上划线
                    var attr = new List<string>
                    {
                        $"x=\"0\" y=\"0\"",
                        $"font-family=\"{cssFont}\"",
                        $"font-size=\"{F(fs)}\"",
                        $"fill=\"{col}\""
                    };
                    if (run.Bold) attr.Add($"font-weight=\"bold\"");
                    if (run.Italic) attr.Add($"font-style=\"italic\"");
                    var deco = new List<string>();
                    if (run.Underline) deco.Add("underline");
                    if (run.Overline) deco.Add("overline");
                    if (deco.Count > 0) attr.Add($"text-decoration=\"{string.Join(" ", deco)}\"");

                    // transform：平移到基线左端点，再绕该点 rotate(旋转) / skewX(倾斜) / scale(宽度压缩)
                    var tf = new List<string> { $"translate({F(sx)}, {F(sy)})" };
                    if (Math.Abs(rot) > 1e-6) tf.Add($"rotate({F(-rot)})");
                    if (Math.Abs(run.Oblique) > 1e-6) tf.Add($"skewX({F(-run.Oblique)})");
                    if (Math.Abs(run.WidthFactor - 1.0) > 1e-6) tf.Add($"scale({F(run.WidthFactor)}, 1)");
                    attr.Add($"transform=\"{string.Join(" ", tf)}\"");

                    sb.AppendLine($"  <text {string.Join(" ", attr)}>{EscapeXml(run.Text)}</text>");
                }
                x += rw;
            }
        }
    }

    #endregion

    #region ===== 基础图元发射（内部统一经 Sx/Sy 翻转坐标）=====
    // dash 为虚线模式（数据单位）；null/空 = 实线。
    private void EmitLine(StringBuilder sb, double x1, double y1, double x2, double y2, string stroke, double w, double[] dash)
    {
        string da = DashAttr(dash);
        sb.AppendLine($"  <line x1=\"{F(Sx(x1))}\" y1=\"{F(Sy(y1))}\" x2=\"{F(Sx(x2))}\" y2=\"{F(Sy(y2))}\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    private void EmitPolyline(StringBuilder sb, List<(double X, double Y)> pts, string stroke, double w, bool closed, double[] dash)
    {
        if (pts.Count < 2) return;
        var d = new StringBuilder();
        d.Append("M");
        for (int i = 0; i < pts.Count; i++)
            d.Append($"{(i == 0 ? "" : "L")}{F(Sx(pts[i].X))},{F(Sy(pts[i].Y))}");
        if (closed) d.Append("Z");
        string da = DashAttr(dash);
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    // 变宽折线（组码43 起宽>0）按 CAD 语义渲染为封闭填充多边形
    private void EmitTaperedPolyline(StringBuilder sb, LwPolyline pl, Transform2D tr, string stroke, double scaleMul, double[] dash)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        if (n < 2) return;
        // 逐顶点宽度（数据单位）：顶点 i 处线宽 = 该顶点 StartWidth
        double[] w = new double[n];
        for (int i = 0; i < n; i++)
            w[i] = verts[i].StartWidth;

        List<(double X, double Y)> left, right;
        var taper = TryBuildTaperOffset(pl, w, tr, _opt.TaperOffsetBuilder);
        if (taper.Ok)
        {
            // 含 bulge（弧段）：弧采样偏移轮廓（GC200 圆盘修复，T5.4）
            left = taper.Left; right = taper.Right;
        }
        else
        {
            // 无 bulge：原逐顶点弦偏移路径（输出与旧实现逐字节一致）
            left = new List<(double X, double Y)>(n);
            right = new List<(double X, double Y)>(n);
            for (int i = 0; i < n; i++)
            {
                double px = verts[i].Location.X, py = verts[i].Location.Y;
                int ni = pl.IsClosed ? (i + 1) % n : (i + 1 < n ? i + 1 : i);
                int pi = pl.IsClosed ? (i - 1 + n) % n : (i - 1 >= 0 ? i - 1 : i);
                double fx = verts[ni].Location.X - px, fy = verts[ni].Location.Y - py;
                double bx = px - verts[pi].Location.X, by = py - verts[pi].Location.Y;
                double fl = Math.Sqrt(fx * fx + fy * fy); if (fl < 1e-9) { fx = 1; fy = 0; } else { fx /= fl; fy /= fl; }
                double bl = Math.Sqrt(bx * bx + by * by); if (bl < 1e-9) { bx = 1; by = 0; } else { bx /= bl; by /= bl; }
                double tx = fx + bx, ty = fy + by;
                double tl = Math.Sqrt(tx * tx + ty * ty); if (tl < 1e-9) { tx = fx; ty = fy; } else { tx /= tl; ty /= tl; }
                double nx = -ty, ny = tx;
                double hw = w[i] / 2.0;
                double lx = px + nx * hw, ly = py + ny * hw;
                double rx = px - nx * hw, ry = py - ny * hw;
                if (tr != null) { var a = tr.Apply(lx, ly); left.Add((a.Item1, a.Item2)); var b = tr.Apply(rx, ry); right.Add((b.Item1, b.Item2)); }
                else { left.Add((lx, ly)); right.Add((rx, ry)); }
            }
        }

        string da = DashAttr(dash);
        var d = new StringBuilder();
        int m = left.Count;
        if (pl.IsClosed)
        {
            // 闭合折线：left/right 各是一条封闭偏移轮廓（内/外环），两个子路径 + evenodd 填充得到"环"
            // （与 OFF 路由 DxfImageExporter.EmitTaperedPolyline 逐字节一致：属性序/0.1/round）
            d.Append("M");
            for (int i = 0; i < m; i++) d.Append($"{(i == 0 ? "" : "L")}{F(Sx(left[i].X))},{F(Sy(left[i].Y))}");
            d.Append("Z M");
            for (int i = 0; i < m; i++) d.Append($"{(i == 0 ? "" : "L")}{F(Sx(right[i].X))},{F(Sy(right[i].Y))}");
            d.Append("Z");
            sb.AppendLine($"  <path d=\"{d}\" fill=\"{stroke}\" fill-rule=\"evenodd\" stroke=\"{stroke}\" stroke-width=\"0.1\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
        }
        else
        {
            // 开放折线：left 正向 + right 反向，两端闭合填充（带/渐变楔形）
            d.Append("M");
            for (int i = 0; i < m; i++) d.Append($"{(i == 0 ? "" : "L")}{F(Sx(left[i].X))},{F(Sy(left[i].Y))}");
            for (int i = m - 1; i >= 0; i--) d.Append($"L{F(Sx(right[i].X))},{F(Sy(right[i].Y))}");
            d.Append("Z");
            sb.AppendLine($"  <path d=\"{d}\" fill=\"{stroke}\" stroke=\"{stroke}\" stroke-width=\"0.1\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
        }
    }

    // [T5.4 GC200 修复] 含 bulge（弧段）的变宽折线偏移轮廓：
    // 弧段按每 2° 采样（与 EmitArcAsPolyline 一致），沿真实弧法向（径向）偏移——
    // 典型收益：GC200 点块（2 顶点 bulge=1、常数宽 0.2、闭合，r=hw=0.1）从 0.2×0.2 弦方块
    // 变为外半径 r+hw 的正确填充圆盘（内环 r−hw≤0 时退化为圆心点，evenodd 仍填实）。
    // 角顶点取入/出边切向的平分线法向（直线边=弦向，弧边=端点切向），宽度沿弧线性插值。
    // 无 bulge 时返回 false → 调用方走原逐顶点弦路径（输出保持不变，像素 parity 不受影响）。
    // 几何核心经 ITaperOffsetBuilder 注入（R13 架构拆分：算法在宿主程序 GDB2CAD.Core.TaperOffset，
    // 本包只含渲染契约）；builder 为 null 时回退弦路径（R10 前行为）。本方法只负责实体类型
    // 适配 + 变换应用（逐点顺序与旧实现一致 → 输出字节不变）。
    private static (bool Ok, List<(double X, double Y)> Left, List<(double X, double Y)> Right) TryBuildTaperOffset(LwPolyline pl, double[] w, Transform2D tr, ITaperOffsetBuilder builder)
    {
        if (builder == null) return (false, null, null);
        var verts = pl.Vertices;
        int n = verts.Count;
        double[] xs = new double[n];
        double[] ys = new double[n];
        double[] bs = new double[n];
        for (int i = 0; i < n; i++) { xs[i] = verts[i].Location.X; ys[i] = verts[i].Location.Y; bs[i] = verts[i].Bulge; }
        var core = builder.TryBuild(n, pl.IsClosed, xs, ys, bs, w);
        if (core == null) return (false, null, null);
        var left = new List<(double X, double Y)>(core.Value.Left.Count);
        var right = new List<(double X, double Y)>(core.Value.Right.Count);
        for (int i = 0; i < core.Value.Left.Count; i++)
        {
            double lx = core.Value.Left[i].X, ly = core.Value.Left[i].Y;
            double rx = core.Value.Right[i].X, ry = core.Value.Right[i].Y;
            if (tr != null)
            {
                var a = tr.Apply(lx, ly); left.Add((a.Item1, a.Item2));
                var b = tr.Apply(rx, ry); right.Add((b.Item1, b.Item2));
            }
            else { left.Add((lx, ly)); right.Add((rx, ry)); }
        }
        return (true, left, right);
    }

    private void EmitCircle(StringBuilder sb, double cx, double cy, double r, string stroke, double w, double[] dash)
    {
        string da = DashAttr(dash);
        sb.AppendLine($"  <circle cx=\"{F(Sx(cx))}\" cy=\"{F(Sy(cy))}\" r=\"{F(r)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    private void EmitEllipse(StringBuilder sb, double cx, double cy, double rx, double ry, double rotDeg, string stroke, double w, double[] dash)
    {
        double scx = Sx(cx), scy = Sy(cy);
        // Y 翻转使椭圆主轴角度取负
        string trs = (Math.Abs(rotDeg) > 1e-6) ? $" transform=\"rotate({F(-rotDeg)} {F(scx)} {F(scy)})\"" : "";
        string da = DashAttr(dash);
        sb.AppendLine($"  <ellipse cx=\"{F(scx)}\" cy=\"{F(scy)}\" rx=\"{F(rx)}\" ry=\"{F(ry)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\"{trs}{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    // CAD 弧恒为从起始角到终止角逆时针（end<start 时跨过 360°）
    private void EmitArcAsPolyline(StringBuilder sb, double cx, double cy, double r, double startDeg, double endDeg, string stroke, double w, double[] dash)
    {
        if (r <= 0) return;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6) span = 2 * Math.PI; // start==end 视为整圆
        int seg = Math.Max(8, (int)Math.Ceiling(span / (Math.PI / 90.0))); // 每 2° 一段
        var d = new StringBuilder();
        for (int i = 0; i <= seg; i++)
        {
            double ang = s + span * i / seg;
            double x = cx + r * Math.Cos(ang);
            double y = cy + r * Math.Sin(ang);
            d.Append($"{(i == 0 ? "M" : "L")}{F(Sx(x))},{F(Sy(y))}");
        }
        string da = DashAttr(dash);
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"{F(w)}\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    private void EmitPoint(StringBuilder sb, double cx, double cy, string color, double lw)
    {
        double radius = Math.Max(0.05, lw * 1.5);
        if (!double.IsFinite(radius) || radius <= 0) radius = 0.05;
        // 透明背景无底色：省略背景色描边光晕（避免透明处出现一圈黑/白边）
        string halo = _transparentBg ? "none" : (_whiteBg ? "#FFFFFF" : "#000000");
        sb.AppendLine($"  <circle cx=\"{F(Sx(cx))}\" cy=\"{F(Sy(cy))}\" r=\"{F(radius)}\" fill=\"{color}\" stroke=\"{halo}\" stroke-width=\"{F(radius * 0.3)}\"/>");
    }

    // R18：普通 TEXT 宽度因子（DXF 41 / WidthFactor）——水平缩放锚定基线起点 (sx,sy)：
    // translate(sx,sy) scale(WF,1) translate(-sx,-sy) 先按文字局部系压缩字宽，再整体旋转（与 MText SVG 的 scale(WF,1) 同语义）。
    // WF=1 时不输出任何 scale（保持既有输出逐字节不变）。
    private void EmitText(StringBuilder sb, double x, double y, string content, double fs, string fill, double rotDeg, string fontFamily, double widthFactor = 1.0)
    {
        if (string.IsNullOrEmpty(content)) return;
        double sx = Sx(x), sy = Sy(y);
        var tf = new List<string>();
        if (Math.Abs(rotDeg) > 1e-6) tf.Add($"rotate({F(rotDeg)} {F(sx)} {F(sy)})");
        if (Math.Abs(widthFactor - 1.0) > 1e-6)
            tf.Add($"translate({F(sx)}, {F(sy)}) scale({F(widthFactor)}, 1) translate({F(-sx)}, {F(-sy)})");
        string trs = tf.Count > 0 ? $" transform=\"{string.Join(" ", tf)}\"" : "";
        string esc = EscapeXml(content);
        sb.AppendLine($"  <text x=\"{F(sx)}\" y=\"{F(sy)}\" font-family=\"{fontFamily}\" font-size=\"{F(fs)}\" fill=\"{fill}\"{trs}>{esc}</text>");
    }

    private static List<(double X, double Y)> QuadPts(XYZ a, XYZ b, XYZ c, XYZ d, Transform2D tr)
        => new[] { a, b, c, d }.Select(v => tr == null ? (v.X, v.Y) : tr.Apply(v.X, v.Y)).ToList();

    private void EmitQuadFill(StringBuilder sb, List<(double X, double Y)> pts, string color)
    {
        if (pts.Count < 3) return;
        var d = new StringBuilder();
        d.Append("M");
        for (int i = 0; i < pts.Count; i++)
            d.Append($"{(i == 0 ? "" : "L")}{F(Sx(pts[i].X))},{F(Sy(pts[i].Y))}");
        d.Append("Z");
        sb.AppendLine($"  <path d=\"{d}\" fill=\"{color}\" stroke=\"{color}\" stroke-width=\"0.15\"/>");
    }
    #endregion

    #region ===== HATCH / SPLINE 渲染核心 =====

    // 记录一个被跳过的实体（类型名或细分原因），导出结束统一告警
    private void RecordSkip(string key)
    {
        _skippedCounts.TryGetValue(key, out int c);
        _skippedCounts[key] = c + 1;
    }

    private void LogSkipped(string kind)
    {
        if (_skippedCounts.Count == 0) return;
        var parts = _skippedCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key + "×" + kv.Value);
        _log($"⚠️  {kind} 未渲染实体（跳过）：{string.Join("  ", parts)}");
    }

    private static void AddPoint(List<XY> pts, double x, double y, Transform2D tr)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        if (tr == null) pts.Add(new XY(x, y));
        else { var (ax, ay) = tr.Apply(x, y); pts.Add(new XY(ax, ay)); }
    }

    // 弧采样（角度为度）
    private static void AppendArcPoints(List<XY> pts, XY center, double r, double startDeg, double endDeg, bool ccw, Transform2D tr)
    {
        if (r <= 0 || !double.IsFinite(r)) return;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6) span = 2 * Math.PI; // start==end 视为整圆
        double dir = ccw ? 1.0 : -1.0;
        int seg = Math.Max(8, (int)Math.Ceiling(span / (Math.PI / 90.0))); // 每 2° 一段
        for (int i = 0; i <= seg; i++)
        {
            double ang = s + dir * span * i / seg;
            AddPoint(pts, center.X + r * Math.Cos(ang), center.Y + r * Math.Sin(ang), tr);
        }
    }

    // 椭圆弧边采样：EndMajorAxis 为主轴向量（长=主轴半径 A），参数角 φ 从主轴起算
    private static void AppendEllipseEdge(List<XY> pts, Hatch.BoundaryPath.Ellipse el, Transform2D tr)
    {
        double ax = el.MajorAxisEndPoint.X, ay = el.MajorAxisEndPoint.Y;
        double A = Math.Sqrt(ax * ax + ay * ay);
        if (A <= 0 || !double.IsFinite(A)) return;
        double ratio = el.RadiusRatio;
        if (!double.IsFinite(ratio) || ratio <= 0) ratio = 1.0;
        if (ratio > 1.0) ratio = 1.0 / ratio; // AutoCAD 恒 ≤1；>1 视为轴互换，取倒数保持 A 为主轴
        double B = A * ratio;
        double ux = ax / A, uy = ay / A;   // 主轴单位向量
        double vx = -uy, vy = ux;          // 次轴单位向量（主轴旋转 90°）
        double s = el.StartAngle;          // fork 弧度（参考实现此处为度→弧度）
        double e = el.EndAngle;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6) span = 2 * Math.PI;
        double dir = el.CounterClockWise ? 1.0 : -1.0;
        int seg = Math.Max(12, (int)Math.Ceiling(span / (Math.PI / 45.0))); // 每 4° 一段
        for (int i = 0; i <= seg; i++)
        {
            double phi = s + dir * span * i / seg;
            AddPoint(pts, el.Center.X + ux * A * Math.Cos(phi) + vx * B * Math.Sin(phi),
                              el.Center.Y + uy * A * Math.Cos(phi) + vy * B * Math.Sin(phi), tr);
        }
    }

    // 多线段边：顶点 Z=bulge（≠0 时该顶点到下一顶点为弧）
    private static void AppendPolylineEdge(List<XY> pts, Hatch.BoundaryPath.Polyline poly, Transform2D tr)
    {
        if (poly.Vertices == null || poly.Vertices.Count == 0) return;
        int nv = poly.Vertices.Count;
        for (int i = 0; i < nv; i++)
        {
            var v = poly.Vertices[i];
            AddPoint(pts, v.X, v.Y, tr);
            var v2 = poly.Vertices[i == nv - 1 && poly.IsClosed ? 0 : i + 1];
            double dx = v2.X - v.X, dy = v2.Y - v.Y;
            if (dx * dx + dy * dy <= 1e-18) continue;      // 零长段
            if (Math.Abs(v.Z) < 1e-12) continue;          // 直线段
            var arc = ArcFromBulge(new XY(v.X, v.Y), new XY(v2.X, v2.Y), v.Z);
            if (Math.Abs(arc.Radius) < 1e-12) continue;    // 退化弧按直线处理
            AppendArcPoints(pts, arc.Center, arc.Radius, arc.StartDeg, arc.EndDeg, true, tr);
        }
    }

    // bulge 分解：p1→p2 弦 + bulge=tan(θ/4) → 弧（逆时针 θ=4*atan(bulge)）
    private static (XY Center, double Radius, double StartDeg, double EndDeg) ArcFromBulge(XY p1, XY p2, double bulge)
    {
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-12) return (p1, 0, 0, 0);
        double theta = 4.0 * Math.Atan(bulge);
        double r = chord / (2.0 * Math.Abs(Math.Sin(theta / 2.0)));
        if (!double.IsFinite(r) || r <= 0) return (p1, 0, 0, 0);
        double midX = (p1.X + p2.X) / 2, midY = (p1.Y + p2.Y) / 2;
        // 圆心在弦中垂线上，距离 h = |r*cos(theta/2)|，方向取决于 bulge 符号
        double h = r * Math.Abs(Math.Cos(theta / 2.0));
        double ux = dx / chord, uy = dy / chord;
        double sgn = bulge >= 0 ? 1.0 : -1.0;
        double cx = midX - sgn * h * (-uy);
        double cy = midY - sgn * h * (ux);
        double a1 = Math.Atan2(p1.Y - cy, p1.X - cx) * 180.0 / Math.PI;
        double a2 = Math.Atan2(p2.Y - cy, p2.X - cx) * 180.0 / Math.PI;
        return (new XY(cx, cy), r, a1, a2);
    }

    // hatch 边界样条边：控制点 Z=权重（DXF 规定），NURBS 采样
    private static void AppendSplineEdge(List<XY> pts, Hatch.BoundaryPath.Spline sp, Transform2D tr)
    {
        if (sp.ControlPoints == null || sp.ControlPoints.Count < 2 || sp.Knots == null) return;
        int n = sp.ControlPoints.Count;
        int p = sp.Degree; if (p < 1) p = 1;
        if (sp.Knots.Count != n + p + 1)
        {
            for (int i = 0; i < n; i++) AddPoint(pts, sp.ControlPoints[i].X, sp.ControlPoints[i].Y, tr); // 回退折线
            return;
        }
        var hx = new double[n]; var hy = new double[n]; var hw = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = sp.ControlPoints[i].Z;
            if (!double.IsFinite(w) || Math.Abs(w) < 1e-9) w = 1.0;
            hx[i] = sp.ControlPoints[i].X * w; hy[i] = sp.ControlPoints[i].Y * w; hw[i] = w;
        }
        pts.AddRange(SampleNurbs(sp.Knots.ToArray(), hx, hy, hw, p, 8, tr));
    }

    // 把 hatch 边界分解为折线子路径集合（tr 变换后的模型坐标），每子路径闭环。
    private static List<List<XY>> BuildHatchLoops(Hatch hatch, Transform2D tr)
    {
        var result = new List<List<XY>>();
        if (hatch == null) return result;
        foreach (var path in hatch.Paths)
        {
            var pts = new List<XY>();
            foreach (var edge in path.Edges)
            {
                switch (edge)
                {
                    case Hatch.BoundaryPath.Line ln:
                        AddPoint(pts, ln.Start.X, ln.Start.Y, tr);
                        AddPoint(pts, ln.End.X, ln.End.Y, tr);
                        break;
                    case Hatch.BoundaryPath.Arc arc:
                        AppendArcPoints(pts, new XY(arc.Center.X, arc.Center.Y), arc.Radius,
                            arc.StartAngle * 180.0 / Math.PI, arc.EndAngle * 180.0 / Math.PI, arc.CounterClockWise, tr);
                        break;
                    case Hatch.BoundaryPath.Ellipse el:
                        AppendEllipseEdge(pts, el, tr);
                        break;
                    case Hatch.BoundaryPath.Spline sp:
                        AppendSplineEdge(pts, sp, tr);
                        break;
                    case Hatch.BoundaryPath.Polyline poly:
                        AppendPolylineEdge(pts, poly, tr);
                        break;
                }
            }
            if (pts.Count >= 2)
            {
                double dx = pts[pts.Count - 1].X - pts[0].X, dy = pts[pts.Count - 1].Y - pts[0].Y;
                if (dx * dx + dy * dy > 1e-12) pts.Add(pts[0]); // 显式闭合（hatch 区域必须封闭）
            }
            if (pts.Count >= 3) result.Add(pts);
        }
        return result;
    }

    // 顶层 Spline 实体采样：控制点+节矢 → de Boor NURBS（支持有理权重）；
    // 仅拟合点 → 过拟合点折线；节矢缺失 → 控制点折线回退。
    private static List<XY> SplineSample(Spline sp, Transform2D tr, int samplesPerSpan)
    {
        var pts = new List<XY>();
        if (sp == null) return pts;
        var cp = sp.ControlPoints;
        if (cp != null && cp.Count >= 2)
        {
            int p = sp.Degree; if (p < 1) p = 1;
            int n = cp.Count;
            if (sp.Knots != null && sp.Knots.Count == n + p + 1)
            {
                var hx = new double[n]; var hy = new double[n]; var hw = new double[n];
                var w = sp.Weights;
                for (int i = 0; i < n; i++)
                {
                    double wi = (w != null && i < w.Count && Math.Abs(w[i]) >= 1e-9) ? w[i] : 1.0;
                    hx[i] = cp[i].X * wi; hy[i] = cp[i].Y * wi; hw[i] = wi;
                }
                pts.AddRange(SampleNurbs(sp.Knots.ToArray(), hx, hy, hw, p, samplesPerSpan, tr));
                return pts;
            }
            for (int i = 0; i < n; i++) AddPoint(pts, cp[i].X, cp[i].Y, tr); // 回退：控制点折线
            return pts;
        }
        if (sp.FitPoints != null)
            foreach (var fp in sp.FitPoints) AddPoint(pts, fp.X, fp.Y, tr);
        return pts;
    }

    // NURBS 逐节 span 中点采样（de Boor 算法；hx/hy/hw 为齐次坐标 x·w/y·w/w）
    private static List<XY> SampleNurbs(double[] knots, double[] hx, double[] hy, double[] hw, int p, int samplesPerSpan, Transform2D tr)
    {
        var pts = new List<XY>();
        int n = hx.Length;
        if (p < 1 || p >= n) return pts;
        for (int s = p; s <= n - 1; s++)
        {
            double k0 = knots[s], k1 = knots[s + 1];
            if (!(k1 > k0)) continue; // 跳过零长 span
            int ns = Math.Max(2, samplesPerSpan);
            for (int j = 0; j < ns; j++)
            {
                double t = k0 + (k1 - k0) * (j + 0.5) / ns;
                DeBoor(hx, hy, hw, knots, p, s, t, out double x, out double y);
                AddPoint(pts, x, y, tr);
            }
        }
        return pts;
    }

    // de Boor 算法求 (齐次) NURBS 在节 span s、参数 t 处的点
    private static void DeBoor(double[] hx, double[] hy, double[] hw, double[] knots, int p, int s, double t, out double x, out double y)
    {
        int n = hx.Length;
        var dx = new double[p + 1];
        var dy = new double[p + 1];
        var dw = new double[p + 1];
        for (int j = 0; j <= p; j++)
        {
            int idx = s - p + j;
            if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
            dx[j] = hx[idx]; dy[j] = hy[idx]; dw[j] = hw[idx];
        }
        for (int k = 1; k <= p; k++)
        {
            for (int j = s - p + k; j <= s; j++)
            {
                double n1 = knots[j], n2 = knots[j + p - k + 1];
                double denom = n2 - n1;
                double a = denom == 0 ? 0.0 : (t - n1) / denom;
                int jj = j - (s - p + k);
                dx[jj] = a * dx[jj] + (1.0 - a) * dx[jj + 1];
                dy[jj] = a * dy[jj] + (1.0 - a) * dy[jj + 1];
                dw[jj] = a * dw[jj] + (1.0 - a) * dw[jj + 1];
            }
        }
        double w = Math.Abs(dw[0]) < 1e-12 ? 1.0 : dw[0];
        x = dx[0] / w; y = dy[0] / w;
    }

    // 判断 hatch 图案是否为"实心填充"（SOLID）。
    private static bool IsSolidPattern(Hatch ht)
    {
        if (ht == null) return true;
        var gp = ht.GradientColor;
        if (gp != null && gp.Enabled) return false; // 渐变（450=1）
        if (ht.IsSolid) return true;
        var p = ht.Pattern;
        // fork HatchPattern 无 Fill 字段：按原始名 SOLID 判断（与 netDxf Name==SOLID 分支一致）
        return p == null || string.Equals(p.Name, "SOLID", StringComparison.OrdinalIgnoreCase);
    }

    // hatch 实心填充色：优先实体真色（420），否则按 CAD ACI 语义（独立 > 随块 > 随图层）
    private string HatchSolidFillHex(Hatch ht, short layerAci, short byBlockAci)
    {
        try
        {
            var c = ht.Color;
            if (c.IsTrueColor)
                return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }
        catch { }
        short aci = 7;
        try { aci = ht.Color.Index; } catch { }
        return ColorOf(aci, layerAci, byBlockAci);
    }

    // 渐变类型按 470 原始名判断：ELLIPTIC → 径向；其余（LINEAR/CYLINDER/…）→ 线性近似。
    private static bool IsEllipticGradient(HatchGradientPattern gp)
        => string.Equals(gp.Name, "ELLIPTIC", StringComparison.OrdinalIgnoreCase);

    // 渐变色标 hex：真色直取，否则走 ACI 官方表（含 7 号白/黑随底色反色）
    private string StopHex(ACadSharp.Color c)
    {
        if (c.IsTrueColor)
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        return AciColor((int)c.Index);
    }

    // 子路径集合 → SVG path d（M/L/Z）
    private static string BuildPathDSvg(List<List<XY>> loops, Func<double, double> fx, Func<double, double> fy)
    {
        var d = new StringBuilder();
        foreach (var loop in loops)
        {
            if (loop.Count == 0) continue;
            for (int i = 0; i < loop.Count; i++)
                d.Append($"{(i == 0 ? "M" : "L")}{F(fx(loop[i].X))},{F(fy(loop[i].Y))}");
            d.Append("Z");
        }
        return d.ToString();
    }

    // SVG HATCH：实心=实体色/真色；渐变=linear/radialGradient；图案 hatch T4.5 实现
    private void EmitHatchSvg(StringBuilder sb, Hatch ht, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var loops = BuildHatchLoops(ht, tr);
        if (loops.Count == 0) { RecordSkip("HATCH(空)"); return; }
        var gp = ht.GradientColor;
        bool gradient = gp != null && gp.Enabled;
        if (!gradient && !IsSolidPattern(ht))
        {
            EmitHatchPatternSvg(sb, ht, loops, tr, scaleMul, layerAci, byBlockAci);
            return;
        }
        string rule = loops.Count > 1 ? "evenodd" : "nonzero";
        string d = BuildPathDSvg(loops, x => Sx(x), y => Sy(y));
        if (gradient)
        {
            string id = "hg" + (_hatchGradSeq++);
            EmitHatchGradientDef(id, loops, gp);
            sb.AppendLine($"  <path d=\"{d}\" fill=\"url(#{id})\" fill-rule=\"{rule}\"/>");
        }
        else
        {
            sb.AppendLine($"  <path d=\"{d}\" fill=\"{HatchSolidFillHex(ht, layerAci, byBlockAci)}\" fill-rule=\"{rule}\"/>");
        }
    }

    // SVG 渐变定义（写入 _svgDefs；userSpaceOnUse 坐标 = 画布像素坐标）
    // 460 角度沿梯度方向；范围取 hatch 包围盒对角线一半
    private void EmitHatchGradientDef(string id, List<List<XY>> loops, HatchGradientPattern gp)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var loop in loops)
            foreach (var p in loop)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        double L = 0.5 * Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        if (!double.IsFinite(L) || L <= 0) L = 1.0;
        // 渐变色标：Colors 列表（463 位置 + 421 颜色）；取首/末两个色标（双端渐变）
        string c1 = "#FFFFFF", c2 = "#000000";
        if (gp.Colors != null && gp.Colors.Count >= 2)
        {
            c1 = StopHex(gp.Colors[0].Color);
            c2 = StopHex(gp.Colors[gp.Colors.Count - 1].Color);
        }
        bool elliptic = IsEllipticGradient(gp);
        if (elliptic)
        {
            _svgDefs.AppendLine($"    <radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" cx=\"{F(Sx(cx))}\" cy=\"{F(Sy(cy))}\" r=\"{F(L)}\">");
        }
        else
        {
            double ang = gp.Angle; // fork 弧度
            double dx = Math.Cos(ang), dy = Math.Sin(ang);
            _svgDefs.AppendLine($"    <linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"{F(Sx(cx - dx * L))}\" y1=\"{F(Sy(cy - dy * L))}\" x2=\"{F(Sx(cx + dx * L))}\" y2=\"{F(Sy(cy + dy * L))}\">");
        }
        _svgDefs.AppendLine($"      <stop offset=\"0\" stop-color=\"{c1}\"/>");
        _svgDefs.AppendLine($"      <stop offset=\"1\" stop-color=\"{c2}\"/>");
        _svgDefs.AppendLine(elliptic ? "    </radialGradient>" : "    </linearGradient>");
    }

    // ===== 图案 HATCH（ANSI31/411B 等带显式线定义者）=====
    // 图案线在 hatch 局部系生成（线定义 → 块变换 tr），与边界（evenodd）求交裁剪后按描边输出；
    // 每 hatch 线数封顶（密度上限），防止 scale=0.0001 之类病态数据生成上万条线。
    // ⚠️ fork 差异：加载器已把图案旋转(52)/缩放(41)烘进 Pattern.Lines（Angle/BasePoint/Offset/DashLengths 为最终值），
    // 故此处不再额外乘图案旋转/缩放（netDxf 版为原始线定义+显式旋转/缩放，结果等价）。
    private const int MaxPatternLinesPerHatch = 2000;
    private static readonly Transform2D IdentityTr = new Transform2D { Ca = 1, Sa = 0, Sx = 1, Sy = 1, Tx = 0, Ty = 0 };

    private void EmitHatchPatternSvg(StringBuilder sb, Hatch ht, List<List<XY>> loops, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var segs = BuildPatternSegments(loops, ht, tr, MaxPatternLinesPerHatch);
        if (segs.Count == 0) { RecordSkip("HATCH(图案)"); return; }
        string col = HatchSolidFillHex(ht, layerAci, byBlockAci);
        double w = Math.Max(0.03, 0.1 * scaleMul); // 图案线细
        var d = new StringBuilder();
        foreach (var s in segs)
            d.Append("M").Append(F(Sx(s.X1))).Append(',').Append(F(Sy(s.Y1)))
             .Append("L").Append(F(Sx(s.X2))).Append(',').Append(F(Sy(s.Y2))).Append(' ');
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(w)}\"/>");
    }

    // 生成图案线线段（世界坐标）。loops=边界闭合折线（世界系），ht=hatch，tr=块变换。
    // fork 版：Lines 已含图案旋转/缩放（烘焙）→ patAngle=0/patScale=1；
    // 每条图案线（无限长）与边界求交，按 evenodd 保留内部区间，dash 段按虚线拆分。
    private List<(double X1, double Y1, double X2, double Y2)> BuildPatternSegments(
        List<List<XY>> loops, Hatch ht, Transform2D tr, int maxLines)
    {
        var segs = new List<(double X1, double Y1, double X2, double Y2)>();
        var pat = ht?.Pattern;
        if (pat == null || pat.Lines == null || pat.Lines.Count == 0) return segs;
        if (loops == null || loops.Count == 0) return segs;
        var t = tr ?? IdentityTr; // 模型空间 hatch 无块变换（恒等）

        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (var loop in loops)
            foreach (var p in loop)
            {
                if (p.X < minx) minx = p.X; if (p.Y < miny) miny = p.Y;
                if (p.X > maxx) maxx = p.X; if (p.Y > maxy) maxy = p.Y;
            }
        if (!(maxx > minx && maxy > miny)) return segs;

        var edges = new List<(double ax, double ay, double bx, double by)>();
        foreach (var loop in loops)
            for (int i = 0; i + 1 < loop.Count; i++)
                edges.Add((loop[i].X, loop[i].Y, loop[i + 1].X, loop[i + 1].Y));
        if (edges.Count == 0) return segs;

        // fork：图案旋转/缩放已烘焙进 Lines → 恒等（netDxf 版此处用 pat.Angle/pat.Scale）
        double patAngle = 0.0;
        double ca = Math.Cos(patAngle), sa = Math.Sin(patAngle);
        double patScale = 1.0;

        // 图案基点（DXF 10/20）：fork 存于 SeedPoints 首项
        double ox = 0, oy = 0;
        if (ht.SeedPoints != null && ht.SeedPoints.Count > 0) { ox = ht.SeedPoints[0].X; oy = ht.SeedPoints[0].Y; }

        int total = 0;
        foreach (var ld in pat.Lines)
        {
            if (ld == null) continue;
            double th = ld.Angle; // fork 弧度（已含图案旋转）
            double ux = Math.Cos(th), uy = Math.Sin(th); // 局部线方向（单位）
            // 局部 → 世界方向：先图案旋转、图案缩放，再块变换的线性部分
            double rux = ca * ux - sa * uy, ruy = sa * ux + ca * uy;
            var (dirX, dirY) = LinearTr(t, rux * patScale, ruy * patScale);
            double dirLen = Math.Sqrt(dirX * dirX + dirY * dirY);
            if (dirLen < 1e-12) continue;
            // 逐线偏移（delta）→ 世界
            double rdx = ca * ld.Offset.X - sa * ld.Offset.Y, rdy = sa * ld.Offset.X + ca * ld.Offset.Y;
            var (offX, offY) = LinearTr(t, rdx * patScale, rdy * patScale);
            double offLen = Math.Sqrt(offX * offX + offY * offY);
            if (offLen < 1e-12) continue;
            // 世界锚点（k=0）：hatch 基点 + 图案旋转缩放后的线原点
            double lox = ca * ld.BasePoint.X - sa * ld.BasePoint.Y, loy = sa * ld.BasePoint.X + ca * ld.BasePoint.Y;
            var (anchorX, anchorY) = t.Apply(ox + lox * patScale, oy + loy * patScale);

            // 世界线法向 & 垂直间距
            double nX = -dirY / dirLen, nY = dirX / dirLen;
            double perp = offX * nX + offY * nY;
            if (Math.Abs(perp) < 1e-12) continue; // 退化（delta 平行于线）

            // bbox 在法向的投影范围 → k 范围
            double nc1 = minx * nX + miny * nY, nc2 = maxx * nX + miny * nY;
            double nc3 = minx * nX + maxy * nY, nc4 = maxx * nX + maxy * nY;
            double nMin = Math.Min(Math.Min(nc1, nc2), Math.Min(nc3, nc4));
            double nMax = Math.Max(Math.Max(nc1, nc2), Math.Max(nc3, nc4));
            double baseN = anchorX * nX + anchorY * nY;
            int kMin = (int)Math.Floor((nMin - baseN) / perp) - 1;
            int kMax = (int)Math.Ceiling((nMax - baseN) / perp) + 1;
            if (kMax < kMin) { int tmp = kMin; kMin = kMax; kMax = tmp; }
            int count = kMax - kMin + 1;
            if (count <= 0) continue;
            // 密度上限：超限时按步长抽稀
            int stride = 1;
            if (total + count > maxLines)
            {
                int remain = Math.Max(1, maxLines - total);
                stride = (int)Math.Ceiling(count / (double)remain);
                if (stride < 1) stride = 1;
            }
            for (int k = kMin; k <= kMax; k += stride)
            {
                double ax = anchorX + k * offX, ay = anchorY + k * offY;
                ClipLineToPoly(ax, ay, dirX, dirY, edges, out var ts);
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    double tm = (ts[i] + ts[i + 1]) * 0.5;
                    if (InsideEvenOdd(ax + tm * dirX, ay + tm * dirY, edges))
                    {
                        double x1 = ax + ts[i] * dirX, y1 = ay + ts[i] * dirY;
                        double x2 = ax + ts[i + 1] * dirX, y2 = ay + ts[i + 1] * dirY;
                        AppendPatternSegs(segs, x1, y1, x2, y2, ld.DashLengths, dirLen);
                    }
                }
                total++;
                if (total >= maxLines) break;
            }
            if (total >= maxLines) break;
        }
        return segs;
    }

    // tr 的线性部分作用于向量（不含平移）
    private static (double X, double Y) LinearTr(Transform2D tr, double vx, double vy)
    {
        double rx = vx * tr.Sx, ry = vy * tr.Sy;
        return (rx * tr.Ca - ry * tr.Sa, rx * tr.Sa + ry * tr.Ca);
    }

    // 直线 A+t·dir 与折线各边的交点参数 t（世界系），排序去重
    private static void ClipLineToPoly(double ax, double ay, double dx, double dy,
        List<(double ax, double ay, double bx, double by)> edges, out List<double> ts)
    {
        ts = new List<double>();
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            double ex = e.bx - e.ax, ey = e.by - e.ay;
            double den = dx * ey - dy * ex; // cross(dir, e)
            if (Math.Abs(den) < 1e-15) continue; // 平行
            double px = e.ax - ax, py = e.ay - ay;
            double t = (px * ey - py * ex) / den; // cross(P1-A, e)
            double s = (px * dy - py * dx) / den; // cross(P1-A, dir)
            if (s >= 0 && s <= 1) ts.Add(t);
        }
        ts.Sort();
        double eps = 1e-9 * Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
        var dedup = new List<double>(ts.Count);
        foreach (var t in ts)
            if (dedup.Count == 0 || Math.Abs(t - dedup[dedup.Count - 1]) > eps)
                dedup.Add(t);
        ts = dedup;
    }

    // evenodd（射线法）内部判定：水平射线与所有边交点数为奇 → 内部
    private static bool InsideEvenOdd(double x, double y, List<(double ax, double ay, double bx, double by)> edges)
    {
        bool inside = false;
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            if ((e.ay > y) != (e.by > y))
            {
                double xint = e.ax + (y - e.ay) * (e.bx - e.ax) / (e.by - e.ay);
                if (x > xint) inside = !inside;
            }
        }
        return inside;
    }

    // 追加图案线段；有 dash 则按虚线拆分（dash 局部单位，dirLen=局部→世界比例）
    private static void AppendPatternSegs(List<(double X1, double Y1, double X2, double Y2)> segs,
        double x1, double y1, double x2, double y2, List<double> dash, double dirLen)
    {
        if (dash == null || dash.Count == 0 || dirLen < 1e-12)
        {
            segs.Add((x1, y1, x2, y2));
            return;
        }
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return;
        double ux = dx / len, uy = dy / len;
        double period = 0;
        for (int i = 0; i < dash.Count; i++) period += Math.Abs(dash[i]);
        if (period < 1e-12) { segs.Add((x1, y1, x2, y2)); return; }
        double s = 0.0; int di = 0;
        while (s < len - 1e-12)
        {
            int idx = di % dash.Count;
            double dl = Math.Abs(dash[idx]) * dirLen;
            double end = Math.Min(s + dl, len);
            if (dash[idx] > 0) // 正=落笔
                segs.Add((x1 + s * ux, y1 + s * uy, x1 + end * ux, y1 + end * uy));
            s = end; di++;
        }
    }

    // SVG 样条（描边折线逼近，宽度/颜色/虚线随实体）
    private void EmitSplineSvg(StringBuilder sb, Spline sp, Transform2D tr, double scaleMul, string col, double[] dash)
    {
        var pts = SplineSample(sp, tr, 16);
        if (pts.Count < 2) { RecordSkip("SPLINE(空)"); return; }
        var d = new StringBuilder();
        for (int i = 0; i < pts.Count; i++)
            d.Append($"{(i == 0 ? "M" : "L")}{F(Sx(pts[i].X))},{F(Sy(pts[i].Y))}");
        string da = DashAttr(dash);
        sb.AppendLine($"  <path d=\"{d}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{F(StrokeWidth(sp, scaleMul))}\" stroke-linejoin=\"round\"{(da.Length > 0 ? $" stroke-dasharray=\"{da}\"" : "")}/>");
    }

    #endregion

    #region ===== 线宽 / 工具 =====

    private static int LineweightValue(LineWeightType lw)
    {
        int v = (int)lw;
        return v; // fork：Default=-3 / ByBlock=-2 / ByLayer=-1 / 0..200
    }

    // 读取实体的显式线宽（毫米）
    private static double LwMm(Entity e)
    {
        try
        {
            int v = LineweightValue(e.LineWeight);
            if (v >= 0) return v / 100.0;
            if (v == (int)LineWeightType.ByLayer) // ByLayer → 取所在图层线宽
            {
                int lv = LineweightValue(e.Layer?.LineWeight ?? LineWeightType.Default);
                if (lv >= 0) return lv / 100.0;
            }
        }
        catch { }
        return 0;
    }

    // 线宽 → 描边宽度（数据单位，1:1 实际尺寸）
    private const double BaseStroke = 0.1;
    private const double DefaultLwMm = 0.13;
    private double StrokeWidth(Entity e, double scaleMul)
    {
        double lw = LwMm(e);
        double w = lw > 0 ? BaseStroke * lw / DefaultLwMm : BaseStroke;
        return Math.Max(0.1, w * scaleMul);
    }

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static string F(double v) => (double.IsFinite(v) ? v : 0).ToString("0.######", Ci);

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");
    #endregion
}
