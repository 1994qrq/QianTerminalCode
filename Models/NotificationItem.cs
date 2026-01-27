using System;

namespace MyAiHelper.Models;

/// <summary>
/// 通知项模型
/// </summary>
public class NotificationItem
{
    /// <summary>
    /// 通知唯一标识
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 关联的标签页ID（用于跳转）
    /// </summary>
    public string TabId { get; set; } = string.Empty;

    /// <summary>
    /// 标签页名称（显示用，创建时快照）
    /// </summary>
    public string TabName { get; set; } = string.Empty;

    /// <summary>
    /// 通知标题
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 通知消息内容
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 通知类型
    /// </summary>
    public NotificationKind Kind { get; set; } = NotificationKind.Info;

    /// <summary>
    /// 创建时间（UTC）
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 是否已读
    /// </summary>
    public bool IsRead { get; set; }

    /// <summary>
    /// 是否为持久通知（不自动消失，直到用户点击）
    /// </summary>
    public bool IsPersistent { get; set; } = false;

    /// <summary>
    /// 来源标识（如 "Claude", "ClaudeHooks"）
    /// </summary>
    public string Source { get; set; } = "Claude";

    /// <summary>
    /// 获取相对时间描述
    /// </summary>
    public string RelativeTime
    {
        get
        {
            var elapsed = DateTime.UtcNow - CreatedAtUtc;
            if (elapsed.TotalSeconds < 60)
                return "刚刚";
            if (elapsed.TotalMinutes < 60)
                return $"{(int)elapsed.TotalMinutes}分钟前";
            if (elapsed.TotalHours < 24)
                return $"{(int)elapsed.TotalHours}小时前";
            if (elapsed.TotalDays < 7)
                return $"{(int)elapsed.TotalDays}天前";
            return CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
        }
    }
}
