namespace MyAiHelper.Models;

/// <summary>
/// 通知类型枚举
/// </summary>
public enum NotificationKind
{
    /// <summary>
    /// 信息通知
    /// </summary>
    Info,

    /// <summary>
    /// 成功通知（任务完成）
    /// </summary>
    Success,

    /// <summary>
    /// 警告通知
    /// </summary>
    Warning,

    /// <summary>
    /// 错误通知
    /// </summary>
    Error
}
