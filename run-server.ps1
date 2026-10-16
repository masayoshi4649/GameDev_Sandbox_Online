<#
機能: 描画を行わない専用サーバーを現在のコンソールで起動します。
引数: Port（int）UDPポート、SavePath（string）都市の保存先、AutosaveSeconds（double）保存間隔、GodotPath（string）エンジン。
返り値: なし。失敗時は例外を送出します。
#>
param(
    [ValidateRange(1024, 65535)][int]$Port = 24567,
    [string]$SavePath = '',
    [ValidateRange(1, 3600)][double]$AutosaveSeconds = 30,
    [string]$GodotPath = 'S:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
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
$interval = $AutosaveSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
Push-Location $PSScriptRoot
try {
    & $dotnetPath build BayfrontSandbox.csproj --nologo
    if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
    & $GodotPath --headless --editor --path $PSScriptRoot --import
    if ($LASTEXITCODE -ne 0) { throw 'リソース取り込みに失敗しました。' }
    $serverArguments = @('--server', "--port=$Port", "--autosave-seconds=$interval")
    if ($SavePath) { $serverArguments += "--save-path=$SavePath" }
    & $GodotPath --headless --audio-driver Dummy --path $PSScriptRoot -- @serverArguments
    if ($LASTEXITCODE -ne 0) { throw "専用サーバーが終了コード $LASTEXITCODE で停止しました。" }
}
finally { Pop-Location }
