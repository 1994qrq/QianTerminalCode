using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CodeBridge.Services;

/// <summary>
/// Claude Hook 配置服务 - 检测、分析和配置 Claude Code 的 hooks
/// </summary>
public class HookConfigService
{
    private static readonly string ClaudeConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude");

    private static readonly string ClaudeSettingsPath = Path.Combine(ClaudeConfigDir, "settings.json");

    private const string HookScriptName = "notify-codebridge.ps1";

    /// <summary>
    /// Hook 配置诊断结果
    /// </summary>
    public class HookDiagnosis
    {
        // 基础状态
        public bool ClaudeConfigExists { get; set; }
        public bool SettingsFileExists { get; set; }
        public bool HookScriptExists { get; set; }
        public bool HookConfigured { get; set; }

        // JSON 分析结果
        public bool IsValidJson { get; set; }
        public string? JsonError { get; set; }
        public string? RawContent { get; set; }

        // 详细诊断
        public bool HasHooksSection { get; set; }
        public bool HasStopHook { get; set; }
        public bool StopHookHasCorrectMatcher { get; set; }
        public bool StopHookHasCorrectCommand { get; set; }
        public string? CurrentStopHookCommand { get; set; }
        public string? ExpectedStopHookCommand { get; set; }

        // 脚本内容检查
        public bool HookScriptIsValid { get; set; }
        public bool HookScriptHasFallback { get; set; }
        public string? HookScriptError { get; set; }

        // 问题列表
        public List<string> Issues { get; set; } = new();
        public List<string> Suggestions { get; set; } = new();

        // 备份信息
        public string? LastBackupPath { get; set; }

        // 状态
        public string Message { get; set; } = "";
        public DiagnosisStatus Status { get; set; } = DiagnosisStatus.Unknown;

        public bool IsFullyConfigured => ClaudeConfigExists && SettingsFileExists &&
                                          HookScriptExists && HookConfigured && IsValidJson &&
                                          HookScriptIsValid && HookScriptHasFallback;

        public bool NeedsRepair => SettingsFileExists && (!IsValidJson || !HookConfigured);
    }

    public enum DiagnosisStatus
    {
        Unknown,
        OK,
        Warning,
        Error,
        NotInstalled
    }

    /// <summary>
    /// 完整诊断 Hook 配置
    /// </summary>
    public HookDiagnosis DiagnoseHookConfiguration()
    {
        var diagnosis = new HookDiagnosis();
        var hookScriptPath = Path.Combine(ClaudeConfigDir, HookScriptName);

        // 生成期望的命令
        diagnosis.ExpectedStopHookCommand = $"powershell -ExecutionPolicy Bypass -File \"{hookScriptPath}\"";

        // 1. 检查 .claude 目录
        diagnosis.ClaudeConfigExists = Directory.Exists(ClaudeConfigDir);
        if (!diagnosis.ClaudeConfigExists)
        {
            diagnosis.Status = DiagnosisStatus.NotInstalled;
            diagnosis.Message = "未找到 Claude 配置目录";
            diagnosis.Issues.Add("~/.claude 目录不存在");
            diagnosis.Suggestions.Add("请先安装并运行一次 Claude Code");
            return diagnosis;
        }

        // 2. 检查 settings.json 是否存在
        diagnosis.SettingsFileExists = File.Exists(ClaudeSettingsPath);
        if (!diagnosis.SettingsFileExists)
        {
            diagnosis.Status = DiagnosisStatus.Warning;
            diagnosis.Message = "settings.json 不存在";
            diagnosis.Issues.Add("settings.json 文件不存在");
            diagnosis.Suggestions.Add("可以自动创建配置文件");
        }

        // 3. 检查 hook 脚本
        diagnosis.HookScriptExists = File.Exists(hookScriptPath);
        if (!diagnosis.HookScriptExists)
        {
            diagnosis.Issues.Add("Hook 脚本文件不存在");
            diagnosis.Suggestions.Add("需要创建 notify-codebridge.ps1 脚本");
        }
        else
        {
            // 验证脚本内容
            AnalyzeHookScript(diagnosis, hookScriptPath);
        }

        // 4. 分析 settings.json 内容
        if (diagnosis.SettingsFileExists)
        {
            AnalyzeSettingsJson(diagnosis);
        }

        // 5. 生成最终状态
        if (diagnosis.IsFullyConfigured)
        {
            diagnosis.Status = DiagnosisStatus.OK;
            diagnosis.Message = "Hook 配置正常，通知功能已启用";
        }
        else if (diagnosis.Issues.Count > 0)
        {
            diagnosis.Status = diagnosis.IsValidJson ? DiagnosisStatus.Warning : DiagnosisStatus.Error;
            diagnosis.Message = $"发现 {diagnosis.Issues.Count} 个问题需要修复";
        }

        return diagnosis;
    }

    /// <summary>
    /// 分析 settings.json 文件
    /// </summary>
    private void AnalyzeSettingsJson(HookDiagnosis diagnosis)
    {
        try
        {
            diagnosis.RawContent = File.ReadAllText(ClaudeSettingsPath);

            // 尝试解析 JSON
            JsonNode? settings;
            try
            {
                settings = JsonNode.Parse(diagnosis.RawContent);
                diagnosis.IsValidJson = settings != null;
            }
            catch (JsonException ex)
            {
                diagnosis.IsValidJson = false;
                diagnosis.JsonError = ex.Message;
                diagnosis.Issues.Add($"JSON 格式错误: {ex.Message}");
                diagnosis.Suggestions.Add("settings.json 不是有效的 JSON 格式，需要修复或重置");
                return;
            }

            if (settings == null)
            {
                diagnosis.IsValidJson = false;
                diagnosis.Issues.Add("settings.json 内容为空");
                return;
            }

            diagnosis.IsValidJson = true;

            // 检查 hooks 节点
            var hooks = settings["hooks"];
            diagnosis.HasHooksSection = hooks != null;

            if (!diagnosis.HasHooksSection)
            {
                diagnosis.Issues.Add("缺少 hooks 配置节");
                diagnosis.Suggestions.Add("需要添加 hooks 配置");
                return;
            }

            // 检查 Stop hook
            var stopHook = hooks?["Stop"];
            diagnosis.HasStopHook = stopHook != null;

            if (!diagnosis.HasStopHook)
            {
                diagnosis.Issues.Add("缺少 Stop hook 配置");
                diagnosis.Suggestions.Add("需要添加 Stop hook 以在任务完成时发送通知");
                return;
            }

            // 分析 Stop hook 内容
            AnalyzeStopHook(diagnosis, stopHook!);
        }
        catch (Exception ex)
        {
            diagnosis.Issues.Add($"读取配置文件失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 分析 Stop hook 配置
    /// </summary>
    private void AnalyzeStopHook(HookDiagnosis diagnosis, JsonNode stopHook)
    {
        try
        {
            var stopHookArray = stopHook.AsArray();
            bool foundCorrectConfig = false;

            foreach (var hookEntry in stopHookArray)
            {
                var matcher = hookEntry?["matcher"]?.GetValue<string>();
                var commands = hookEntry?["hooks"]?.AsArray();

                if (matcher == "*")
                {
                    diagnosis.StopHookHasCorrectMatcher = true;

                    if (commands != null)
                    {
                        foreach (var cmd in commands)
                        {
                            // 支持两种格式：
                            // 1. 对象格式（官方标准）: { "type": "command", "command": "..." }
                            // 2. 字符串格式（旧版）: "powershell ..."
                            string? cmdStr = null;

                            if (cmd is JsonObject cmdObj)
                            {
                                // 对象格式，获取 command 属性
                                cmdStr = cmdObj["command"]?.GetValue<string>();
                            }
                            else if (cmd is JsonValue cmdVal)
                            {
                                // 字符串格式
                                cmdStr = cmdVal.GetValue<string>();
                            }

                            if (cmdStr != null)
                            {
                                diagnosis.CurrentStopHookCommand = cmdStr;

                                if (cmdStr.Contains(HookScriptName))
                                {
                                    diagnosis.StopHookHasCorrectCommand = true;
                                    diagnosis.HookConfigured = true;
                                    foundCorrectConfig = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (foundCorrectConfig) break;
            }

            if (!diagnosis.StopHookHasCorrectMatcher)
            {
                diagnosis.Issues.Add("Stop hook 缺少通配符匹配器 (matcher: \"*\")");
                diagnosis.Suggestions.Add("需要添加正确的匹配器配置");
            }

            if (!diagnosis.StopHookHasCorrectCommand)
            {
                diagnosis.Issues.Add("Stop hook 未配置 CodeBridge 通知脚本");
                diagnosis.Suggestions.Add("需要添加 notify-codebridge.ps1 脚本调用");
            }
        }
        catch (Exception ex)
        {
            diagnosis.Issues.Add($"解析 Stop hook 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 分析 Hook 脚本内容
    /// </summary>
    private void AnalyzeHookScript(HookDiagnosis diagnosis, string scriptPath)
    {
        try
        {
            var content = File.ReadAllText(scriptPath);

            // 检查是否包含关键标识（由我们程序创建）
            bool hasCodeBridgeHeader = content.Contains("CodeBridge Notification Hook") ||
                                        content.Contains("飞跃侠·CodeBridge");
            bool hasPipeName = content.Contains("CodeBridgePipe");
            bool hasJsonMessage = content.Contains("ConvertTo-Json");

            // 检查是否有降级通知逻辑
            diagnosis.HookScriptHasFallback = content.Contains("System.Windows.Forms.NotifyIcon") ||
                                               content.Contains("ShowBalloonTip");

            // 检查是否有中文乱码问题（常见编码错误特征）
            bool hasEncodingIssue = content.Contains("鍦") || content.Contains("瀹") ||
                                    content.Contains("浜") || content.Contains("鍔");

            if (hasEncodingIssue)
            {
                diagnosis.HookScriptIsValid = false;
                diagnosis.HookScriptError = "脚本包含编码错误的中文字符";
                diagnosis.Issues.Add("Hook 脚本存在编码问题，包含乱码");
                diagnosis.Suggestions.Add("需要重新生成脚本以修复编码问题");
                return;
            }

            if (!hasCodeBridgeHeader || !hasPipeName)
            {
                diagnosis.HookScriptIsValid = false;
                diagnosis.HookScriptError = "脚本不是由 CodeBridge 创建";
                diagnosis.Issues.Add("Hook 脚本不是由本程序创建，可能是旧版本或手动创建");
                diagnosis.Suggestions.Add("建议重新生成脚本以获得最佳兼容性");
                return;
            }

            if (!diagnosis.HookScriptHasFallback)
            {
                diagnosis.Issues.Add("Hook 脚本缺少降级通知功能");
                diagnosis.Suggestions.Add("建议更新脚本以支持程序未运行时的 Windows 原生通知");
            }

            diagnosis.HookScriptIsValid = hasCodeBridgeHeader && hasPipeName && hasJsonMessage;

            if (!diagnosis.HookScriptIsValid)
            {
                diagnosis.HookScriptError = "脚本内容不完整";
                diagnosis.Issues.Add("Hook 脚本内容不完整，缺少必要组件");
                diagnosis.Suggestions.Add("需要重新生成完整的脚本");
            }
        }
        catch (Exception ex)
        {
            diagnosis.HookScriptIsValid = false;
            diagnosis.HookScriptError = ex.Message;
            diagnosis.Issues.Add($"无法读取 Hook 脚本: {ex.Message}");
        }
    }

    /// <summary>
    /// 创建配置文件备份
    /// </summary>
    public (bool Success, string BackupPath, string Message) BackupSettings()
    {
        try
        {
            if (!File.Exists(ClaudeSettingsPath))
            {
                return (false, "", "settings.json 不存在，无需备份");
            }

            // 生成唯一备份文件名: settings.backup.20260129_143052_a1b2c3.json
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var uniqueId = Guid.NewGuid().ToString("N")[..6]; // 取前6位
            var backupFileName = $"settings.backup.{timestamp}_{uniqueId}.json";
            var backupPath = Path.Combine(ClaudeConfigDir, backupFileName);

            File.Copy(ClaudeSettingsPath, backupPath, overwrite: false);

            return (true, backupPath, $"已备份到: {backupFileName}");
        }
        catch (Exception ex)
        {
            return (false, "", $"备份失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 列出所有备份文件
    /// </summary>
    public List<(string FileName, DateTime Created, long Size)> ListBackups()
    {
        var backups = new List<(string FileName, DateTime Created, long Size)>();

        if (!Directory.Exists(ClaudeConfigDir))
            return backups;

        var files = Directory.GetFiles(ClaudeConfigDir, "settings.backup.*.json");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            backups.Add((info.Name, info.CreationTime, info.Length));
        }

        backups.Sort((a, b) => b.Created.CompareTo(a.Created)); // 最新的在前
        return backups;
    }

    /// <summary>
    /// 从备份恢复
    /// </summary>
    public async Task<(bool Success, string Message)> RestoreFromBackupAsync(string backupFileName)
    {
        try
        {
            var backupPath = Path.Combine(ClaudeConfigDir, backupFileName);
            if (!File.Exists(backupPath))
            {
                return (false, "备份文件不存在");
            }

            // 验证备份文件是有效的 JSON
            var content = await File.ReadAllTextAsync(backupPath);
            try
            {
                JsonNode.Parse(content);
            }
            catch
            {
                return (false, "备份文件不是有效的 JSON");
            }

            // 先备份当前文件
            if (File.Exists(ClaudeSettingsPath))
            {
                var (success, _, _) = BackupSettings();
                if (!success)
                {
                    return (false, "无法备份当前配置");
                }
            }

            // 恢复
            await File.WriteAllTextAsync(ClaudeSettingsPath, content);
            return (true, "已从备份恢复配置");
        }
        catch (Exception ex)
        {
            return (false, $"恢复失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 修复 Hook 配置（带自动备份）
    /// </summary>
    public async Task<(bool Success, string Message, string? BackupPath)> RepairHookConfigurationAsync()
    {
        try
        {
            // 1. 确保 .claude 目录存在
            if (!Directory.Exists(ClaudeConfigDir))
            {
                return (false, "未找到 Claude 配置目录，请先安装并运行一次 Claude Code", null);
            }

            // 2. 备份现有配置
            string? backupPath = null;
            if (File.Exists(ClaudeSettingsPath))
            {
                var (backupSuccess, path, backupMsg) = BackupSettings();
                if (backupSuccess)
                {
                    backupPath = path;
                }
                else
                {
                    return (false, $"备份失败，修复已取消: {backupMsg}", null);
                }
            }

            // 3. 创建/更新 hook 脚本（使用 UTF-8 with BOM 编码，确保 PowerShell 正确读取中文）
            var hookScriptPath = Path.Combine(ClaudeConfigDir, HookScriptName);
            var scriptContent = GenerateHookScript();
            await File.WriteAllTextAsync(hookScriptPath, scriptContent, new System.Text.UTF8Encoding(true));

            // 4. 读取或创建 settings.json
            JsonNode? settings;
            if (File.Exists(ClaudeSettingsPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(ClaudeSettingsPath);
                    settings = JsonNode.Parse(json);

                    // 如果解析失败，创建新的
                    if (settings == null)
                    {
                        settings = new JsonObject();
                    }
                }
                catch (JsonException)
                {
                    // JSON 损坏，创建新的配置
                    settings = new JsonObject();
                }
            }
            else
            {
                settings = new JsonObject();
            }

            // 5. 确保 hooks 对象存在
            if (settings["hooks"] == null)
            {
                settings["hooks"] = new JsonObject();
            }

            // 6. 配置 Stop hook（使用官方标准格式）
            var stopHookCommand = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{hookScriptPath}\"";
            var stopHookConfig = new JsonArray
            {
                new JsonObject
                {
                    ["matcher"] = "*",
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = stopHookCommand,
                            ["timeout"] = 10000
                        }
                    }
                }
            };

            settings["hooks"]!["Stop"] = stopHookConfig;

            // 7. 写回配置文件
            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = settings.ToJsonString(options);
            await File.WriteAllTextAsync(ClaudeSettingsPath, updatedJson);

            var message = backupPath != null
                ? $"Hook 配置已修复！原配置已备份到: {Path.GetFileName(backupPath)}"
                : "Hook 配置已创建！";

            return (true, message, backupPath);
        }
        catch (Exception ex)
        {
            return (false, $"修复失败: {ex.Message}", null);
        }
    }

    /// <summary>
    /// 一键配置 Hook（兼容旧接口）
    /// </summary>
    public async Task<(bool Success, string Message)> ConfigureHookAsync()
    {
        var (success, message, _) = await RepairHookConfigurationAsync();
        return (success, message);
    }

    /// <summary>
    /// 生成 Hook 脚本内容
    /// </summary>
    private string GenerateHookScript()
    {
        // 中文通知内容，使用 UTF-8 with BOM 编码写入
        return @"# 飞跃侠·CodeBridge Notification Hook
# Claude Code 任务完成时发送通知到 飞跃侠·CodeBridge
# 如果 飞跃侠·CodeBridge 未运行，则降级为 Windows 原生通知

param()

$pipeName = 'CodeBridgePipe'
$cwd = Get-Location
$folderName = Split-Path $cwd -Leaf

# 构建通知消息
$message = @{
    type = 'task_complete'
    tabId = $cwd.Path
    title = '任务完成'
    message = ""Claude 在 $folderName 完成了任务""
    severity = 'success'
    cwd = $cwd.Path
    timestamp = (Get-Date).ToString('o')
    isPersistent = $true
} | ConvertTo-Json -Compress

$notificationSent = $false

try {
    # 尝试连接 飞跃侠·CodeBridge 命名管道
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    $pipe.Connect(1000)  # 1秒超时

    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.WriteLine($message)
    $writer.Flush()
    $writer.Close()
    $pipe.Close()
    $notificationSent = $true
}
catch {
    # 飞跃侠·CodeBridge 未运行，将使用降级通知
}

# 降级方案：Windows 原生气泡通知
if (-not $notificationSent) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing

        $notify = New-Object System.Windows.Forms.NotifyIcon
        $notify.Icon = [System.Drawing.SystemIcons]::Information
        $notify.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Info
        $notify.BalloonTipTitle = '飞跃侠·CodeBridge'
        $notify.BalloonTipText = ""Claude 在 $folderName 完成了任务""
        $notify.Visible = $true
        $notify.ShowBalloonTip(5000)

        # 等待气泡显示后清理
        Start-Sleep -Milliseconds 5500
        $notify.Visible = $false
        $notify.Dispose()
    }
    catch {
        # 静默失败
    }
}
";
    }

    /// <summary>
    /// 移除 Hook 配置
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveHookAsync()
    {
        try
        {
            // 备份现有配置
            if (File.Exists(ClaudeSettingsPath))
            {
                BackupSettings();
            }

            // 删除脚本文件
            var hookScriptPath = Path.Combine(ClaudeConfigDir, HookScriptName);
            if (File.Exists(hookScriptPath))
            {
                File.Delete(hookScriptPath);
            }

            // 从 settings.json 移除配置
            if (File.Exists(ClaudeSettingsPath))
            {
                var json = await File.ReadAllTextAsync(ClaudeSettingsPath);
                var settings = JsonNode.Parse(json);

                if (settings?["hooks"]?["Stop"] != null)
                {
                    var stopHooks = settings["hooks"]!["Stop"]!.AsArray();
                    for (int i = stopHooks.Count - 1; i >= 0; i--)
                    {
                        var commands = stopHooks[i]?["hooks"]?.AsArray();
                        if (commands != null)
                        {
                            for (int j = commands.Count - 1; j >= 0; j--)
                            {
                                if (commands[j]?.GetValue<string>()?.Contains(HookScriptName) == true)
                                {
                                    commands.RemoveAt(j);
                                }
                            }
                            // 如果 hooks 数组为空，删除整个条目
                            if (commands.Count == 0)
                            {
                                stopHooks.RemoveAt(i);
                            }
                        }
                    }
                    // 如果 Stop 数组为空，删除它
                    if (stopHooks.Count == 0)
                    {
                        ((JsonObject)settings["hooks"]!).Remove("Stop");
                    }
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = settings!.ToJsonString(options);
                await File.WriteAllTextAsync(ClaudeSettingsPath, updatedJson);
            }

            return (true, "Hook 配置已移除");
        }
        catch (Exception ex)
        {
            return (false, $"移除失败: {ex.Message}");
        }
    }

    // ===== 兼容旧接口 =====

    /// <summary>
    /// Hook 配置状态（兼容旧接口）
    /// </summary>
    public class HookStatus
    {
        public bool ClaudeConfigExists { get; set; }
        public bool SettingsFileExists { get; set; }
        public bool HookScriptExists { get; set; }
        public bool HookConfigured { get; set; }
        public string Message { get; set; } = "";
        public bool IsFullyConfigured => ClaudeConfigExists && SettingsFileExists && HookScriptExists && HookConfigured;
    }

    /// <summary>
    /// 检测 Hook 配置状态（兼容旧接口）
    /// </summary>
    public HookStatus CheckHookStatus()
    {
        var diagnosis = DiagnoseHookConfiguration();
        return new HookStatus
        {
            ClaudeConfigExists = diagnosis.ClaudeConfigExists,
            SettingsFileExists = diagnosis.SettingsFileExists,
            HookScriptExists = diagnosis.HookScriptExists,
            HookConfigured = diagnosis.HookConfigured,
            Message = diagnosis.Message
        };
    }
}
