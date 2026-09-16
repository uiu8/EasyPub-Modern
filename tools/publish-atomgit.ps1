<#
.SYNOPSIS
    把已构建好的版本同步发布到 AtomGit 镜像仓库：创建 Release → 上传附件 → 回校验。

.DESCRIPTION
    AtomGit 的接口与 GitHub/Gitee 同构（/api/v5/repos/{owner}/{repo}/releases/...），
    但有几处实测出来的差异，脚本里都做了处理：

      * 附件上传是两步：先 GET .../releases/{tag}/upload_url 取一个预签名地址和一组
        必需的请求头，再用 PUT 把文件传上去（对象存储在 file.gitcode.com）。
      * assets 里会混进平台自动生成的源码包（type=source），必须靠 type 区分，
        否则回校验会数错附件个数。
      * 附件的 browser_download_url 落在 gitcode.com 上——客户端要能访问这个域。

    与 publish-github.ps1 一样，发布说明取自 docs/RELEASE_v<版本>.md。

.EXAMPLE
    pwsh tools/publish-atomgit.ps1 -Version 1.57.2 -Codename backup-name
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Codename,
    [string]$Owner = 'Wohl',
    [string]$Repository = 'EasyPub-Modern',
    [string]$TokenPath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($TokenPath)) { $TokenPath = Join-Path $root '.atomgit-token' }

$tag = "v$Version"
$output = Join-Path $root "outputs\EasyPubModern-v$Version-$Codename-win-x64"
$files = @("$output.zip", (Join-Path $root "outputs\EasyPubModern-Setup-v$Version-x64.exe"))
$notes = Join-Path $root "docs\RELEASE_v$Version.md"
$api = "https://atomgit.com/api/v5/repos/$Owner/$Repository"

# ---- 0) 凭据与产物 ----------------------------------------------------------
if (-not (Test-Path $TokenPath)) {
    throw "找不到 AtomGit 访问令牌文件 $TokenPath。请在 AtomGit 生成访问令牌后写入该文件（不要提交到仓库）。"
}
$token = (Get-Content $TokenPath -Raw).Trim()
if ($token.Length -lt 8) { throw "$TokenPath 内容看起来不是有效的令牌。" }
foreach ($file in $files) {
    if (-not (Test-Path $file)) { throw "缺少产物 $file；先跑 tools/build-release.ps1。" }
}
if (-not (Test-Path $notes)) { throw "缺少发布说明 $notes。" }

# 令牌只经查询参数传递（这是 AtomGit 文档要求的方式），因此任何回显都要先脱敏。
function Hide-Token([string]$text) {
    if ([string]::IsNullOrEmpty($token)) { return $text }
    return $text -replace [regex]::Escape($token), '***'
}
function Invoke-AtomGit([string]$Uri, [string]$Method = 'Get', $Body = $null) {
    # 注意别用 $args 当变量名——它是 PowerShell 的自动变量。
    $params = @{ Uri = "$Uri$(if ($Uri.Contains('?')) { '&' } else { '?' })access_token=$token"; Method = $Method; TimeoutSec = 120 }
    if ($null -ne $Body) { $params.Body = $Body; $params.ContentType = 'application/json; charset=utf-8' }
    try { return Invoke-RestMethod @params }
    catch { throw (Hide-Token $_.Exception.Message) }
}

# ---- 1) 仓库可达性与重复发布检查 --------------------------------------------
Write-Host "目标：AtomGit $Owner/$Repository  版本 $tag"
$existing = $null
try { $existing = Invoke-AtomGit "$api/releases/tags/$tag" } catch { $existing = $null }
if ($existing) { throw "$tag 在 AtomGit 上已经存在，不重复发布。" }

# ---- 2) 创建 Release --------------------------------------------------------
$payload = @{
    tag_name       = $tag
    name           = "EasyPub Modern $Version"
    body           = (Get-Content $notes -Raw)
    release_status = 'latest'
} | ConvertTo-Json -Depth 3
$release = Invoke-AtomGit "$api/releases" 'Post' $payload
Write-Host "Release 已创建：$tag（release_status=$($release.release_status)）"

# ---- 3) 逐个上传附件 --------------------------------------------------------
foreach ($file in $files) {
    $name = Split-Path $file -Leaf
    $size = [math]::Round((Get-Item $file).Length / 1MB, 2)
    Write-Host "上传 $name（$size MB）"
    # 两步：先取预签名上传地址（必须带 file_name），再用 PUT 传文件。
    $uploadUrl = "$api/releases/$tag/upload_url?access_token=$token&file_name=$([uri]::EscapeDataString($name))"
    try { $upload = Invoke-RestMethod -Uri $uploadUrl -TimeoutSec 60 }
    catch { throw (Hide-Token "取上传地址失败：$($_.Exception.Message)") }
    $headers = @{}
    $upload.headers.PSObject.Properties | ForEach-Object { $headers[$_.Name] = [string]$_.Value }
    try {
        $response = Invoke-WebRequest -Uri $upload.url -Method Put -InFile $file -Headers $headers -TimeoutSec 1800 -UseBasicParsing
    } catch {
        throw (Hide-Token "上传 $name 失败：$($_.Exception.Message)")
    }
    if ($response.StatusCode -ne 200) { throw "上传 $name 返回 HTTP $($response.StatusCode)。" }
    Write-Host "  完成"
}

# ---- 4) 回校验：远端必须真的收到两个 attach 附件 -----------------------------
$latest = Invoke-AtomGit "$api/releases/latest"
$attached = @($latest.assets | Where-Object { $_.type -eq 'attach' })
Write-Host "远端 attach 附件："
$attached | ForEach-Object { Write-Host "  $($_.name)" }
if ($attached.Count -ne $files.Count) {
    throw "远端只有 $($attached.Count) 个附件，应为 $($files.Count) 个（源码包 type=source 已排除）。"
}
foreach ($file in $files) {
    $name = Split-Path $file -Leaf
    if (-not ($attached | Where-Object { $_.name -eq $name })) { throw "远端没有找到附件 $name。" }
}

Write-Host ''
Write-Host "同步完成：$api/releases/tag/$tag"
Write-Host '客户端的更新检查现在会在此源上看到这个版本（GitHub 不可达时自动回退到这里）。'
