namespace ACadSharp.Formats.Image;

/// <summary>
/// PNG 分块（瓦片）描述：整幅 1:1×OutputZoom 画布超出位图像素上限且未开启合并时，
/// 按网格切分为若干瓦片文件，每个瓦片对应一张 PNG。
/// 坐标系：像素、原点左上、+x 向右 +y 向下（与位图一致）。
/// 瓦片是整幅画布的一个矩形子区域（px0,py0 为子区域左上角在整幅画布中的像素偏移，
/// tilePxW/tilePxH 为子区域像素尺寸），渲染时把整幅场景画进该子区域画布（越界像素自动裁剪）。
/// 故每张瓦片的「0 点」（子区域左上角）与「对角点」（子区域右下角）对应的真实数据坐标可由
/// 整幅画布包围盒（ImageExportResult.DataX0/X1/Y0/Y1）线性反算。
/// </summary>
public sealed class ImageTile
{
    /// <summary>瓦片 PNG 文件名（不含目录）。</summary>
    public string FileName { get; init; } = "";

    /// <summary>瓦片 PNG 绝对路径。</summary>
    public string FilePath { get; init; } = "";

    /// <summary>瓦片像素宽。</summary>
    public int TilePxW { get; init; }

    /// <summary>瓦片像素高。</summary>
    public int TilePxH { get; init; }

    /// <summary>瓦片左上角在整幅画布中的像素 X 偏移。</summary>
    public int Px0 { get; init; }

    /// <summary>瓦片左上角在整幅画布中的像素 Y 偏移。</summary>
    public int Py0 { get; init; }
}
