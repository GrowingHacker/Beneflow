using System.Text;

namespace Beneflow.Api.Utils;

/// <summary>
/// 拼音码生成工具：取中文名称的拼音首字母，用于收银台快速搜索。
/// 例如 "花生米" → "HSM"，"矿泉水" → "KQS"。
/// 使用 GB2312 编码区间查表，覆盖常用汉字。
/// </summary>
public static class PinyinHelper
{
    /// <summary>生成拼音首字母码（与数据库 PinyinCode 列 nvarchar(20) 对齐，超长截断）</summary>
    public static string GetPinyinCode(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var sb = new StringBuilder();
        foreach (var ch in name!.Trim())
        {
            if (ch is >= 'a' and <= 'z') { sb.Append(char.ToUpper(ch)); continue; }
            if (ch is >= 'A' and <= 'Z') { sb.Append(ch); continue; }
            if (ch is >= '0' and <= '9') { sb.Append(ch); continue; }

            var letter = GetFirstLetter(ch);
            if (!string.IsNullOrEmpty(letter)) sb.Append(letter);
        }
        var code = sb.ToString().ToUpper();
        return code.Length > 20 ? code[..20] : code;
    }

    /// <summary>
    /// 单个汉字 → 拼音首字母。
    /// 利用 GB2312 编码中汉字按拼音排序的特性，通过编码区间查表。
    /// 区间边界已用边界汉字逐一实证：A 起于 B0A1(啊)，Z 终于 D7F9(座)。
    /// </summary>
    private static string GetFirstLetter(char ch)
    {
        // 注册 GB2312 编码（.NET Core/5+ 需要手动注册）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb = Encoding.GetEncoding("GB2312");
        var bytes = gb.GetBytes(new[] { ch });
        if (bytes.Length < 2) return "";

        var code = bytes[0] << 8 | bytes[1];
        if (code < 0xB0A1 || code > 0xD7F9) return "";   // 非 GB2312 一级/二级汉字（含 GBK 扩展区，无拼音序）
        return code switch
        {
            >= 0xB0A1 and <= 0xB0C4 => "A",   // 啊 … 澳
            >= 0xB0C5 and <= 0xB2C0 => "B",   // 芭 … 怖
            >= 0xB2C1 and <= 0xB4ED => "C",   // 擦 … 错
            >= 0xB4EE and <= 0xB6E9 => "D",   // 搭 … 堕
            >= 0xB6EA and <= 0xB7A1 => "E",   // 蛾 … 贰
            >= 0xB7A2 and <= 0xB8C0 => "F",   // 发 … 咐
            >= 0xB8C1 and <= 0xB9FD => "G",   // 噶 … 过
            >= 0xB9FE and <= 0xBBF6 => "H",   // 哈 … 祸
            >= 0xBBF7 and <= 0xBFA5 => "J",   // 击 … 骏
            >= 0xBFA6 and <= 0xC0AB => "K",   // 喀 … 阔
            >= 0xC0AC and <= 0xC2E7 => "L",   // 垃 … 络
            >= 0xC2E8 and <= 0xC4C2 => "M",   // 妈 … 穆
            >= 0xC4C3 and <= 0xC5B5 => "N",   // 拿 … 诺
            >= 0xC5B6 and <= 0xC5BD => "O",   // 哦 … 沤
            >= 0xC5BE and <= 0xC6D9 => "P",   // 啪 … 瀑
            >= 0xC6DA and <= 0xC8BA => "Q",   // 期 … 群
            >= 0xC8BB and <= 0xC8F5 => "R",   // 然 … 弱
            >= 0xC8F6 and <= 0xCBF9 => "S",   // 撒 … 所
            >= 0xCBFA and <= 0xCDD9 => "T",   // 塌 … 唾
            >= 0xCDDA and <= 0xCEF3 => "W",   // 挖 … 误
            >= 0xCEF4 and <= 0xD1B8 => "X",   // 昔 … 
            >= 0xD1B9 and <= 0xD4D0 => "Y",   // 压 … 孕
            >= 0xD4D1 and <= 0xD7F9 => "Z",   // 匝 … 座
            _ => ""
        };
    }
}
