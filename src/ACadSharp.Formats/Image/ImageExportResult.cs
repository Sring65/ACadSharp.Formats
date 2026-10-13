namespace ACadSharp.Formats.Image;

/// <summary>
/// 图像导出结果（T5）：除成功标志外，回传画布像素尺寸与数据坐标包围盒，
/// 供调用方（GDB2CAD）生成「图片像素 ↔ 真实数据坐标」对照（如同名 .json：
/// 图片 (0,0) 与对角点对应的真实坐标）。
/// 画布与数据坐标的映射（PNG/SVG/PDF 一致，均 1:1×OutputZoom、2% 边距、Y 翻转）：
///   px_x = (dataX − DataX0) × (CanvasPixelsW / DataW)
///   px_y = (DataY1 − dataY) × (CanvasPixelsH / DataH)
/// 故图片左上角 (0,0) ↔ (DataX0, DataY1)，右下角 (W,H) ↔ (DataX1, DataY0)。
/// </summary>
public sealed class ImageExportResult
{
    /// <summary>是否成功写出图像文件。</summary>
    public bool Success { get; init; }

    /// <summary>画布像素宽（= 1:1 数据宽 × OutputZoom，位图含上限缩小）。</summary>
    public int CanvasPixelsW { get; init; }

    /// <summary>画布像素高。</summary>
    public int CanvasPixelsH { get; init; }

    /// <summary>画布左边界对应的数据坐标（含 2% 边距）。</summary>
    public double DataX0 { get; init; }

    /// <summary>画布右边界对应的数据坐标。</summary>
    public double DataX1 { get; init; }

    /// <summary>画布下边界对应的数据坐标。</summary>
    public double DataY0 { get; init; }

    /// <summary>画布上边界对应的数据坐标。</summary>
    public double DataY1 { get; init; }
}
