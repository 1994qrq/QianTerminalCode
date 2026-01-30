using System;

namespace CodeBridge.Models;

/// <summary>
/// 标签配置模型
/// </summary>
public class TabConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool AutoRunClaude { get; set; }
    public bool ContinueSession { get; set; }  // -c 选项：继续上次会话
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;  // 创建时间
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;   // 最后使用时间
    public bool IsPinned { get; set; }
    public bool IsDisabled { get; set; }  // 是否禁用（跳过初始化）
}
