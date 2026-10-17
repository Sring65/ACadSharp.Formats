using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Formats.Pdf;
using ACadSharp.Text;
using CSMath;

namespace ACadSharp.Formats.Image;

/// <summary>
/// PNG 光栅输出端（T4.9）：用纯托管 RasterCanvas 把与 SVG 端完全相同的几何渲染到位图并写出 PNG
/// （零外部依赖，纯托管软件光栅化渲染）。
/// 渲染语义严格对齐 SVG 端（FullImageEngine.Svg.cs）：1:1 画布 + 2% 边距 + OutputZoom 放大
/// + 黑/白背景 + ACI 官方色表（ACI 7 黑底显白/白底显黑）+ 虚线（图案自路径起点）+ 复杂线型（SHX 真实字形）
/// + NURBS 样条 de Boor 采样 + HATCH（实心/渐变/图案）+ 变宽折线（ITaperOffsetBuilder）+ TEXT/MText
/// （基线=插入点，旋转/倾斜/宽度因子；TTF 用 TrueTypeFont 真字形轮廓填充，SHX 用真实笔迹路径）
/// + 点标记（圆盘）+ 嵌套块展开 + ByBlock/ByLayer 继承。
/// </summary>
internal partial class ImageEngine
{
    // ===== 位图尺寸安全上限（仅 PNG 输出端）=====
    private const int PngMaxSide = 16384;           // 单侧像素上限
    private const long PngMaxPixels = 32_000_000L;  // 总像素上限（≈128MB RGBA）

    // PNG 输出上下文（WritePng 设置；单线程导出，非并发）
    private RasterCanvas _rc;
    private double _pngKx; // 画布 X：每数据单位像素数（W/_w）
    private double _pngKy; // 画布 Y：每数据单位像素数（H/_h）

    // 画布坐标映射（与 SVG 的 Sx/Sy 同语义：Y 翻转、边距 _margin）
    private double PngSx(double x) => _margin * _pngKx + (x - _minX) * _pngKx;
    private double PngSy(double y) => _h * _pngKy - (_margin * _pngKy + (y - _minY) * _pngKy);
    private double PngK => (_pngKx + _pngKy) * 0.5;

    #region ===== 颜色工具 =====

    // hex(#RRGGBB) → Rgb（带缓存；引擎内所有颜色均为该格式）
    private static readonly ConcurrentDictionary<string, RasterCanvas.Rgb> _hexRgbCache = new();
    private static RasterCanvas.Rgb HexToRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return RasterCanvas.Rgb.White;
        if (_hexRgbCache.TryGetValue(hex, out var c)) return c;
        RasterCanvas.Rgb col;
        try
        {
            col = new RasterCanvas.Rgb(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        }
        catch { col = RasterCanvas.Rgb.White; }
        _hexRgbCache[hex] = col;
        return col;
    }

    #endregion

    #region ===== PNG 导出入口 =====

    // PNG 文字对齐宽度（真实字体度量，数据单位）：用 TrueTypeFont 实测字宽（CJK≈1em、拉丁≈0.5em），
    // 再乘宽度因子。修正 EstimateTextWidth 估算值（CJK 1.0em/半角 0.55em）与真实字宽不一致导致的
    // 居中/右对正/中间对齐文字漂移。解析失败（字体族不可用）回退到 EstimateTextWidth 估算。
    private double PngTextWidth(TextEntity t, string family)
    {
        string txt = t.Value ?? "";
        double wf = Math.Max(t.WidthFactor, 1e-9);
        if (txt.Length == 0) return 0;
        var fr = PickFont(family, txt);
        if (fr == null) return EstimateTextWidth(txt, t.Height) * wf; // 无字体 → 估算兜底
        double total = 0;
        for (int i = 0; i < txt.Length; i++)
            // 必须 (int) 强转：传 char 时 C# 重载决议优先绑定 GetAdvanceWidth(ushort glyphIndex)
            // （char→ushort 是无符号精确转换，优于 char→int），把 Unicode 码点当字形索引 → 越界返回 0。
            // 中文汉字码点大（U+4E00+）必越界 → advance 读成 0 → 渲染时后续字符叠上前字符（"文字重叠"）。
            total += fr.GetAdvanceWidth((int)txt[i]) / fr.UnitsPerEm;
        return total * t.Height * wf;
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
        ExpandViewForPointGlyphs(); // 点样式字形（r 可达视高 5%）四边扩界，避免边缘点被裁

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
        var rc = new RasterCanvas(W, H);
        {
            if (_transparentBg) rc.Clear(0, 0, 0, 0);
            else rc.Clear(_whiteBg ? (byte)255 : (byte)0, _whiteBg ? (byte)255 : (byte)0, _whiteBg ? (byte)255 : (byte)0, 255);
            _rc = rc;
            _pngKx = (double)W / _w;
            _pngKy = (double)H / _h;

            foreach (var ent in AllEntities())
                EmitEntityPng(ent, null, 1.0, 7, null, null, _ltScale); // 顶层无 INSERT 上下文，ByBlock 兜底白/实线

            LogSkipped("PNG");
            if (!rc.SavePng(outPath))
            {
                _log($"❌  PNG 写文件失败：{outPath}");
                _rc = null;
                return new ImageExportResult { Success = false };
            }
        }
        _rc = null;
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

    // 发射单个实体（含 Insert 展开）。参数语义与 EmitEntitySvg 完全一致。
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
                    // 普通描边路径采样世界折线点（含 bulge 弧段）；变宽路径走 EmitTaperedPng（自有 bulge 逻辑）
                    var pts = LwPolylineSamplePoints(pl, tr);
                    // 变宽判断看**任意**顶点 StartWidth(40) 或 ConstantWidth(43)（首顶点窄端 sw=0 的纺锤形不能漏判）；
                    // EndWidth(41) 不单独触发（闭合圆环全 sw=0/ew=常数 应走描边+bulge 弧，否则变宽路径退化成 0 宽消失）。
                    // 常数宽(43)：全顶点等宽归一形式 → 物化为逐顶点
                    if (pl.ConstantWidth > 0 && !pl.Vertices.Any(v => v.StartWidth > 0))
                        foreach (var v in pl.Vertices) { v.StartWidth = pl.ConstantWidth; v.EndWidth = pl.ConstantWidth; }
                    // 变宽：任意顶点 起宽(40)/终宽(41) 非零 或 常数宽(43) 非零 → 走填充变宽路径；逐顶点有效宽度
                    // 由 TaperedVertexWidths 计算（正确处理"每节起点/终点线宽不等"，与 SVG/PDF 一致）。
                    bool hasTaper = pl.Vertices.Any(v => v.StartWidth > 0 || v.EndWidth > 0) || pl.ConstantWidth > 0;
                    if (hasTaper && pts.Count >= 2)
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
                        if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                        if (tr == null) pts.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts.Add((a.Item1, a.Item2)); }
                    }
                    if (pts.Count >= 2) StrokePts(pts, p3.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Polyline2D p2d:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    if (TryEmitComplexPng(ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = new List<(double X, double Y)>(p2d.Vertices.Count);
                    foreach (var v in p2d.Vertices)
                    {
                        if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                        if (tr == null) pts.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts.Add((a.Item1, a.Item2)); }
                    }
                    if (pts.Count >= 2) StrokePts(pts, p2d.IsClosed, col, StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Mesh mesh:
                {
                    // 3D 网格：渲染边线框
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (mesh.Edges != null && mesh.Vertices != null && mesh.Vertices.Count > 0)
                        foreach (var e in mesh.Edges)
                        {
                            if (e.Start < 0 || e.End < 0 || e.Start >= mesh.Vertices.Count || e.End >= mesh.Vertices.Count) continue;
                            var va = mesh.Vertices[e.Start];
                            var vb = mesh.Vertices[e.End];
                            if (tr == null)
                                StrokePts(new List<(double X, double Y)> { (va.X, va.Y), (vb.X, vb.Y) }, false, col, w, null);
                            else
                            {
                                var a = tr.Apply(va.X, va.Y); var b = tr.Apply(vb.X, vb.Y);
                                StrokePts(new List<(double X, double Y)> { (a.Item1, a.Item2), (b.Item1, b.Item2) }, false, col, w, null);
                            }
                        }
                }
                break;

            case PolyfaceMesh pf:
                {
                    // 旧式 3D 多面体网格：按 Face 索引连线成线框
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPng(ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolyfaceMeshWire(pf, tr))
                        StrokePts(new List<(double X, double Y)> { (a.AX, a.AY), (b.BX, b.BY) }, false, col, w, null);
                }
                break;

            case PolygonMesh pm:
                {
                    // M×N 多边形网格：网格线框
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPng(ent, tr, pat, col, w)) break;
                    foreach (var (a, b) in PolygonMeshWire(pm, tr))
                        StrokePts(new List<(double X, double Y)> { (a.AX, a.AY), (b.BX, b.BY) }, false, col, w, null);
                }
                break;

            case Wipeout wo:
                {
                    // 擦除多边形：用背景色填充（透明背景无底色可擦则跳过）
                    if (!_transparentBg && _rc != null)
                    {
                        var wpoly = WipeoutWorldPoly(wo, tr);
                        if (wpoly.Count >= 3)
                        {
                            var pp = new List<RasterCanvas.P>(wpoly.Count);
                            foreach (var p in wpoly) pp.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
                            _rc.Fill(new List<List<RasterCanvas.P>> { pp }, false, _whiteBg ? RasterCanvas.Rgb.White : RasterCanvas.Rgb.Black);
                        }
                    }
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
                    // fork MajorAxis/MinorAxis 为主/短轴全长(2a/2b)，采样点用半轴 → 各除以 2
                    double rx = el.MajorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    double ry = el.MinorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    if (rx > 0 && ry > 0)
                    {
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
                    double lw = StrokeWidth(ent, scaleMul);
                    var col = HexToRgb(ColorOf(EntityAciIndex(ent), layerAci, byBlockAci));
                    EmitPointPng(cx, cy, col, lw);
                }
                break;

            case TextEntity t:
                {
                    string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
                    // SHX 样式：真实 SHX 笔迹路径（与 AutoCAD 一致字宽/字形）；非 SHX/字体缺失 → 回退原 TTF 路径
                    var shx = TryBuildShxTextWorld(t, tr, scaleMul);
                    if (shx != null)
                    {
                        var pb = new PolyBuilder();
                        for (int k = 0; k < shx.Value.Type.Count; k++)
                        {
                            var p = shx.Value.Pts[k];
                            switch (shx.Value.Type[k])
                            {
                                case 0: pb.MoveTo((float)PngSx(p[0]), (float)PngSy(p[1])); break;
                                case 1: pb.LineTo((float)PngSx(p[0]), (float)PngSy(p[1])); break;
                                case 2: pb.QuadTo((float)PngSx(p[0]), (float)PngSy(p[1]), (float)PngSx(p[2]), (float)PngSy(p[3])); break;
                                case 3: pb.CubicTo((float)PngSx(p[0]), (float)PngSy(p[1]), (float)PngSx(p[2]), (float)PngSy(p[3]), (float)PngSx(p[4]), (float)PngSy(p[5])); break;
                            }
                        }
                        var penCol = HexToRgb(col);
                        float pw = (float)Math.Max(0.0, shx.Value.PenWidth * PngK);
                        foreach (var pl in pb.ToPolylines(false))
                            if (pl.Count >= 2) _rc.StrokePolyline(pl, false, pw, penCol);
                        break;
                    }
                    // TTF 样式：纯托管真字形轮廓填充（TrueTypeFont 替代 SkiaSystemFont）
                    string family = ResolveFontFamily(GetStyleFont(t.Style));
                    double? wReal = _useRealTextWidth ? PngTextWidth(t, family) : null;
                    var (bx, by, brot, bscale) = TextBaseline(t, tr, wReal);
                    double fs = t.Height * scaleMul * bscale;
                    DrawTextPng(bx, by, t.Value ?? "", Math.Max(fs, 0.5), col, -brot, family, t.WidthFactor);
                }
                break;

            case MText mt:
                EmitMTextPng(mt, tr, scaleMul, layerAci, byBlockAci, GetStyleFont(mt.Style));
                break;

            case Solid so:
                EmitQuadFillPng(SolidQuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, tr),
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

            case XLine xl:
                {
                    var (px, py) = tr == null ? (xl.FirstPoint.X, xl.FirstPoint.Y) : tr.Apply(xl.FirstPoint.X, xl.FirstPoint.Y);
                    var (dx, dy) = TransformDirection(xl.Direction.X, xl.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, true, out var x1, out var y1, out var x2, out var y2))
                        StrokePts(new List<(double X, double Y)> { (x1, y1), (x2, y2) }, false,
                            ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case Ray ray:
                {
                    var (px, py) = tr == null ? (ray.StartPoint.X, ray.StartPoint.Y) : tr.Apply(ray.StartPoint.X, ray.StartPoint.Y);
                    var (dx, dy) = TransformDirection(ray.Direction.X, ray.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, false, out var x1, out var y1, out var x2, out var y2))
                        StrokePts(new List<(double X, double Y)> { (x1, y1), (x2, y2) }, false,
                            ColorOf(EntityAciIndex(ent), layerAci, byBlockAci), StrokeWidth(ent, scaleMul), dash);
                }
                break;

            case MLine ml:
                {
                    // 多线：按 DXF 41 参数化提取各子线的实际画线段（含画/断模式与开口长短），
                    // 逐段描边细线。MLINE 正确语义=一组平行细线（AutoCAD 2D 渲染永不填充）。
                    double w = StrokeWidth(ent, scaleMul);
                    foreach (var el in MLineDrawnElements(ml, tr))
                    {
                        string col = ColorOf(el.Aci, layerAci, byBlockAci);
                        foreach (var s in el.Segs)
                        {
                            var pts = new List<(double X, double Y)> { (s.X1, s.Y1), (s.X2, s.Y2) };
                            // 画/断由 41 参数决定；线型 dash 不再叠加（避免小段被 dash 切碎）
                            StrokePts(pts, false, col, w, null);
                        }
                    }
                }
                break;

            case Leader ld:
                {
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
                        double alen = ld.TextHeight > 1e-9 ? ld.TextHeight * scaleMul : Math.Max(w * 6.0, 4.0 * (tr?.Scale ?? 1.0));
                        // 引线：起点移到箭头底边中心，避免细线穿入实心三角内部
                        var lp = new List<(double X, double Y)>(pts);
                        if (LeaderArrowBase(pts, alen, out var lbx, out var lby))
                            lp[0] = (lbx, lby);
                        // 水平着陆线（末段非水平时补画，供关联文字坐落）
                        if (LeaderLandingEndpoint(ld, pts, out var lex, out var ley))
                            lp.Add((lex, ley));
                        StrokePts(lp, false, col, w, dash);
                        // 实心箭头三角（AutoCAD LEADER 默认实心；尖端 pts[0] 指向目标端）
                        if (LeaderArrowWings(ld, pts, alen, out var w1x, out var w1y, out var w2x, out var w2y))
                            EmitQuadFillPng(new List<(double X, double Y)> { (pts[0].X, pts[0].Y), (w1x, w1y), (w2x, w2y) }, col);
                    }
                }
                break;

            case Dimension dim:
                {
                    // 标注：递归渲染 dim.Block 内实体（尺寸线/箭头/文字/弧/Insert 均为世界坐标）。
                    // 跳过 Defpoints 图层的参考点（AutoCAD 不显示）。
                    if (dim.Block != null && dim.Block.Entities.Count > 0)
                    {
                        short dimAci = EffectiveAci(ent, byBlockAci);
                        short dimLayerAci = 7;
                        try { dimLayerAci = LayerAciIndex(dim.Layer); } catch { }
                        foreach (var be in dim.Block.Entities)
                        {
                            if (be is Point)
                            {
                                try { if (be.Layer != null && be.Layer.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) continue; } catch { }
                            }
                            EmitEntityPng(be, null, 1.0, dimAci, null, null, ltMul, dimLayerAci);
                        }
                    }
                    else
                        RecordSkip(ent.GetType().Name);
                }
                break;

            case Shape shp:
                {
                    // SHX 形状实体：用 shape 字体库真实字形笔迹渲染（PolyBuilder 曲线 → 画布描边）
                    EmitShapeEntityPng(shp, tr,
                        ColorOf(EntityAciIndex(shp), layerAci, byBlockAci),
                        StrokeWidth(shp, scaleMul));
                }
                break;

            default:
                RecordSkip(ent.GetType().Name);
                break;
        }
    }

    // 点样式（$PDMODE/$PDSIZE）→ PNG 位图，与 SVG/PDF 端逐样式一致（2026-10-16 定稿 + 10-17 缩小一半）：
    //   ① 笔画与直线一致（lw×k，保底 1px 不加粗）；② 框半径 = 点大小值 × 0.5（数据单位）
    //   ③ 方框半宽/圆框半径 = 同一半径；④ 加号/X 半伸出 = 2r（伸出框外一 r）；竖线总长 = r（第 5 列）
    //   位0-2：0=点(实心小圆) 1=不显示 2=加号 3=X 4=竖线；位5(32)=外加圆框；位6(64)=外加方框
    private void EmitPointPng(double cx, double cy, RasterCanvas.Rgb col, double lw)
    {
        int mode = _pdMode;
        int shape = mode & 7;
        bool hasFrame = (mode & 96) != 0;
        if (shape == 1 && !hasFrame) return;       // PDMODE=1：无中心图形也无外框 → 完全无显示
                                                      // 位0=1 但带外框（33/65/97）= 只画外框、无中心图形
        double r = PointGlyphRadius();                // 框半径（数据单位）= 点大小值
        if (!double.IsFinite(r) || r <= 0) return;
        double k = PngK;                              // 每数据单位像素数
        // 笔画与直线一致（像素）不加粗，但保底 1px：ScanFill 按像素中心线(py+0.5)扫描，
        // 亚像素(<1px)描边带可能整体落在两条采样线之间被整段丢弃（sample2004 加号臂消失根因）；
        // 任意长度≥1 的区间必含一条采样线，且符合用户定稿"线段像素为1，点渲染1像素"。
        float w = (float)Math.Max(lw * k, 1.0);
        double arm = r * 2.0;                         // 加号/X 半伸出 = 2r（伸出框外一 r）
        float fx = (float)PngSx(cx), fy = (float)PngSy(cy);
        float x0 = (float)PngSx(cx - r), x1 = (float)PngSx(cx + r);
        float y0 = (float)PngSy(cy - r), y1 = (float)PngSy(cy + r);
        float ax0 = (float)PngSx(cx - arm), ax1 = (float)PngSx(cx + arm);
        float ay0 = (float)PngSy(cy - arm), ay1 = (float)PngSy(cy + arm);
        RasterCanvas.P P(float px, float py) => new RasterCanvas.P(px, py);
        // 外加圆框（位5=32；对话框第 2 行）：48 段折线逼近圆，半径 = 点大小值
        if ((mode & 32) != 0)
        {
            float rad = Math.Abs(x1 - fx);
            int m = Math.Max(16, (int)Math.Ceiling(2 * Math.PI * Math.Max(rad, 1f) / 2.0));
            var pts = new List<RasterCanvas.P>(m + 1);
            for (int i = 0; i <= m; i++)
            {
                double th = 2 * Math.PI * i / m;
                pts.Add(P(fx + (float)(rad * Math.Cos(th)), fy + (float)(rad * Math.Sin(th))));
            }
            _rc.StrokePolyline(pts, true, w, col);
        }
        // 外加方框（位6=64；对话框第 3 行）：半宽 = 点大小值
        if ((mode & 64) != 0)
            _rc.StrokePolyline(new List<RasterCanvas.P> { P(x0, y0), P(x0, y1), P(x1, y1), P(x1, y0) }, true, w, col);
        // 中心图形
        switch (shape)
        {
            case 0: // 点：小实心圆（随框半径适度缩放）
            {
                float rad = (float)Math.Max(w, r * k * 0.15);
                // 背景色光晕环（透明背景无底色，跳过，避免在透明上出现一圈边）
                if (!_transparentBg)
                    _rc.FillDisc(fx, fy, rad + Math.Max(0.5f, rad * 0.3f), _whiteBg ? RasterCanvas.Rgb.White : RasterCanvas.Rgb.Black);
                _rc.FillDisc(fx, fy, rad, col);
                break;
            }
            case 2: // 加号（±2r，伸出框外一 r）
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(ax0, fy), P(ax1, fy) }, false, w, col);
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(fx, ay0), P(fx, ay1) }, false, w, col);
                break;
            case 3: // X（±2r，伸出框外一 r）
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(ax0, ay0), P(ax1, ay1) }, false, w, col);
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(ax0, ay1), P(ax1, ay0) }, false, w, col);
                break;
            case 4: // 竖线（总长 = r；2026-10-16 用户定稿：第 5 列竖线长度为 r（点大小值）；10-17 缩小一半后 = 点大小值/2）
            {
                double vh = r * 0.5;
                _rc.StrokePolyline(new List<RasterCanvas.P> { P(fx, (float)PngSy(cy - vh)), P(fx, (float)PngSy(cy + vh)) }, false, w, col);
                break;
            }
        }
    }

    // SHX Shape 实体 → PNG（EmitShapeEntityPb）。AutoCAD shape 变换：
    //   局部点(字体单位) → 缩放(Size，X 额外×RelativeXScale) → 斜切(Oblique: x+=y·tanθ)
    //   → 旋转(Rotation) → 平移(InsertionPoint)；块内 shape 再整体过块变换 tr。
    // 插入点 = 字形原点(0,0)（与复杂线型 shape 的"包围盒中心对齐"不同）。
    private void EmitShapeEntityPng(Shape shp, Transform2D tr, string col, double w)
    {
        var pb = new PolyBuilder();
        GlyphRec? rec = null;
        var style = shp.ShapeStyle;
        if (style != null && !string.IsNullOrEmpty(style.Filename))
        {
            var font = GetShapeFont(style.Filename);
            if (font != null) rec = GetGlyph(font, style.Filename, shp.ShapeIndex);
        }
        double ipx = shp.InsertionPoint.X, ipy = shp.InsertionPoint.Y;
        if (tr != null) { var a = tr.Apply(ipx, ipy); ipx = a.Item1; ipy = a.Item2; }
        double size = shp.Size > 1e-9 ? shp.Size : 1.0;
        double relX = shp.RelativeXScale > 1e-9 ? shp.RelativeXScale : 1.0;
        double shear = Math.Tan(shp.ObliqueAngle);
        double cr = Math.Cos(shp.Rotation), sr = Math.Sin(shp.Rotation);
        // 字形点(字体单位) → 世界坐标（再经 PngSx/PngSy 映射到像素）
        void Xf(double gx, double gy, out double wx, out double wy)
        {
            double sx = gx * relX * size + gy * size * shear; // X 缩放 + 斜切
            double sy = gy * size;                            // Y 缩放
            double lx = sx * cr - sy * sr;                    // 旋转
            double ly = sx * sr + sy * cr;
            wx = ipx + lx; wy = ipy + ly;
            if (tr != null) { var t = tr.Apply(wx, wy); wx = t.Item1; wy = t.Item2; }
        }
        if (rec == null)
        {
            // 回退"点"：插入点处短实线
            double dd = 0.2 * size;
            Xf(-dd, 0, out double ax, out double ay);
            Xf(dd, 0, out double bx, out double by);
            pb.MoveTo((float)PngSx(ax), (float)PngSy(ay));
            pb.LineTo((float)PngSx(bx), (float)PngSy(by));
        }
        else
        {
            for (int k = 0; k < rec.Type.Count; k++)
            {
                var p = rec.Pts[k];
                switch (rec.Type[k])
                {
                    case 0: Xf(p[0], p[1], out double mx, out double my); pb.MoveTo((float)PngSx(mx), (float)PngSy(my)); break;
                    case 1: Xf(p[0], p[1], out double lx, out double ly); pb.LineTo((float)PngSx(lx), (float)PngSy(ly)); break;
                    case 2:
                        Xf(p[0], p[1], out double qx, out double qy);
                        Xf(p[2], p[3], out double ex, out double ey);
                        pb.QuadTo((float)PngSx(qx), (float)PngSy(qy), (float)PngSx(ex), (float)PngSy(ey));
                        break;
                    case 3:
                        Xf(p[0], p[1], out double c1x, out double c1y);
                        Xf(p[2], p[3], out double c2x, out double c2y);
                        Xf(p[4], p[5], out double ex2, out double ey2);
                        pb.CubicTo((float)PngSx(c1x), (float)PngSy(c1y), (float)PngSx(c2x), (float)PngSy(c2y), (float)PngSx(ex2), (float)PngSy(ey2));
                        break;
                }
            }
        }
        var penCol = HexToRgb(col);
        float pw = (float)Math.Max(0.0, w * PngK);
        foreach (var pl in pb.ToPolylines(false))
            if (pl.Count >= 2) _rc.StrokePolyline(pl, false, pw, penCol);
    }

    // Insert 展开（与 EmitInsertSvg 同逻辑）
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

    // 描边折线（数据单位坐标）。dashed 时手动沿路径走查 dash/gap（相位从路径起点），闭合 + 无虚线 → 干净转角。
    private void StrokePts(List<(double X, double Y)> pts, bool closed, string col, double w, double[] dash)
    {
        if (pts == null || pts.Count < 2 || _rc == null) return;
        var colRgb = HexToRgb(col);
        float widthPx = (float)Math.Max(0.0, w * PngK);
        bool dashed = dash != null && dash.Length > 0;
        if (dashed)
        {
            var cum = BuildCum(pts);
            var spans = WalkDashPx(pts, cum, dash);
            foreach (var sp in spans)
                if (sp.Count >= 2) _rc.StrokePolyline(sp, false, widthPx, colRgb);
        }
        else
        {
            var pp = new List<RasterCanvas.P>(pts.Count);
            foreach (var p in pts) pp.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            _rc.StrokePolyline(pp, closed, widthPx, colRgb);
        }
    }

    // 沿折线走查 [dash, gap, dash, ...]（正长度交替，dash 起头），落笔区间生成像素子折线。
    private List<List<RasterCanvas.P>> WalkDashPx(List<(double X, double Y)> pts, double[] cum, double[] dash)
    {
        var result = new List<List<RasterCanvas.P>>();
        double L = cum[cum.Length - 1];
        if (L <= 1e-9) return result;
        double period = 0;
        foreach (var v in dash) period += v;
        if (period <= 1e-9) return result;
        double pos = 0;
        int i = 0, guard = 0;
        while (pos < L - 1e-9 && guard++ < 50000000)
        {
            int idx = i % dash.Length;
            double span = dash[idx];
            // dash 数组按 [dash, gap, dash, gap, ...] 正长度交替（dash 起头）：
            // 偶数下标=dash 段（落笔）、奇数下标=gap 段（跳过）。
            // 旧实现每段都落笔 → 简单虚线（DASHED 等）在 PNG 下被画成实线。
            if (idx % 2 == 0 && span > 1e-9)
            {
                var sub = new List<RasterCanvas.P>();
                AppendSpanPathPx(sub, pts, cum, pos, Math.Min(pos + span, L));
                if (sub.Count >= 2) result.Add(sub);
            }
            pos += span;
            i++;
        }
        return result;
    }

    // 把弧长区间 [s0, s1] 的子路径追加到像素折线（端点精确插值 + 区间内原始采样顶点）。
    private void AppendSpanPathPx(List<RasterCanvas.P> sub, List<(double X, double Y)> pts, double[] cum, double s0, double s1)
    {
        if (s1 - s0 < 1e-9) return;
        int n = pts.Count;
        double L = cum[n - 1];
        if (s0 < 0) s0 = 0;
        if (s1 > L) s1 = L;
        if (s1 - s0 < 1e-9) return;

        PathPointAt(pts, cum, s0, out double ax, out double ay, out _, out _);
        sub.Add(new RasterCanvas.P((float)PngSx(ax), (float)PngSy(ay)));
        for (int i = 1; i < n; i++)
        {
            double c = cum[i];
            if (c <= s0 + 1e-9 || c >= s1 - 1e-9) continue;
            sub.Add(new RasterCanvas.P((float)PngSx(pts[i].X), (float)PngSy(pts[i].Y)));
        }
        PathPointAt(pts, cum, s1, out double bx, out double by, out _, out _);
        sub.Add(new RasterCanvas.P((float)PngSx(bx), (float)PngSy(by)));
    }

    #endregion

    #region ===== 复杂线型（SHX 真实字形，WalkPattern 同算法）=====

    // 复杂线型（PNG）：沿路径走查模式生成描边路径（dash 子路径 + 字形子路径）。返回 false = 不支持/空。
    private bool TryEmitComplexPng(Entity ent, Transform2D tr, LtpPattern pat, string col, double w)
    {
        if (pat == null) return false;
        if (!TryGetPathPoints(ent, tr, out var pts) || pts.Count < 2) return false;
        var cum = BuildCum(pts);
        if (cum[cum.Length - 1] <= 1e-9) return false;
        var pb = new PolyBuilder();
        var texts = new List<LtpTextPlace>();
        WalkPatternPx(pb, pts, cum, pat, texts);
        if (pb.IsEmpty && texts.Count == 0) return false;
        var colRgb = HexToRgb(col);
        float widthPx = (float)Math.Max(0.0, w * PngK);
        foreach (var pl in pb.ToPolylines(false))
            if (pl.Count >= 2) _rc.StrokePolyline(pl, false, widthPx, colRgb);
        // 文字段：用 TTF 实心字形渲染（rotCanvas 传 -数据系角）
        foreach (var p in texts)
            DrawTextPng(p.X, p.Y, p.Text, p.Height, col, -p.RotDeg, ResolveFontFamily(p.Font), p.WidthFactor);
        return true;
    }

    // 沿路径走查模式（WalkPattern 的位图版）：dash→线子路径；gap→留空；shape→段中点真实 SHX 字形（沿切向）。
    private void WalkPatternPx(PolyBuilder pb, List<(double X, double Y)> pts, double[] cum, LtpPattern pat, List<LtpTextPlace> textOut = null)
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
                AppendSpanPb(pb, pts, cum, pos, Math.Min(pos + span, L));
            }
            else if (seg.Kind == LtpSeg.Shape)
            {
                double at = pos + seg.CenterOffset;
                PathPointAt(pts, cum, at, out double px, out double py, out double tx, out double ty);
                EmitShapeGlyphPb(pb, at, px, py, tx, ty, seg);
            }
            else if (seg.Kind == LtpSeg.Text && textOut != null && !string.IsNullOrEmpty(seg.TextContent))
            {
                double at = pos + seg.CenterOffset;
                PathPointAt(pts, cum, at, out double cx, out double cy, out double tx, out double ty);
                textOut.Add(BuildTextPlace(cx, cy, tx, ty, seg));
            }
            pos += span;
            i++;
        }
    }

    // 把弧长区间 [s0, s1] 的子路径追加到 PolyBuilder。
    private void AppendSpanPb(PolyBuilder pb, List<(double X, double Y)> pts, double[] cum, double s0, double s1)
    {
        if (s1 - s0 < 1e-9) return;
        int n = pts.Count;
        double L = cum[n - 1];
        if (s0 < 0) s0 = 0;
        if (s1 > L) s1 = L;
        if (s1 - s0 < 1e-9) return;
        PathPointAt(pts, cum, s0, out double ax, out double ay, out _, out _);
        pb.MoveTo((float)PngSx(ax), (float)PngSy(ay));
        for (int i = 1; i < n; i++)
        {
            double c = cum[i];
            if (c <= s0 + 1e-9 || c >= s1 - 1e-9) continue;
            pb.LineTo((float)PngSx(pts[i].X), (float)PngSy(pts[i].Y));
        }
        PathPointAt(pts, cum, s1, out double bx, out double by, out _, out _);
        pb.LineTo((float)PngSx(bx), (float)PngSy(by));
    }

    // 在路径点绘制真实 SHX 字形（追加到同一描边路径；EmitShapeGlyph 的位图版）。
    private void EmitShapeGlyphPb(PolyBuilder pb, double at, double px, double py, double tx, double ty, LtpSeg seg)
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
            // SHX 库/形状不可用 → 回退"点"（沿切向居中的短实线），保持图案节奏
            double dd = seg.Len > 1e-9 ? Math.Min(seg.ShapeScale * 4.0, 0.9 * seg.Len) : 0.1;
            if (dd < 0.05) dd = 0.05;
            pb.MoveTo((float)PngSx(px - tx * dd / 2), (float)PngSy(py - ty * dd / 2));
            pb.LineTo((float)PngSx(px + tx * dd / 2), (float)PngSy(py + ty * dd / 2));
            return;
        }
        // shape 缩放 = 组码46 标称缩放（已含 $LTSCALE 全局线型缩放，见 GetActiveLineType→pat.Scaled）。
        // 不额外硬编码 0.5——该缩放由图纸 header $LTSCALE 决定，链路已正确应用。
        double S = seg.ShapeScale > 1e-9 ? seg.ShapeScale : 1.0;
        double ang;
        switch (seg.RotType)
        {
            // relative（74 bit0=0）：字形角 = 切向角 + 组码50 偏移。漏加 ShapeRot 会让
            // "180° 交替"线型（BATTING 弹簧）两个 shape 同向画成锯齿。
            case 1: ang = Math.Atan2(ty, tx) + seg.ShapeRot * Math.PI / 180.0; break;
            case 2: ang = seg.ShapeRot * Math.PI / 180.0; break; // absolute（74 bit0=1）
            default: ang = 0; break;                   // upright
        }
        double ca = Math.Cos(ang), sa = Math.Sin(ang);
        // offset（DXF 44/45）：字形局部 +X 与路径行进方向相反 → 切向分量取负，法向分量取 OY。
        double oxw = -seg.OX * tx - seg.OY * ty;
        double oyw = -seg.OX * ty + seg.OY * tx;
        // 【关键】按字形原点(0,0)锚定到"路径点+offset"（DXF 线型 shape 插入点语义），非包围盒中心。
        // 与 SVG/PDF 的 EmitShapeGlyph 保持一致（BATTING 实测：原点锚定使 0°/180° 两弧分居基线两侧→
        // 螺旋弹簧/环内对角弦；按包围盒中心锚定则弧心落基线→同心圆相切，错误）。
        void Xf(float gx, float gy, out double mx, out double my)
        {
            double lx = gx * S;   // 原点锚定
            double ly = gy * S;
            double wx = px + oxw + lx * ca - ly * sa;
            double wy = py + oyw + lx * sa + ly * ca;
            mx = wx; my = wy;
        }
        for (int k = 0; k < rec.Type.Count; k++)
        {
            var p = rec.Pts[k];
            switch (rec.Type[k])
            {
                case 0:
                    Xf(p[0], p[1], out double mx, out double my);
                    pb.MoveTo((float)PngSx(mx), (float)PngSy(my));
                    break;
                case 1:
                    Xf(p[0], p[1], out double lx, out double ly);
                    pb.LineTo((float)PngSx(lx), (float)PngSy(ly));
                    break;
                case 2:
                    Xf(p[0], p[1], out double qx, out double qy);
                    Xf(p[2], p[3], out double ex, out double ey);
                    pb.QuadTo((float)PngSx(qx), (float)PngSy(qy), (float)PngSx(ex), (float)PngSy(ey));
                    break;
                case 3:
                    Xf(p[0], p[1], out double c1x, out double c1y);
                    Xf(p[2], p[3], out double c2x, out double c2y);
                    Xf(p[4], p[5], out double ex2, out double ey2);
                    pb.CubicTo((float)PngSx(c1x), (float)PngSy(c1y), (float)PngSx(c2x), (float)PngSy(c2y),
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
        string styleBig = GetStyleBigFont(mt.Style);
        var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height, styleFont, styleBig), mt.RectangleWidth, styleFont, styleBig);
        if (layout.Lines.Count == 0) return;
        var (baselines, anchorX, anchorY) = MTextLocalLayout(layout, mt);

        double rot = mt.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0);
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double px = mt.InsertPoint.X, py = mt.InsertPoint.Y;
        if (tr != null) { var ap = tr.Apply(px, py); px = ap.Item1; py = ap.Item2; }

        for (int li = 0; li < layout.Lines.Count; li++)
        {
            double x = layout.Lines[li].FirstIndent * scaleMul; // 段落首行缩进（\pi）
            foreach (var run in layout.Lines[li].Runs)
            {
                double rw = MTextRunWidth(run, styleFont, styleBig) * scaleMul;
                if (run.IsFraction)
                {
                    // \S<num>/<den> 分数：分子/分母上下分层 + 中间分数线（与 SVG 端一致）
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    double fs = Math.Max(run.Height * scaleMul, 0.2);
                    string fam = ResolveFontFamily(!string.IsNullOrEmpty(run.Font) ? run.Font : styleFont);
                    int sl = run.Text.IndexOf('/');
                    string num = run.Text.Substring(0, sl);
                    string den = sl + 1 < run.Text.Length ? run.Text.Substring(sl + 1) : "";
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                    double numW = EstimateTextWidth(num, run.Height) * wf * scaleMul;
                    double denW = EstimateTextWidth(den, run.Height) * wf * scaleMul;
                    double fracW = Math.Max(numW, denW);
                    // 分数堆叠几何（CAD y 向上，相对主基线；与 SVG 端同一套 AutoCAD 实测值）
                    double H = run.Height;
                    double numOff = 0.72 * H;   // 分子基线：主基线上方
                    double denOff = 0.30 * H;   // 分母基线：主基线下方
                    double barOff = 0.58 * H;   // 分数线：主基线上方
                    double ob = EffOblique(run);
                    double numX = x + (fracW - numW) / 2.0;
                    double denX = x + (fracW - denW) / 2.0;
                    void LocToWorld(double lx, double ly, out double wx, out double wy)
                    {
                        double dx = (lx - anchorX) * scaleMul, dy = (ly - anchorY) * scaleMul;
                        wx = px + dx * ca - dy * sa; wy = py + dx * sa + dy * ca;
                    }
                    if (!string.IsNullOrEmpty(num))
                    {
                        LocToWorld(numX, baselines[li] + numOff, out double wnx, out double wny);
                        DrawTextPng(wnx, wny, num, fs, col, -rot, fam, run.WidthFactor, run.Bold, run.Italic, ob, false, false, numW);
                    }
                    if (!string.IsNullOrEmpty(den))
                    {
                        LocToWorld(denX, baselines[li] - denOff, out double wdx, out double wdy);
                        DrawTextPng(wdx, wdy, den, fs, col, -rot, fam, run.WidthFactor, run.Bold, run.Italic, ob, false, false, denW);
                    }
                    if (_rc != null && fracW > 1e-6)
                    {
                        LocToWorld(x, baselines[li] + barOff, out double h1x, out double h1y);
                        LocToWorld(x + fracW, baselines[li] + barOff, out double h2x, out double h2y);
                        var pp = new List<RasterCanvas.P>
                        {
                            new RasterCanvas.P((float)PngSx(h1x), (float)PngSy(h1y)),
                            new RasterCanvas.P((float)PngSx(h2x), (float)PngSy(h2y))
                        };
                        _rc.StrokePolyline(pp, false, (float)Math.Max(1.0, fs * PngK * 0.04), HexToRgb(col));
                    }
                }
                else if (!string.IsNullOrEmpty(run.Text))
                {
                    string col = run.Color.HasValue ? AciColor(run.Color.Value) : baseCol;
                    double fs = Math.Max(run.Height * scaleMul, 0.2);
                    string effFont = !string.IsNullOrEmpty(run.Font) ? run.Font : styleFont;
                    string effBig = string.IsNullOrEmpty(run.Font) ? styleBig : null;
                    double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                    double baseLineY = baselines[li] + run.BaselineShift * run.Height;
                    double dx = (x - anchorX) * scaleMul;
                    // \S 堆叠：基线按 BaselineShift（em）上下移（与 SVG 端一致）
                    double dy = (baseLineY - anchorY) * scaleMul;
                    double ox = px + dx * ca - dy * sa;
                    double oy = py + dx * sa + dy * ca;
                    // SHX 笔迹优先（simplex.shx 等）：真实 SHX 字形描边，字高随 em 自然（不再经 SimSun 压缩→偏小）
                    if (TryBuildMTextShxLocal(effFont, effBig, run.Text, run.Height, wf, out List<int> st, out List<double[]> sp, out _))
                    {
                        DrawShxRunPng(ox, oy, st, sp, run.Height, wf, fs, -rot, EffOblique(run), col);
                    }
                    else
                    {
                        // 回退 TTF（含 CJK 无 bigfont / 内联 TTF / 字形缺失）→ 原 DrawTextPng（行为不变）
                        string fam = ResolveFontFamily(effFont);
                        DrawTextPng(ox, oy, run.Text, fs, col, -rot, fam, wf,
                            run.Bold, run.Italic, EffOblique(run), run.Underline, run.Overline, rw);
                    }
                }
                x += rw;
            }
        }
    }

    // 基线文字绘制（画布坐标系）。
    //   (x,y)        = 基线左端点（数据坐标，TextBaseline/MText 已算好）
    //   fsData       = 字高（数据单位，含 scaleMul）
    //   rotCanvas    = 画布系旋转角（度；数据系 CCW 角 θ 传 −θ：画布 y 向下，正角=视觉顺时针）
    //   widthFactor  = 字宽因子（沿文字自身基线方向压缩，先缩后转）
    //   oblique      = 倾斜角（度，CAD \Q 语义：正=上端右倾）
    // 字体解析：PdfFontManager.FindTtf（族名→TTF/TTC 文件）→ TrueTypeFont 取字形轮廓，纯托管填充。
    private void DrawTextPng(double x, double y, string content, double fsData, string colHex, double rotCanvas,
        string family, double widthFactor, bool bold = false, bool italic = false, double oblique = 0.0,
        bool underline = false, bool overline = false, double runWidthData = 0.0)
    {
        if (string.IsNullOrEmpty(content) || _rc == null) return;
        double sx = PngSx(x), sy = PngSy(y);
        float fs = (float)(fsData * PngK);
        if (fs <= 0) return;

        var fr = PickFont(family, content);
        if (fr == null) return; // 无任何可用 TTF → 跳过（不劣化）

        double upm = fr.UnitsPerEm;
        double wf = widthFactor > 1e-9 ? widthFactor : 1.0;
        double tanA = Math.Tan(-oblique * Math.PI / 180.0); // SVG skewX(−oblique)
        double th = rotCanvas * Math.PI / 180.0;
        double cosT = Math.Cos(th), sinT = Math.Sin(th);
        var col = HexToRgb(colHex);

        double penAdv = 0; // 累计 advance（em 单位，未乘 wf）
        for (int ci = 0; ci < content.Length; ci++)
        {
            char ch = content[ci];
            int gi = fr.GetGlyphIndex(ch);
            if (gi != 0)
            {
                var sink = new GlyphSink();
                // gi 是字形索引（glyph index），必须显式 (ushort) 命中 TryGetGlyph(ushort) 重载。
                // 若传 int 会选中 TryGetGlyph(int codepoint) 重载，内部把 gi 当 Unicode 码点再查一次
                // cmap → 取错字形（多个字符 gi 作码点映射到同一/默认 glyph）→ 文字乱码/竖条纹。
                fr.TryGetGlyph((ushort)gi, sink);
                // 同一字形的所有轮廓（外环 + 字怀内环）收集后【一次性】Fill(evenOdd=多轮廓)：
                // 逐轮廓单独 Fill(evenOdd:false) 会把字怀内环也独立填实 → 中间空洞粘连（O/A/0/8/9/6 的孔被堵）。
                // 奇偶规则下内环挖空，与 HATCH 实心路径（evenOdd = contours.Count>1）一致。
                var glyphContours = new List<List<RasterCanvas.P>>();
                foreach (var contour in sink.Contours)
                {
                    if (contour.Count < 3) continue;
                    var px = new List<RasterCanvas.P>(contour.Count + 1);
                    foreach (var fp in contour)
                    {
                        double ex = fp.X / upm + penAdv;
                        double ey = fp.Y / upm;
                        double Lx = ex * fs * wf;
                        double Ly = -ey * fs;          // 翻转 y 到屏幕向下
                        Lx += tanA * Ly;              // 倾斜
                        double Xr = Lx * cosT - Ly * sinT; // 旋转
                        double Yr = Lx * sinT + Ly * cosT;
                        px.Add(new RasterCanvas.P((float)(sx + Xr), (float)(sy + Yr)));
                    }
                    if (px.Count >= 3)
                    {
                        px.Add(px[0]); // 闭合轮廓
                        glyphContours.Add(px);
                    }
                }
                if (glyphContours.Count > 0)
                    _rc.Fill(glyphContours, glyphContours.Count > 1, col);
            }
            // (int) 强转强制走 GetAdvanceWidth(int codepoint) 重载：传 char 时 C# 优先绑定
            // GetAdvanceWidth(ushort glyphIndex)，把码点当字形索引 → 中文（U+4E00+）越界返回 0，
            // penAdv 不推进 → 后续字符叠上前字符（"文字重叠"根因）。英文码点小恰好未暴露。
            double adv = fr.GetAdvanceWidth((int)ch) / upm;
            // penAdv 只累加原始 advance（em），不再乘 wf：
            // Lx=(fp.X/upm+penAdv)×fs×wf 已统一乘一次 wf——若 penAdv 再乘 wf 会变成 wf²，
            // 使 \W 宽字距文本第 2 字起被右推（advance 放大 wf 倍），中文出现异常空隙。
            penAdv += adv;
        }

        // 下/上划线：沿文字方向（旋转后的局部 x 轴），长度 = run 宽（数据单位 × PngK）
        if ((underline || overline) && runWidthData > 0)
        {
            float oy = underline ? (float)(0.10 * fs) : (float)(-0.85 * fs);
            double dirx = cosT, diry = sinT;
            double offx = -oy * sinT, offy = oy * cosT;
            double len = runWidthData * PngK;
            double x0 = sx + offx, y0 = sy + offy;
            double x1 = x0 + dirx * len, y1 = y0 + diry * len;
            float wline = (float)Math.Max(1f, fs * 0.05f);
            var pp = new List<RasterCanvas.P> { new RasterCanvas.P((float)x0, (float)y0), new RasterCanvas.P((float)x1, (float)y1) };
            _rc.StrokePolyline(pp, false, wline, col);
        }
    }

    // SHX 笔迹 → 画布描边（与 DrawTextPng 同一屏幕变换：wf 压缩 / tanA 倾斜 / rotCanvas 旋转）。
    // types/pts = TryBuildMTextShxLocal 输出（基线原点、数据单位、y 上，不含 wf）；H=字高（数据单位，1 em）。
    // 用于 MTEXT simplex.shx 等 SHX 基础样式：真实 SHX 字形描边，字高随 em 自然（不再经 SimSun 压缩→偏小）。
    private void DrawShxRunPng(double ox, double oy, List<int> types, List<double[]> pts, double H, double wf,
        double fsData, double rotCanvas, double oblique, string colHex)
    {
        if (_rc == null || pts.Count == 0) return;
        float originSx = (float)PngSx(ox), originSy = (float)PngSy(oy);
        float fs = (float)(fsData * PngK);
        if (fs <= 0) return;
        double wf2 = wf > 1e-9 ? wf : 1.0;
        double tanA = Math.Tan(-oblique * Math.PI / 180.0); // 与 DrawTextPng 同（skewX(−oblique)）
        double th = rotCanvas * Math.PI / 180.0;
        double cosT = Math.Cos(th), sinT = Math.Sin(th);
        var col = HexToRgb(colHex);
        float penW = (float)Math.Max(fs * 0.05, 0.2);

        // 局部点（基线原点、数据单位、y 上）→ 画布像素（与 DrawTextPng 同：ex_em=lx/H，ey_em=ly/H）
        void ToScr(double lx, double ly, out float X, out float Y)
        {
            double ex = lx / H, ey = ly / H;
            double Lx = ex * fs * wf2;
            double Ly = -ey * fs;
            Lx += tanA * Ly; // 倾斜（SVG skewX(−oblique) 经 Y 翻转等价）
            double Xr = Lx * cosT - Ly * sinT;
            double Yr = Lx * sinT + Ly * cosT;
            X = originSx + (float)Xr;
            Y = originSy + (float)Yr;
        }

        var cur = new List<RasterCanvas.P>();
        for (int k = 0; k < types.Count; k++)
        {
            var p = pts[k];
            switch (types[k])
            {
                case 0: // Move：抬笔 → 新子路径
                    if (cur.Count >= 2) _rc.StrokePolyline(cur, false, penW, col);
                    ToScr(p[0], p[1], out float mx, out float my);
                    cur = new List<RasterCanvas.P> { new RasterCanvas.P(mx, my) };
                    break;
                case 1: // Line
                    ToScr(p[0], p[1], out float lx, out float ly);
                    cur.Add(new RasterCanvas.P(lx, ly));
                    break;
                case 2: // Quad（控制, 终点）
                    ToScr(p[0], p[1], out float qcx, out float qcy);
                    ToScr(p[2], p[3], out float qex, out float qey);
                    if (cur.Count > 0)
                    {
                        var s = cur[cur.Count - 1];
                        RasterCanvas.FlattenQuad(s.X, s.Y, qcx, qcy, qex, qey, cur);
                    }
                    break;
                case 3: // Cubic（控制1, 控制2, 终点）
                    ToScr(p[0], p[1], out float c1x, out float c1y);
                    ToScr(p[2], p[3], out float c2x, out float c2y);
                    ToScr(p[4], p[5], out float e1x, out float e1y);
                    if (cur.Count > 0)
                    {
                        var s = cur[cur.Count - 1];
                        RasterCanvas.FlattenCubic(s.X, s.Y, c1x, c1y, c2x, c2y, e1x, e1y, cur);
                    }
                    break;
            }
        }
        if (cur.Count >= 2) _rc.StrokePolyline(cur, false, penW, col);
    }

    // 族名 → 可用 TrueTypeFont（纯托管）。先按 family（CSS 列表支持）尝试，含 CJK 时追加常用 CJK 兜底，
    // 选取能覆盖文本全部码位的字体；均不可用返回 null（调用方跳过文字）。
    private static readonly Dictionary<string, TrueTypeFont> _ttfCache = new(StringComparer.OrdinalIgnoreCase);
    private static TrueTypeFont PickFont(string family, string text)
    {
        var fams = new List<string>();
        if (!string.IsNullOrEmpty(family))
            foreach (var p in family.Split(','))
            {
                string f = p.Trim();
                if (f.Length == 0 || f == "serif" || f == "sans-serif" || f == "monospace") continue;
                fams.Add(f);
            }
        if (fams.Count == 0) fams.Add("Microsoft YaHei");
        // 常用 CJK 兜底（覆盖校验失败回退）
        foreach (var c in new[] { "Microsoft YaHei", "SimSun", "SimHei", "DengXian", "KaiTi", "FangSong" })
            if (!fams.Contains(c)) fams.Add(c);

        TrueTypeFont first = null;
        foreach (var f in fams)
        {
            if (_ttfCache.TryGetValue(f, out var cached))
            {
                if (cached == null) continue;        // 已知缺失
                if (Covers(cached, text)) return cached;
                if (first == null) first = cached;
                continue;
            }
            string path = PdfFontManager.FindTtf(f);
            if (string.IsNullOrEmpty(path)) { _ttfCache[f] = null; continue; }
            var tf = TrueTypeFont.LoadFromFile(path);
            if (tf == null) { _ttfCache[f] = null; continue; }
            _ttfCache[f] = tf;
            if (Covers(tf, text)) return tf;
            if (first == null) first = tf;
        }
        return first;
    }

    private static bool Covers(TrueTypeFont tf, string text)
    {
        foreach (var ch in text)
        {
            if (ch < 0x20) continue;
            if (tf.GetGlyphIndex(ch) == 0) return false;
        }
        return true;
    }

    // IGlyphSink 收集器：把字形轮廓命令收为若干闭合轮廓（字体单位）。
    private sealed class GlyphSink : IGlyphSink
    {
        public readonly List<List<RasterCanvas.P>> Contours = new();
        private List<RasterCanvas.P> _cur;
        public void MoveTo(float x, float y) { _cur = new List<RasterCanvas.P>(); Contours.Add(_cur); _cur.Add(new RasterCanvas.P(x, y)); }
        public void LineTo(float x, float y) { _cur?.Add(new RasterCanvas.P(x, y)); }
        public void QuadTo(float cx, float cy, float x, float y)
        {
            if (_cur == null || _cur.Count == 0) return;
            var s = _cur[_cur.Count - 1];
            RasterCanvas.FlattenQuad(s.X, s.Y, cx, cy, x, y, _cur);
        }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            if (_cur == null || _cur.Count == 0) return;
            var s = _cur[_cur.Count - 1];
            RasterCanvas.FlattenCubic(s.X, s.Y, c1x, c1y, c2x, c2y, x, y, _cur);
        }
        public void Close() { /* 字形轮廓本就闭合，无需额外操作 */ }
    }

    #endregion

    #region ===== 填充（四边形 / HATCH 实心/渐变/图案）=====

    // 四边形填充（Solid/Face3D；填充 + 0.15 宽同色描边防 AA 缝隙）
    private void EmitQuadFillPng(List<(double X, double Y)> pts, string color)
    {
        if (pts == null || pts.Count < 3 || _rc == null) return;
        var contour = new List<RasterCanvas.P>(pts.Count);
        foreach (var p in pts) contour.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
        var col = HexToRgb(color);
        _rc.Fill(new List<List<RasterCanvas.P>> { contour }, false, col);
        float w = (float)Math.Max(0.05, 0.15 * PngK);
        _rc.StrokePolyline(contour, true, w, col);
    }

    // HATCH → PNG（实心=实体色/真色；渐变=线性/径向双端插值；图案=裁剪线段描边）
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
        var contours = new List<List<RasterCanvas.P>>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            var c = new List<RasterCanvas.P>(loop.Count);
            foreach (var p in loop)
            {
                float fx = (float)PngSx(p.X), fy = (float)PngSy(p.Y);
                c.Add(new RasterCanvas.P(fx, fy));
                if (fx < minX) minX = fx; if (fy < minY) minY = fy;
                if (fx > maxX) maxX = fx; if (fy > maxY) maxY = fy;
            }
            contours.Add(c);
        }
        if (contours.Count == 0) { RecordSkip("HATCH(空)"); return; }
        bool evenOdd = contours.Count > 1;
        if (gradient)
        {
            // 接入共享 9 类型 LUT 模型：minX/Y 已是像素单位（PngSx/PngSy），画布系 Y 翻转→uy 取反、yDown=true。
            var spec = MakeHatchGradSpec(gp);
            double ang = gp.Angle;
            double ux = Math.Cos(ang), uy = -Math.Sin(ang);
            var geom = ComputeHatchGradGeom(spec, minX, minY, maxX, maxY, ux, uy, true);
            var c1 = HexToRgb(StopHex(spec.C1));
            var c2 = HexToRgb(StopHex(spec.C2));
            var prof = new Func<float, float>(u => (float)HatchGradProfileLut(spec, u));
            if (geom.Radial)
                _rc.FillGradientProfile(contours, evenOdd, new RasterCanvas.P(0, 0), new RasterCanvas.P(0, 0), c1, c2, true, (float)geom.Fx, (float)geom.Fy, (float)geom.R, prof);
            else
                _rc.FillGradientProfile(contours, evenOdd,
                    new RasterCanvas.P((float)geom.Px0, (float)geom.Py0),
                    new RasterCanvas.P((float)geom.Px1, (float)geom.Py1),
                    c1, c2, false, 0, 0, 0, prof);
        }
        else
        {
            _rc.Fill(contours, evenOdd, HexToRgb(HatchSolidFillHex(ht, layerAci, byBlockAci)));
        }
    }

    // 图案 HATCH（EmitHatchPatternSvg 的位图版：BuildPatternSegments 共享几何 → 描边）
    private void EmitHatchPatternPng(Hatch ht, List<List<XY>> loops, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var segs = BuildPatternSegments(loops, ht, tr, MaxPatternLinesPerHatch);
        if (segs.Count == 0) { RecordSkip("HATCH(图案)"); return; }
        string col = HatchSolidFillHex(ht, layerAci, byBlockAci);
        double w = Math.Max(0.03, 0.1 * scaleMul);
        var colRgb = HexToRgb(col);
        float widthPx = (float)(w * PngK);
        foreach (var s in segs)
        {
            var pp = new List<RasterCanvas.P> {
                new RasterCanvas.P((float)PngSx(s.X1), (float)PngSy(s.Y1)),
                new RasterCanvas.P((float)PngSx(s.X2), (float)PngSy(s.Y2)) };
            _rc.StrokePolyline(pp, false, widthPx, colRgb);
        }
    }

    #endregion

    #region ===== 变宽折线（填充多边形，EmitTaperedPolyline 的位图版）=====

    private void EmitTaperedPng(LwPolyline pl, Transform2D tr, string stroke, double scaleMul)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        if (n < 2 || _rc == null) return;
        // 逐顶点有效宽度：无 bulge 弦偏移路径用（顶点 i 宽 = 起宽40，为0时继承入段终宽41）
        double[] w = TaperedVertexWidths(pl);
        // 逐段起/终点宽：含 bulge 弧偏移路径用（段 i 从 40_i 渐变到 41_i，段间顶点收尖）
        var (sw, ew) = TaperedSegmentWidths(pl);

        List<(double X, double Y)> left, right;
        var taper = TryBuildTaperOffset(pl, sw, ew, tr, _opt.TaperOffsetBuilder);
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

        var col = HexToRgb(stroke);
        var contours = new List<List<RasterCanvas.P>>();
        int m = left.Count;
        if (m < 2) return;
        if (pl.IsClosed)
        {
            // 闭合：left/right 各一条封闭偏移轮廓（内/外环），even-odd 填充得"环"
            var cl = new List<RasterCanvas.P>(m); foreach (var p in left) cl.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            var cr = new List<RasterCanvas.P>(m); foreach (var p in right) cr.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            contours.Add(cl); contours.Add(cr);
            _rc.Fill(contours, true, col);
            float wd = (float)Math.Max(0.05, 0.1 * PngK);
            _rc.StrokePolyline(cl, true, wd, col);
            _rc.StrokePolyline(cr, true, wd, col);
        }
        else
        {
            // 开放：left 正向 + right 反向，两端闭合填充（带/楔形）
            var c = new List<RasterCanvas.P>(2 * m);
            foreach (var p in left) c.Add(new RasterCanvas.P((float)PngSx(p.X), (float)PngSy(p.Y)));
            for (int i = m - 1; i >= 0; i--) c.Add(new RasterCanvas.P((float)PngSx(right[i].X), (float)PngSy(right[i].Y)));
            contours.Add(c);
            _rc.Fill(contours, false, col);
            float wd = (float)Math.Max(0.05, 0.1 * PngK);
            _rc.StrokePolyline(c, true, wd, col);
        }
    }

    #endregion
}
