using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MyAiHelper.Models;
using MyAiHelper.Native;

namespace MyAiHelper.Services;

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

        public TerminalSession(string id, ConPtyNative.PseudoConsole pty, JobObjectNative.JobObject job, Process process)
        {
            Id = id;
            PseudoConsole = pty;
            JobObject = job;
            Process = process;

            // 启动输出读取任务
            Task.Run(async () => await ReadOutputAsync(_cts.Token));
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
                        OutputReceived?.Invoke(output);
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

    public TerminalSession Start(TabConfig config, Action<string> onOutput)
    {
        var sessionId = config.Id;

        // 创建伪终端
        var pty = ConPtyNative.PseudoConsole.Create(80, 24);

        // 创建 Job Object
        var job = JobObjectNative.JobObject.Create();

        // 选择可用的工作目录（空/无效路径会导致子进程立刻退出）
        var workingDirectory = ResolveWorkingDirectory(config.WorkingDirectory);

        // 启动 PowerShell 7 (pwsh.exe) - 提供更好的终端体验
        var commandLine = GetShellCommandLine();
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
        Task.Delay(300).ContinueWith(_ =>
        {
            var setEnvCommand = $"$env:MYAIHELPER_TAB_ID='{config.Id}'; $env:MYAIHELPER_CWD='{config.WorkingDirectory.Replace("'", "''")}'; Clear-Host\r\n";
            session.SendInput(setEnvCommand);
        });

        // 自动执行 Claude 命令
        if (config.AutoRunClaude)
        {
            Task.Delay(800).ContinueWith(_ =>
            {
                // 如果启用了继续会话，则添加 -c 参数
                var command = config.ContinueSession
                    ? "claude --dangerously-skip-permissions -c\r\n"
                    : "claude --dangerously-skip-permissions\r\n";
                session.SendInput(command);
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
    /// 获取 Shell 命令行，优先使用 PowerShell 7，回退到 PowerShell 5，最后使用 cmd.exe
    /// </summary>
    private static string GetShellCommandLine()
    {
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
