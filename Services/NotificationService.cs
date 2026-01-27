using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using MyAiHelper.Models;

namespace MyAiHelper.Services;

/// <summary>
/// 通知服务 - 管理应用程序通知
/// </summary>
public class NotificationService
{
    private const int MaxNotifications = 100;
    private readonly Dispatcher _dispatcher;
    private readonly object _lock = new();

    /// <summary>
    /// 通知列表（UI绑定用）
    /// </summary>
    public ObservableCollection<NotificationItem> Notifications { get; } = new();

    /// <summary>
    /// 未读通知数量
    /// </summary>
    public int UnreadCount => Notifications.Count(n => !n.IsRead);

    /// <summary>
    /// 新通知添加事件（用于触发悬浮通知）
    /// </summary>
    public event Action<NotificationItem>? NotificationAdded;

    /// <summary>
    /// 未读数量变化事件
    /// </summary>
    public event Action<int>? UnreadCountChanged;

    public NotificationService()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// 添加通知
    /// </summary>
    public void Add(NotificationItem notification)
    {
        _dispatcher.Invoke(() =>
        {
            lock (_lock)
            {
                // 去重：同一标签页在短时间内不重复通知
                var duplicate = Notifications.FirstOrDefault(n =>
                    n.TabId == notification.TabId &&
                    (DateTime.UtcNow - n.CreatedAtUtc).TotalSeconds < 5);

                if (duplicate != null)
                    return;

                // 添加到列表头部
                Notifications.Insert(0, notification);

                // 限制最大数量
                while (Notifications.Count > MaxNotifications)
                {
                    Notifications.RemoveAt(Notifications.Count - 1);
                }
            }

            // 触发事件
            NotificationAdded?.Invoke(notification);
            OnUnreadCountChanged();
        });
    }

    /// <summary>
    /// 快速添加任务完成通知
    /// </summary>
    public void AddTaskCompleted(string tabId, string tabName, string message = "Claude Code 任务已完成")
    {
        Add(new NotificationItem
        {
            TabId = tabId,
            TabName = tabName,
            Title = "任务完成",
            Message = message,
            Kind = NotificationKind.Success,
            Source = "Claude"
        });
    }

    /// <summary>
    /// 标记通知为已读
    /// </summary>
    public void MarkAsRead(Guid notificationId)
    {
        _dispatcher.Invoke(() =>
        {
            var notification = Notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null && !notification.IsRead)
            {
                notification.IsRead = true;
                OnUnreadCountChanged();
            }
        });
    }

    /// <summary>
    /// 标记所有通知为已读
    /// </summary>
    public void MarkAllAsRead()
    {
        _dispatcher.Invoke(() =>
        {
            foreach (var notification in Notifications.Where(n => !n.IsRead))
            {
                notification.IsRead = true;
            }
            OnUnreadCountChanged();
        });
    }

    /// <summary>
    /// 移除通知
    /// </summary>
    public void Remove(Guid notificationId)
    {
        _dispatcher.Invoke(() =>
        {
            var notification = Notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification != null)
            {
                Notifications.Remove(notification);
                OnUnreadCountChanged();
            }
        });
    }

    /// <summary>
    /// 清空所有通知
    /// </summary>
    public void ClearAll()
    {
        _dispatcher.Invoke(() =>
        {
            Notifications.Clear();
            OnUnreadCountChanged();
        });
    }

    private void OnUnreadCountChanged()
    {
        UnreadCountChanged?.Invoke(UnreadCount);
    }
}
