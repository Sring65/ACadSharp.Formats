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

    /// <summary>
    /// PNG 分块（瓦片）列表：仅当 PNG 整幅画布超出位图像素上限且 PngMergeToSingle=false 时填充
    /// （多张 PNG，每张对应一个 <see cref="ImageTile"/>）。单图输出（未超限、或已合并）时为空。
    /// 注意：本对象的 CanvasPixelsW/H 与 DataX0/X1/Y0/Y1 始终是「整幅画布」的值（合并图即整幅缩小，
    /// 分块时为各瓦片共同的全局画布），各瓦片的子区域由 <see cref="ImageTile"/> 描述。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<ImageTile> Tiles { get; init; } =
        System.Array.Empty<ImageTile>();

    /// <summary>PNG 是否因超像素上限被流式合并为单图（PngMergeToSingle=true 且超限时为 true）。</summary>
    public bool MergedToSingle { get; init; }
}
