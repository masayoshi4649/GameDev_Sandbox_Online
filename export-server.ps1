<#
機能: GPU不要のLinux x86_64専用サーバーの配布ファイルを作成します。
引数: GodotPath（string）.NET版エンジン、OutputDirectory（string）出力先。
返り値: なし。配布に必要な実行ファイル、PCK、.NETデータを出力します。
#>
param(
    [string]$GodotPath = 'S:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build\server')
)
$ErrorActionPreference = 'Stop'
$dotnetPath = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (!$command) { throw '先に .\setup.ps1 で.NET SDKを導入してください。' }
    $dotnetPath = $command.Source
}
if (!(Test-Path -LiteralPath $GodotPath)) { throw "Godotが見つかりません: $GodotPath" }
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Push-Location $PSScriptRoot
try {
    & $dotnetPath build BayfrontSandbox.csproj --nologo
    if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
    & $GodotPath --headless --editor --path $PSScriptRoot --import
    if ($LASTEXITCODE -ne 0) { throw 'リソース取り込みに失敗しました。' }
    Write-Host 'Linux専用サーバーを出力しています。'
    $exportOutput = & $GodotPath --headless --path $PSScriptRoot --export-release 'Linux 専用サーバー' (Join-Path $OutputDirectory 'BayfrontServer.x86_64') 2>&1
    $exportCode = $LASTEXITCODE
    $exportLog = Join-Path $OutputDirectory 'export.log'
    $exportOutput | Set-Content -LiteralPath $exportLog -Encoding utf8
    $managedFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Filter 'BayfrontSandbox.dll')
    $runtimeFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Filter 'libcoreclr.so')
    if ($exportCode -ne 0 -or ($exportOutput -match '^ERROR:') -or $managedFiles.Count -eq 0 -or $runtimeFiles.Count -eq 0) {
        $exportOutput | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
        throw "Linux向け出力に失敗しました。詳細ログ: $exportLog"
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server\run-server.sh') -Destination $OutputDirectory -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets\Fonts\OFL.txt') -Destination $OutputDirectory -Force
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Assets\Licenses') -Filter '*.txt' | Copy-Item -Destination $OutputDirectory -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\専用サーバー.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
    Write-Host "Linux用の配布ファイルを作成しました: $OutputDirectory"
    Write-Host '実行ファイルだけでなく、PCKと.NETデータを含むフォルダー全体を配布してください。'
}
finally { Pop-Location }
