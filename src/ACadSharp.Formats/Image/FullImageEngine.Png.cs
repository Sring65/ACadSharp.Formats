using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using SkiaSharp;

namespace ACadSharp.Formats.Image;

/// <summary>
/// PNG 光栅输出端（T4.9）：用 SkiaSharp 把与 SVG 端完全相同的几何渲染到位图并写出 PNG。
/// 渲染语义严格对齐 SVG 端（FullImageEngine.Svg.cs）：1:1 画布 + 2% 边距 + OutputZoom 放大
/// + 黑/白背景 + ACI 官方色表（ACI 7 黑底显白/白底显黑）+ 虚线（图案自路径起点，相位与
/// stroke-dasharray 一致）+ 复杂线型（SHX 真实字形，WalkPattern 同算法）+ NURBS 样条 de Boor
/// 采样 + HATCH（实心/渐变/图案）+ 变宽折线（ITaperOffsetBuilder）+ TEXT/MText（CJK 系统字体，
/// 基线=插入点，旋转/倾斜/宽度因子）+ 点标记（光晕环）+ 嵌套块展开 + ByBlock/ByLayer 继承。
/// 与 SVG 端的差异（位图固有限制）：
///   ① 圆/椭圆按 TryGetPathPoints 相同采样密度折线化（虚线相位与复杂线型模式一致，均为
///      CAD 约定：0°/长轴端点起、逆时针）；
///   ② 画布有像素上限（单侧 <see cref="PngMaxSide"/>、总量 <see cref="PngMaxPixels"/>），
///      超出时等比缩小并记日志——位图不像 SVG/PDF 可无损放大；
///   ③ 文字用系统字体渲染（SkiaSharp 按族名取 GDI 字体），非内嵌 TTF 轮廓。
/// </summary>
internal partial class ImageEngine
{
    // ===== 位图尺寸安全上限（仅 PNG 输出端）=====
    private const int PngMaxSide = 16384;           // 单侧像素上限（Skia 大位图常见上限）
    private const long PngMaxPixels = 32_000_000L;   // 总像素上限（≈128MB RGBA）

    // PNG 输出上下文（WritePng 设置；单线程导出，非并发）
    private SKCanvas _pngC;
    private double _pngKx; // 画布 X：每数据单位像素数（W/_w）
    private double _pngKy; // 画布 Y：每数据单位像素数（H/_h）

    // 画布坐标映射（与 SVG 的 Sx/Sy 同语义：Y 翻转、边距 _margin；注意 Py 已被 PDF 端占用）
    private double PngSx(double x) => _margin * _pngKx + (x - _minX) * _pngKx;
    private double PngSy(double y) => _h * _pngKy - (_margin * _pngKy + (y - _minY) * _pngKy);
    private double PngK => (_pngKx + _pngKy) * 0.5;

    #region ===== PNG 导出入口 =====

    // PNG 文字对齐宽度（真实字体度量，数据单位）：按解析到的字体族/粗斜体用 SKFont.MeasureText
    // 实测字宽（CJK 实测≈1.167em、半角≈0.5em），再乘宽度因子。修正 EstimateTextWidth 估算值
    // （CJK 1.0em/半角 0.55em）与真实字宽不一致导致的居中/右对正/中间对齐文字漂移。
    // 解析失败（字体族不可用）回退到 EstimateTextWidth 估算（与 SVG/PDF 一致，不劣化）。
    private double PngTextWidth(TextEntity t, string family)
    {
        string txt = t.Value ?? "";
        double wf = Math.Max(t.WidthFactor, 1e-9);
        if (txt.Length == 0) return 0;
        var style = new SKFontStyle(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        SKTypeface? tf = null;
        if (!string.IsNullOrEmpty(family))
            foreach (var part in family.Split(','))
            {
                string f = part.Trim();
                if (f.Length == 0 || f == "serif" || f == "sans-serif" || f == "monospace") continue;
                try { var c = SKTypeface.FromFamilyName(f, style); if (c != null) { tf = c; break; } } catch { }
            }
        if (tf == null) return EstimateTextWidth(txt, t.Height) * wf; // 无字体 → 估算兜底
        try
        {
            using (tf)
            {
                using var font = new SKFont(tf, 1f);
                float w = font.MeasureText(txt);
                return (double)w * t.Height * wf;
            }
        }
        catch { return EstimateTextWidth(txt, t.Height) * wf; }
    }

    public ImageExportResult WritePng(string outPath)
    {
        if (!ComputeExtents())
        {
            _log("⚠️  PNG 无可用实体，跳过");
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

        double zoom = _opt.OutputZoom > 0 ? _opt.OutputZoom : 1.0;
        int W = (int)Math.Ceiling(_w * zoom);
        int H = (int)Math.Ceiling(_h * zoom);
        if (W < 1) W = 1;
        if (H < 1) H = 1;
        if (W > PngMaxSide || H > PngMaxSide || (long)W * H > PngMaxPixels)
        {
            // 超限等比缩小（保宽高比）：取"单侧上限"与"总像素上限"中更紧者
            double f = Math.Min(PngMaxSide / (double)W, PngMaxSide / (double)H);
            if ((long)W * H > PngMaxPixels)
                f = Math.Min(f, Math.Sqrt(PngMaxPixels / ((double)W * H)));
            int W0 = W, H0 = H;
            W = Math.Max(1, (int)(W * f));
            H = Math.Max(1, (int)(H * f));
            _log($"⚠️  PNG 画布超出位图上限，已等比缩小至 {W}×{H}（初始 1:1×{F(zoom)} = {W0}×{H0}）");
        }

            _skippedCounts.Clear();
        _useRealTextWidth = true; // PNG 端：文字对齐锚点用真实字体度量（修正居中/右对正漂移）
            using (var bmp = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul))
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(_transparentBg ? SKColors.Transparent : (_whiteBg ? SKColors.White : SKColors.Black));
                _pngC = canvas;
                _pngKx = (double)W / _w;
                _pngKy = (double)H / _h;

                foreach (var ent in AllEntities())
                    EmitEntityPng(ent, null, 1.0, 7, null, null, _ltScale); // 顶层无 INSERT 上下文，ByBlock 兜底白/实线

                LogSkipped("PNG");
                // SkiaSharp 3.x：SavePng 扩展已移除 → SKBitmap.Encode 到 File 流。
                using (var fs = File.Create(outPath))
                {
                    if (!bmp.Encode(fs, SKEncodedImageFormat.Png, 100))
                    {
                        _log($"❌  PNG 写文件失败：{outPath}");
                        _pngC = null;
                        return new ImageExportResult { Success = false };
                    }
                }
            }
        _pngC = null;
        _log($"✅  PNG 已生成：{outPath}（画板 {W}×{H}，1:1 内容放大 {F(_pngKx)}×{F(_pngKy)} 倍{(_transparentBg ? "，透明背景" : "")}）");
        return new ImageExportResult
        {
            Success = true,
            CanvasPixelsW = W,
            CanvasPixelsH = H,
            DataX0 = _minX - _margin,
            DataX1 = _maxX + _margin,
            DataY0 = _minY - _margin,
            DataY1 = _maxY + _margin,
        };
    }

    #endregion

    #region ===== 实体分发（与 EmitEntitySvg 逐 case 对齐）=====

    // 发射单个实体（含 Insert 展开）。参数语义与 EmitEntitySvg 完全一致：
    // tr=累积变换（嵌套块）、scaleMul=线宽/字高倍率、byBlockAci=随块颜色、byBlockDash=随块虚线、
    // byBlockPat=随块复杂线型、ltMul=线型比例乘数、byLayerOverride=随图层颜色（块内=插入点块图层色）。
    private void EmitEntityPng(Entity ent, Transform2D tr, double scaleMul, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul, short? byLayerOverride = null)
    {
        short layerAci;
        if (byLayerOverride.HasValue) layerAci = byLayerOverride.Value;
        else { layerAci = 7; try { layerAci = LayerAciIndex(ent.Layer); } catch { } }
        double[] dash = ResolveDash(ent, byBlockDash, ltMul);
        LtpPattern pat = ResolvePattern(ent, byBlockPat, ltMul); // 复杂线型（含 SHX 段）→ 沿路径真实渲染

        switch (ent)
        {
            case Insert ins:
                EmitInsertPng(ins, tr, byBlockAci, dash, pat, ltMul);
                break;

            case Line ln:
                {
                    var (x1, y1) = tr == null ? (ln.StartPoint.X, ln.StartPoint.Y) : tr.Apply(ln.StartPoint.X, ln.StartPoint.Y);
                    var (x2, y2) = tr == null ? (ln.EndPoint.X, ln.EndPoint.Y) : tr.Apply(ln.EndPoint.X, ln.EndPoint.Y);
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (!TryEmitComplexPng(ent, tr, pat, col, w))
                        StrokePts(new List<(double X, double Y)> { (x1, y1), (x2, y2) }, false, col, w, dash);
                }
                break;

            case LwPolyline pl:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = new List<(double X, double Y)>(pl.Vertices.Count);
                    foreach (var v in pl.Vertices)
                    {
                        if (tr == null) pts.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts.Add((a.Item1, a.Item2)); }
                    }
                    // 几何宽度（组码43，数据单位，真实加粗）：>0 时按 CAD 语义渲染为"填充多边形"；否则按线宽(370)描边。
                    // DWG 往返：全顶点等宽被归一为 ConstantWidth → 先物化为逐顶点宽度（与 SVG 端同序，含顶点变更）。
                    double sw = pl.Vertices.FirstOrDefault()?.StartWidth ?? 0;
                    if (sw <= 0 && pl.ConstantWidth > 0)
                    {
                        foreach (var v in pl.Vertices) { v.StartWidth = pl.ConstantWidth; v.EndWidth = pl.ConstantWidth; }
                        sw = pl.ConstantWidth;
                    }
                    if (sw > 0 && pts.Count >= 2)
                    {
                        EmitTaperedPng(pl, tr, col, scaleMul);
                        break;
                    }
                    StrokePts(pts, pl.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Polyline3D p3:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = new List<(double X, double Y)>(p3.Vertices.Count);
                    foreach (var v in p3.Vertices)
                    {
                        if (tr == null) pts.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts.Add((a.Item1, a.Item2)); }
                    }
                    StrokePts(pts, p3.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Arc a: // 必须先于 Circle（fork 继承关系）
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (a.Center.X, a.Center.Y) : tr.Apply(a.Center.X, a.Center.Y);
                    double r = a.Radius * (tr?.Scale ?? 1.0);
                    if (r > 0)
                    {
                        // 与 EmitArcAsPolyline 同采样：每 2° 一段（CAD 弧恒逆时针，end<start 跨 360°）
                        double s = a.StartAngle * 180.0 / Math.PI, e = a.EndAngle * 180.0 / Math.PI;
                        double span = e - s;
                        while (span < 0) span += 360.0;
                        while (span >= 360.0) span -= 360.0;
                        if (span < 1e-6) span = 360.0;
                        int seg = Math.Max(8, (int)Math.Ceiling(span / 2.0));
                        var pts = new List<(double X, double Y)>(seg + 1);
                        for (int i = 0; i <= seg; i++)
                        {
                            double ang = (s + span * i / seg) * Math.PI / 180.0;
                            pts.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
                        }
                        StrokePts(pts, false, col, StrokeWidth(ent, scaleMul), dash);
                    }
                }
                break;

            case Circle c:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (c.Center.X, c.Center.Y) : tr.Apply(c.Center.X, c.Center.Y);
                    double r = c.Radius * (tr?.Scale ?? 1.0);
                    if (r > 0)
                    {
                        // 与 TryGetPathPoints 同采样（CAD 线型从 0° 逆时针起）
                        int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * r / 0.5), 64, 100000);
                        var pts = new List<(double X, double Y)>(m + 1);
                        for (int i = 0; i <= m; i++)
                        {
                            double ang = 2 * Math.PI * i / m;
                            pts.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
                        }
                        StrokePts(pts, true, col, StrokeWidth(ent, scaleMul), dash);
                    }
                }
                break;

            case Ellipse el:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (el.Center.X, el.Center.Y) : tr.Apply(el.Center.X, el.Center.Y);
                    double rx = el.MajorAxis * (tr?.Scale ?? 1.0);
                    double ry = el.MinorAxis * (tr?.Scale ?? 1.0);
                    if (rx > 0 && ry > 0)
                    {
                        // 与 TryGetPathPoints 同采样（从长轴端点起逆时针）
                        double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0);
                        double ca = Math.Cos(rot * Math.PI / 180.0), sa = Math.Sin(rot * Math.PI / 180.0);
                        int m = (int)Math.Clamp(Math.Ceiling(2 * Math.PI * Math.Max(rx, ry) / 0.5), 64, 100000);
                        var pts = new List<(double X, double Y)>(m + 1);
                        for (int i = 0; i <= m; i++)
                        {
                            double th = 2 * Math.PI * i / m;
                            double ex = rx * Math.Cos(th), ey = ry * Math.Sin(th);
                            pts.Add((cx + ex * ca - ey * sa, cy + ex * sa + ey * ca));
                        }
                        StrokePts(pts, true, col, StrokeWidth(ent, scaleMul), dash);
                    }
                }
                break;

            case Point pt:
                {
                    var (cx, cy) = tr == null ? (pt.Location.X, pt.Location.Y) : tr.Apply(pt.Location.X, pt.Location.Y);
                    // 点标记：颜色随实体/图层，直径 = 3× 当前线宽，外加背景色光晕环（与 EmitPoint 一致）
                    double lw = StrokeWidth(ent, scaleMul);
                    double radius = Math.Max(0.05, lw * 1.5);
                    if (!double.IsFinite(radius) || radius <= 0) radius = 0.05;
                    double fx = PngSx(cx), fy = PngSy(cy);
                    using (var pf = new SKPaint { IsAntialias = true, Color = HexSk(ColorOf(EntityAciIndex(ent), layerAci, byBlockAci)) })
                        _pngC.DrawCircle((float)fx, (float)fy, (float)(radius * PngK), pf);
                    if (!_transparentBg)
                        // 背景色光晕环（透明背景无底色，跳过，避免在透明上出现一圈黑边）
                        using (var ps = new SKPaint { IsAntialias = true, IsStroke = true, Color = _whiteBg ? SKColors.White : SKColors.Black, StrokeWidth = (float)(radius * 0.3 * PngK) })
                            _pngC.DrawCircle((float)fx, (float)fy, (float)(radius * PngK), ps);
                }
                break;

            case TextEntity t:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    // SHX 样式：真实 SHX 笔迹路径（与 AutoCAD 一致字宽/字形）；非 SHX/字体缺失 → 回退原系统字体路径
                    var shx = TryBuildShxTextWorld(t, tr, scaleMul);
                    if (shx != null)
                    {
                        using (var path = new SKPath())
                        {
                            for (int k = 0; k < shx.Value.Type.Count; k++)
                            {
                                var p = shx.Value.Pts[k];
                                switch (shx.Value.Type[k])
                                {
                                    case 0: path.MoveTo((float)PngSx(p[0]), (float)PngSy(p[1])); break;
                                    case 1: path.LineTo((float)PngSx(p[0]), (float)PngSy(p[1])); break;
                                    case 2: path.QuadTo((float)PngSx(p[0]), (float)PngSy(p[1]), (float)PngSx(p[2]), (float)PngSy(p[3])); break;
                                    case 3: path.CubicTo((float)PngSx(p[0]), (float)PngSy(p[1]), (float)PngSx(p[2]), (float)PngSy(p[3]), (float)PngSx(p[4]), (float)PngSy(p[5])); break;
                                }
                            }
                            using (var paint = new SKPaint { IsAntialias = true, IsStroke = true, Color = HexSk(col), StrokeWidth = (float)Math.Max(0.0, shx.Value.PenWidth * PngK), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round })
                                _pngC.DrawPath(path, paint);
                        }
                        break;
                    }
                    // TTF 样式：原系统字体路径（不变）
                    string family = ResolveFontFamily(GetStyleFont(t.Style));
                    double? wReal = _useRealTextWidth ? PngTextWidth(t, family) : null;
                    var (bx, by, brot, bscale) = TextBaseline(t, tr, wReal);
                    double fs = t.Height * scaleMul * bscale;
                    DrawTextPng(bx, by, t.Value ?? "", Math.Max(fs, 0.5), col, -brot, family, t.WidthFactor);
                }
                break;

            case MText mt:
                // 多行文字：解析格式码 + 参考列宽换行 + 附着点定位（与 EmitMTextSvg 同算法）
                EmitMTextPng(mt, tr, scaleMul, layerAci, byBlockAci, GetStyleFont(mt.Style));
                break;

            case Solid so:
                EmitQuadFillPng(QuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, tr),
                    ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Face3D fd:
                EmitQuadFillPng(QuadPts(fd.FirstCorner, fd.SecondCorner, fd.ThirdCorner, fd.FourthCorner, tr),
                    ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                break;

            case Hatch ht:
                EmitHatchPng(ht, tr, scaleMul, layerAci, byBlockAci);
                break;

            case Spline sp:
                {
                    string col = ColorOf(EntityAciIndex(sp), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(sp, scaleMul))) break;
                    var sp2 = SplineSample(sp, tr, 16);
                    if (sp2.Count < 2) { RecordSkip("SPLINE(空)"); break; }
                    var pts = new List<(double X, double Y)>(sp2.Count);
                    foreach (var p in sp2) pts.Add((p.X, p.Y));
                    StrokePts(pts, false, col, StrokeWidth(sp, scaleMul), dash);
                }
                break;

            default:
                RecordSkip(ent.GetType().Name);
                break;
        }
    }

    // Insert 展开（与 EmitInsertSvg 同逻辑：变换组合 + 有效颜色/线型继承 + 块内 ByLayer=插入点块图层色）
    private void EmitInsertPng(Insert ins, Transform2D parent, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul)
    {
        if (ins?.Block == null) return;
        var tr = Transform2D.Compose(ins, parent);
        short insAci = EffectiveAci(ins, byBlockAci);
        double[] insDash = ResolveDash(ins, byBlockDash, ltMul);
        LtpPattern insPat = ResolvePattern(ins, byBlockPat, ltMul);
        short insLayerAci = 7;
        try { insLayerAci = LayerAciIndex(ins.Layer); } catch { }
        double childLtMul = ltMul * BlockScaleOf(ins);
        foreach (var e in ins.Block.Entities)
            EmitEntityPng(e, tr, tr.Scale, insAci, insDash, insPat, childLtMul, insLayerAci);
    }

    #endregion

    #region ===== 描边基础（折线 + 虚线走查，相位与 SVG stroke-dasharray 一致）=====

    // 描边折线（数据单位坐标）。dashed 时手动沿路径走查 dash/gap（相位从路径起点，与
    // stroke-dasharray 及复杂线型模式一致）；闭合 + 无虚线 → Close() 干净转角。
    private void StrokePts(List<(double X, double Y)> pts, bool closed, string col, double w, double[] dash)
    {
        if (pts == null || pts.Count < 2) return;
        bool dashed = dash != null && dash.Length > 0;
        using var paint = StrokePaint(col, w);
        using var path = new SKPath();
        if (dashed)
        {
            var walk = pts;
            if (closed)
            {
                // 补首点收尾，保证图案走完闭合边（与 TryGetPathPoints 闭合处理同语义）
                walk = new List<(double X, double Y)>(pts.Count + 1);
                walk.AddRange(pts);
                walk.Add(pts[0]);
            }
            var cum = BuildCum(walk);
            WalkDashPx(path, walk, cum, dash);
        }
        else
        {
            path.MoveTo((float)PngSx(pts[0].X), (float)PngSy(pts[0].Y));
            for (int i = 1; i < pts.Count; i++)
                path.LineTo((float)PngSx(pts[i].X), (float)PngSy(pts[i].Y));
            if (closed) path.Close();
        }
        _pngC.DrawPath(path, paint);
    }

    // 描边画笔（与 SVG 端一致：round 转角、butt 端帽、抗锯齿）
    private SKPaint StrokePaint(string colHex, double wData)
    {
        return new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            Color = HexSk(colHex),
            StrokeWidth = (float)Math.Max(0.0, wData * PngK),
            StrokeJoin = SKStrokeJoin.Round,
        };
    }

    // 沿折线走查 [dash, gap, dash, ...]（正长度交替，dash 起头），落笔区间追加为子路径。
    private void WalkDashPx(SKPath path, List<(double X, double Y)> pts, double[] cum, double[] dash)
    {
        double L = cum[cum.Length - 1];
        if (L <= 1e-9) return;
        double period = 0;
        foreach (var v in dash) period += v;
        if (period <= 1e-9) return;
        double pos = 0;
        int i = 0, guard = 0;
        while (pos < L - 1e-9 && guard++ < 50000000)
        {
            double span = dash[i % dash.Length];
            if (span > 1e-9)
                AppendSpanPathPx(path, pts, cum, pos, Math.Min(pos + span, L), PngSx, PngSy);
            pos += span;
            i++;
        }
    }

    // 把弧长区间 [s0, s1] 的子路径追加到 SKPath（端点精确插值 + 区间内原始采样顶点；AppendSpanPath 的位图版）
    private static void AppendSpanPathPx(SKPath path, List<(double X, double Y)> pts, double[] cum, double s0, double s1,
        Func<double, double> fx, Func<double, double> fy)
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

        path.MoveTo((float)m0x, (float)m0y);
        for (int i = 1; i < n; i++)
        {
            double c = cum[i];
            if (c <= s0 + 1e-9 || c >= s1 - 1e-9) continue;
            path.LineTo((float)fx(pts[i].X), (float)fy(pts[i].Y));
        }
        double dx = m1x - m0x, dy = m1y - m0y;
        if (dx * dx + dy * dy > 1e-18)
            path.LineTo((float)m1x, (float)m1y);
    }

    #endregion

    #region ===== 复杂线型（SHX 真实字形，WalkPattern 同算法）=====

    // 复杂线型（PNG）：沿路径走查模式生成单个描边 SKPath（dash 子路径 + 字形子路径）。
    // 返回 false = 不支持/空（调用方回退 dasharray 逻辑）。与 TryEmitComplexSvg 逐行对应。
    private bool TryEmitComplexPng(Entity ent, Transform2D tr, LtpPattern pat, string col, double w)
    {
        if (pat == null) return false;
        if (!TryGetPathPoints(ent, tr, out var pts) || pts.Count < 2) return false;
        var cum = BuildCum(pts);
        if (cum[cum.Length - 1] <= 1e-9) return false;
        using var path = new SKPath();
        WalkPatternPx(path, pts, cum, pat);
        if (path.IsEmpty) return false;
        using var paint = StrokePaint(col, w);
        _pngC.DrawPath(path, paint);
        return true;
    }

    // 沿路径走查模式（WalkPattern 的位图版）：dash→线子路径；gap→留空；shape→段中点真实 SHX 字形（沿切向）。
    private void WalkPatternPx(SKPath path, List<(double X, double Y)> pts, double[] cum, LtpPattern pat)
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
                AppendSpanPathPx(path, pts, cum, pos, Math.Min(pos + span, L), PngSx, PngSy);
            }
            else if (seg.Kind == LtpSeg.Shape)
            {
                double at = pos + seg.CenterOffset;
                PathPointAt(pts, cum, at, out double px, out double py, out double tx, out double ty);
                EmitShapeGlyphPx(path, at, px, py, tx, ty, seg);
            }
            pos += span;
            i++;
        }
    }

    // 在路径点绘制真实 SHX 字形（追加到同一描边路径；EmitShapeGlyph 的位图版）。
    private void EmitShapeGlyphPx(SKPath path, double at, double px, double py, double tx, double ty, LtpSeg seg)
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
            // SHX 库/形状不可用 → 回退"点"（沿切向居中的短实线），保持图案节奏（与 SVG 端一致）
            double dd = seg.Len > 1e-9 ? Math.Min(seg.ShapeScale * 4.0, 0.9 * seg.Len) : 0.1;
            if (dd < 0.05) dd = 0.05;
            path.MoveTo((float)PngSx(px - tx * dd / 2), (float)PngSy(py - ty * dd / 2));
            path.LineTo((float)PngSx(px + tx * dd / 2), (float)PngSy(py + ty * dd / 2));
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
        // 字形点(字体单位) → 画布坐标：平移(包围盒中心→路径点)+旋转+缩放（与 EmitShapeGlyph 同序）
        void Xf(float gx, float gy, out double mx, out double my)
        {
            double lx = (gx - rec.CX) * S + ox;
            double ly = (gy - rec.CY) * S + oy;
            double wx = px + lx * ca - ly * sa;
            double wy = py + lx * sa + ly * ca;
            mx = wx; my = wy;
        }
        for (int k = 0; k < rec.Type.Count; k++)
        {
            var p = rec.Pts[k];
            switch (rec.Type[k])
            {
                case 0:
                    Xf(p[0], p[1], out double mx, out double my);
                    path.MoveTo((float)PngSx(mx), (float)PngSy(my));
                    break;
                case 1:
                    Xf(p[0], p[1], out double lx, out double ly);
                    path.LineTo((float)PngSx(lx), (float)PngSy(ly));
                    break;
                case 2:
                    Xf(p[0], p[1], out double qx, out double qy);
                    Xf(p[2], p[3], out double ex, out double ey);
                    path.QuadTo((float)PngSx(qx), (float)PngSy(qy), (float)PngSx(ex), (float)PngSy(ey));
                    break;
                case 3:
                    Xf(p[0], p[1], out double c1x, out double c1y);
                    Xf(p[2], p[3], out double c2x, out double c2y);
                    Xf(p[4], p[5], out double ex2, out double ey2);
                    path.CubicTo((float)PngSx(c1x), (float)PngSy(c1y), (float)PngSx(c2x), (float)PngSy(c2y),
                        (float)PngSx(ex2), (float)PngSy(ey2));
                    break;
            }
        }
    }

    #endregion

    #region ===== 文字（TEXT / MText：基线=插入点，旋转/倾斜/宽度因子）=====

    // MTEXT → PNG（EmitMTextSvg 的位图版：逐 run 定位，同一局部坐标系→世界→画布映射）
    private void EmitMTextPng(MText mt, Transform2D tr, double scaleMul, short layerAci, short byBlockAci, string styleFont)
    {
        string baseCol = ColorOf(EntityAciIndex(mt), layerAci, byBlockAci);
        var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height), mt.RectangleWidth);
        if (layout.Lines.Count == 0) return;
        var (baselines, anchorX, anchorY) = MTextLocalLayout(layout, mt);

        double rot = mt.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double px = mt.InsertPoint.X, py = mt.InsertPoint.Y;
        if (tr != null) { var ap = tr.Apply(px, py); px = ap.Item1; py = ap.Item2; }

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
                    string fam = ResolveFontFamily(!string.IsNullOrEmpty(run.Font) ? run.Font : styleFont);
                    // 局部点→世界：P + R·(local − anchor)（局部偏移乘 scaleMul，与 SVG 端 ToWorld 同式）
                    double dx = (x - anchorX) * scaleMul, dy = (baselines[li] - anchorY) * scaleMul;
                    double wx = px + dx * ca - dy * sa;
                    double wy = py + dx * sa + dy * ca;
                    DrawTextPng(wx, wy, run.Text, fs, col, -rot, fam, run.WidthFactor,
                        run.Bold, run.Italic, run.Oblique, run.Underline, run.Overline, rw);
                }
                x += rw;
            }
        }
    }

    // 基线文字绘制（画布坐标系）。
    //   (x,y)        = 基线左端点（数据坐标，TextBaseline/MText 已算好）
    //   fsData       = 字高（数据单位，含 scaleMul）
    //   rotCanvas    = 画布系旋转角（度；数据系 CCW 角 θ 传 −θ：画布 y 向下，正角=视觉顺时针）
    //   widthFactor  = 字宽因子（沿文字自身基线方向压缩，先缩后转——与 SVG scale(WF,1) 同语义）
    //   oblique      = 倾斜角（度，CAD \Q 语义：正=上端右倾）
    // 局部系原点=基线左端点，文字沿局部 +x 排布；矩阵 = T(画布点)·R·K(skewX)·S(WF,1)。
    private void DrawTextPng(double x, double y, string content, double fsData, string colHex, double rotCanvas,
        string family, double widthFactor, bool bold = false, bool italic = false, double oblique = 0.0,
        bool underline = false, bool overline = false, double runWidthData = 0.0)
    {
        if (string.IsNullOrEmpty(content) || _pngC == null) return;
        double sx = PngSx(x), sy = PngSy(y);
        float fs = (float)(fsData * PngK);
        if (fs <= 0) return;

        // SkiaSharp 3.x：SKMatrix 无 Scale/Translation/6 参构造；PostConcat 返回新矩阵（2.x 为就地 void）。
        // 目标映射 local(u,v)→画布：T(sx,sy)·R(θ)·K(skewX)·S(wf,1)，即先缩字宽、再倾斜、再旋转、最后平移到锚点。
        // 用 PostConcat 顺序拼接：S → K → R → T（后接者作用在右侧/先作用于局部坐标）。
        // 关键：锚点 local(0,0) 必须映射到 (sx,sy)。旧版用 PreConcat 链（=S·R·K·T，顺序颠倒）会把
        // 平移也乘进缩放 → 锚点被移到 (sx·wf, sy)，水平（及旋转文字的垂直）整体漂移，与 SVG 端
        // （SVG 的 transform 列表 T·R·K 顺序正确）不一致。改 PostConcat 后锚点精确落在 (sx,sy)。
        double wf = widthFactor > 1e-9 ? widthFactor : 1.0;
        var m = SKMatrix.CreateScale((float)wf, 1.0f);
        if (Math.Abs(oblique) > 1e-6)
            // SVG skewX(α)：x' = x + tan(α)·y（y 向下）；CAD 正倾角 → α = −oblique
            m = m.PostConcat(SKMatrix.CreateSkew((float)Math.Tan(-oblique * Math.PI / 180.0), 0f));
        if (Math.Abs(rotCanvas) > 1e-6)
            m = m.PostConcat(SKMatrix.CreateRotationDegrees((float)rotCanvas));
        m = m.PostConcat(SKMatrix.CreateTranslation((float)sx, (float)sy));

        using (var tf = GetPngTypeface(family, bold, italic, content))
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Color = HexSk(colHex),
            TextSize = fs,
            Typeface = tf ?? SKTypeface.Default,
        })
        {
            _pngC.Save();
            _pngC.Concat(m);
            _pngC.DrawText(content, 0f, 0f, paint); // y=0 即基线
            if ((underline || overline) && runWidthData > 0)
            {
                // 下/上划线：局部系内沿基线方向，长 = run 宽（数据单位）×PngK（矩阵 S(WF) 会同步拉伸）
                float rw = (float)(runWidthData * PngK);
                using var dp = new SKPaint { IsAntialias = true, Color = paint.Color, IsStroke = true, StrokeWidth = Math.Max(1f, fs * 0.05f) };
                if (underline)
                    _pngC.DrawLine(0f, fs * 0.10f, rw, fs * 0.10f, dp);
                if (overline)
                    _pngC.DrawLine(0f, -fs * 0.85f, rw, -fs * 0.85f, dp);
            }
            _pngC.Restore();
        }
    }

    // 字体族解析：ResolveFontFamily 返回 CSS 族列表（如 "SimSun, 宋体, serif"），按序尝试；
    // 均失败返回 null（调用方用 SKTypeface.Default 兜底）。
    // SkiaSharp 3.x：字体按族名解析为 SKTypeface.FromFamilyName（2.x 的 SKTypeface.Create 已移除）。
    // 额外做 CJK 字形覆盖校验：FromFamilyName 可能回退到不含 CJK 字形的基础字体，
    // 若文本含 CJK 而解析结果不覆盖，则丢弃该族继续尝试下一族，避免中文渲染成空白/点阵。
    private static SKTypeface? GetPngTypeface(string family, bool bold, bool italic, string content)
    {
        if (string.IsNullOrEmpty(family)) return null;
        var style = new SKFontStyle(
            bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        foreach (var part in family.Split(','))
        {
            string f = part.Trim();
            if (f.Length == 0 || f == "serif" || f == "sans-serif" || f == "monospace") continue;
            try
            {
                var tf = SKTypeface.FromFamilyName(f, style);
                if (tf == null) continue;
                if (!TypefaceCoversText(tf, content)) { tf.Dispose(); continue; }
                return tf;
            }
            catch { }
        }
        return null;
    }

    // 取样校验字体是否覆盖文本中的 CJK 码位（<0x2E80 的拉丁/数字不算）：
    // 无 CJK 内容直接通过；有 CJK 内容时只要有一个 CJK 码位缺字形即判不覆盖。
    private static bool TypefaceCoversText(SKTypeface tf, string content)
    {
        if (string.IsNullOrEmpty(content)) return true;
        int cjkChecked = 0;
        foreach (var ch in content)
        {
            if (ch < 0x2E80) continue;
            cjkChecked++;
            if (!tf.ContainsGlyph(ch)) return false;
            if (cjkChecked >= 64) break;
        }
        return true;
    }

    #endregion

    #region ===== 填充（四边形 / HATCH 实心/渐变/图案）=====

    // 四边形填充（Solid/Face3D；与 EmitQuadFill 一致：填充 + 0.15 宽同色描边防 AA 缝隙）
    private void EmitQuadFillPng(List<(double X, double Y)> pts, string color)
    {
        if (pts == null || pts.Count < 3 || _pngC == null) return;
        using var path = new SKPath();
        path.MoveTo((float)PngSx(pts[0].X), (float)PngSy(pts[0].Y));
        for (int i = 1; i < pts.Count; i++)
            path.LineTo((float)PngSx(pts[i].X), (float)PngSy(pts[i].Y));
        path.Close();
        var col = HexSk(color);
        using (var pf = new SKPaint { IsAntialias = true, Color = col })
            _pngC.DrawPath(path, pf);
        using (var ps = new SKPaint { IsAntialias = true, IsStroke = true, Color = col, StrokeWidth = (float)Math.Max(0.05, 0.15 * PngK) })
            _pngC.DrawPath(path, ps);
    }

    // HATCH → PNG（EmitHatchSvg 的位图版：实心=实体色/真色；渐变=linear/radial 双端 shader；图案=裁剪线段描边）
    private void EmitHatchPng(Hatch ht, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var loops = BuildHatchLoops(ht, tr);
        if (loops.Count == 0) { RecordSkip("HATCH(空)"); return; }
        var gp = ht.GradientColor;
        bool gradient = gp != null && gp.Enabled;
        if (!gradient && !IsSolidPattern(ht))
        {
            EmitHatchPatternPng(ht, loops, tr, scaleMul, layerAci, byBlockAci);
            return;
        }
        using var path = new SKPath();
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            path.MoveTo((float)PngSx(loop[0].X), (float)PngSy(loop[0].Y));
            for (int i = 1; i < loop.Count; i++)
                path.LineTo((float)PngSx(loop[i].X), (float)PngSy(loop[i].Y));
            path.Close();
        }
        // SkiaSharp 3.x：填充规则改由 SKPath.FillType 承载（SKPaint.FillStyle/FillType 已移除）。
        path.FillType = loops.Count > 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = SKColors.White, // 渐变时 shader 色不被 Color 调制
        };
        if (gradient)
        {
            // 渐变几何与 EmitHatchGradientDef 严格一致：范围=包围盒对角线一半，460 角度沿梯度方向，取首/末色标
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
            SKColor c1 = SKColors.White, c2 = SKColors.Black;
            if (gp.Colors != null && gp.Colors.Count >= 2)
            {
                c1 = HexSk(StopHex(gp.Colors[0].Color));
                c2 = HexSk(StopHex(gp.Colors[gp.Colors.Count - 1].Color));
            }
            SKShader shader;
            if (IsEllipticGradient(gp))
            {
                // 椭圆渐变：以包围盒中心为圆心、半径 L·PngK 的径向渐变（首→末色标，Clamp）
                shader = SKShader.CreateRadialGradient(
                    new SKPoint((float)PngSx(cx), (float)PngSy(cy)), (float)(L * PngK),
                    new[] { c1, c2 }, SKShaderTileMode.Clamp);
            }
            else
            {
                double ang = gp.Angle; // fork 弧度
                double dx = Math.Cos(ang), dy = Math.Sin(ang);
                shader = SKShader.CreateLinearGradient(
                    new SKPoint((float)PngSx(cx - dx * L), (float)PngSy(cy - dy * L)),
                    new SKPoint((float)PngSx(cx + dx * L), (float)PngSy(cy + dy * L)),
                    new[] { c1, c2 }, SKShaderTileMode.Clamp);
            }
            paint.Shader = shader;
            _pngC.DrawPath(path, paint);
            shader?.Dispose();
        }
        else
        {
            paint.Color = HexSk(HatchSolidFillHex(ht, layerAci, byBlockAci));
            _pngC.DrawPath(path, paint);
        }
    }

    // 图案 HATCH（EmitHatchPatternSvg 的位图版：BuildPatternSegments 共享几何 → 描边）
    private void EmitHatchPatternPng(Hatch ht, List<List<XY>> loops, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var segs = BuildPatternSegments(loops, ht, tr, MaxPatternLinesPerHatch);
        if (segs.Count == 0) { RecordSkip("HATCH(图案)"); return; }
        string col = HatchSolidFillHex(ht, layerAci, byBlockAci);
        double w = Math.Max(0.03, 0.1 * scaleMul); // 图案线细
        using var path = new SKPath();
        foreach (var s in segs)
        {
            path.MoveTo((float)PngSx(s.X1), (float)PngSy(s.Y1));
            path.LineTo((float)PngSx(s.X2), (float)PngSy(s.Y2));
        }
        using var paint = StrokePaint(col, w);
        _pngC.DrawPath(path, paint);
    }

    #endregion

    #region ===== 变宽折线（填充多边形，EmitTaperedPolyline 的位图版）=====

    private void EmitTaperedPng(LwPolyline pl, Transform2D tr, string stroke, double scaleMul)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        if (n < 2 || _pngC == null) return;
        double[] w = new double[n];
        for (int i = 0; i < n; i++)
            w[i] = verts[i].StartWidth;

        List<(double X, double Y)> left, right;
        var taper = TryBuildTaperOffset(pl, w, tr, _opt.TaperOffsetBuilder);
        if (taper.Ok)
        {
            left = taper.Left; right = taper.Right;
        }
        else
        {
            // 无 bulge：逐顶点弦偏移（与 SVG 端逐行同式）
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

        var col = HexSk(stroke);
        using var path = new SKPath();
        int m = left.Count;
        if (m < 2) return;
        if (pl.IsClosed)
        {
            // 闭合：left/right 各一条封闭偏移轮廓（内/外环），两子路径 + evenodd 填充得"环"
            path.MoveTo((float)PngSx(left[0].X), (float)PngSy(left[0].Y));
            for (int i = 1; i < m; i++) path.LineTo((float)PngSx(left[i].X), (float)PngSy(left[i].Y));
            path.Close();
            path.MoveTo((float)PngSx(right[0].X), (float)PngSy(right[0].Y));
            for (int i = 1; i < m; i++) path.LineTo((float)PngSx(right[i].X), (float)PngSy(right[i].Y));
            path.Close();
            // SkiaSharp 3.x：填充规则在 SKPath.FillType（SKPaint.FillType 已移除）
            path.FillType = SKPathFillType.EvenOdd;
            using (var pf = new SKPaint { IsAntialias = true, Color = col, Style = SKPaintStyle.Fill })
                _pngC.DrawPath(path, pf);
            using (var ps = new SKPaint { IsAntialias = true, IsStroke = true, Color = col, StrokeWidth = (float)Math.Max(0.05, 0.1 * PngK) })
                _pngC.DrawPath(path, ps);
        }
        else
        {
            // 开放：left 正向 + right 反向，两端闭合填充（带/楔形）
            path.MoveTo((float)PngSx(left[0].X), (float)PngSy(left[0].Y));
            for (int i = 1; i < m; i++) path.LineTo((float)PngSx(left[i].X), (float)PngSy(left[i].Y));
            for (int i = m - 1; i >= 0; i--) path.LineTo((float)PngSx(right[i].X), (float)PngSy(right[i].Y));
            path.Close();
            using (var pf = new SKPaint { IsAntialias = true, Color = col })
                _pngC.DrawPath(path, pf);
            using (var ps = new SKPaint { IsAntialias = true, IsStroke = true, Color = col, StrokeWidth = (float)Math.Max(0.05, 0.1 * PngK) })
                _pngC.DrawPath(path, ps);
        }
    }

    #endregion

    #region ===== 颜色工具 =====

    // hex(#RRGGBB) → SKColor（带缓存；引擎内所有颜色均为该格式：AciHex/StopHex/HatchSolidFillHex）
    private static readonly ConcurrentDictionary<string, SKColor> _hexSkCache = new();
    private static SKColor HexSk(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return SKColors.White;
        if (_hexSkCache.TryGetValue(hex, out var c)) return c;
        SKColor col;
        try
        {
            col = new SKColor(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16),
                255);
        }
        catch { col = SKColors.White; }
        _hexSkCache[hex] = col;
        return col;
    }

    #endregion
}
