<#
機能: 現在の利用者向けにMicrosoft公式スクリプトで.NET 8 SDKを導入します。
引数: なし。
返り値: なし。失敗時は例外を送出します。
#>
param()
$ErrorActionPreference = 'Stop'
$sdkDirectory = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$sdkExecutable = Join-Path $sdkDirectory 'dotnet.exe'
if ((Test-Path -LiteralPath $sdkExecutable) -and (& $sdkExecutable --list-sdks)) {
    Write-Host '.NET SDKは導入済みです。'
    exit 0
}
$installerPath = Join-Path $env:TEMP 'bayfront-dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
& $installerPath -Channel 8.0 -InstallDir $sdkDirectory -NoPath
if ($LASTEXITCODE -ne 0) { throw '.NET SDKの導入に失敗しました。' }
Write-Host '.NET SDKを導入しました。.\run.ps1 で都市を開けます。'
