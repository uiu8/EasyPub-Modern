<#
.SYNOPSIS
    把已构建好的版本发布到 GitHub：推送代码 → 创建 Release → 上传资产 → 回校验。

.DESCRIPTION
    先用 tools/build-release.ps1 产出便携包与安装包，再用本脚本发布。
    发布说明取自 docs/RELEASE_v<版本>.md —— 没有这份文档就不发，避免发出没有说明的版本。

    网络说明：本机直连 github.com:443 不通，仓库级 git 代理已配置（git config http.proxy）；
    gh 走 api.github.com 与 uploads.github.com 可直连，因此无需额外设置。
    换机器若 push 失败，先确认 git 能访问 github.com。

.EXAMPLE
    pwsh tools/publish-github.ps1 -Version 1.57.0 -Codename in-app-update
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Codename,
    [string]$Repository = 'uiu8/EasyPub-Modern',
    [switch]$SkipPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
$output = Join-Path $root "outputs\EasyPubModern-v$Version-$Codename-win-x64"
$zip = "$output.zip"
$setup = Join-Path $root "outputs\EasyPubModern-Setup-v$Version-x64.exe"
$notes = Join-Path $root "docs\RELEASE_v$Version.md"

# ---- 1) 产物与说明齐不齐 ----------------------------------------------------
foreach ($file in $zip, $setup) {
    if (-not (Test-Path $file)) { throw "缺少产物 $file；先跑 tools/build-release.ps1。" }
}
if (-not (Test-Path $notes)) { throw "缺少发布说明 $notes；先写好这份文档再发布。" }
$body = Get-Content $notes -Raw
if ($body.Length -lt 200) { throw "$notes 内容过短，像是没写完。" }
Write-Host "待发布：$tag　便携包 $([math]::Round((Get-Item $zip).Length / 1MB, 2)) MB　安装包 $([math]::Round((Get-Item $setup).Length / 1MB, 2)) MB"

# ---- 2) 远端是否已经有这个 tag ----------------------------------------------
$existing = gh release view $tag --repo $Repository --json tagName 2>$null
if ($LASTEXITCODE -eq 0 -and $existing) { throw "$tag 已经存在，不能重复发布。要重发请先在 GitHub 上删掉它。" }

# ---- 3) 推送代码 ------------------------------------------------------------
if (-not $SkipPush) {
    $dirty = git -C $root status --porcelain
    if ($dirty) { Write-Warning "工作区有未提交改动，它们不会进入这次发布：`n$dirty" }
    Write-Host '正在推送 main'
    git -C $root push origin main
    if ($LASTEXITCODE -ne 0) { throw 'git push 失败：确认网络（可能需要代理）与凭据。' }
}

# ---- 4) 创建 Release --------------------------------------------------------
Write-Host "正在创建 $tag"
gh release create $tag --repo $Repository --title "EasyPub Modern $Version" --notes-file $notes --target main
if ($LASTEXITCODE -ne 0) { throw "创建 Release 失败（$LASTEXITCODE）。" }

# ---- 5) 上传资产 ------------------------------------------------------------
Write-Host '正在上传资产（约 130MB，需要几分钟）'
gh release upload $tag $zip $setup --repo $Repository
if ($LASTEXITCODE -ne 0) { throw "上传资产失败（$LASTEXITCODE）。" }

# ---- 6) 回校验：远端必须真的收到两个资产 -------------------------------------
$assets = gh release view $tag --repo $Repository --json assets --jq '.assets[] | "\(.name) \(.size) \(.state)"'
Write-Host '远端资产：'
$assets | ForEach-Object { Write-Host "  $_" }
$count = ($assets | Measure-Object).Count
if ($count -ne 2) { throw "远端只有 $count 个资产，应为 2 个。" }

# 大小必须与本地一致——上传中断会留下截断的资产。
$local = @{ (Split-Path $zip -Leaf) = (Get-Item $zip).Length; (Split-Path $setup -Leaf) = (Get-Item $setup).Length }
foreach ($line in $assets) {
    $parts = $line -split ' '
    $name = $parts[0]
    $size = [long]$parts[1]
    if ($local.ContainsKey($name) -and $local[$name] -ne $size) {
        throw "$name 远端 $size 字节，本地 $($local[$name]) 字节，大小对不上。"
    }
}
Write-Host ''
Write-Host "发布完成：https://github.com/$Repository/releases/tag/$tag"
