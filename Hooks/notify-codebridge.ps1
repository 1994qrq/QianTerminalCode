# 飞跃侠·CodeBridge Claude Hook 通知脚本
# 当 Claude Code 触发 Hook 时，通过命名管道发送通知到 飞跃侠·CodeBridge

param(
    [string]$EventType = "Stop"
)

# 从 stdin 读取 Hook 传递的 JSON 数据
$stdinData = $null
try {
    if ([Console]::IsInputRedirected) {
        $stdinData = [Console]::In.ReadToEnd()
    }
} catch {
    # 忽略读取错误
}

# 读取由 飞跃侠·CodeBridge 注入的标签页 ID
$tabId = $env:CODEBRIDGE_TAB_ID
$cwd = $env:CODEBRIDGE_CWD
if ([string]::IsNullOrEmpty($cwd)) {
    $cwd = (Get-Location).Path
}

# 如果没有 tabId（不在 飞跃侠·CodeBridge 中运行），直接退出
if ([string]::IsNullOrEmpty($tabId)) {
    exit 0
}

# 解析 stdin 中的事件类型（如果有的话）
if (-not [string]::IsNullOrEmpty($stdinData)) {
    try {
        $jsonData = $stdinData | ConvertFrom-Json
        if ($jsonData.event) {
            $EventType = $jsonData.event
        }
    } catch {
        # JSON 解析失败，使用默认值
    }
}

# 根据事件类型设置消息
$title = "需要关注"
$message = "Claude 已停止输出，可能需要您的输入"
$severity = "info"

switch ($EventType.ToLower()) {
    "stop" {
        $title = "需要关注"
        $message = "Claude 已完成回复，请查看"
        $severity = "info"
    }
    "notification" {
        $title = "Claude 通知"
        $message = "Claude 发送了一条通知"
        $severity = "info"
    }
    "permissionrequest" {
        $title = "权限请求"
        $message = "Claude 需要您的授权"
        $severity = "warning"
    }
    "posttoolusefailure" {
        $title = "工具执行失败"
        $message = "Claude 工具执行出现错误"
        $severity = "error"
    }
    default {
        $title = "需要关注"
        $message = "Claude 需要您的注意"
        $severity = "info"
    }
}

# 构造 JSON 消息
$payload = @{
    type = $EventType
    tabId = $tabId
    title = $title
    message = $message
    severity = $severity
    cwd = $cwd
    timestamp = (Get-Date).ToUniversalTime().ToString("o")
    isPersistent = $true
} | ConvertTo-Json -Compress

# 通过命名管道发送消息
$pipeName = "CodeBridgePipe"

try {
    $pipeClient = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::Out)

    # 尝试连接，超时 500ms
    $pipeClient.Connect(500)

    if ($pipeClient.IsConnected) {
        $writer = New-Object System.IO.StreamWriter($pipeClient)
        $writer.AutoFlush = $true
        $writer.WriteLine($payload)
        $writer.Dispose()
    }

    $pipeClient.Dispose()
} catch {
    # 连接失败（飞跃侠·CodeBridge 可能未运行），静默忽略
}

exit 0
