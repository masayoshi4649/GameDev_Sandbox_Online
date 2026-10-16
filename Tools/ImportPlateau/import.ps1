[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$importDirectory = $PSScriptRoot
Push-Location -LiteralPath $importDirectory
try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw 'Node.js 22以降を導入してください。'
    }
    & npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Draco依存ライブラリの導入に失敗しました。' }

    # ----------------------------------------

    & node import.mjs
    if ($LASTEXITCODE -ne 0) { throw 'PLATEAU実データの取込に失敗しました。' }
}
finally {
    Pop-Location
}
