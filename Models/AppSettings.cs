using System.Collections.Generic;

namespace CodeBridge.Models;

/// <summary>
/// 应用设置模型
/// </summary>
public class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string LastDirectory { get; set; } = string.Empty;
    public List<TabConfig> Tabs { get; set; } = new();
    public List<TabConfig> History { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public UserPreferences Preferences { get; set; } = new();
}

public class WindowSettings
{
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 800;
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public string State { get; set; } = "Normal";
}

/// <summary>
/// 用户偏好设置
/// </summary>
public class UserPreferences
{
    /// <summary>
    /// 是否已完成首次引导
    /// </summary>
    public bool HasCompletedOnboarding { get; set; } = false;

    /// <summary>
    /// 启动时全屏（隐藏任务栏）
    /// </summary>
    public bool StartFullScreen { get; set; } = true;

    /// <summary>
    /// 启动时自动最大化
    /// </summary>
    public bool StartMaximized { get; set; } = true;

    /// <summary>
    /// 关闭时最小化到托盘（而非退出）
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = false;

    /// <summary>
    /// 启用桌面通知
    /// </summary>
    public bool EnableDesktopNotifications { get; set; } = true;

    /// <summary>
    /// 通知持续时间（秒），0 表示永久
    /// </summary>
    public int NotificationDurationSeconds { get; set; } = 0;

    /// <summary>
    /// 悬浮状态栏快捷键配置
    /// </summary>
    public StatusBarHotkey StatusBarHotkey { get; set; } = new();

    /// <summary>
    /// 远程控制设置
    /// </summary>
    public RemoteControlSettings RemoteControl { get; set; } = new();

    /// <summary>
    /// 终端 Shell 类型（cmd 或 powershell）
    /// </summary>
    public string ShellType { get; set; } = "powershell";
}

/// <summary>
/// 远程控制设置
/// </summary>
public class RemoteControlSettings
{
    /// <summary>
    /// 是否启用远程控制
    /// </summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// 本地服务端口
    /// </summary>
    public int Port { get; set; } = 8765;

    /// <summary>
    /// 是否自动启动隧道
    /// </summary>
    public bool AutoStartTunnel { get; set; } = false;
}

/// <summary>
/// 悬浮状态栏快捷键配置
/// </summary>
public class StatusBarHotkey
{
    /// <summary>
    /// 是否启用快捷键
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 修饰键（Ctrl, Shift, Alt 的组合）
    /// </summary>
    public string Modifiers { get; set; } = "Ctrl+Shift";

    /// <summary>
    /// 主键
    /// </summary>
    public string Key { get; set; } = "Space";

    /// <summary>
    /// 获取完整的快捷键显示文本
    /// </summary>
    public string DisplayText => $"{Modifiers}+{Key}";
}
