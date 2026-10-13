<#
    FundDca 停止脚本
    ---------------------------------------------------------------------------
    按端口（5173 / 5000）找到监听进程，taskkill /T /F 结束整棵进程树
    （dotnet run / npm run dev 都会派生子进程，只杀父进程不够）。
    再兜底处理 .run\*.pid 里记录的进程。

    参数：
      -Quiet  不输出到控制台（供静默调用）
#>
[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Continue'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot   = Split-Path -Parent $scriptDir
$runDir    = Join-Path $appRoot '.run'
$logDir    = Join-Path $appRoot 'logs'

foreach ($dir in @($runDir, $logDir)) {
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

$launcherLog = Join-Path $logDir 'launcher.log'

function Write-Info([string]$Message) {
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    try { Add-Content -LiteralPath $launcherLog -Value $line -Encoding UTF8 } catch { }
    if (-not $Quiet) { Write-Host $line }
}

function Get-RecordedPid([string]$PidFile) {
    if (-not (Test-Path -LiteralPath $PidFile)) { return 0 }
    $raw = Get-Content -LiteralPath $PidFile -Raw -ErrorAction SilentlyContinue
    $value = 0
    if ($null -ne $raw -and [int]::TryParse($raw.ToString().Trim(), [ref]$value)) { return $value }
    return 0
}

function Stop-ProcessTree([int]$ProcId, [string]$Reason) {
    if ($ProcId -le 0) { return $false }
    $null = & taskkill.exe /PID $ProcId /T /F 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Info ('已结束进程树 PID={0}（{1}）' -f $ProcId, $Reason)
        return $true
    }
    Write-Info ('尝试结束 PID={0} 未生效（{1}），可能已自行退出' -f $ProcId, $Reason)
    return $false
}

function Stop-ByPort([int]$Port, [string]$Name) {
    $stopped = @()
    $listeners = @()
    try {
        $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    } catch {
        Write-Info ('查询端口 {0} 失败：{1}' -f $Port, $_.Exception.Message)
    }
    if (-not $listeners -or $listeners.Count -eq 0) {
        Write-Info ('{0}：端口 {1} 未被占用' -f $Name, $Port)
        return
    }
    foreach ($item in $listeners) {
        $ownerId = [int]$item.OwningProcess
        if ($ownerId -le 0 -or $stopped -contains $ownerId) { continue }
        $stopped += $ownerId
        Stop-ProcessTree $ownerId ('占用端口 {0}' -f $Port)
    }
}

Write-Info '开始停止 FundDca 前后端服务'

Stop-ByPort 5173 '前端'
Stop-ByPort 5000 '后端'

# 兜底：端口可能尚未开始监听，但进程已存在
foreach ($name in @('frontend.pid', 'backend.pid')) {
    $pidFile = Join-Path $runDir $name
    $procId = Get-RecordedPid $pidFile
    if ($procId -gt 0 -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
        Stop-ProcessTree $procId ('.run\{0} 记录' -f $name)
    }
}

foreach ($leftover in @('frontend.pid', 'backend.pid', 'launching.lock')) {
    $path = Join-Path $runDir $leftover
    if (Test-Path -LiteralPath $path) {
        try { Remove-Item -LiteralPath $path -Force } catch { }
    }
}

Start-Sleep -Milliseconds 300

$stillUp = @()
foreach ($port in @(5173, 5000)) {
    $hit = $null
    try { $hit = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue } catch { }
    if ($hit) { $stillUp += $port }
}
if ($stillUp.Count -eq 0) {
    Write-Info '前后端服务已全部停止，端口 5173 / 5000 已释放'
} else {
    Write-Info ('以下端口仍在监听：{0}' -f ($stillUp -join ', '))
}
