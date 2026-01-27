using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;

namespace MyAiHelper.Services;

/// <summary>
/// 任务完成检测器 - 监听终端输出并识别任务完成/空闲信号
/// </summary>
public class TaskCompletionDetector : IDisposable
{
    /// <summary>
    /// 任务完成事件参数
    /// </summary>
    public class TaskCompletedEventArgs : EventArgs
    {
        public string TabId { get; set; } = string.Empty;
        public int ExitCode { get; set; }
        public string RawLine { get; set; } = string.Empty;
        public DateTime CompletedAtUtc { get; set; } = DateTime.UtcNow;
        public bool IsIdleTimeout { get; set; } // 是否为空闲超时触发
    }

    /// <summary>
    /// 任务完成事件
    /// </summary>
    public event EventHandler<TaskCompletedEventArgs>? TaskCompleted;

    private readonly string _tabId;
    private readonly StringBuilder _lineBuffer = new();
    private readonly Timer _idleTimer;
    private bool _isDisposed = false;
    private bool _hasTriggeredIdle = false; // 防止重复触发
    private DateTime _lastOutputTime = DateTime.UtcNow;

    // ANSI 转义序列正则（用于剥离颜色等控制字符）
    private static readonly Regex AnsiRegex = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\].*?(\x07|\x1B\\)",
        RegexOptions.Compiled);

    // 哨兵格式正则：[[CLAUDE_DONE:<tabId>:<exitCode>]]
    private static readonly Regex SentinelRegex = new(
        @"^\[\[CLAUDE_DONE:(?<tab>[^:]+):(?<code>-?\d+)\]\]$",
        RegexOptions.Compiled);

    // Claude Code 完成/等待输入特征正则
    private static readonly Regex[] CompletionPatterns = new[]
    {
        // Claude Code 任务完成标志
        new Regex(@"◇\s*(Task completed|任务完成)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"✓\s*(Done|完成|Completed)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // Claude 退出消息
        new Regex(@"(Goodbye|再见|Session ended)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // Claude 等待用户输入的提示符
        new Regex(@"^>\s*$", RegexOptions.Compiled),  // Claude 的输入提示符
        new Regex(@"❯\s*$", RegexOptions.Compiled),   // 终端提示符
        new Regex(@"^\$\s*$", RegexOptions.Compiled), // bash 提示符
    };

    /// <summary>
    /// 是否启用启发式检测（默认启用）
    /// </summary>
    public bool EnableHeuristics { get; set; } = true;

    /// <summary>
    /// 空闲超时时间（秒），超过此时间无输出则触发通知
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 是否启用空闲超时检测（默认关闭，需要手动启用）
    /// </summary>
    public bool EnableIdleTimeout { get; set; } = false;

    /// <summary>
    /// 是否已收到过输出（用于防止启动时误触发）
    /// </summary>
    private bool _hasReceivedOutput = false;

    public TaskCompletionDetector(string tabId)
    {
        _tabId = tabId;

        // 初始化空闲检测定时器
        _idleTimer = new Timer(1000); // 每秒检查一次
        _idleTimer.Elapsed += OnIdleTimerElapsed;
        _idleTimer.AutoReset = true;
        _idleTimer.Start();
    }

    /// <summary>
    /// 空闲检测定时器回调
    /// </summary>
    private void OnIdleTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        // 必须已收到过输出，才能触发空闲检测（防止启动时误触发）
        if (_isDisposed || !EnableIdleTimeout || _hasTriggeredIdle || !_hasReceivedOutput)
            return;

        var idleSeconds = (DateTime.UtcNow - _lastOutputTime).TotalSeconds;
        if (idleSeconds >= IdleTimeoutSeconds)
        {
            _hasTriggeredIdle = true;
            OnTaskCompleted(0, "[Idle Timeout]", isIdleTimeout: true);
        }
    }

    /// <summary>
    /// 处理终端输出块
    /// </summary>
    /// <param name="output">原始输出内容（可能包含ANSI和部分行）</param>
    public void ProcessOutput(string output)
    {
        if (string.IsNullOrEmpty(output) || _isDisposed)
            return;

        // 标记已收到输出
        _hasReceivedOutput = true;

        // 更新最后输出时间，重置空闲检测
        _lastOutputTime = DateTime.UtcNow;
        _hasTriggeredIdle = false; // 有新输出，重置空闲触发标记

        // 追加到行缓冲区
        _lineBuffer.Append(output);

        // 按换行符分割处理完整行
        var content = _lineBuffer.ToString();
        var lines = content.Split('\n');

        // 最后一个元素可能是不完整的行，保留在缓冲区
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].TrimEnd('\r');
            ProcessLine(line);
        }

        // 保留最后一个不完整的行
        _lineBuffer.Clear();
        _lineBuffer.Append(lines[^1]);
    }

    /// <summary>
    /// 处理单行输出
    /// </summary>
    private void ProcessLine(string rawLine)
    {
        if (_isDisposed) return;

        // 剥离 ANSI 转义序列
        var cleanLine = StripAnsi(rawLine).Trim();

        if (string.IsNullOrEmpty(cleanLine))
            return;

        // 1. 优先检测哨兵（最可靠）
        var sentinelMatch = SentinelRegex.Match(cleanLine);
        if (sentinelMatch.Success)
        {
            var tabId = sentinelMatch.Groups["tab"].Value;
            var exitCode = int.Parse(sentinelMatch.Groups["code"].Value);

            // 只响应属于当前标签的哨兵
            if (tabId == _tabId)
            {
                OnTaskCompleted(exitCode, rawLine);
            }
            return;
        }

        // 2. 启发式检测（可选）
        if (EnableHeuristics)
        {
            foreach (var pattern in CompletionPatterns)
            {
                if (pattern.IsMatch(cleanLine))
                {
                    OnTaskCompleted(0, rawLine);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 剥离 ANSI 转义序列
    /// </summary>
    private static string StripAnsi(string input)
    {
        return AnsiRegex.Replace(input, string.Empty);
    }

    /// <summary>
    /// 触发任务完成事件
    /// </summary>
    private void OnTaskCompleted(int exitCode, string rawLine, bool isIdleTimeout = false)
    {
        if (_isDisposed) return;

        TaskCompleted?.Invoke(this, new TaskCompletedEventArgs
        {
            TabId = _tabId,
            ExitCode = exitCode,
            RawLine = rawLine,
            CompletedAtUtc = DateTime.UtcNow,
            IsIdleTimeout = isIdleTimeout
        });
    }

    /// <summary>
    /// 检查是否处于空闲状态
    /// </summary>
    public bool IsIdle => (DateTime.UtcNow - _lastOutputTime).TotalSeconds > IdleTimeoutSeconds;

    /// <summary>
    /// 重置检测器状态
    /// </summary>
    public void Reset()
    {
        _lineBuffer.Clear();
        _lastOutputTime = DateTime.UtcNow;
        _hasTriggeredIdle = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _idleTimer.Stop();
        _idleTimer.Dispose();
    }
}
