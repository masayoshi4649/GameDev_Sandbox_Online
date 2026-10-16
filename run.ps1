<#
機能: .NET SDKを検出し、ビルド後にゲームまたはGodotエディターを起動します。
引数: GodotPath（string）エンジンの実行ファイル、Editor（switch）編集モード、SkipBuild（switch）ビルド省略、Compatibility（switch）互換描画、Sample（switch）旧簡略地図。
返り値: なし。失敗時は例外を送出します。
#>
param(
    [string]$GodotPath = 'S:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe',
    [switch]$Editor,
    [switch]$SkipBuild,
    [switch]$Compatibility,
    [switch]$Sample
)
$ErrorActionPreference = 'Stop'
$sdkCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)
$dotnetPath = $null
foreach ($candidate in $sdkCandidates) {
    if ((Test-Path -LiteralPath $candidate) -and (& $candidate --list-sdks)) { $dotnetPath = $candidate; break }
}
if (!$dotnetPath) { throw '.NET 8 SDKが必要です。先に .\setup.ps1 を実行してください。' }
if (!(Test-Path -LiteralPath $GodotPath)) { throw "Godotが見つかりません: $GodotPath" }
if (!$Sample) {
    foreach ($file in @('ground.json', 'elevation.json', 'buildings.json', 'plateau-manifest.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot "Data\RealWorld\$file"))) { throw '実都市データがありません。先に .\import-world.ps1 を実行してください。' }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Data\RealWorld\plateau-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($tile in $manifest.tiles) {
        if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot ($tile.path -replace '^res://', '')))) { throw '実建物のキャッシュが不足しています。.\import-world.ps1 で再取得してください。' }
        if (!$tile.featurePath -or !$tile.featurePath.StartsWith('res://') -or !(Test-Path -LiteralPath (Join-Path $PSScriptRoot ($tile.featurePath -replace '^res://', '')) -PathType Leaf)) {
            throw "実建物を個別に編集するための地物対応表が不足しています。.\import-world.ps1 で再生成してください。対象: $($tile.id)"
        }
    }
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnetPath
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
Push-Location $PSScriptRoot
try {
    if (!$SkipBuild) {
        & $dotnetPath build BayfrontSandbox.csproj --nologo
        if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
        $consolePath = $GodotPath -replace '(?<!_console)\.exe$', '_console.exe'
        if (!(Test-Path -LiteralPath $consolePath)) { $consolePath = $GodotPath }
        & $consolePath --headless --editor --path $PSScriptRoot --import
        if ($LASTEXITCODE -ne 0) { throw 'アセットのインポートに失敗しました。' }
    }
    $engineArguments = @('--path', ('"' + $PSScriptRoot + '"'))
    if ($Editor) { $engineArguments += '--editor' }
    if ($Compatibility) { $engineArguments += @('--rendering-method', 'gl_compatibility') }
    if ($Sample) { $engineArguments += @('--', '--sample-map') }
    $gameProcess = Start-Process -FilePath $GodotPath -ArgumentList $engineArguments -WorkingDirectory $PSScriptRoot -WindowStyle Normal -PassThru
    if ($gameProcess.WaitForExit(1000)) { throw "Godotが起動直後に終了しました。終了コード: $($gameProcess.ExitCode)" }
    Write-Host "ゲーム画面を開きました。プロセスID: $($gameProcess.Id)"
}
finally { Pop-Location }
