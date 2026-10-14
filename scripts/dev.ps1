<#
.SYNOPSIS
    道化太初 Windows 一键本地开发启动脚本 (PowerShell 原生)
.DESCRIPTION
    对齐 scripts/dev.sh 流程：
    1. 检查 YuoCore submodule 与前端依赖
    2. 检查并拉起本地数据库 (PostgreSQL 5432)，执行 migrations/ 兼容性迁移链
    3. 清理残留孤儿进程（5128 / 15172 端口）
    4. 启动后端热重启守护 scripts/dev-hot.mjs：
       监听 src/、config/game-data/、vendor/YuoCore 变更，静默 60s 防抖后
       dotnet build，编译失败保持旧进程，成功才优雅热重启；崩溃自动拉起
    5. 等待后端 /healthz 就绪并探测运维面板端点
    6. 启动前端 Vite 开发服务器 (http://127.0.0.1:15172)
    7. 监听退出信号，Ctrl+C 时级联终止前后端子进程树
#>

param(
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$rootDir = (Get-Item $PSScriptRoot).Parent.FullName
Set-Location $rootDir

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "         道化太初 Windows 开发环境启动" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 0. 依赖自检（对齐 dev.sh：YuoCore submodule + 前端 node_modules）
if (-not (Test-Path "vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj")) {
    Write-Host "[0/5] 初始化 YuoCore submodule..." -ForegroundColor Yellow
    git submodule update --init --recursive
}
if (-not (Test-Path "frontend/node_modules")) {
    Write-Host "[0/5] 安装前端依赖..." -ForegroundColor Yellow
    npm --prefix frontend install
}

# 1. 检查数据库 (PostgreSQL: 5432)
Write-Host "[1/5] 检查本地 PostgreSQL 数据库 (5432)..." -ForegroundColor Yellow
$dbPortReady = $false
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect("127.0.0.1", 5432, $null, $null)
    $dbPortReady = $iar.AsyncWaitHandle.WaitOne(1000)
    if ($dbPortReady) { $tcp.EndConnect($iar) }
    $tcp.Close()
} catch {
    $dbPortReady = $false
}

if (-not $dbPortReady) {
    Write-Host "  -> 5432 端口未监听，尝试唤起本地 PostgreSQL..." -ForegroundColor Gray
    $pgCtl = "C:\Program Files\PostgreSQL\16\bin\pg_ctl.exe"
    $pgData = "C:\Program Files\PostgreSQL\16\data"
    if (Test-Path $pgCtl -and (Test-Path $pgData)) {
        & $pgCtl start -D $pgData -l "$env:TEMP\daohua-pg.log" | Out-Null
        Start-Sleep -Seconds 2
    } elseif (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host "  -> 尝试使用 Docker Compose 启动数据库..." -ForegroundColor Gray
        docker compose up -d db | Out-Null
        Start-Sleep -Seconds 3
    }
}

# 再次验证端口
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect("127.0.0.1", 5432, $null, $null)
    $dbPortReady = $iar.AsyncWaitHandle.WaitOne(2000)
    if ($dbPortReady) { $tcp.EndConnect($iar) }
    $tcp.Close()
} catch {
    $dbPortReady = $false
}

if ($dbPortReady) {
    Write-Host "  -> 数据库连接已就绪 (127.0.0.1:5432)。" -ForegroundColor Green

    # 自动执行 migrations/ 兼容性迁移链（对齐 dev.sh）
    $env:PGPASSWORD = "daohua-local-dev"
    $psql = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
    if (Test-Path $psql) {
        $curVerStr = & $psql -U daohua -h 127.0.0.1 -d daohua -t -A -c "SELECT CASE WHEN to_regclass('platform.schema_version') IS NULL THEN 0 ELSE COALESCE((SELECT max(version) FROM platform.schema_version), 0) END;" 2>$null
        $curVer = 0
        [int]::TryParse($curVerStr, [ref]$curVer) | Out-Null
        
        $reqVer = 0
        if (Test-Path "src/Server/Clean/PostgresSchema.cs") {
            $match = Select-String -Path "src/Server/Clean/PostgresSchema.cs" -Pattern 'Version\s*=\s*(\d+)' | Select-Object -First 1
            if ($match -and $match.Matches.Groups.Count -gt 1) {
                [int]::TryParse($match.Matches.Groups[1].Value, [ref]$reqVer) | Out-Null
            }
        }

        if ($reqVer -gt $curVer) {
            Write-Host "  -> 检测到本地 schema v$curVer 落后于代码要求 v$reqVer，自动执行 migrations/ 迁移..." -ForegroundColor Yellow
            Get-ChildItem -Path "migrations" -Filter "*_v*_to_v*.sql" -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
                if ($_.Name -match '_v(\d+)_to_v(\d+)\.sql') {
                    $fromVer = [int]$Matches[1]
                    $toVer = [int]$Matches[2]
                    if ($fromVer -ge $curVer -and $toVer -le $reqVer) {
                        Write-Host "     应用迁移: $($_.Name)" -ForegroundColor Gray
                        & $psql -U daohua -h 127.0.0.1 -d daohua -f $_.FullName | Out-Null
                        $curVer = $toVer
                    }
                }
            }
            Write-Host "  -> 迁移完成，当前 schema 已对齐至 v$curVer。" -ForegroundColor Green
        }
    }
} else {
    Write-Warning "  -> 未能连通 127.0.0.1:5432。请先启动本地 PostgreSQL 服务或 Docker。"
}

# 2. 清理旧进程残留 (避免 5128 / 15172 端口冲突)
#    Server.exe 是服务端实体进程；node dev-hot 是上次会话的热重启守护，
#    不清理会导致守护误杀本次新起的服务端实例（其崩溃自动拉起会重复抢占端口）。
Write-Host "[2/5] 清理旧会话残留进程..." -ForegroundColor Yellow
Get-Process -Name Server -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force; Write-Host "  -> 已清理旧 Server 进程 (PID: $($_.Id))" -ForegroundColor Gray } catch {}
}
Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'dev-hot\.mjs' } |
    ForEach-Object {
        try { Stop-Process -Id $_.ProcessId -Force; Write-Host "  -> 已清理旧 dev-hot 守护进程 (PID: $($_.ProcessId))" -ForegroundColor Gray } catch {}
    }

# 3. 启动后端热重启守护（对齐 dev.sh：node scripts/dev-hot.mjs 守护 dotnet run，
#    源码变更静默 60s 后先 dotnet build，编译失败保持旧进程，成功才平滑热重启，
#    崩溃自动拉起；独立窗口承载 [server dev] 守护日志）
Write-Host "[3/5] 启动服务端热重启守护 (http://127.0.0.1:5128)..." -ForegroundColor Yellow
$backendProcess = Start-Process node -ArgumentList "scripts/dev-hot.mjs" -WorkingDirectory $rootDir -PassThru

# 4. 等待服务端健康端点就绪（对齐 dev.sh：最长 60s 轮询）
Write-Host "[4/5] 等待服务端就绪..." -ForegroundColor Yellow
$backendReady = $false
for ($i = 0; $i -lt 120; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri "http://127.0.0.1:5128/healthz" -UseBasicParsing -TimeoutSec 2
        if ($resp.StatusCode -eq 200) { $backendReady = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $backendReady) {
    Write-Warning "  -> 服务端未能在等待窗口内就绪，请检查热重启守护窗口的构建日志。"
} else {
    Write-Host "  -> 服务端已就绪。" -ForegroundColor Green
    # 运维面板数据端点探测：缺失仅告警不阻断（对齐 dev.sh）
    try {
        Invoke-WebRequest -Uri "http://127.0.0.1:5128/ops/api/overview" -Headers @{ "X-Ops-Token" = "daohua-local-ops" } -UseBasicParsing -TimeoutSec 2 | Out-Null
        Write-Host "  -> 运维面板数据端点已就绪 (/ops/api/overview)。" -ForegroundColor Green
    } catch {
        Write-Warning "  -> 运维面板数据端点 (/ops/api/overview) 未就绪，面板可能不可用（请检查 Ops:Enabled 与 Ops:Token）。"
    }
}

# 5. 启动前端（对齐 dev.sh：固定 127.0.0.1:15172 且不自动开浏览器）
Write-Host "[5/5] 启动前端开发服务器 (http://127.0.0.1:15172)..." -ForegroundColor Yellow
$frontendProcess = Start-Process npm -ArgumentList "--prefix frontend run dev -- --host 127.0.0.1 --port 15172 --no-open" -WorkingDirectory $rootDir -PassThru

Write-Host ""
Write-Host "--------------------------------------------------" -ForegroundColor Green
Write-Host " 服务已在后台拉起：" -ForegroundColor Green
Write-Host "   前端地址: http://127.0.0.1:15172" -ForegroundColor Cyan
Write-Host "   后端 API: http://127.0.0.1:5128" -ForegroundColor Cyan
Write-Host "   健康检查: http://127.0.0.1:5128/healthz" -ForegroundColor Cyan
Write-Host "   热更新:   C# 源码/配表变更静默 60s 后重建重启（编译失败保持旧进程）" -ForegroundColor Cyan
Write-Host "--------------------------------------------------" -ForegroundColor Green
Write-Host " 按 Ctrl+C 停止前后端服务..." -ForegroundColor Gray
Write-Host ""

# 6. 守护并捕获退出：taskkill /T 级联终止整棵进程树
#    （dev-hot 守护 → dotnet run → Server.exe；npm → vite），再按名扫尾 Server 实体进程。
try {
    while (-not $backendProcess.HasExited -and -not $frontendProcess.HasExited) {
        Start-Sleep -Seconds 1
    }
} finally {
    Write-Host "`n正在停止前后端服务..." -ForegroundColor Yellow
    foreach ($proc in @($backendProcess, $frontendProcess)) {
        if ($proc -and -not $proc.HasExited) {
            try { taskkill /PID $proc.Id /T /F 2>$null | Out-Null } catch { }
        }
    }
    # 级联停止 Server 实体进程（兜底：进程树外残留的孤儿实例）
    Get-Process -Name Server -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "全部服务已停止。" -ForegroundColor Gray
}
