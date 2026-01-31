using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBridge.Services;

/// <summary>
/// 终端输出缓冲器 - 合并高频输出，避免消息乱序和碎片化
/// </summary>
public class TerminalOutputBuffer : IDisposable
{
    private readonly ConcurrentDictionary<string, TabBuffer> _buffers = new();
    private readonly Func<string, string, Task> _sendFunc;
    private readonly int _flushIntervalMs;
    private readonly Timer _flushTimer;
    private readonly AnsiOutputFilter? _outputFilter;
    private bool _disposed;

    /// <summary>
    /// 是否启用移动端过滤模式
    /// </summary>
    public bool EnableMobileFilter { get; set; } = true;

    /// <summary>
    /// 创建输出缓冲器
    /// </summary>
    /// <param name="sendFunc">发送函数 (tabId, output) -> Task</param>
    /// <param name="flushIntervalMs">刷新间隔（毫秒），默认 50ms</param>
    /// <param name="enableMobileFilter">是否启用移动端过滤</param>
    public TerminalOutputBuffer(Func<string, string, Task> sendFunc, int flushIntervalMs = 50, bool enableMobileFilter = true)
    {
        _sendFunc = sendFunc;
        _flushIntervalMs = flushIntervalMs;
        _flushTimer = new Timer(FlushAll, null, flushIntervalMs, flushIntervalMs);

        if (enableMobileFilter)
        {
            _outputFilter = AnsiOutputFilter.CreateForMobile();
        }
        EnableMobileFilter = enableMobileFilter;
    }

    /// <summary>
    /// 添加输出到缓冲区
    /// </summary>
    public void Append(string tabId, string output)
    {
        if (_disposed || string.IsNullOrEmpty(output)) return;

        var buffer = _buffers.GetOrAdd(tabId, _ => new TabBuffer());
        buffer.Append(output);
    }

    /// <summary>
    /// 定时刷新所有缓冲区
    /// </summary>
    private void FlushAll(object? state)
    {
        if (_disposed) return;

        try
        {
            foreach (var kvp in _buffers)
            {
                var tabId = kvp.Key;
                var buffer = kvp.Value;

                var output = buffer.FlushAndGet();
                if (!string.IsNullOrEmpty(output))
                {
                    // 顺序发送，不使用 Fire-and-Forget
                    _ = SendSequentialAsync(tabId, output);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TerminalOutputBuffer] FlushAll 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 顺序发送（使用锁确保同一 Tab 的消息按顺序发送）
    /// </summary>
    private async Task SendSequentialAsync(string tabId, string output)
    {
        if (_disposed) return;

        try
        {
            if (_buffers.TryGetValue(tabId, out var buffer))
            {
                await buffer.SendLock.WaitAsync();
                try
                {
                    if (_disposed) return;

                    // 应用移动端过滤器
                    var filteredOutput = output;
                    if (EnableMobileFilter && _outputFilter != null)
                    {
                        filteredOutput = _outputFilter.Filter(output);
                    }

                    // 调试日志：查看实际传输的数据
                    System.Diagnostics.Debug.WriteLine($"[RemoteOutput] TabId={tabId}, Original={output.Length}, Filtered={filteredOutput.Length}");
                    await _sendFunc(tabId, filteredOutput);
                }
                finally
                {
                    buffer.SendLock.Release();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // 对象已释放，忽略
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TerminalOutputBuffer] SendSequentialAsync 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 截断字符串用于日志显示
    /// </summary>
    private static string TruncateForLog(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        // 转义控制字符
        var escaped = s.Replace("\x1b", "\\e").Replace("\r", "\\r").Replace("\n", "\\n");
        return escaped.Length > 100 ? escaped.Substring(0, 100) + "..." : escaped;
    }

    /// <summary>
    /// 立即刷新指定 Tab 的缓冲区
    /// </summary>
    public async Task FlushAsync(string tabId)
    {
        if (_buffers.TryGetValue(tabId, out var buffer))
        {
            var output = buffer.FlushAndGet();
            if (!string.IsNullOrEmpty(output))
            {
                await SendSequentialAsync(tabId, output);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();

        // 最后刷新一次
        foreach (var kvp in _buffers)
        {
            var output = kvp.Value.FlushAndGet();
            if (!string.IsNullOrEmpty(output))
            {
                _ = _sendFunc(kvp.Key, output);
            }
            kvp.Value.Dispose();
        }
        _buffers.Clear();
    }

    /// <summary>
    /// 单个 Tab 的缓冲区
    /// </summary>
    private class TabBuffer : IDisposable
    {
        private readonly StringBuilder _sb = new();
        private readonly object _lock = new();
        public readonly SemaphoreSlim SendLock = new(1, 1);

        public void Append(string text)
        {
            lock (_lock)
            {
                _sb.Append(text);
            }
        }

        public string FlushAndGet()
        {
            lock (_lock)
            {
                if (_sb.Length == 0) return string.Empty;
                var result = _sb.ToString();
                _sb.Clear();
                return result;
            }
        }

        public void Dispose()
        {
            SendLock.Dispose();
        }
    }
}
