<#
    FundDca 静默启动编排脚本
    ---------------------------------------------------------------------------
    由 scripts\launch-hidden.vbs 以隐藏窗口方式调用（也可直接手动执行）。

    与旧版 一键启动.bat 的区别：
      1. 前后端都用 cmd.exe 的隐藏窗口启动，用户看不到任何终端窗口；
         它们的输出重定向到 logs\backend*.log / logs\frontend*.log。
      2. 只等「前端开发服务器」就绪就立刻打开浏览器，不再等后端编译 + 连云端库。
         页面会先渲染骨架屏，等后端起来后自动填入数据（见 web\src 的 waitForBackend）。

    参数：
      -NoBrowser              不打开浏览器（自测用）
      -Restart                先停止已运行的前后端，再重新启动
      -FrontendTimeoutSeconds 等待前端就绪的秒数，默认 90
#>
[CmdletBinding()]
param(
    [switch]$NoBrowser,
    [switch]$Restart,
    [int]$FrontendTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot   = Split-Path -Parent $scriptDir      # fund-dca-app
$runDir    = Join-Path $appRoot '.run'
$logDir    = Join-Path $appRoot 'logs'

foreach ($dir in @($runDir, $logDir)) {
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

$launcherLog = Join-Path $logDir 'launcher.log'
$backendOut  = Join-Path $logDir 'backend.log'
$backendErr  = Join-Path $logDir 'backend.err.log'
$frontendOut = Join-Path $logDir 'frontend.log'
$frontendErr = Join-Path $logDir 'frontend.err.log'
$backendPid  = Join-Path $runDir  'backend.pid'
$frontendPid = Join-Path $runDir  'frontend.pid'
$lockFile    = Join-Path $runDir  'launching.lock'

$FE_PORT = 5173
$BE_PORT = 5000
$FE_URL  = 'http://localhost:5173/'

function Write-LauncherLog([string]$Message) {
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    try { Add-Content -LiteralPath $launcherLog -Value $line -Encoding UTF8 } catch { }
}

function Test-PortOpen([string]$HostName, [int]$Port, [int]$TimeoutMs = 400) {
    # 注意：默认构造的 TcpClient 是 IPv4 套接字，直连 ::1 会抛
    # "地址与套接字地址族不匹配"，必须按目标地址显式指定地址族。
    if ($HostName -eq '::1') {
        $client = [System.Net.Sockets.TcpClient]::new([System.Net.Sockets.AddressFamily]::InterNetworkV6)
    } else {
        $client = [System.Net.Sockets.TcpClient]::new()
    }
    try {
        $async = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

# 端口是否已被监听。
# 注意不能只探测 127.0.0.1：Node 的 localhost 解析在 Windows 上优先 IPv6，
# Vite 可能只监听 ::1；也不能只依赖连接（某些环境下会被拦），故分三层判断：
#   ① IPv4 直连  ② IPv6 直连  ③ 读系统 TCP 监听表（两种地址族都能覆盖）
function Test-PortListening([int]$Port) {
    if (Test-PortOpen '127.0.0.1' $Port 250) { return $true }
    if (Test-PortOpen '::1' $Port 250) { return $true }
    try {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if ($conn) { return $true }
    } catch { }
    return $false
}

function Test-FrontendOpen { return (Test-PortListening $FE_PORT) }
function Test-BackendOpen  { return (Test-PortListening $BE_PORT) }

function Get-RecordedPid([string]$PidFile) {
    if (-not (Test-Path -LiteralPath $PidFile)) { return 0 }
    $raw = Get-Content -LiteralPath $PidFile -Raw -ErrorAction SilentlyContinue
    $value = 0
    if ($null -ne $raw -and [int]::TryParse($raw.ToString().Trim(), [ref]$value)) { return $value }
    return 0
}

function Test-RecordedAlive([string]$PidFile) {
    $procId = Get-RecordedPid $PidFile
    if ($procId -le 0) { return $false }
    return [bool](Get-Process -Id $procId -ErrorAction SilentlyContinue)
}

# 把上一轮的日志挪到 .1，避免 Start-Process 重定向时因文件被占用而失败
function Reset-Log([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $backup = "$Path.1"
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    try {
        Move-Item -LiteralPath $Path -Destination $backup -Force
    } catch {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

# 以隐藏窗口启动一个 cmd 包装脚本，并从此刻起把它的输出落盘
function Start-HiddenService([string]$ScriptPath, [string]$OutLog, [string]$ErrLog, [string]$PidFile) {
    Reset-Log $OutLog
    Reset-Log $ErrLog
    $argLine = '/d /c call "{0}"' -f $ScriptPath
    $proc = Start-Process -FilePath 'cmd.exe' `
        -ArgumentList $argLine `
        -WorkingDirectory $appRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $OutLog `
        -RedirectStandardError $ErrLog `
        -PassThru
    Set-Content -LiteralPath $PidFile -Value $proc.Id -Encoding ASCII
    return $proc.Id
}

function Show-ErrorMessage([string]$Message) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Message, 'FundDca 启动失败', 'OK', 'Error') | Out-Null
    } catch {
        Write-Host $Message
    }
}

# ===========================================================================
try {
    if (Test-Path -LiteralPath $lockFile) {
        $age = (Get-Date) - (Get-Item -LiteralPath $lockFile).LastWriteTime
        if ($age.TotalSeconds -lt 120) {
            Write-LauncherLog '已有一次启动流程正在进行，本次重复启动被忽略'
            exit 0
        }
        Write-LauncherLog '发现过期的启动锁，继续启动'
    }
    Set-Content -LiteralPath $lockFile -Value (Get-Date -Format o) -Encoding ASCII

    if ($Restart) {
        Write-LauncherLog '收到 -Restart，先停止已有服务'
        & (Join-Path $scriptDir 'stop-app.ps1') -Quiet
        Start-Sleep -Milliseconds 800
    }

    Write-LauncherLog ('开始启动：appRoot={0}' -f $appRoot)

    # ---------- 后端（编译 + 连云端库较慢，放后台跑，不阻塞浏览器打开） ----------
    if (Test-BackendOpen) {
        Write-LauncherLog '后端已在运行（端口 5000 已监听），跳过启动'
    } elseif (Test-RecordedAlive $backendPid) {
        Write-LauncherLog '后端进程仍在启动中（PID 存活但端口未就绪），跳过启动'
    } else {
        $bpid = Start-HiddenService (Join-Path $scriptDir 'run-backend.cmd') $backendOut $backendErr $backendPid
        Write-LauncherLog ('后端已后台启动（cmd PID={0}），日志 -> logs\backend.log' -f $bpid)
    }

    # ---------- 前端 ----------
    if (Test-FrontendOpen) {
        Write-LauncherLog '前端已在运行（端口 5173 已监听），跳过启动'
    } elseif (Test-RecordedAlive $frontendPid) {
        Write-LauncherLog '前端进程仍在启动中（PID 存活但端口未就绪），跳过启动'
    } else {
        $fpid = Start-HiddenService (Join-Path $scriptDir 'run-frontend.cmd') $frontendOut $frontendErr $frontendPid
        Write-LauncherLog ('前端已后台启动（cmd PID={0}），日志 -> logs\frontend.log' -f $fpid)
    }

    # ---------- 只等前端；一就绪立即开浏览器 ----------
    $elapsed = 0
    $timeoutMs = $FrontendTimeoutSeconds * 1000
    $ready = $false
    while ($elapsed -lt $timeoutMs) {
        if (Test-FrontendOpen) { $ready = $true; break }
        Start-Sleep -Milliseconds 300
        $elapsed += 300
        if ($elapsed -gt 4000 -and -not (Test-RecordedAlive $frontendPid)) {
            Write-LauncherLog '前端进程在端口就绪前已退出，停止等待'
            break
        }
    }

    if ($ready) {
        Write-LauncherLog ('前端已就绪，耗时约 {0:N1}s' -f ($elapsed / 1000))
        if ($NoBrowser) {
            Write-LauncherLog '-NoBrowser 已指定，跳过打开浏览器'
        } else {
            Start-Process $FE_URL
            Write-LauncherLog ('已打开默认浏览器：{0}（后端可能仍在启动，页面会先显示骨架再自动填入数据）' -f $FE_URL)
        }
    } else {
        $tail = ''
        if (Test-Path -LiteralPath $frontendErr) {
            $tail = (Get-Content -LiteralPath $frontendErr -Tail 12 -ErrorAction SilentlyContinue | Out-String)
        }
        Write-LauncherLog ('前端在 {0} 秒内未就绪，放弃等待' -f $FrontendTimeoutSeconds)
        if ($tail) { Write-LauncherLog ('frontend.err.log 末尾：' + $tail) }
        Show-ErrorMessage ("前端开发服务器（Vite）未能在 {0} 秒内就绪。`n`n请查看日志：`n  {1}`n  {2}`n`n常见原因：web 目录尚未执行 npm install，或端口 5173 被占用。" -f $FrontendTimeoutSeconds, $frontendErr, $frontendOut)
    }
} finally {
    Remove-Item -LiteralPath $lockFile -Force -ErrorAction SilentlyContinue
}
