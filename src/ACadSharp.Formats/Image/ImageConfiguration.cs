using System;

namespace ACadSharp.Formats.Image;

/// <summary>
/// 图像导出配置（SVG/PDF 共用，T4）。
/// 画布/颜色/线型语义与 GDB2CAD.Core.DxfImageExporter（netDxf 输入版）严格一致，
/// 本导出器把输入换成 ACadSharp 文档（见 执行状态_全量ACadSharp.md T4）。
/// </summary>
public class ImageConfiguration
{
	/// <summary>
	/// 白色背景（默认 false = 黑底）。白底时 ACI 7（白/黑）显示为黑色（与 AutoCAD 一致），点标记反色。
	/// </summary>
	public bool WhiteBackground { get; set; }

	/// <summary>
	/// 透明背景（仅 PNG 光栅输出端生效；默认 false）。
	/// true = 画布不绘制背景色（保留位图 alpha 通道为全透明），适合把位图叠加到其他底图/网页；
	/// ACI 7（白/黑）仍按黑底语义渲染为白色（透明背景下白色最可读），点标记不再绘制底色光晕环。
	/// 与 <see cref="WhiteBackground"/> 互斥：同时为 true 时透明优先（透明背景没有"底色"可言）。
	/// SVG/PDF 输出端忽略此项（矢量格式背景由 <see cref="WhiteBackground"/> 控制，保持原有行为不变）。
	/// </summary>
	public bool TransparentBackground { get; set; }

	/// <summary>
	/// 输出放大倍数：内部画板 1:1（1 绘图单位 = 1 像素），最终画布整体放大该倍数
	/// （SVG width/height、PDF CTM+MediaBox）。默认 10（2026-09-23 用户要求）。
	/// </summary>
	public double OutputZoom { get; set; } = 10.0d;

	/// <summary>
	/// 日志回调（进度/告警）。
	/// </summary>
	public Action<string> Log { get; set; }

	/// <summary>
	/// 支持文件名（如 "AAA.SHX"、"simhei.ttf"，大小写不敏感）→ 绝对路径 的解析器。
	/// 由调用方提供搜索目录（Support\fonts + 祖先目录等）；找不到返回 null（调用方按回退策略处理）。
	/// </summary>
	public Func<string, string> FileProvider { get; set; }

	/// <summary>
	/// 弧线/圆采样精度（每整圆的细分点数下限，复杂线型走查另按弧长自适应）。
	/// </summary>
	public int ArcPrecision { get; set; } = 256;

	/// <summary>
	/// 变宽折线（逐顶点宽度 + bulge 弧段）偏移轮廓构建器（R13 架构拆分：算法在宿主程序
	/// GDB2CAD.Core.TaperOffset，本包只含渲染契约 <see cref="ITaperOffsetBuilder"/>）。
	/// null 时含 bulge 的锥形折线回退逐顶点弦偏移（R10 前行为）。
	/// </summary>
	public ITaperOffsetBuilder TaperOffsetBuilder { get; set; }

	internal void Notify(string message)
	{
		this.Log?.Invoke(message);
	}
}
