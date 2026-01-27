using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MyAiHelper.Services;

/// <summary>
/// IPC 服务 - 通过命名管道接收来自 Claude Hook 的通知
/// </summary>
public class IpcService : IDisposable
{
    private const string PipeName = "MyAiHelperPipe";
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;
    private bool _isDisposed = false;

    /// <summary>
    /// Hook 消息接收事件
    /// </summary>
    public event Action<HookMessage>? MessageReceived;

    /// <summary>
    /// Hook 消息数据结构
    /// </summary>
    public class HookMessage
    {
        public string Type { get; set; } = string.Empty;
        public string TabId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Severity { get; set; } = "info";
        public string Cwd { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public bool IsPersistent { get; set; } = true;
    }

    /// <summary>
    /// 启动 IPC 监听服务
    /// </summary>
    public void Start()
    {
        if (_listenerTask != null)
            return;

        _listenerTask = Task.Run(ListenAsync);
    }

    /// <summary>
    /// 监听命名管道
    /// </summary>
    private async Task ListenAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                // 等待客户端连接
                await server.WaitForConnectionAsync(_cts.Token);

                // 读取消息
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync();

                if (!string.IsNullOrEmpty(line))
                {
                    ProcessMessage(line);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，退出循环
                break;
            }
            catch (Exception ex)
            {
                // 记录错误但继续监听
                System.Diagnostics.Debug.WriteLine($"[IpcService] Error: {ex.Message}");
                await Task.Delay(100, _cts.Token); // 短暂延迟后重试
            }
        }
    }

    /// <summary>
    /// 处理接收到的消息
    /// </summary>
    private void ProcessMessage(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var message = JsonSerializer.Deserialize<HookMessage>(json, options);

            if (message != null)
            {
                // 在 UI 线程触发事件
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    MessageReceived?.Invoke(message);
                });
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IpcService] JSON parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// 停止 IPC 服务
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _cts.Dispose();
    }
}
