using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ACadSharp.Formats.Pdf;

namespace ACadSharp.Formats.Image;

/// <summary>图像引擎 PDF 部分（与 FullImageEngine.Svg.cs 中的 SVG 部分共享同一 partial 类）。</summary>
internal partial class ImageEngine
{
    // ===== PDF 状态 =====
    private PdfFontManager _pdfFonts;
    private Dictionary<int, (int ShadingType, string Coords, string C0, string C1)> _pdfShadings;
    private int _pdfShadingSeq;
    private const int MaxPdfShadings = 64;

    #region ===== 导出 PDF =====
    public ImageExportResult WritePdf(string outPath)
    {
        try
        {
            if (!ComputeExtents())
            {
                _log("⚠️  PDF 无可用实体，跳过");
                return new ImageExportResult { Success = false };
            }

            _scale = 1.0;
            double dataW = _maxX - _minX, dataH = _maxY - _minY;
            _margin = Math.Max(dataW, dataH) * 0.02 + 1.0;
            if (!double.IsFinite(_margin) || _margin <= 0) _margin = 2.0;
            _w = dataW + 2 * _margin;
            _h = dataH + 2 * _margin;

            // 加载字体管理（默认 SimHei + 按样式/内联 \f 按需加载其他 TTF，分别内嵌）
            _pdfFonts = new PdfFontManager(_log);
            BuildLinetypeDash();

            // HATCH 渐变 shading 状态（对象号区从字体区之后顺排；内容流生成时逐个登记）
            _pdfShadings = new Dictionary<int, (int ShadingType, string Coords, string C0, string C1)>();
            _pdfShadingSeq = 0;
            _skippedCounts.Clear(); // P2：每格式单独计数，日志不跨格式累计

            // 1) 构建内容流（字节级：文字为 2 字节 GID，可能含任意二进制字节）
            var cs = new MemoryStream();
            // 输出放大：内容流前置 CTM，几何/线宽/虚线/文字/HATCH 渐变统一 ×OutputZoom
            CsAscii(cs, F(_opt.OutputZoom) + " 0 0 " + F(_opt.OutputZoom) + " 0 0 cm\n");
            // 背景（默认黑色；WhiteBackground=true 时白色；TransparentBackground=true 时省略背景填充=透明页）
            if (!_transparentBg)
                CsAscii(cs, (_whiteBg ? "1 1 1" : "0 0 0") + " rg\n0 0 " + F(_w) + " " + F(_h) + " re f\n");
            foreach (var ent in AllEntities())
                EmitEntityPdf(cs, ent, null, 1.0, 7, null, null, _ltScale); // 顶层无 INSERT 上下文，ByBlock 兜底白/实线
            byte[] content = cs.ToArray();

            // R20b：字体对象/资源表必须在内容流生成【之后】定稿——字体（如宋体 SimSun）是内容流生成
            // 过程中经 GetSlot 按需加载的，提前按 _pdfFonts.Count 定稿会漏掉它们：内容流引用了 /F2，
            // 但 /Resources 与字体对象区只写了 /F1 → 查看器代用 Latin 字体逐字节渲染 SimSun GID
            // → 中文乱码（test1234.pdf 图框）。对象号分配规则不变：
            // 每个字体占 5 个对象（Type0/CIDFontType2/FontDescriptor/ToUnicode/FontFile2），
            // 槽位 i（1-based）→ 对象号 5 + (i-1)*5；shading 区紧随字体区之后。
            int numFonts = _pdfFonts.HasFont ? _pdfFonts.Count : 1;
            var fontRes = new StringBuilder();
            for (int i = 1; i <= numFonts; i++)
                fontRes.Append("/F" + i + " " + (5 + (i - 1) * 5) + " 0 R ");
            string fontResStr = fontRes.ToString().Trim();
            int shadingBase = _pdfFonts.HasFont ? (5 + numFonts * 5) : 6;

            // 2) 组装 PDF 文件（记录每个对象真实偏移，生成合法 xref/startxref）
            var file = new MemoryStream();
            var offsets = new Dictionary<int, long>();
            void PutAscii(string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                file.Write(b, 0, b.Length);
            }
            void PutObj(int num, byte[] body)
            {
                offsets[num] = file.Position;
                PutAscii(num + " 0 obj\n");
                file.Write(body, 0, body.Length);
                PutAscii("\nendobj\n");
            }

            PutAscii("%PDF-1.4\n");
            var binHeader = new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }; // 高字节标记（推荐，提示查看器内容为二进制）
            file.Write(binHeader, 0, binHeader.Length);

            // HATCH 渐变 shading 对象（在对象 3 之前写出，供 Page /Shading 资源引用）
            var shadingRes = new StringBuilder();
            foreach (var kv in _pdfShadings.OrderBy(p => p.Key))
            {
                int shObj = shadingBase + kv.Key * 2;
                int fnObj = shObj + 1;
                shadingRes.Append("/Sh" + kv.Key + " " + shObj + " 0 R ");
                PutObj(shObj, Encoding.ASCII.GetBytes("<< /Type /Shading /ShadingType " + kv.Value.ShadingType +
                                                    " /Coords [" + kv.Value.Coords + "] /Function " + fnObj + " 0 R /ColorSpace /DeviceRGB /Extend [true true] >>"));
                PutObj(fnObj, Encoding.ASCII.GetBytes("<< /Type /Function /FunctionType 2 /Domain [0 1] /C0 [" + kv.Value.C0 +
                                                    "] /C1 [" + kv.Value.C1 + "] /Encode [0 1] >>"));
            }

            // 对象 1 Catalog / 2 Pages / 3 Page
            PutObj(1, Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"));
            PutObj(2, Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));
            PutObj(3, Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(_w * _opt.OutputZoom) + " " + F(_h * _opt.OutputZoom) +
                                             "] /Resources << /Font << " + fontResStr + " >>" +
                                              (shadingRes.Length > 0 ? " /Shading << " + shadingRes.ToString().Trim() + " >>" : "") +
                                              " >> /Contents 4 0 R >>"));

            // 对象 4 内容流
            {
                var head = Encoding.ASCII.GetBytes("<< /Length " + content.Length + " >>\nstream\n");
                var tail = Encoding.ASCII.GetBytes("\nendstream");
                var body = new byte[head.Length + content.Length + tail.Length];
                Buffer.BlockCopy(head, 0, body, 0, head.Length);
                Buffer.BlockCopy(content, 0, body, head.Length, content.Length);
                Buffer.BlockCopy(tail, 0, body, head.Length + content.Length, tail.Length);
                PutObj(4, body);
            }

            if (_pdfFonts.HasFont)
            {
                for (int i = 1; i <= numFonts; i++)
                {
                    CjkFont f = _pdfFonts.FontAt(i);
                    int type0 = 5 + (i - 1) * 5;
                    int cid = type0 + 1;
                    int desc = type0 + 2;
                    int touni = type0 + 3;
                    int file2 = type0 + 4;
                    // Type0 字体（Identity-H）
                    PutObj(type0, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type0 /BaseFont /" + f.FontName +
                                                          " /Encoding /Identity-H /DescendantFonts [" + cid + " 0 R] /ToUnicode " + touni + " 0 R >>"));
                    // CIDFontType2（/W 显式给出所用 GID 字宽，避免查看器使用字体文件内损坏的 hmtx）
                    PutObj(cid, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /" + f.FontName +
                                                          " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
                                                          " /W [" + f.WidthArray() + "]" +
                                                          " /FontDescriptor " + desc + " 0 R /DW 1000 >>"));
                    // FontDescriptor
                    PutObj(desc, Encoding.ASCII.GetBytes(f.DescriptorDict(file2)));
                    // ToUnicode CMap（GID → Unicode，供文本提取/搜索）。
                    // 必须是合法流对象（<< /Length N >> stream ... endstream），否则查看器
                    // 无法解析 CMap，PDF 文本复制/搜索全部失效（渲染不受影响）。
                    {
                        byte[] cmap = CjkFont.BuildCMapStream(f.UsedGidToUnicode);
                        var head = Encoding.ASCII.GetBytes("<< /Length " + cmap.Length + " >>\nstream\n");
                        var tail = Encoding.ASCII.GetBytes("\nendstream");
                        var body = new byte[head.Length + cmap.Length + tail.Length];
                        Buffer.BlockCopy(head, 0, body, 0, head.Length);
                        Buffer.BlockCopy(cmap, 0, body, head.Length, cmap.Length);
                        Buffer.BlockCopy(tail, 0, body, head.Length + cmap.Length, tail.Length);
                        PutObj(touni, body);
                    }
                    // FontFile2（TTF 原始字节）
                    {
                        var head = Encoding.ASCII.GetBytes("<< /Length " + f.TtfBytes.Length + " /Length1 " + f.TtfBytes.Length + " >>\nstream\n");
                        var tail = Encoding.ASCII.GetBytes("\nendstream");
                        var body = new byte[head.Length + f.TtfBytes.Length + tail.Length];
                        Buffer.BlockCopy(head, 0, body, 0, head.Length);
                        Buffer.BlockCopy(f.TtfBytes, 0, body, head.Length, f.TtfBytes.Length);
                        Buffer.BlockCopy(tail, 0, body, head.Length + f.TtfBytes.Length, tail.Length);
                        PutObj(file2, body);
                    }
                }
            }
            else
            {
                // 无 CJK 字体：回退 Helvetica（仅 Latin-1 可显示，中文已在内容流中替换为 "?"）
                PutObj(5, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
            }

            // xref + trailer + startxref（每条 xref 记录必须恰好 20 字节）
            // 对象号：1 Catalog / 2 Pages / 3 Page / 4 内容流 / 5..(4+5*numFonts) 字体 / 之后 = HATCH 渐变 shading
            int size = offsets.Count == 0 ? 1 : offsets.Max(o => o.Key) + 1;
            long xrefPos = file.Position;
            PutAscii("xref\n0 " + size + "\n");
            PutAscii("0000000000 65535 f \n");
            for (int i = 1; i < size; i++)
            {
                long off;
                if (offsets.TryGetValue(i, out off))
                    PutAscii(off.ToString("D10") + " 00000 n \n");
                else
                    PutAscii("0000000000 00000 f \n"); // 预留未用对象号（如未用渐变 shading 槽位）
            }
            PutAscii("trailer\n<< /Size " + size + " /Root 1 0 R >>\nstartxref\n" + xrefPos + "\n%%EOF\n");

            File.WriteAllBytes(outPath, file.ToArray());
            string fontNote = _pdfFonts.HasFont
                ? "已内嵌 " + _pdfFonts.Count + " 个中文字体（" + string.Join(", ", _pdfFonts.UsedFontNames()) + "）"
                : "无中文字体（中文显示为?）";
            _log($"✅  PDF 已生成：{outPath}（{(_transparentBg ? "透明背景" : (_whiteBg ? "白底" : "黑底"))}，页面 {F(_w * _opt.OutputZoom)}×{F(_h * _opt.OutputZoom)} pt，1:1 内容放大 {F(_opt.OutputZoom)} 倍，{fontNote}）");
            LogSkipped("PDF");
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
        catch (Exception ex)
        {
            _log($"❌  PDF 导出异常：{ex.Message}");
            return new ImageExportResult { Success = false };
        }
    }

    // 发射单个实体（PDF 内容流）。参数语义同 EmitEntitySvg。
    private void EmitEntityPdf(MemoryStream cs, Entity ent, Transform2D tr, double scaleMul, short byBlockAci, double[] byBlockDash, LtpPattern byBlockPat, double ltMul, short? byLayerOverride = null)
    {
        // 块内 ByLayer 图元随"插入点块所在图层"（byLayerOverride），顶层用自身图层色
        short layerAci;
        if (byLayerOverride.HasValue) layerAci = byLayerOverride.Value;
        else { layerAci = 7; try { layerAci = LayerAciIndex(ent.Layer); } catch { } }
        string col = ColorOf(EntityAciIndex(ent), layerAci, byBlockAci);
        double[] dash = ResolveDash(ent, byBlockDash, ltMul);
        LtpPattern pat = ResolvePattern(ent, byBlockPat, ltMul); // 复杂线型（含 SHX 段）→ 沿路径真实渲染

        switch (ent)
        {
            case Insert ins:
                if (ins?.Block != null)
                {
                    var t = Transform2D.Compose(ins, tr);
                    // 块内容的"随块"颜色/线型 = 该 INSERT 的有效值（独立 > 图层；嵌套 ByBlock 继承上层）
                    short insAci = EffectiveAci(ins, byBlockAci);
                    double[] insDash = ResolveDash(ins, byBlockDash, ltMul);
                    LtpPattern insPat = ResolvePattern(ins, byBlockPat, ltMul);
                    // 块内"随图层"图元 = 该 INSERT 所在图层色（CAD 语义）
                    short insLayerAci = 7;
                    try { insLayerAci = LayerAciIndex(ins.Layer); } catch { }
                    // 块内实体的线型比例额外乘块插入比例（与 SVG 路径一致）
                    double childLtMul = ltMul * BlockScaleOf(ins);
                    foreach (var e in ins.Block.Entities) EmitEntityPdf(cs, e, t, t.Scale, insAci, insDash, insPat, childLtMul, insLayerAci);
                }
                break;

            case Line ln:
                {
                    var (x1, y1) = tr == null ? (ln.StartPoint.X, ln.StartPoint.Y) : tr.Apply(ln.StartPoint.X, ln.StartPoint.Y);
                    var (x2, y2) = tr == null ? (ln.EndPoint.X, ln.EndPoint.Y) : tr.Apply(ln.EndPoint.X, ln.EndPoint.Y);
                    double w = StrokeWidth(ent, scaleMul);
                    if (!TryEmitComplexPdf(cs, ent, tr, pat, col, w))
                        PdfStroke(cs, col, w, dash, F(Sx(x1)) + " " + F(Py(y1)) + " m " + F(Sx(x2)) + " " + F(Py(y2)) + " l S");
                }
                break;

            case LwPolyline pl:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    // 变宽判断看**任意**顶点 StartWidth(40) 或 ConstantWidth(43)（首顶点窄端 sw=0 的纺锤形不能漏判）；
                    // EndWidth(41) 不单独触发（闭合圆环全 sw=0/ew=常数 应走描边+bulge 弧，否则变宽路径退化成 0 宽消失）。
                    // 常数宽(43)：全顶点等宽归一形式 → 物化为逐顶点
                    if (pl.ConstantWidth > 0 && !pl.Vertices.Any(v => v.StartWidth > 0))
                        foreach (var v in pl.Vertices) { v.StartWidth = pl.ConstantWidth; v.EndWidth = pl.ConstantWidth; }
                    // 变宽：任意顶点 起宽(40)/终宽(41) 非零 或 常数宽(43) 非零 → 走填充变宽路径；逐顶点有效宽度
                    // 由 TaperedVertexWidths 计算（正确处理"每节起点/终点线宽不等"，与 SVG/PNG 一致）。
                    bool hasTaper = pl.Vertices.Any(v => v.StartWidth > 0 || v.EndWidth > 0) || pl.ConstantWidth > 0;
                    if (hasTaper && pl.Vertices.Count >= 2)
                    {
                        PdfTaperedPolyline(cs, pl, tr, col, dash);
                        break;
                    }
                    // 普通描边路径采样世界折线点（含 bulge 弧段）；变宽路径走 PdfTaperedPolyline（自有 bulge 逻辑）
                    var pts = LwPolylineSamplePoints(pl, tr);
                    var d = new StringBuilder();
                    for (int i = 0; i < pts.Count; i++) d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                    if (pl.IsClosed) d.Append("h ");
                    PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, d + "S");
                }
                break;

            case Polyline3D p3:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts = p3.Vertices.Where(v => IsDisplayVertex(v)) // 跳过样条框架控制点（不显示）
                        .Select(v => tr == null ? (v.Location.X, v.Location.Y) : tr.Apply(v.Location.X, v.Location.Y)).ToList();
                    if (pts.Count >= 2)
                    {
                        var d = new StringBuilder();
                        for (int i = 0; i < pts.Count; i++) d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                        if (p3.IsClosed) d.Append("h "); // 闭合 3D 折线补闭合段
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, d + "S");
                    }
                }
                break;

            case Polyline2D p2d:
                {
                    // 2D 多段线（老式 POLYLINE+VERTEX）：顶点折线折线（bulge 弧段暂按直线弦）
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var pts2d = new List<(double X, double Y)>(p2d.Vertices.Count);
                    foreach (var v in p2d.Vertices)
                    {
                        if (!IsDisplayVertex(v)) continue; // 跳过样条框架控制点（不显示）
                        if (tr == null) pts2d.Add((v.Location.X, v.Location.Y));
                        else { var a = tr.Apply(v.Location.X, v.Location.Y); pts2d.Add((a.Item1, a.Item2)); }
                    }
                    if (pts2d.Count >= 2)
                    {
                        var d2d = new StringBuilder();
                        for (int i = 0; i < pts2d.Count; i++) d2d.Append(F(Sx(pts2d[i].X)) + " " + F(Py(pts2d[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                        if (p2d.IsClosed) d2d.Append("h ");
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, d2d + "S");
                    }
                }
                break;

            case Mesh mesh:
                {
                    // 3D 网格：渲染边线框
                    double w = StrokeWidth(ent, scaleMul);
                    if (mesh.Edges != null && mesh.Vertices != null && mesh.Vertices.Count > 0)
                    {
                        var d = new StringBuilder();
                        foreach (var e in mesh.Edges)
                        {
                            if (e.Start < 0 || e.End < 0 || e.Start >= mesh.Vertices.Count || e.End >= mesh.Vertices.Count) continue;
                            var va = mesh.Vertices[e.Start];
                            var vb = mesh.Vertices[e.End];
                            double ax, ay, bx, by;
                            if (tr == null) { ax = va.X; ay = va.Y; bx = vb.X; by = vb.Y; }
                            else { var a = tr.Apply(va.X, va.Y); var b = tr.Apply(vb.X, vb.Y); ax = a.Item1; ay = a.Item2; bx = b.Item1; by = b.Item2; }
                            d.Append(F(Sx(ax)) + " " + F(Py(ay)) + " m " + F(Sx(bx)) + " " + F(Py(by)) + " l ");
                        }
                        if (d.Length > 0) PdfStroke(cs, col, w, null, d + "S");
                    }
                }
                break;

            case PolyfaceMesh pf:
                {
                    // 旧式 3D 多面体网格：按 Face 索引连线成线框
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, w)) break;
                    var dpf = new StringBuilder();
                    foreach (var (a, b) in PolyfaceMeshWire(pf, tr))
                        dpf.Append(F(Sx(a.AX)) + " " + F(Py(a.AY)) + " m " + F(Sx(b.BX)) + " " + F(Py(b.BY)) + " l ");
                    if (dpf.Length > 0) PdfStroke(cs, col, w, null, dpf + "S");
                }
                break;

            case PolygonMesh pm:
                {
                    // M×N 多边形网格：网格线框
                    double w = StrokeWidth(ent, scaleMul);
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, w)) break;
                    var dpm = new StringBuilder();
                    foreach (var (a, b) in PolygonMeshWire(pm, tr))
                        dpm.Append(F(Sx(a.AX)) + " " + F(Py(a.AY)) + " m " + F(Sx(b.BX)) + " " + F(Py(b.BY)) + " l ");
                    if (dpm.Length > 0) PdfStroke(cs, col, w, null, dpm + "S");
                }
                break;

            case Wipeout wo:
                {
                    // 擦除多边形：用背景色填充（透明背景无底色可擦则跳过）
                    if (!_transparentBg)
                        PdfQuadFill(cs, _whiteBg ? "#FFFFFF" : "#000000", WipeoutWorldPoly(wo, tr));
                }
                break;

            case Arc a: // 必须先于 Circle（fork 继承关系）
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (a.Center.X, a.Center.Y) : tr.Apply(a.Center.X, a.Center.Y);
                    double r = a.Radius * (tr?.Scale ?? 1.0);
                    // fork 角度为弧度 → 转度（参考实现入参为度）
                    PdfArc(cs, col, StrokeWidth(ent, scaleMul), dash, Sx(cx), Py(cy), r, a.StartAngle * 180.0 / Math.PI, a.EndAngle * 180.0 / Math.PI);
                }
                break;

            case Circle c:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (c.Center.X, c.Center.Y) : tr.Apply(c.Center.X, c.Center.Y);
                    double r = c.Radius * (tr?.Scale ?? 1.0);
                    // 整圆：三次贝塞尔逼近（PDF 无 arc 操作符，旧写法被合规查看器忽略，
                    // GC117 等块内的圆圈因此整体不显示）
                    var dCircle = new StringBuilder();
                    AppendCircleBezier(dCircle, Sx(cx), Py(cy), r);
                    PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash, dCircle.ToString() + "S");
                }
                break;

            case Ellipse el:
                {
                    if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(ent, scaleMul))) break;
                    var (cx, cy) = tr == null ? (el.Center.X, el.Center.Y) : tr.Apply(el.Center.X, el.Center.Y);
                    // fork MajorAxis/MinorAxis 为主/短轴全长(2a/2b)，PdfEllipse 用半轴 → 各除以 2
                    double rx = el.MajorAxis / 2.0 * (tr?.Scale ?? 1.0), ry = el.MinorAxis / 2.0 * (tr?.Scale ?? 1.0);
                    double rot = el.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度 → 度
                    PdfEllipse(cs, col, StrokeWidth(ent, scaleMul), dash, Sx(cx), Py(cy), rx, ry, rot);
                }
                break;

            case Point pt:
                {
                    var (cx, cy) = tr == null ? (pt.Location.X, pt.Location.Y) : tr.Apply(pt.Location.X, pt.Location.Y);
                    // 点标记与 SVG 端一致：大小与线宽保持一致（直径 = 3× 当前线宽）；
                    // 填充整圆同样用贝塞尔（旧 "0 360 arc" 被合规查看器忽略，点会消失）。
                    // 填充必须显式设置填充色 rg：PdfStroke 只设置描边色 RG，
                    // 填充色会沿用上一条操作残留的状态——前面恰好是黑色背景时点被填成
                    // 黑色（黑底不可见），残留红色文字状态时又显示为红色，行为不确定。
                    double pr = Math.Max(0.05, StrokeWidth(pt, scaleMul) * 1.5);
                    var dDot = new StringBuilder();
                    AppendCircleBezier(dDot, Sx(cx), Py(cy), pr);
                    CsAscii(cs, HexToRgbPdf(col) + " rg\n" + HexToRgbPdf(col) + " RG\n0.6 w\n" + dDot + "f\n");
                }
                break;

            case TextEntity t:
                {
                    // SHX 样式：真实 SHX 笔迹路径（与 AutoCAD 一致字宽）；非 SHX/字体缺失 → 回退原系统字体路径
                    var shx = TryBuildShxTextWorld(t, tr, scaleMul);
                    if (shx != null)
                    {
                        var d = new StringBuilder();
                        double curX = 0, curY = 0; bool haveCur = false;
                        for (int k = 0; k < shx.Value.Type.Count; k++)
                        {
                            var p = shx.Value.Pts[k];
                            // PDF y 向上（Py），无二次贝塞尔 → 转三次
                            switch (shx.Value.Type[k])
                            {
                                case 0: d.Append(F(Sx(p[0]))).Append(' ').Append(F(Py(p[1]))).Append(" m "); curX = Sx(p[0]); curY = Py(p[1]); haveCur = true; break;
                                case 1: d.Append(F(Sx(p[0]))).Append(' ').Append(F(Py(p[1]))).Append(" l "); curX = Sx(p[0]); curY = Py(p[1]); haveCur = true; break;
                                case 2:
                                {
                                    double p0x = haveCur ? curX : Sx(p[0]);
                                    double p0y = haveCur ? curY : Py(p[1]);
                                    double cx = Sx(p[0]), cy = Py(p[1]), ex = Sx(p[2]), ey = Py(p[3]);
                                    double c1x = p0x + 2.0 / 3.0 * (cx - p0x), c1y = p0y + 2.0 / 3.0 * (cy - p0y);
                                    double c2x = ex + 2.0 / 3.0 * (cx - ex), c2y = ey + 2.0 / 3.0 * (cy - ey);
                                    d.Append(F(c1x)).Append(' ').Append(F(c1y)).Append(' ').Append(F(c2x)).Append(' ').Append(F(c2y)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey)).Append(" c ");
                                    curX = ex; curY = ey; haveCur = true; break;
                                }
                                case 3: d.Append(F(Sx(p[0]))).Append(' ').Append(F(Py(p[1]))).Append(' ').Append(F(Sx(p[2]))).Append(' ').Append(F(Py(p[3]))).Append(' ').Append(F(Sx(p[4]))).Append(' ').Append(F(Py(p[5]))).Append(" c "); curX = Sx(p[4]); curY = Py(p[5]); haveCur = true; break;
                            }
                        }
                        PdfStroke(cs, col, shx.Value.PenWidth, null, d + "S");
                        break;
                    }
                    // TTF 样式：原系统字体路径（不变）
                    var (bx, by, brot, bscale) = TextBaseline(t, tr);
                    double fs = t.Height * scaleMul * bscale;
                    int slot = _pdfFonts.GetSlot(ResolveFontFamily(GetStyleFont(t.Style)));
                    PdfText(cs, col, fs, Sx(bx), Py(by), brot, t.Value ?? "", slot, t.WidthFactor);
                }
                break;

            case MText mt:
                // 多行文字：解析格式码（\P 换行/\H 分段字号/\f 字体）+ 参考列宽换行 + 附着点定位
                EmitMTextPdf(cs, mt, tr, scaleMul, layerAci, byBlockAci, GetStyleFont(mt.Style));
                break;

            case Solid so:
                PdfQuadFill(cs, col, SolidQuadPts(so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner, tr));
                break;
            case Face3D fd:
                PdfQuadFill(cs, col, QuadPts(fd.FirstCorner, fd.SecondCorner, fd.ThirdCorner, fd.FourthCorner, tr));
                break;

            case Hatch ht:
                EmitHatchPdf(cs, ht, tr, scaleMul, layerAci, byBlockAci);
                break;
            case Spline sp:
                if (TryEmitComplexPdf(cs, ent, tr, pat, col, StrokeWidth(sp, scaleMul))) break;
                EmitSplinePdf(cs, sp, tr, scaleMul, col, dash);
                break;

            case XLine xl:
                {
                    var (px, py) = tr == null ? (xl.FirstPoint.X, xl.FirstPoint.Y) : tr.Apply(xl.FirstPoint.X, xl.FirstPoint.Y);
                    var (dx, dy) = TransformDirection(xl.Direction.X, xl.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, true, out var x1, out var y1, out var x2, out var y2))
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash,
                            F(Sx(x1)) + " " + F(Py(y1)) + " m " + F(Sx(x2)) + " " + F(Py(y2)) + " l S");
                }
                break;

            case Ray ray:
                {
                    var (px, py) = tr == null ? (ray.StartPoint.X, ray.StartPoint.Y) : tr.Apply(ray.StartPoint.X, ray.StartPoint.Y);
                    var (dx, dy) = TransformDirection(ray.Direction.X, ray.Direction.Y, tr);
                    if (ClipInfiniteLineToCanvas(px, py, dx, dy, false, out var x1, out var y1, out var x2, out var y2))
                        PdfStroke(cs, col, StrokeWidth(ent, scaleMul), dash,
                            F(Sx(x1)) + " " + F(Py(y1)) + " m " + F(Sx(x2)) + " " + F(Py(y2)) + " l S");
                }
                break;

            case MLine ml:
                {
                    double w = StrokeWidth(ent, scaleMul);
                    foreach (var (poly, aci) in MLineOffsetPolys(ml, tr))
                    {
                        string ecol = ColorOf(aci, layerAci, byBlockAci);
                        var d = new StringBuilder();
                        for (int i = 0; i < poly.Count; i++)
                            d.Append(F(Sx(poly[i].X)) + " " + F(Py(poly[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                        PdfStroke(cs, ecol, w, dash, d + "S");
                    }
                }
                break;

            case Leader ld:
                {
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
                        var linePts = pts;
                        if (LeaderArrowBase(pts, alen, out var lbx, out var lby))
                        {
                            linePts = new List<(double X, double Y)>(pts);
                            linePts[0] = (lbx, lby);
                        }
                        var d = new StringBuilder();
                        for (int i = 0; i < linePts.Count; i++)
                            d.Append(F(Sx(linePts[i].X)) + " " + F(Py(linePts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
                        PdfStroke(cs, col, w, dash, d + "S");
                        // 实心箭头三角（AutoCAD LEADER 默认实心；尖端 pts[0] 指向目标端）
                        if (LeaderArrowWings(ld, pts, alen, out var w1x, out var w1y, out var w2x, out var w2y))
                            PdfQuadFill(cs, col, new List<(double X, double Y)> { (pts[0].X, pts[0].Y), (w1x, w1y), (w2x, w2y) });
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
                            EmitEntityPdf(cs, be, null, 1.0, dimAci, null, null, ltMul, dimLayerAci);
                        }
                    }
                    else
                        RecordSkip(ent.GetType().Name);
                }
                break;

            case Shape shp:
                {
                    // SHX 形状实体：用 shape 字体库真实字形笔迹渲染（BuildShapePath 共享，PDF 用 Sx/Py 不翻转 Y）
                    string shpCol = ColorOf(EntityAciIndex(shp), layerAci, byBlockAci);
                    string shpPath = BuildShapePath(shp, tr, Sx, Py, pdf: true);
                    if (shpPath.Length > 0)
                        PdfStroke(cs, shpCol, StrokeWidth(shp, scaleMul), null, shpPath + " S");
                }
                break;

            default:
                RecordSkip(ent.GetType().Name);
                break;
        }
    }

    // 复杂线型（PDF）：沿路径走查模式 → 内容流路径串 + S（虚线状态显式复位为实线）
    private bool TryEmitComplexPdf(MemoryStream cs, Entity ent, Transform2D tr, LtpPattern pat, string col, double w)
    {
        if (pat == null) return false;
        if (!TryGetPathPoints(ent, tr, out var pts) || pts.Count < 2) return false;
        var cum = BuildCum(pts);
        if (cum[cum.Length - 1] <= 1e-9) return false;
        var d = new StringBuilder();
        var texts = new List<LtpTextPlace>();
        WalkPattern(d, pts, cum, pat, Sx, Py, true, texts);
        if (d.Length == 0 && texts.Count == 0) return false;
        if (d.Length > 0)
            PdfStroke(cs, col, w, null, d + "S");
        // 文字段：原生 PDF 文字（坐标需 Sx/Py 映射，rotDeg 数据系角）
        foreach (var p in texts)
        {
            int slot = _pdfFonts != null ? _pdfFonts.GetSlot(ResolveFontFamily(p.Font)) : 0;
            PdfText(cs, col, p.Height, Sx(p.X), Py(p.Y), p.RotDeg, p.Text, slot, p.WidthFactor);
        }
        return true;
    }

    // 多行文字（PDF）：与 EmitMTextSvg 同一布局，逐 run 用 Tm 6 参数文本矩阵 + 2 字节 GID 串
    private void EmitMTextPdf(MemoryStream cs, MText mt, Transform2D tr, double scaleMul, short layerAci, short byBlockAci, string styleFont)
    {
        string baseCol = ColorOf(EntityAciIndex(mt), layerAci, byBlockAci);
        var layout = WrapMText(ParseMText(mt.Value ?? "", mt.Height), mt.RectangleWidth);
        if (layout.Lines.Count == 0) return;
        var (baselines, anchorX, anchorY) = MTextLocalLayout(layout, mt);

        double rot = mt.Rotation * 180.0 / Math.PI + (tr?.Rotation ?? 0.0); // fork 弧度（只读，源自 AlignmentPoint）→ 度
        double rad = rot * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double px = mt.InsertPoint.X, py = mt.InsertPoint.Y;
        if (tr != null) { var ap = tr.Apply(px, py); px = ap.Item1; py = ap.Item2; }

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
                    int slot = _pdfFonts.GetSlot(ResolveFontFamily(!string.IsNullOrEmpty(run.Font) ? run.Font : styleFont));
                    CjkFont font = _pdfFonts.FontAt(slot);
                    ToWorld(x, baselines[li], out double wx, out double wy);
                    byte[] data = font != null ? font.Encode(run.Text) : PdfFallbackTextBytes(run.Text);
                    if (data.Length > 0)
                    {
                        // R18：run 宽度因子 → Tm = [ca·WF, sa·WF, −sa, ca, x, y]（与 MText SVG 的 scale(WF,1) 对齐；
                        // run 起点推进 MTextRunWidth 已含 WF，WF=1 时逐字节不变）
                        double wf = run.WidthFactor > 0 ? run.WidthFactor : 1.0;
                        CsAscii(cs, "BT\n/F" + slot + " " + F(fs) + " Tf\n" + HexToRgbPdf(col) + " rg\n" +
                                   F(ca * wf) + " " + F(sa * wf) + " " + F(-sa) + " " + F(ca) + " " + F(Sx(wx)) + " " + F(Py(wy)) + " Tm\n");
                        CsString(cs, data);
                        CsAscii(cs, " Tj\nET\n");
                    }
                    if (run.Underline)
                    {
                        ToWorld(x, baselines[li] - 0.08 * run.Height, out double u1x, out double u1y);
                        ToWorld(x + rw, baselines[li] - 0.08 * run.Height, out double u2x, out double u2y);
                        PdfStroke(cs, col, Math.Max(0.05, fs * 0.04), null, F(Sx(u1x)) + " " + F(Py(u1y)) + " m " + F(Sx(u2x)) + " " + F(Py(u2y)) + " l S");
                    }
                }
                x += rw;
            }
        }
    }

    // PDF 样条（描边折线逼近）
    private void EmitSplinePdf(MemoryStream cs, Spline sp, Transform2D tr, double scaleMul, string col, double[] dash)
    {
        var pts = SplineSample(sp, tr, 16);
        if (pts.Count < 2) { RecordSkip("SPLINE(空)"); return; }
        var d = new StringBuilder();
        for (int i = 0; i < pts.Count; i++)
            d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
        PdfStroke(cs, col, StrokeWidth(sp, scaleMul), dash, d + "S");
    }

    // PDF HATCH（实心=rg f/f*；渐变=Type2 轴向 / Type3 径向 shading，/Sh<n> sh）
    private void EmitHatchPdf(MemoryStream cs, Hatch ht, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var loops = BuildHatchLoops(ht, tr);
        if (loops.Count == 0) { RecordSkip("HATCH(空)"); return; }
        var gp = ht.GradientColor;
        bool gradient = gp != null && gp.Enabled;
        if (!gradient && !IsSolidPattern(ht)) { EmitHatchPatternPdf(cs, ht, loops, tr, scaleMul, layerAci, byBlockAci); return; }
        string rule = loops.Count > 1 ? "f*" : "f";
        string d = BuildPathDPdf(loops, x => Sx(x), y => Py(y));
        if (gradient)
        {
            int slot = _pdfShadingSeq++;
            if (slot >= MaxPdfShadings)
            {
                CsAscii(cs, HexToRgbPdf(StopHex(gp.Colors[0].Color)) + " rg\n" + d + rule + "\n"); // 超限降级首色
                RecordSkip("HATCH(渐变超限)");
                return;
            }
            _pdfShadings[slot] = MakePdfShading(loops, gp);
            CsAscii(cs, "/Sh" + slot + " sh\n");
        }
        else
        {
            CsAscii(cs, HexToRgbPdf(HatchSolidFillHex(ht, layerAci, byBlockAci)) + " rg\n" + d + rule + "\n");
        }
    }

    // 计算 PDF shading 参数（坐标为 PDF 用户空间，y 向上）
    private (int ShadingType, string Coords, string C0, string C1) MakePdfShading(List<List<XY>> loops, HatchGradientPattern gp)
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
        // 渐变色标：Colors 列表取首/末两个（双端渐变）
        string c1 = HexToRgbPdf(StopHex(gp.Colors[0].Color));
        string c2 = HexToRgbPdf(StopHex(gp.Colors[gp.Colors.Count - 1].Color));
        if (IsEllipticGradient(gp))
            return (3, $"{F(Sx(cx))} {F(Py(cy))} {F(Sx(cx))} {F(Py(cy))} 0 0 {F(L)} 0", c1, c2);
        double ang = gp.Angle; // fork 弧度
        double dx = Math.Cos(ang), dy = Math.Sin(ang);
        return (2, $"{F(Sx(cx - dx * L))} {F(Py(cy - dy * L))} {F(Sx(cx + dx * L))} {F(Py(cy + dy * L))}", c1, c2);
    }

    // 子路径集合 → PDF 路径串（m/l/h）
    private static string BuildPathDPdf(List<List<XY>> loops, Func<double, double> fx, Func<double, double> fy)
    {
        var d = new StringBuilder();
        foreach (var loop in loops)
        {
            if (loop.Count == 0) continue;
            for (int i = 0; i < loop.Count; i++)
                d.Append($"{F(fx(loop[i].X))} {F(fy(loop[i].Y))} {(i == 0 ? "m" : "l")} ");
            d.Append("h ");
        }
        return d.ToString();
    }

    // 图案 HATCH（PDF）：裁剪线段描边
    private void EmitHatchPatternPdf(MemoryStream cs, Hatch ht, List<List<XY>> loops, Transform2D tr, double scaleMul, short layerAci, short byBlockAci)
    {
        var segs = BuildPatternSegments(loops, ht, tr, MaxPatternLinesPerHatch);
        if (segs.Count == 0) { RecordSkip("HATCH(图案)"); return; }
        string col = HexToRgbPdf(HatchSolidFillHex(ht, layerAci, byBlockAci));
        double w = Math.Max(0.03, 0.1 * scaleMul);
        var d = new StringBuilder();
        foreach (var s in segs)
            d.Append(F(Sx(s.X1))).Append(' ').Append(F(Py(s.Y1))).Append(" m ")
             .Append(F(Sx(s.X2))).Append(' ').Append(F(Py(s.Y2))).Append(" l ");
        PdfStroke(cs, col, w, null, d.ToString() + "S");
    }

    // PDF 变宽折线：沿两侧按逐顶点宽度偏移生成外轮廓，闭合后填充（f 操作符）。
    // 含 bulge（弧段）时走 ImageEngine.TryBuildTaperOffset 弧采样偏移（与 SVG 同语义，T5.4 GC200 修复）。
    private void PdfTaperedPolyline(MemoryStream cs, LwPolyline pl, Transform2D tr, string hex, double[] dash)
    {
        var verts = pl.Vertices;
        int n = verts.Count;
        if (n < 2) return;
        // 逐顶点有效宽度：无 bulge 弦偏移路径用（顶点 i 宽 = 起宽40，为0时继承入段终宽41）
        double[] w = TaperedVertexWidths(pl);
        // 逐段起/终点宽：含 bulge 弧偏移路径用（段 i 从 40_i 渐变到 41_i，段间顶点收尖）
        var (sw, ew) = TaperedSegmentWidths(pl);

        List<(double X, double Y)> left, right;
        var taper = TryBuildTaperOffset(pl, sw, ew, tr, _opt.TaperOffsetBuilder);
        if (taper.Ok)
        {
            // 含 bulge：弧采样偏移轮廓
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

        var d = new StringBuilder();
        int m = left.Count;
        if (pl.IsClosed)
        {
            // 圆盘特例（宽度 ≥ 弧半径，一偏移环退化为单点——典型 GC200 点块）：
            // 单条三次贝塞尔整圆 + 非零填充（f）——与 netDxf 路由 DxfImageExporter.AppendCircleBezier
            // 同法（样条曲线替换圆形）：PDF 无 arc 操作符，且 evenodd + 退化子路径组合在部分合规
            // 查看器中整条被忽略（圆盘不显示），贝塞尔整圆是最稳妥的标准写法。
            var disc = _opt.TaperOffsetBuilder?.TryCircleDisc(left, right);
            if (disc != null)
            {
                double cx = Sx(disc.Value.Cx), cy = Py(disc.Value.Cy), R = disc.Value.R;
                double k = 0.5522847498307936 * R; // κ = 4/3·tan(π/8)
                var dd = new StringBuilder();
                dd.Append(F(cx + R) + " " + F(cy) + " m ");
                dd.Append(F(cx + R) + " " + F(cy + k) + " " + F(cx + k) + " " + F(cy + R) + " " + F(cx) + " " + F(cy + R) + " c ");
                dd.Append(F(cx - k) + " " + F(cy + R) + " " + F(cx - R) + " " + F(cy + k) + " " + F(cx - R) + " " + F(cy) + " c ");
                dd.Append(F(cx - R) + " " + F(cy - k) + " " + F(cx - k) + " " + F(cy - R) + " " + F(cx) + " " + F(cy - R) + " c ");
                dd.Append(F(cx + k) + " " + F(cy - R) + " " + F(cx + R) + " " + F(cy - k) + " " + F(cx + R) + " " + F(cy) + " c h f");
                CsAscii(cs, HexToRgbPdf(hex) + " rg\n" + dd + "\n");
                return;
            }

            // 闭合折线：left/right 各一条封闭偏移轮廓（内/外环），
            // 两个子路径 + even-odd 填充（f*）得到"环"。
            for (int i = 0; i < m; i++) d.Append(F(Sx(left[i].X)) + " " + F(Py(left[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
            d.Append("h ");
            for (int i = 0; i < m; i++) d.Append(F(Sx(right[i].X)) + " " + F(Py(right[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
            d.Append("h f*");
        }
        else
        {
            // 开放折线：left 正向 + right 反向，两端闭合填充
            for (int i = 0; i < m; i++) d.Append(F(Sx(left[i].X)) + " " + F(Py(left[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
            for (int i = m - 1; i >= 0; i--) d.Append(F(Sx(right[i].X)) + " " + F(Py(right[i].Y)) + " l ");
            d.Append("h f");
        }
        CsAscii(cs, HexToRgbPdf(hex) + " rg\n" + d + "\n");
    }

    // ===== 内容流字节级写入辅助 =====
    private static void CsAscii(MemoryStream cs, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        cs.Write(b, 0, b.Length);
    }

    // PDF 字符串字面量（支持任意二进制字节 —— 2 字节 GID 文本）。
    // 必须转义：\ ( ) 以及 CR/LF（PDF 规范：字符串字面量中的换行序列会被查看器忽略，
    // 而 GID 字节可能恰好是 0x0D/0x0A，不转义会丢字节导致文字错位）。
    private static void CsString(MemoryStream cs, byte[] data)
    {
        CsAscii(cs, "(");
        foreach (byte b in data)
        {
            if (b == 0x28) CsAscii(cs, "\\(");
            else if (b == 0x29) CsAscii(cs, "\\)");
            else if (b == 0x5C) CsAscii(cs, "\\\\");
            else if (b == 0x0A) CsAscii(cs, "\\012");
            else if (b == 0x0D) CsAscii(cs, "\\015");
            else cs.WriteByte(b);
        }
        CsAscii(cs, ")");
    }

    // dash 为虚线模式（数据单位）；null/空 = 实线。PDF 用 d 操作符设置虚线（[数组] 相位 d）。
    // 注意：d 状态在内容流中持久，实线必须显式写 [] 0 d 复位，
    // 否则会继承前一条虚线的 dash 参数（旧版实线漏复位 → 虚线之后的实线被画成虚线）。
    private static string DashOp(double[] dash)
    {
        if (dash == null || dash.Length == 0) return "[] 0 d\n";
        var sb = new StringBuilder("[");
        for (int i = 0; i < dash.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(F(dash[i]));
        }
        sb.Append("] 0 d\n");
        return sb.ToString();
    }

    private static void PdfStroke(MemoryStream cs, string hex, double w, double[] dash, string op)
    {
        CsAscii(cs, HexToRgbPdf(hex) + " RG\n" + F(w) + " w\n" + DashOp(dash) + op + "\n");
    }

    // 整圆/整椭圆：三次贝塞尔逼近，全部使用 PDF 标准路径操作符（m/l/c/h）。
    // 注意：PDF 规范里没有 arc/circle 路径操作符——旧版写的 "x y r 0 360 arc"
    // 不是合法操作符，Acrobat / Chrome(PDFium) 等合规查看器直接忽略，
    // 这是 GC117 等点块的圆圈和点在 PDF 中整体不显示的根因。
    private static void AppendCircleBezier(StringBuilder d, double cx, double cy, double r)
    {
        double k = 0.5522847498307936 * r;
        d.Append(F(cx + r) + " " + F(cy) + " m ");
        d.Append(F(cx + r) + " " + F(cy + k) + " " + F(cx + k) + " " + F(cy + r) + " " + F(cx) + " " + F(cy + r) + " c ");
        d.Append(F(cx - k) + " " + F(cy + r) + " " + F(cx - r) + " " + F(cy + k) + " " + F(cx - r) + " " + F(cy) + " c ");
        d.Append(F(cx - r) + " " + F(cy - k) + " " + F(cx - k) + " " + F(cy - r) + " " + F(cx) + " " + F(cy - r) + " c ");
        d.Append(F(cx + k) + " " + F(cy - r) + " " + F(cx + r) + " " + F(cy - k) + " " + F(cx + r) + " " + F(cy) + " c h ");
    }

    // 椭圆：8 段（每段 45°）三次贝塞尔逼近（y 向上坐标系，旋转角与 CAD 同向，直接取正）。
    // 控制点偏移 = k·r(t)·单位切向量，r(t)=√(a²sin²t+b²cos²t) 为该参数处的"局部半轴"
    //（圆时退化为半径，k=4/3·tan(Δ/4)，Δ 为段角）。
    private static void PdfEllipse(MemoryStream cs, string hex, double w, double[] dash, double cx, double cy, double rx, double ry, double rotDeg)
    {
        CsAscii(cs, HexToRgbPdf(hex) + " RG\n" + F(w) + " w\n" + DashOp(dash));
        if (rx <= 0 || ry <= 0) return;
        double a = rotDeg * Math.PI / 180.0;
        double ca = Math.Cos(a), sa = Math.Sin(a);
        void P(double t, out double x, out double y)
        {
            double ex = rx * Math.Cos(t), ey = ry * Math.Sin(t);
            x = cx + ex * ca - ey * sa;
            y = cy + ex * sa + ey * ca;
        }
        void T(double t, out double tx, out double ty)
        {
            double vx = -rx * Math.Sin(t), vy = ry * Math.Cos(t);
            double len = Math.Sqrt(vx * vx + vy * vy);
            if (len <= 0) { tx = 0; ty = 0; return; }
            tx = (vx * ca - vy * sa) / len;
            ty = (vx * sa + vy * ca) / len;
        }
        int segs = 8;
        double da = 2 * Math.PI / segs;
        double k = 4.0 / 3.0 * Math.Tan(da / 4.0);
        var d = new StringBuilder();
        P(0, out double x0, out double y0);
        d.Append(F(x0) + " " + F(y0) + " m ");
        for (int i = 0; i < segs; i++)
        {
            double t1 = da * (i + 1);
            P(t1, out double x1, out double y1);
            double r0 = Math.Sqrt(Math.Pow(rx * Math.Sin(da * i), 2) + Math.Pow(ry * Math.Cos(da * i), 2));
            double r1 = Math.Sqrt(Math.Pow(rx * Math.Sin(t1), 2) + Math.Pow(ry * Math.Cos(t1), 2));
            T(da * i, out double t0x, out double t0y);
            T(t1, out double t1x, out double t1y);
            double c1x = x0 + k * r0 * t0x, c1y = y0 + k * r0 * t0y;
            double c2x = x1 - k * r1 * t1x, c2y = y1 - k * r1 * t1y;
            d.Append(F(c1x) + " " + F(c1y) + " " + F(c2x) + " " + F(c2y) + " " + F(x1) + " " + F(y1) + " c ");
            x0 = x1; y0 = y1;
        }
        CsAscii(cs, d + "h S\n");
    }

    // 弧：CAD 弧恒为从起始角到终止角逆时针（end<start 时跨过 360°）。
    // 用三次贝塞尔逼近（每段 ≤90°）——arcto 只接受 5 个操作数（x y r start end），
    // 且只能逆时针；旧版多写了一个 sweep 操作数，残留操作数污染其后所有指令。
    private static void PdfArc(MemoryStream cs, string hex, double w, double[] dash, double cx, double cy, double r, double startDeg, double endDeg)
    {
        CsAscii(cs, HexToRgbPdf(hex) + " RG\n" + F(w) + " w\n" + DashOp(dash));
        if (r <= 0) return;
        double s = startDeg * Math.PI / 180.0;
        double e = endDeg * Math.PI / 180.0;
        double span = e - s;
        while (span < 0) span += 2 * Math.PI;
        while (span >= 2 * Math.PI) span -= 2 * Math.PI;
        if (span < 1e-6)
        {
            // start==end 视为整圆：贝塞尔逼近（PDF 无 arc 操作符，旧写法被合规查看器忽略）
            var dFull = new StringBuilder();
            AppendCircleBezier(dFull, cx, cy, r);
            CsAscii(cs, dFull + "S\n");
            return;
        }
        int segs = Math.Max(1, (int)Math.Ceiling(span / (Math.PI / 2))); // 每段 ≤90°
        double a0 = s;
        double x0 = cx + r * Math.Cos(a0), y0 = cy + r * Math.Sin(a0);
        var d = new StringBuilder();
        d.Append(F(x0) + " " + F(y0) + " m ");
        for (int i = 0; i < segs; i++)
        {
            double a1 = s + span * (i + 1) / segs;
            double x1 = cx + r * Math.Cos(a1), y1 = cy + r * Math.Sin(a1);
            double da = span / segs;
            double k = 4.0 / 3.0 * Math.Tan(da / 4.0);
            // 逆时针单位切向量
            double t0x = -Math.Sin(a0), t0y = Math.Cos(a0);
            double t1x = -Math.Sin(a1), t1y = Math.Cos(a1);
            double c1x = x0 + k * r * t0x, c1y = y0 + k * r * t0y;
            double c2x = x1 - k * r * t1x, c2y = y1 - k * r * t1y;
            d.Append(F(c1x) + " " + F(c1y) + " " + F(c2x) + " " + F(c2y) + " " + F(x1) + " " + F(y1) + " c ");
            a0 = a1; x0 = x1; y0 = y1;
        }
        CsAscii(cs, d + "S\n");
    }

    // 文字：Tm 必须给完整 6 参数文本矩阵 [cos sin -sin cos x y]（旧版只写 3 个参数，
    // 同样污染操作数栈）。文字串为 2 字节 GID（Identity-H）或回退 ASCII。
    // R18：宽度因子（DXF 41）→ Tm = [cr·WF, sr·WF, −sr, cr, x, y]（文字局部系先水平压缩再旋转；WF=1 逐字节不变）。
    private void PdfText(MemoryStream cs, string hex, double fs, double x, double y, double rotDeg, string text, int slot, double widthFactor = 1.0)
    {
        if (string.IsNullOrEmpty(text)) return;
        CjkFont font = _pdfFonts != null ? _pdfFonts.FontAt(slot) : null;
        byte[] data = font != null ? font.Encode(text) : PdfFallbackTextBytes(text);
        if (data.Length == 0) return;
        double r = rotDeg * Math.PI / 180.0;
        double cr = Math.Cos(r), sr = Math.Sin(r);
        CsAscii(cs, "BT\n/F" + slot + " " + F(fs) + " Tf\n" + HexToRgbPdf(hex) + " rg\n" +
                   F(cr * widthFactor) + " " + F(sr * widthFactor) + " " + F(-sr) + " " + F(cr) + " " + F(x) + " " + F(y) + " Tm\n");
        CsString(cs, data);
        CsAscii(cs, " Tj\nET\n");
    }

    // 无 CJK 字体时的回退：保留可打印 ASCII，其余替换为 "?"
    private static byte[] PdfFallbackTextBytes(string text)
    {
        var b = new byte[text.Length];
        int n = 0;
        foreach (char ch in text) b[n++] = (byte)(ch >= 32 && ch < 127 ? ch : '?');
        return b;
    }

    private void PdfQuadFill(MemoryStream cs, string hex, List<(double X, double Y)> pts)
    {
        if (pts.Count < 3) return;
        var d = new StringBuilder();
        for (int i = 0; i < pts.Count; i++) d.Append(F(Sx(pts[i].X)) + " " + F(Py(pts[i].Y)) + " " + (i == 0 ? "m" : "l") + " ");
        d.Append("h ");
        CsAscii(cs, HexToRgbPdf(hex) + " rg\n" + d + "f\n");
    }

    // SVG hex "#RRGGBB" → PDF RGB 分量串 "r g b"（0..1）
    private static string HexToRgbPdf(string hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return "1 1 1";
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        // 与参考实现 DxfImageExporter.HexToRgbPdf 逐字节一致：最多 3 位小数、去尾零（0.###），
        // 不是 6 位小数（PDF 颜色行 0.647 0 0.161 vs 0.647059 0 0.160784 的 11 万行差异根因）。
        return (r / 255.0).ToString("0.###", CultureInfo.InvariantCulture) + " "
             + (g / 255.0).ToString("0.###", CultureInfo.InvariantCulture) + " "
             + (b / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
    #endregion
}
