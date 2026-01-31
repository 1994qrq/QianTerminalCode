using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CodeBridge.Services;

/// <summary>
/// 远程控制认证服务 - Token 生成与验证
/// </summary>
public class RemoteAuthService
{
    private string _currentToken = string.Empty;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly TimeSpan _tokenLifetime = TimeSpan.FromHours(24);
    private readonly object _tokenLock = new();

    /// <summary>
    /// 已认证的连接（WebSocket Context ID -> 认证时间）
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _authenticatedConnections = new();

    /// <summary>
    /// 最大并发连接数
    /// </summary>
    public int MaxConnections { get; set; } = 5;

    /// <summary>
    /// 当前连接数
    /// </summary>
    public int CurrentConnectionCount => _authenticatedConnections.Count;

    /// <summary>
    /// 获取当前 Token（如果已过期则自动刷新）
    /// </summary>
    public string CurrentToken
    {
        get
        {
            lock (_tokenLock)
            {
                if (DateTime.UtcNow >= _tokenExpiry || string.IsNullOrEmpty(_currentToken))
                {
                    RefreshToken();
                }
                return _currentToken;
            }
        }
    }

    /// <summary>
    /// Token 过期时间
    /// </summary>
    public DateTime TokenExpiry => _tokenExpiry;

    /// <summary>
    /// 刷新 Token（生成新的 6 位数字 PIN）
    /// </summary>
    public string RefreshToken()
    {
        lock (_tokenLock)
        {
            // 生成 6 位随机数字
            _currentToken = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            _tokenExpiry = DateTime.UtcNow.Add(_tokenLifetime);

            // 清除所有已认证连接（Token 已变更）
            _authenticatedConnections.Clear();

            return _currentToken;
        }
    }

    /// <summary>
    /// 验证 Token
    /// </summary>
    public bool VerifyToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        lock (_tokenLock)
        {
            if (DateTime.UtcNow >= _tokenExpiry) return false;
            return string.Equals(_currentToken, token, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 尝试认证连接
    /// </summary>
    /// <param name="connectionId">连接标识</param>
    /// <param name="token">提供的 Token</param>
    /// <returns>认证是否成功</returns>
    public bool TryAuthenticate(string connectionId, string token)
    {
        if (!VerifyToken(token)) return false;

        // 检查连接数限制
        if (_authenticatedConnections.Count >= MaxConnections)
        {
            // 清理过期连接后重试
            CleanupExpiredConnections();
            if (_authenticatedConnections.Count >= MaxConnections)
            {
                return false;
            }
        }

        _authenticatedConnections[connectionId] = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// 检查连接是否已认证
    /// </summary>
    public bool IsAuthenticated(string connectionId)
    {
        return _authenticatedConnections.ContainsKey(connectionId);
    }

    /// <summary>
    /// 移除连接
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        _authenticatedConnections.TryRemove(connectionId, out _);
    }

    /// <summary>
    /// 清理过期连接（超过 Token 生命周期的连接）
    /// </summary>
    private void CleanupExpiredConnections()
    {
        var cutoff = DateTime.UtcNow.Subtract(_tokenLifetime);
        foreach (var kvp in _authenticatedConnections)
        {
            if (kvp.Value < cutoff)
            {
                _authenticatedConnections.TryRemove(kvp.Key, out _);
            }
        }
    }
}
