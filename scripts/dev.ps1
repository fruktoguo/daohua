<#
.SYNOPSIS
    道化太初 Windows 一键本地开发启动脚本 (PowerShell 原生)
.DESCRIPTION
    对齐 scripts/dev.sh 流程：
    1. 检查 YuoCore submodule 与前端依赖
    2. 检查并拉起本地数据库 (PostgreSQL 5432)，经 Git Bash 委托
       scripts/reset-outdated-players.sh 处置存档/schema 漂移（空库直接放行，
       由服务端 PostgresSchema.Sql 直建）
    3. 清理残留孤儿进程（5128 / 15172 端口）
    4. 启动后端热重启守护 scripts/dev-hot.mjs：
       只监听服务端编译闭包（src/Domain、src/Ecs、src/Protocol、src/Server、src/Server.Clean）
       与 config/game-data/、vendor/YuoCore 变更，静默 3s 防抖后
       dotnet build，编译失败保持旧进程，成功才优雅热重启；崩溃自动拉起
    5. 等待后端 /healthz 就绪并探测运维面板端点
    6. 启动前端 Vite 开发服务器 (http://127.0.0.1:15172)
    7. 监听退出信号，Ctrl+C 时级联终止前后端子进程树
#>

param(
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
# PS 7.4+ 默认为 $true，会把原生命令的非零退出变成终止错误，绕过下方对
# $LASTEXITCODE 的显式检查；本脚本统一自行判定原生命令结果。
$PSNativeCommandUseErrorActionPreference = $false
$rootDir = (Get-Item $PSScriptRoot).Parent.FullName
Set-Location $rootDir

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "         道化太初 Windows 开发环境启动" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 0. 依赖自检（对齐 dev.sh：YuoCore submodule + 前端 node_modules 与原生依赖）
if (-not (Test-Path "vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj")) {
    Write-Host "[0/5] 初始化 YuoCore submodule..." -ForegroundColor Yellow
    git submodule update --init --recursive
}
$npmCmd = Get-Command npm.cmd -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $npmCmd) {
    Write-Host "  -> 错误: PATH 中未找到 npm.cmd，请确认 Node.js 已安装。" -ForegroundColor Red
    exit 1
}
$needNpmInstall = -not (Test-Path "frontend/node_modules")
if (-not $needNpmInstall) {
    # 验证 esbuild 原生二进制是否适配当前平台（避免跨 WSL/Linux/Windows 切换时 @esbuild 原生二进制错配）
    try {
        $null = node scripts/export-i18n-csv.mjs 2>&1
        if ($LASTEXITCODE -ne 0) {
            $needNpmInstall = $true
        }
    } catch {
        $needNpmInstall = $true
    }
}
if ($needNpmInstall) {
    Write-Host "[0/5] 安装/修复前端原生依赖 (npm install)..." -ForegroundColor Yellow
    & $npmCmd --prefix frontend install
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

    # 存档/schema 漂移处置（对齐 dev.sh → reset-outdated-players.sh）：
    # 空库（无 platform.schema_version）直接放行，服务端启动时由 PostgresSchema.Sql
    # 直建当前版本；已初始化库交给 migrations/ 兼容性迁移链 + 不可迁移存档的
    # 控制台 RESET 交互确认，迁移失败或操作者拒绝一律中止启动。
    $env:PGPASSWORD = "daohua-local-dev"
    $psql = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
    if (-not (Test-Path $psql)) {
        $psqlCmd = Get-Command psql -ErrorAction SilentlyContinue
        if ($psqlCmd) { $psql = $psqlCmd.Source }
    }
    $dbInitialized = $true
    if (Test-Path $psql) {
        # 让处置脚本内 command -v psql 命中本机 PostgreSQL（其优先探测 docker
        # 容器 daohua-local-db，未运行时回退到 PATH 上的 psql）
        $env:PATH = "$(Split-Path $psql);$env:PATH"
        $hasVersionTable = & $psql -U daohua -h 127.0.0.1 -d daohua -t -A -v ON_ERROR_STOP=1 -c "SELECT to_regclass('platform.schema_version') IS NOT NULL;" 2>$null
        $dbInitialized = ("$hasVersionTable".Trim() -eq 't')
    }
    if (-not $dbInitialized) {
        Write-Host "  -> 本地库尚未初始化（无 platform.schema_version），跳过迁移：服务端启动时由 PostgresSchema.Sql 直建当前 schema。" -ForegroundColor Gray
    } else {
        # 已初始化（或本机无 psql 无法判定）时走完整处置链；优先 Git for Windows
        # 自带 bash，排除 PATH 上指向 WSL 的 System32\bash.exe 及 WindowsApps\bash.exe
        $bashExe = $null
        $gitCandidates = @(
            "$env:ProgramFiles\Git\bin\bash.exe",
            "${env:ProgramFiles(x86)}\Git\bin\bash.exe",
            "$env:LOCALAPPDATA\Programs\Git\bin\bash.exe",
            "$env:USERPROFILE\scoop\apps\git\current\bin\bash.exe"
        )
        foreach ($candidate in $gitCandidates) {
            if (Test-Path $candidate) { $bashExe = $candidate; break }
        }
        if (-not $bashExe) {
            $gitCmd = Get-Command git -ErrorAction SilentlyContinue
            if ($gitCmd) {
                $candidate = Join-Path (Split-Path (Split-Path $gitCmd.Source)) "bin\bash.exe"
                if (Test-Path $candidate) { $bashExe = $candidate }
            }
        }
        if (-not $bashExe) {
            $bashCmd = Get-Command bash -ErrorAction SilentlyContinue
            if ($bashCmd -and $bashCmd.Source -notmatch 'System32\\bash\.exe$' -and $bashCmd.Source -notmatch 'WindowsApps\\bash\.exe$') {
                $bashExe = $bashCmd.Source
            }
        }
        if (-not $bashExe) {
            Write-Host "  -> 错误: 本地库已初始化但未找到 Git Bash，无法执行存档漂移处置链；请安装 Git for Windows 或改用 scripts/dev.sh 启动。" -ForegroundColor Red
            exit 1
        }
        & $bashExe "scripts/reset-outdated-players.sh"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  -> 错误: 存档版本漂移未处置（迁移失败或已取消重置），启动中止。" -ForegroundColor Red
            exit 1
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
foreach ($port in @(5128, 15172)) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | ForEach-Object {
        $procId = $_.OwningProcess
        if ($procId -and $procId -ne 0) {
            try {
                $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
                if ($p) {
                    Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
                    Write-Host "  -> 已清理占用端口 $port 的残留进程 $($p.Name) (PID: $procId)" -ForegroundColor Gray
                }
            } catch {}
        }
    }
}

# 3. 启动后端热重启守护（对齐 dev.sh：node scripts/dev-hot.mjs 守护 dotnet run，
#    源码变更静默 3s 后先 dotnet build，编译失败保持旧进程，成功才平滑热重启，
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
#    Start-Process 必须显式指向 npm.cmd：裸 "npm" 会按文件关联解析到 npm.ps1，
#    被记事本等编辑器打开而不是执行，导致前端永远起不来且监控循环空转。
Write-Host "[5/5] 启动前端开发服务器 (http://127.0.0.1:15172)..." -ForegroundColor Yellow
$frontendProcess = Start-Process $npmCmd -ArgumentList "--prefix frontend run dev -- --host 127.0.0.1 --port 15172 --no-open" -WorkingDirectory $rootDir -PassThru

# 等待 vite 就绪（最长 30s）；进程提前退出立即报错，超时仅告警（冷启动依赖打包可能较慢）
$frontendReady = $false
for ($i = 0; $i -lt 60 -and -not $frontendProcess.HasExited; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri "http://127.0.0.1:15172" -UseBasicParsing -TimeoutSec 2
        if ($resp.StatusCode -eq 200) { $frontendReady = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 500
}
if ($frontendProcess.HasExited) {
    Write-Host "  -> 错误: 前端进程已提前退出 (ExitCode: $($frontendProcess.ExitCode))。" -ForegroundColor Red
    Write-Host "  -> 正在诊断前端退出原因..." -ForegroundColor Yellow
    $diagOutput = & $npmCmd --prefix frontend run dev -- --host 127.0.0.1 --port 15172 --no-open 2>&1
    Write-Host ($diagOutput -join "`n") -ForegroundColor Red
} elseif ($frontendReady) {
    Write-Host "  -> 前端已就绪。" -ForegroundColor Green
} else {
    Write-Warning "  -> 前端未能在 30s 内就绪，请查看 npm.cmd 窗口日志。"
}

if (-not $frontendProcess.HasExited) {
    Write-Host ""
    Write-Host "--------------------------------------------------" -ForegroundColor Green
    Write-Host " 服务已在后台拉起：" -ForegroundColor Green
    Write-Host "   前端地址: http://127.0.0.1:15172" -ForegroundColor Cyan
    Write-Host "   后端 API: http://127.0.0.1:5128" -ForegroundColor Cyan
    Write-Host "   健康检查: http://127.0.0.1:5128/healthz" -ForegroundColor Cyan
    Write-Host "   热更新:   C# 源码/配表变更静默 3s 后重建重启（编译失败保持旧进程）" -ForegroundColor Cyan
    Write-Host "--------------------------------------------------" -ForegroundColor Green
    Write-Host " 按 Ctrl+C 停止前后端服务..." -ForegroundColor Gray
    Write-Host ""
}

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
