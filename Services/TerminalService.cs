using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeBridge.Models;
using CodeBridge.Native;

namespace CodeBridge.Services;

/// <summary>
/// 终端服务 - 管理 ConPTY 会话
/// </summary>
public class TerminalService : IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

    public class TerminalSession : IDisposable
    {
        public string Id { get; }
        public ConPtyNative.PseudoConsole PseudoConsole { get; }
        public JobObjectNative.JobObject JobObject { get; }
        public Process Process { get; }
        public event Action<string>? OutputReceived;

        private readonly CancellationTokenSource _cts = new();

        // 用于检测 -c 失败并自动重试
        private bool _pendingContinueRetry = false;
        private readonly object _retryLock = new();

        public TerminalSession(string id, ConPtyNative.PseudoConsole pty, JobObjectNative.JobObject job, Process process)
        {
            Id = id;
            PseudoConsole = pty;
            JobObject = job;
            Process = process;

            // 启动输出读取任务
            Task.Run(async () => await ReadOutputAsync(_cts.Token));
        }

        /// <summary>
        /// 标记需要检测 -c 失败
        /// </summary>
        public void EnableContinueRetryDetection()
        {
            lock (_retryLock)
            {
                _pendingContinueRetry = true;
            }

            // 3 秒后自动取消检测（防止误触发）
            Task.Delay(3000).ContinueWith(_ =>
            {
                lock (_retryLock)
                {
                    _pendingContinueRetry = false;
                }
            });
        }

        private async Task ReadOutputAsync(CancellationToken ct)
        {
            try
            {
                var buffer = new char[4096];
                while (!ct.IsCancellationRequested)
                {
                    var read = await PseudoConsole.Output.ReadAsync(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        var output = new string(buffer, 0, read);

                        // 检测 -c 失败并自动重试
                        bool shouldRetry = false;
                        lock (_retryLock)
                        {
                            if (_pendingContinueRetry && output.Contains("No conversation found to continue"))
                            {
                                _pendingContinueRetry = false;
                                shouldRetry = true;
                            }
                        }

                        OutputReceived?.Invoke(output);

                        // 延迟重试（不带 -c）
                        if (shouldRetry)
                        {
                            _ = Task.Delay(500).ContinueWith(_ =>
                            {
                                SendInput("claude --dangerously-skip-permissions\r\n");
                            });
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // 忽略读取异常
            }
        }

        public void SendInput(string input)
        {
            PseudoConsole.Input.Write(input);
        }

        public void Resize(int cols, int rows)
        {
            PseudoConsole.Resize(cols, rows);
        }

        public void Dispose()
        {
            _cts.Cancel();

            try
            {
                if (!Process.HasExited)
                    Process.Kill();
            }
            catch { }

            JobObject.Dispose();
            PseudoConsole.Dispose();
            Process.Dispose();
        }
    }

    public TerminalSession Start(TabConfig config, Action<string> onOutput, string shellType = "powershell")
    {
        var sessionId = config.Id;

        // 创建伪终端
        var pty = ConPtyNative.PseudoConsole.Create(80, 24);

        // 创建 Job Object
        var job = JobObjectNative.JobObject.Create();

        // 选择可用的工作目录（空/无效路径会导致子进程立刻退出）
        var workingDirectory = ResolveWorkingDirectory(config.WorkingDirectory);

        // 根据用户设置选择 Shell
        var commandLine = GetShellCommandLine(shellType);
        var process = ConPtyNative.CreateProcess(
            commandLine,
            workingDirectory,
            pty.Handle,
            out var processHandle);

        // 分配到 Job Object
        try
        {
            job.AssignProcess(processHandle);
        }
        finally
        {
            processHandle.Dispose();
        }

        // 创建会话
        var session = new TerminalSession(sessionId, pty, job, process);
        session.OutputReceived += onOutput;

        _sessions[sessionId] = session;

        // 注入环境变量，供 Claude Hook 脚本识别标签页
        // 根据 Shell 类型使用不同的语法
        Task.Delay(300).ContinueWith(_ =>
        {
            string setEnvCommand;
            if (shellType.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                // CMD 语法：set VAR=value
                var escapedCwd = config.WorkingDirectory.Replace("\"", "\"\"");
                setEnvCommand = $"set MYAIHELPER_TAB_ID={config.Id}&& set MYAIHELPER_CWD={escapedCwd}&& cls\r\n";
            }
            else
            {
                // PowerShell 语法：$env:VAR='value'
                setEnvCommand = $"$env:MYAIHELPER_TAB_ID='{config.Id}'; $env:MYAIHELPER_CWD='{config.WorkingDirectory.Replace("'", "''")}'; Clear-Host\r\n";
            }
            session.SendInput(setEnvCommand);
        });

        // 自动执行 Claude 命令
        if (config.AutoRunClaude)
        {
            Task.Delay(800).ContinueWith(_ =>
            {
                // 如果启用了继续会话，则添加 -c 参数
                if (config.ContinueSession)
                {
                    // 启用 -c 失败检测，自动重试
                    session.EnableContinueRetryDetection();
                    session.SendInput("claude --dangerously-skip-permissions -c\r\n");
                }
                else
                {
                    session.SendInput("claude --dangerously-skip-permissions\r\n");
                }
            });
        }

        return session;
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            return workingDirectory;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
            return userProfile;

        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// 获取 Shell 命令行
    /// </summary>
    /// <param name="shellType">Shell 类型：powershell 或 cmd</param>
    private static string GetShellCommandLine(string shellType = "powershell")
    {
        if (shellType.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            // 使用 cmd.exe
            return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        }

        // 默认使用 PowerShell
        // 优先: PowerShell 7 (pwsh.exe)
        var pwshPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe"),
        };

        foreach (var path in pwshPaths)
        {
            if (File.Exists(path))
                return $"\"{path}\" -NoLogo";
        }

        // 尝试从 PATH 中查找 pwsh.exe
        var pwshInPath = FindInPath("pwsh.exe");
        if (!string.IsNullOrEmpty(pwshInPath))
            return $"\"{pwshInPath}\" -NoLogo";

        // 回退: PowerShell 5 (powershell.exe)
        var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershellPath))
            return $"\"{powershellPath}\" -NoLogo";

        // 最终回退: cmd.exe
        return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    }

    private static string? FindInPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var path in pathEnv.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(path, executable);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    private static string BuildCommandLine(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return "cmd.exe";

        if (executable.Contains(' ') && !executable.StartsWith('"'))
            return $"\"{executable}\"";

        return executable;
    }

    public void SendInput(string sessionId, string input)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.SendInput(input);
        }
    }

    public void Resize(string sessionId, int cols, int rows)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Resize(cols, rows);
        }
    }

    public void Stop(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}
