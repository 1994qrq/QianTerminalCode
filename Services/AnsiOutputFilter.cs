using System;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeBridge.Services;

/// <summary>
/// ANSI 输出过滤器 - 为移动端简化终端输出
/// </summary>
public class AnsiOutputFilter
{
    // 保留的 ANSI 序列（颜色和基本格式）
    // SGR (Select Graphic Rendition): ESC[...m
    private static readonly Regex SgrPattern = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    // 需要移除的复杂 ANSI 序列
    private static readonly Regex[] RemovePatterns = new[]
    {
        // 光标移动: ESC[nA (上), ESC[nB (下), ESC[nC (右), ESC[nD (左)
        new Regex(@"\x1b\[\d*[ABCD]", RegexOptions.Compiled),

        // 光标定位: ESC[n;nH 或 ESC[n;nf
        new Regex(@"\x1b\[\d*;\d*[Hf]", RegexOptions.Compiled),
        new Regex(@"\x1b\[\d*[Hf]", RegexOptions.Compiled),

        // 清屏/清行: ESC[nJ, ESC[nK
        new Regex(@"\x1b\[\d*[JK]", RegexOptions.Compiled),

        // 滚动: ESC[nS (上), ESC[nT (下)
        new Regex(@"\x1b\[\d*[ST]", RegexOptions.Compiled),

        // 保存/恢复光标: ESC[s, ESC[u, ESC 7, ESC 8
        new Regex(@"\x1b\[[su]", RegexOptions.Compiled),
        new Regex(@"\x1b[78]", RegexOptions.Compiled),

        // 光标显示/隐藏: ESC[?25h, ESC[?25l
        new Regex(@"\x1b\[\?\d+[hl]", RegexOptions.Compiled),

        // 其他私有模式序列
        new Regex(@"\x1b\[\?\d+[a-zA-Z]", RegexOptions.Compiled),

        // OSC 序列 (标题等): ESC]...BEL 或 ESC]...ESC\
        new Regex(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", RegexOptions.Compiled),

        // DCS 序列: ESC P...ESC\
        new Regex(@"\x1bP[^\x1b]*\x1b\\", RegexOptions.Compiled),
    };

    // Unicode 绘图字符替换表
    private static readonly (string From, string To)[] CharReplacements = new[]
    {
        // Box Drawing 字符 -> ASCII
        ("─", "-"),
        ("│", "|"),
        ("┌", "+"),
        ("┐", "+"),
        ("└", "+"),
        ("┘", "+"),
        ("├", "+"),
        ("┤", "+"),
        ("┬", "+"),
        ("┴", "+"),
        ("┼", "+"),
        ("═", "="),
        ("║", "|"),
        ("╔", "+"),
        ("╗", "+"),
        ("╚", "+"),
        ("╝", "+"),

        // 常见 Emoji/符号 -> ASCII
        ("▶", ">"),
        ("◀", "<"),
        ("►", ">"),
        ("◄", "<"),
        ("▸", ">"),
        ("◂", "<"),
        ("●", "*"),
        ("○", "o"),
        ("◉", "*"),
        ("◎", "o"),
        ("★", "*"),
        ("☆", "*"),
        ("✓", "[v]"),
        ("✔", "[v]"),
        ("✗", "[x]"),
        ("✘", "[x]"),
        ("⚠", "[!]"),
        ("❌", "[x]"),
        ("✅", "[v]"),
        ("🔥", "[fire]"),
        ("🚀", "[rocket]"),
        ("💡", "[idea]"),
        ("📦", "[pkg]"),
        ("🔧", "[tool]"),
        ("⚡", "[zap]"),
        ("🎯", "[target]"),
        ("📝", "[note]"),
        ("🔍", "[search]"),
        ("⏳", "[wait]"),
        ("✨", "*"),
        ("🎉", "[!]"),
        ("👍", "[+1]"),
        ("👎", "[-1]"),
        ("🤖", "[bot]"),
        ("💻", "[pc]"),
        ("📁", "[dir]"),
        ("📄", "[file]"),

        // Spinner 字符
        ("⠋", "|"),
        ("⠙", "/"),
        ("⠹", "-"),
        ("⠸", "\\"),
        ("⠼", "|"),
        ("⠴", "/"),
        ("⠦", "-"),
        ("⠧", "\\"),
        ("⠇", "|"),
        ("⠏", "/"),

        // Powerline 符号 (使用 Unicode 码点)
        ("\uE0B0", ">"),  //
        ("\uE0B2", "<"),  //
        ("\uE0B1", ">"),  //
        ("\uE0B3", "<"),  //
    };

    /// <summary>
    /// 过滤模式
    /// </summary>
    public enum FilterMode
    {
        /// <summary>
        /// 不过滤，原样输出
        /// </summary>
        None,

        /// <summary>
        /// 轻度过滤：移除光标移动序列，保留颜色
        /// </summary>
        Light,

        /// <summary>
        /// 中度过滤：移除复杂序列 + 替换 Unicode 绘图字符
        /// </summary>
        Medium,

        /// <summary>
        /// 重度过滤：纯文本模式，移除所有 ANSI 序列
        /// </summary>
        Heavy
    }

    private readonly FilterMode _mode;

    public AnsiOutputFilter(FilterMode mode = FilterMode.Medium)
    {
        _mode = mode;
    }

    /// <summary>
    /// 过滤终端输出
    /// </summary>
    public string Filter(string input)
    {
        if (string.IsNullOrEmpty(input) || _mode == FilterMode.None)
            return input;

        var result = input;

        switch (_mode)
        {
            case FilterMode.Light:
                result = RemoveCursorMovement(result);
                break;

            case FilterMode.Medium:
                result = RemoveComplexSequences(result);
                result = HandleCarriageReturn(result);
                result = ReplaceUnicodeChars(result);
                break;

            case FilterMode.Heavy:
                result = RemoveAllAnsi(result);
                result = HandleCarriageReturn(result);
                result = ReplaceUnicodeChars(result);
                break;
        }

        // 清理多余的空行和行尾空格
        result = CleanupOutput(result);

        return result;
    }

    /// <summary>
    /// 处理回车符导致的行覆盖问题
    /// Spinner 动画使用 \r 覆盖同一行，在移动端会造成混乱
    /// </summary>
    private static string HandleCarriageReturn(string input)
    {
        if (!input.Contains('\r'))
            return input;

        var result = new StringBuilder();
        var lines = input.Split('\n');

        foreach (var line in lines)
        {
            if (line.Contains('\r'))
            {
                // 处理包含 \r 的行：取最后一个 \r 后面的内容
                var lastCrIndex = line.LastIndexOf('\r');
                var content = line.Substring(lastCrIndex + 1);

                // 如果有内容就添加
                if (!string.IsNullOrWhiteSpace(StripAnsi(content)))
                {
                    result.AppendLine(content.TrimEnd());
                }
            }
            else
            {
                // 普通行直接添加
                result.AppendLine(line);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// 仅移除光标移动序列
    /// </summary>
    private static string RemoveCursorMovement(string input)
    {
        var result = input;

        // 只移除光标移动相关的序列
        result = Regex.Replace(result, @"\x1b\[\d*[ABCD]", "");
        result = Regex.Replace(result, @"\x1b\[\d*;\d*[Hf]", "");
        result = Regex.Replace(result, @"\x1b\[\d*[Hf]", "");

        return result;
    }

    /// <summary>
    /// 移除复杂的 ANSI 序列，保留颜色
    /// </summary>
    private static string RemoveComplexSequences(string input)
    {
        var result = input;

        foreach (var pattern in RemovePatterns)
        {
            result = pattern.Replace(result, "");
        }

        return result;
    }

    /// <summary>
    /// 移除所有 ANSI 序列
    /// </summary>
    private static string RemoveAllAnsi(string input)
    {
        // 移除所有 ESC 开头的序列
        return Regex.Replace(input, @"\x1b\[[0-9;]*[a-zA-Z]|\x1b[^\[0-9a-zA-Z]?", "");
    }

    /// <summary>
    /// 替换 Unicode 绘图字符为 ASCII
    /// </summary>
    private static string ReplaceUnicodeChars(string input)
    {
        var result = input;

        foreach (var (from, to) in CharReplacements)
        {
            result = result.Replace(from, to);
        }

        return result;
    }

    /// <summary>
    /// 清理输出：移除多余空行、行尾空格
    /// </summary>
    private static string CleanupOutput(string input)
    {
        var lines = input.Split('\n');
        var sb = new StringBuilder();
        var consecutiveEmpty = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();

            if (string.IsNullOrWhiteSpace(StripAnsi(trimmed)))
            {
                consecutiveEmpty++;
                // 最多保留 2 个连续空行
                if (consecutiveEmpty <= 2)
                {
                    sb.AppendLine();
                }
            }
            else
            {
                consecutiveEmpty = 0;
                sb.AppendLine(trimmed);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 去除 ANSI 序列（用于判断是否为空行）
    /// </summary>
    private static string StripAnsi(string input)
    {
        return Regex.Replace(input, @"\x1b\[[0-9;]*[a-zA-Z]", "");
    }

    /// <summary>
    /// 创建适合移动端的过滤器
    /// </summary>
    public static AnsiOutputFilter CreateForMobile() => new(FilterMode.Medium);

    /// <summary>
    /// 创建纯文本过滤器
    /// </summary>
    public static AnsiOutputFilter CreatePlainText() => new(FilterMode.Heavy);
}
