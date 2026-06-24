<#
.SYNOPSIS
    一键发布 LCPSAutomate 新版本。

.DESCRIPTION
    自动完成：改 csproj 版本号 → 提交 → publish 多文件 self-contained 包 →
    打 zip → 打 git tag → 推送 → 创建 GitHub Release 并上传 zip。

.PARAMETER Version
    新版本号，纯数字（如 1.0.1 / 1.1.0 / 2.0.0），不要带 "v" 前缀。

.PARAMETER Notes
    Release 说明，可选。不传则用最近一次提交的标题 + 这次到上一个 tag 的提交列表。

.PARAMETER PreRelease
    标记为 pre-release，可选。

.PARAMETER SkipPush
    只在本地完成所有步骤（包括 publish 和 zip），不推 git tag、不创建 Release，用于演练。

.EXAMPLE
    .\release.ps1 1.0.1
    .\release.ps1 1.1.0 -Notes "新增 X 功能；修复 Y bug"
    .\release.ps1 2.0.0-beta1 -PreRelease
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [string]$Notes,

    [switch]$PreRelease,

    [switch]$SkipPush
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Compress-Archive 不刷屏

# ---------- 路径 & 工具 ----------
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $RepoRoot

$Csproj  = Join-Path $RepoRoot 'LCPSAutomate.csproj'
$Tag     = "v$Version"
$PubDir  = Join-Path $RepoRoot "publish\$Tag"
$ZipName = "LCPSAutomate-$Tag-win-x64.zip"
$ZipPath = Join-Path $RepoRoot "publish\$ZipName"

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Die($msg)  { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# 找 gh.exe —— winget 装完不一定立刻进 PATH
$Gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $Gh) {
    $candidate = 'C:\Program Files\GitHub CLI\gh.exe'
    if (Test-Path $candidate) { $Gh = $candidate }
}
if (-not $SkipPush -and -not $Gh) {
    Die "未找到 gh CLI。装一下：winget install --id GitHub.cli  或加 -SkipPush 跳过 Release。"
}

# ---------- 预检 ----------
Step "预检"

if ($Version -notmatch '^\d+\.\d+\.\d+(-[\w.]+)?$') {
    Die "版本号格式不对：$Version（应类似 1.0.1 或 2.0.0-beta1）"
}

if (-not (Test-Path $Csproj)) { Die "找不到 $Csproj" }

# 工作区必须干净
$dirty = git status --porcelain
if ($dirty) {
    Write-Host $dirty
    Die "工作区有未提交的改动，请先 commit/stash。"
}

# 必须在 main
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { Die "当前分支是 $branch，请切到 main。" }

# tag 不能已存在
$tagExists = git tag --list $Tag
if ($tagExists) { Die "tag $Tag 已存在（本地）。" }

if (-not $SkipPush) {
    git fetch --tags --quiet
    $remoteTag = git ls-remote --tags origin "refs/tags/$Tag"
    if ($remoteTag) { Die "tag $Tag 已存在（远端 origin）。" }
}

# 用作 release notes 起点：上一个 tag
$prevTag = (git describe --tags --abbrev=0 2>$null)
if ($LASTEXITCODE -ne 0) { $prevTag = $null }

# ---------- 1. 改 csproj 版本号 ----------
Step "更新 csproj 版本号 -> $Version"

$assemblyVer = if ($Version -match '^(\d+\.\d+\.\d+)') { "$($Matches[1]).0" } else { "$Version.0" }

$csprojText = Get-Content $Csproj -Raw -Encoding UTF8
$csprojText = [Regex]::Replace($csprojText, '<Version>[^<]*</Version>',                 "<Version>$Version</Version>")
$csprojText = [Regex]::Replace($csprojText, '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$assemblyVer</AssemblyVersion>")
$csprojText = [Regex]::Replace($csprojText, '<FileVersion>[^<]*</FileVersion>',         "<FileVersion>$assemblyVer</FileVersion>")
Set-Content $Csproj -Value $csprojText -Encoding UTF8 -NoNewline

git add $Csproj
git commit -m "发布 $Tag" | Out-Null

# ---------- 2. dotnet publish（多文件 self-contained）----------
Step "dotnet publish -> $PubDir"

if (Test-Path $PubDir) { Remove-Item -Recurse -Force $PubDir }

dotnet publish $Csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $PubDir `
    -nologo
if ($LASTEXITCODE -ne 0) { Die "dotnet publish 失败" }

# nlog.config 必须存在
if (-not (Test-Path (Join-Path $PubDir 'nlog.config'))) {
    Die "publish 目录里没有 nlog.config，请检查 csproj 里的 CopyToOutputDirectory 设置。"
}

# ---------- 3. 打 zip ----------
Step "打包 -> $ZipPath"
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
Compress-Archive -Path (Join-Path $PubDir '*') -DestinationPath $ZipPath -Force

$zipSize = (Get-Item $ZipPath).Length / 1MB
Write-Host ("    zip 大小: {0:N1} MB" -f $zipSize)

# ---------- 4. 推送 git ----------
if ($SkipPush) {
    Step "SkipPush 已指定，跳过 git push / tag / Release"
    Write-Host "  本地 commit 已生成，zip 在: $ZipPath"
    Write-Host "  撤销本地 commit：git reset --soft HEAD~1"
    return
}

Step "推送 main + tag"
git push origin main
if ($LASTEXITCODE -ne 0) { Die "git push main 失败" }

# 准备 tag 消息
$tagMessage = "$Tag"
if ($Notes) { $tagMessage = "$Tag`n`n$Notes" }
git tag -a $Tag -m $tagMessage
git push origin $Tag
if ($LASTEXITCODE -ne 0) { Die "git push tag 失败" }

# ---------- 5. 创建 GitHub Release ----------
Step "创建 GitHub Release"

# Release notes：用户给就用用户的；没给就用 git log 自动生成
if (-not $Notes) {
    if ($prevTag) {
        $log = git log "$prevTag..HEAD" --pretty=format:"- %s" --no-merges
        $Notes = "## 变更`n`n$log`n`n## 下载`n`n下载 ``$ZipName``，解压到任意目录，双击 ``LCPSAutomate.exe`` 运行（自带 .NET 8 运行时）。"
    } else {
        $Notes = "首个发布版本。下载 ``$ZipName``，解压后运行 ``LCPSAutomate.exe``。"
    }
}

$ghArgs = @('release', 'create', $Tag, $ZipPath, '--title', "$Tag", '--notes', $Notes)
if ($PreRelease) { $ghArgs += '--prerelease' }

& $Gh @ghArgs
if ($LASTEXITCODE -ne 0) { Die "gh release create 失败" }

Step "完成 ✓"
Write-Host "  https://github.com/Henxun/LCPSAutomate/releases/tag/$Tag" -ForegroundColor Green
