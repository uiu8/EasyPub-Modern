<#
.SYNOPSIS
    构建一个 EasyPub Modern 发布版本：校验版本号 → 编译 → 补齐运行时文件 → 打便携包 → 出安装包。

.DESCRIPTION
    本脚本不改任何版本号，只校验并构建。发版前请先把版本号改到四处：
    EasyPub.Desktop.csproj 的 Version / AssemblyVersion / FileVersion，
    以及 installer/EasyPubModern.iss 的 AppVersion 与 PublishDir。
    不一致会直接报错退出——历史上漏改 AssemblyVersion 会让窗口标题显示旧版本，
    漏改 PublishDir 会让 ISCC 报 "No files found"。

    构建完成后用 tools/publish-github.ps1 推送并创建 GitHub Release。

.EXAMPLE
    pwsh tools/build-release.ps1 -Version 1.57.0 -Codename in-app-update
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Codename,
    [string]$Dotnet = 'D:\software\dotnet-sdk-10.0.302\dotnet.exe',
    [string]$Iscc = 'D:\software\Inno Setup 7\ISCC.exe',
    [string]$Kindling = 'C:\Users\13168\Desktop\kindling-cli-windows.exe'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\EasyPub.Desktop\EasyPub.Desktop.csproj'
$iss = Join-Path $root 'installer\EasyPubModern.iss'
$output = Join-Path $root "outputs\EasyPubModern-v$Version-$Codename-win-x64"

# ---- 1) 版本号闸门：四处必须与参数一致 -------------------------------------
$csproj = Get-Content $project -Raw
foreach ($field in 'Version', 'AssemblyVersion', 'FileVersion') {
    $expected = if ($field -eq 'Version') { $Version } else { "$Version.0" }
    if ($csproj -notmatch "<$field>$([regex]::Escape($expected))</$field>") {
        throw "$field 不是 $expected；请先在 $project 里改好四处版本号。"
    }
}
$issText = Get-Content $iss -Raw
$appVersionPattern = '#define AppVersion "' + [regex]::Escape($Version) + '"'
if ($issText -notmatch $appVersionPattern) {
    throw "AppVersion 不是 $Version；请先在 installer/EasyPubModern.iss 里改好。"
}
$publishDirName = "EasyPubModern-v$Version-$Codename-win-x64"
if ($issText -notmatch ([regex]::Escape($publishDirName))) {
    throw "PublishDir 与本次产物目录名不一致；请把 iss 里的 PublishDir 改成 ..\outputs\EasyPubModern-v$Version-$Codename-win-x64。"
}
Write-Host "版本号校验通过：$Version ($Codename)"

# ---- 2) 停掉会锁住构建输出的进程 -------------------------------------------
# 便携版是从 outputs 运行的，不锁 src 下的构建输出，不必打断用户。
Get-Process -Name 'EasyPub.Desktop' -ErrorAction SilentlyContinue | ForEach-Object {
    $path = try { $_.Path } catch { '' }
    if ($path -like "$root\src\*") { Write-Host "停止 $path（它锁着构建输出）"; $_ | Stop-Process -Force }
    else { Write-Host "保留运行中的 $path" }
}
Start-Sleep -Seconds 2

# ---- 3) 改过 Core 就必须清掉它的 obj/bin -----------------------------------
# MSBuild 会静默跳过 CoreCompile，留下旧逻辑打出来的包。
foreach ($directory in 'obj', 'bin') {
    $target = Join-Path $root "src\EasyPub.Core\$directory"
    if (Test-Path -LiteralPath $target) {
        $resolved = (Resolve-Path -LiteralPath $target).Path
        if (-not $resolved.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Core 产物路径越界' }
        $backup = Join-Path $root ('work\build-backups\' + (Get-Date -Format yyyyMMdd-HHmmss-fff) + '-release')
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        Get-ChildItem -LiteralPath $resolved -Recurse -File | Select-Object FullName,Length |
            Export-Csv (Join-Path $backup "$directory.csv") -NoTypeInformation
        Move-Item -LiteralPath $resolved -Destination (Join-Path $backup $directory)
    }
}

# ---- 4) 发布 ----------------------------------------------------------------
Write-Host "正在编译发布到 $output"
& $Dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）。" }

# ---- 5) 补齐 kindlegen ------------------------------------------------------
# 源要用"确实存在"的目录：签名里那个 v1.48.0 早已不存在，Copy-Item 静默失败会发出残包。
$package = Get-Item (Join-Path $output 'EasyPub.Desktop.exe')
if (-not $package) { throw "没有产出 EasyPub.Desktop.exe。" }
if (-not (Test-Path (Join-Path $output 'bin\kindlegen_v2.9.exe'))) {
    $source = Get-ChildItem (Join-Path $root 'outputs') -Directory -Filter 'EasyPubModern-v*-win-x64' |
        Where-Object { Test-Path (Join-Path $_.FullName 'bin\kindlegen_v2.9.exe') } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $source) { throw '找不到任何带 kindlegen 的历史产物，无法补齐运行时依赖。' }
    New-Item -ItemType Directory -Path (Join-Path $output 'bin') -Force | Out-Null
    Copy-Item (Join-Path $source.FullName 'bin\kindlegen_v2.9.exe') (Join-Path $output 'bin\') -Force
    Write-Host "kindlegen 取自 $($source.Name)"
}
if (-not (Test-Path (Join-Path $output 'config.xml'))) {
    $config = Get-ChildItem (Join-Path $root 'src\EasyPub.Desktop\config.xml') -ErrorAction SilentlyContinue
    if ($config) { Copy-Item $config.FullName $output -Force }
}

# ---- 6) 便携包 + 体积闸门 ---------------------------------------------------
if (-not (Test-Path -LiteralPath $Kindling)) { throw '缺少 Kindling 引擎，不能发布残包。' }
Copy-Item -LiteralPath $Kindling -Destination (Join-Path $output 'bin\kindling-cli-windows.exe')
Copy-Item -LiteralPath (Join-Path $root 'vendor\kindling\LICENSE') -Destination (Join-Path $output 'bin\Kindling-LICENSE.txt')
$usageGuide = Join-Path $root "docs\v$Version-使用说明.md"
if (Test-Path -LiteralPath $usageGuide) {
    Copy-Item -LiteralPath $usageGuide -Destination (Join-Path $output '使用说明.md') -Force
}
$zip = "$output.zip"
if (Test-Path $zip) { throw '发布包已存在，请使用新版本或先归档旧包。' }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
$zipItem = Get-Item $zip
$megabytes = [math]::Round($zipItem.Length / 1MB, 2)
# 完整包约 65MB。跌破 60MB 基本就是漏了 kindlegen（约 7.5MB），必须拦下来。
if ($megabytes -lt 60) { throw "便携包只有 $megabytes MB，像是漏了 kindlegen；请检查 $output\bin。" }
Write-Host "便携包：$($zipItem.Name)　$megabytes MB"

# ---- 7) 安装包 --------------------------------------------------------------
Write-Host '正在生成安装包'
& $Iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 失败（$LASTEXITCODE）。" }
$setup = Get-ChildItem (Join-Path $root 'outputs') -Filter "EasyPubModern-Setup-v$Version-x64.exe" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "没有找到安装包 EasyPubModern-Setup-v$Version-x64.exe。" }

Write-Host ''
Write-Host '构建完成：'
Write-Host "  exe    $([math]::Round($package.Length / 1MB, 2)) MB　$($package.FullName)"
Write-Host "  zip    $megabytes MB　$zip"
Write-Host "  setup  $([math]::Round($setup.Length / 1MB, 2)) MB　$($setup.FullName)"
