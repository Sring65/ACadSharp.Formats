using System.Collections.Generic;

namespace ACadSharp.Formats.Image;

/// <summary>
/// 变宽折线（逐顶点宽度 + bulge 弧段）偏移轮廓构建器 —— 渲染器扩展点（R13 架构拆分）：
/// 本包只定义契约（DWG 数据 → 图片渲染），算法实现由宿主程序提供并注入
/// （GDB2CAD.Core.TaperOffset，经 ImageConfiguration.TaperOffsetBuilder 传入）。
/// 为 null（独立使用本包）时，渲染器回退到旧的逐顶点弦偏移
/// （R10 前行为：含 bulge 的常宽折线按弦渲染，GC200 类点符号呈方块）。
/// </summary>
public interface ITaperOffsetBuilder
{
    /// <summary>
    /// 含 bulge（|bulge|&gt;1e-9 的边）时构建弧采样偏移轮廓；无 bulge 返回 null →
    /// 渲染器走逐顶点弦偏移路径（输出与旧实现逐字节一致）。
    /// </summary>
    /// <param name="n">顶点数（≥2）。</param>
    /// <param name="closed">是否闭合（闭合时边数 = n，开放 = n−1）。</param>
    /// <param name="xs">n 个顶点 X（实体局部坐标，未含块插入变换）。</param>
    /// <param name="ys">n 个顶点 Y。</param>
    /// <param name="bulges">顶点 i 处出边 bulge（n 个；开放折线末顶点忽略）。</param>
    /// <param name="widths">顶点 i 全宽（数据单位，CAD 组码 43 语义）。</param>
    /// <returns>(左偏移环, 右偏移环)——行进方向左侧/右侧，与输入同坐标系（未变换）；无 bulge 时 null。</returns>
    (List<(double X, double Y)> Left, List<(double X, double Y)> Right)? TryBuild(
        int n, bool closed, double[] xs, double[] ys, double[] bulges, double[] widths);

    /// <summary>
    /// 两偏移环之一退化为单点（宽度 ≥ 弧半径，GC200 类点符号）→ 返回实心圆盘 (圆心, 半径)；否则 null。
    /// PDF 渲染器据此发「三次贝塞尔整圆 + 非零填充」（evenodd + 退化子路径在部分合规查看器中被整条忽略）。
    /// </summary>
    (double Cx, double Cy, double R)? TryCircleDisc(List<(double X, double Y)> a, List<(double X, double Y)> b);
}
