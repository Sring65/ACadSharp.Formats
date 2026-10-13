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

namespace ACadSharp.Formats.Pdf;

/// <summary>
/// PDF 多字体管理：默认 SimHei + 按样式/内联 \f 按需加载其他 TTF，分别内嵌。
/// 每个字体占一个 /F&lt;n&gt; 槽位；未知族/无 TTF（SHX/.ttc/缺失）回退默认槽位。
/// </summary>
internal sealed class PdfFontManager
{
    public CjkFont Default; // SimHei（或 null）
    private readonly List<CjkFont> _fonts = new();
    private readonly Dictionary<string, CjkFont> _byFamily = new();
    private readonly Dictionary<CjkFont, int> _slotByFont = new();
    private readonly Action<string> _log;

    public PdfFontManager(Action<string> log)
    {
        _log = log;
        Default = CjkFont.TryLoad(log);
        if (Default != null)
        {
            _fonts.Add(Default);
            _slotByFont[Default] = 1;
        }
    }

    public bool HasFont => Default != null;
    public int Count => _fonts.Count;
    // 越界/空列表返回 null（无 CJK 字体时 _fonts 为空，调用方按 null 走 Helvetica 回退；
    // 旧实现 _fonts[slot-1] 在无字体时抛 IndexOutOfRangeException 导致 PDF 整体导出失败）。
    public CjkFont FontAt(int slot) => slot >= 1 && slot <= _fonts.Count ? _fonts[slot - 1] : null;

    // 该族名对应的 /F&lt;n&gt; 槽位（未知/无 TTF → 默认槽位）。
    public int GetSlot(string family)
    {
        CjkFont f = GetFont(family);
        return SlotOf(f);
    }

    // 该族名对应的 CjkFont（未知/无 TTF → 默认）。
    public CjkFont GetFont(string family)
    {
        if (Default == null) return null;
        if (string.IsNullOrEmpty(family)) return Default;
        if (_byFamily.TryGetValue(family, out var f)) return f;
        string path = FindTtf(family);
        // 与默认字体同一文件 → 直接复用，避免重复加载大字体
        if (path != null && Default != null && string.Equals(path, Default.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            _byFamily[family] = Default;
            return Default;
        }
        CjkFont loaded = null;
        if (path != null && path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            loaded = CjkFont.TryLoadFile(path, _log);
        if (loaded == null || loaded.SourcePath == Default.SourcePath)
        {
            _byFamily[family] = Default;
            return Default;
        }
        _byFamily[family] = loaded;
        _fonts.Add(loaded);
        _slotByFont[loaded] = _fonts.Count;
        return loaded;
    }

    public int SlotOf(CjkFont font)
    {
        if (font == null) return 1;
        return _slotByFont.TryGetValue(font, out int s) ? s : 1;
    }

    // 实际用到的字体族名（去重，按槽位顺序），用于日志。
    public List<string> UsedFontNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (var f in _fonts)
            if (seen.Add(f.FontName)) names.Add(f.FontName);
        return names;
    }

    // 标准族名 → 系统 TTF 文件路径（仅 .ttf；.ttc/SHX/缺失 → null）
    private static string FindTtf(string family)
    {
        if (string.IsNullOrEmpty(family)) return null;
        string[] candidates;
        switch (family)
        {
            case "SimHei": candidates = new[] { "simhei.ttf" }; break;
            case "SimSun": candidates = new[] { "simsun.ttf" }; break; // 系统为 .ttc，无独立 .ttf → 回退
            case "DengXian": candidates = new[] { "Deng.ttf" }; break;
            case "KaiTi": candidates = new[] { "simkai.ttf" }; break;
            case "FangSong": candidates = new[] { "simfang.ttf" }; break;
            case "SimLi": candidates = new[] { "SIMLI.TTF" }; break;
            case "Microsoft YaHei": candidates = new[] { "msyh.ttf" }; break; // .ttc → 回退
            default: candidates = new[] { family + ".ttf", family.ToLowerInvariant() + ".ttf" }; break;
        }
        return CjkFont.FindFontFile(candidates);
    }
}
