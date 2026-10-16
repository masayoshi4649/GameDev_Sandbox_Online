<#
機能: 国の公開地理データを取得・変換し、描画用と CPU 用の成果物を検査します。
引数: SkipDownload/switch 通信・依存導入・再生成を行わず既存成果物だけ検査します。
      Workers/int 写真・地表取得の並列数、MeshSize/int 写真 1 枚の地表頂点数、PhotoZoom/int 元写真のズームです。
      SkipTextureCache/switch 建物写真の事前変換を省略、TextureCacheSize/int 表示用写真の長辺上限です。
返り値: なし。失敗時は例外を送出し、実行記録を artifacts に保存します。
#>
[CmdletBinding()]
param(
    [switch]$SkipDownload,
    [ValidateRange(1, 6)][int]$Workers = 6,
    [ValidateSet(17, 33, 65)][int]$MeshSize = 65,
    [ValidateSet(16, 17, 18)][int]$PhotoZoom = 18,
    [switch]$SkipTextureCache,
    [ValidateRange(256, 8192)][int]$TextureCacheSize = 2048
)

$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$runtimeDirectory = Join-Path $projectDirectory 'build/import-runtime'
$plateauDirectory = Join-Path $projectDirectory 'Tools/ImportPlateau'
$groundDirectory = Join-Path $projectDirectory 'Tools/ImportGround'
$artifactDirectory = Join-Path $projectDirectory 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$logPath = Join-Path $artifactDirectory ('import-world-{0}.log' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))


# ----------------------------------------
# 実行環境の準備
function Invoke-ImportCommand {
    <#
    機能: 外部コマンドの終了コードを検査し、失敗を後続処理へ伝えます。
    引数: Executable/string 実行ファイル、Arguments/string[] 引数、Description/string 作業名。
    返り値: なし。標準出力は画面と実行記録へ表示します。
    #>
    param([string]$Executable, [string[]]$Arguments, [string]$Description)
    Write-Host $Description
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Description に失敗しました。終了コード: $LASTEXITCODE"
    }
}


function Get-ImportNode {
    <#
    機能: Node.js 22 以降を選び、見つからない場合は公式の検証済み版を展開します。
    引数: なし。現在の PATH と build/import-runtime を参照します。
    返り値: string 使用する node.exe の絶対パス。
    #>
    $existing = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing) {
        $version = & $existing.Source --version
        if ($LASTEXITCODE -eq 0 -and $version -match '^v(\d+)\.' -and [int]$Matches[1] -ge 22) {
            return $existing.Source
        }
    }
    $nodeVersion = '24.14.0'
    $nodeName = "node-v$nodeVersion-win-x64"
    $nodeDirectory = Join-Path $runtimeDirectory $nodeName
    $nodePath = Join-Path $nodeDirectory 'node.exe'
    if (-not (Test-Path -LiteralPath $nodePath)) {
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
        $archiveName = "$nodeName.zip"
        $archivePath = Join-Path $runtimeDirectory $archiveName
        $releaseUrl = "https://nodejs.org/dist/v$nodeVersion"
        Write-Host "Node.js $nodeVersion を公式配布元から準備します。"
        if (-not (Test-Path -LiteralPath $archivePath)) {
            Invoke-WebRequest -Uri "$releaseUrl/$archiveName" -OutFile $archivePath
        }
        $checksums = (Invoke-WebRequest -Uri "$releaseUrl/SHASUMS256.txt").Content
        $line = $checksums -split '\r?\n' | Where-Object { $_.EndsWith("  $archiveName") } | Select-Object -First 1
        if (-not $line -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne ($line -split '\s+')[0]) {
            throw 'Node.js の公式 SHA-256 と取得ファイルが一致しません。'
        }
        Expand-Archive -LiteralPath $archivePath -DestinationPath $runtimeDirectory -Force
    }
    return $nodePath
}


function Get-ImportPython {
    <#
    機能: Python 3.14 以降を選び、見つからない場合は公式インストーラーで準備します。
    引数: なし。既存の仮想環境、PATH、build/import-runtime を参照します。
    返り値: string 使用する python.exe の絶対パス。
    #>
    $candidates = @(
        (Join-Path $groundDirectory '.venv/Scripts/python.exe'),
        (Join-Path $runtimeDirectory 'Python314/python.exe')
    )
    $existing = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existing -and $existing.Source -notlike '*WindowsApps*') {
        $candidates += $existing.Source
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            & $candidate -c 'import sys; sys.exit(0 if sys.version_info >= (3,14) else 1)'
            if ($LASTEXITCODE -eq 0) { return $candidate }
        }
    }
    $pythonVersion = '3.14.3'
    $pythonDirectory = Join-Path $runtimeDirectory 'Python314'
    $installerPath = Join-Path $runtimeDirectory "python-$pythonVersion-amd64.exe"
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    Write-Host "Python $pythonVersion を公式配布元から準備します。"
    if (-not (Test-Path -LiteralPath $installerPath)) {
        Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-amd64.exe" -OutFile $installerPath
    }
    $expectedHash = 'b68ad91421afbbd1a628105199c8c5f6179b21ba799067a8d8c0bbac3b7defb0'
    if ((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'Python の公式 SHA-256 と取得ファイルが一致しません。'
    }
    $installerArguments = @('/quiet', '/norestart', 'InstallAllUsers=0', "TargetDir=`"$pythonDirectory`"", 'PrependPath=0', 'Include_launcher=0', 'Include_test=0', 'Include_doc=0', 'Include_pip=1')
    $installation = Start-Process -FilePath $installerPath -ArgumentList $installerArguments -WindowStyle Hidden -Wait -PassThru
    if ($installation.ExitCode -notin @(0, 3010)) {
        throw "Python の準備に失敗しました。終了コード: $($installation.ExitCode)"
    }
    return (Join-Path $pythonDirectory 'python.exe')
}


function Initialize-ImportDependencies {
    <#
    機能: 固定バージョンの Draco と地理処理ライブラリを初回に準備します。
    引数: NodePath/string Node 実行ファイル、PythonPath/string Python 実行ファイル。
    返り値: string 地表取り込み専用の Python 仮想環境パス。
    #>
    param([string]$NodePath, [string]$PythonPath)
    $env:PATH = (Split-Path -Parent $NodePath) + [IO.Path]::PathSeparator + $env:PATH
    $dracoPackage = Join-Path $plateauDirectory 'node_modules/draco3dgltf/package.json'
    $dracoReady = $false
    if (Test-Path -LiteralPath $dracoPackage) {
        $dracoReady = (Get-Content -LiteralPath $dracoPackage -Encoding UTF8 -Raw | ConvertFrom-Json).version -eq '1.5.8'
    }
    if (-not $dracoReady) {
        Push-Location -LiteralPath $plateauDirectory
        try {
            $npmPath = Join-Path (Split-Path -Parent $NodePath) 'npm.cmd'
            Invoke-ImportCommand $npmPath @('ci', '--ignore-scripts') '固定版の公式 Draco を導入します。'
        }
        finally { Pop-Location }
    }

    # ----------------------------------------
    # Python の依存はプロジェクト専用の仮想環境へ配置
    $venvPython = Join-Path $groundDirectory '.venv/Scripts/python.exe'
    if (-not (Test-Path -LiteralPath $venvPython)) {
        Invoke-ImportCommand $PythonPath @('-m', 'venv', (Join-Path $groundDirectory '.venv')) '地表取り込み用の仮想環境を作成します。'
    }
    $requirementCheck = 'import sys,importlib.metadata as m; from pathlib import Path; pairs=[s.split("==") for s in Path(sys.argv[1]).read_text().splitlines() if "==" in s]; sys.exit(0 if all(m.version(n)==v for n,v in pairs) else 1)'
    & $venvPython -c $requirementCheck (Join-Path $groundDirectory 'requirements.txt') 2>$null
    if ($LASTEXITCODE -ne 0) {
        Invoke-ImportCommand $venvPython @('-m', 'pip', 'install', '-r', (Join-Path $groundDirectory 'requirements.txt')) '固定版の地理処理ライブラリを導入します。'
    }
    return $venvPython
}


# ----------------------------------------
# 生成結果の参照・件数・完全性の検査
function Resolve-ImportResource {
    <#
    機能: 成果物に記録された Godot リソース参照を実ファイルへ解決します。
    引数: ResourcePath/string res:// から始まる参照。
    返り値: string プロジェクト内の既存ファイルの絶対パス。
    #>
    param([string]$ResourcePath)
    if (-not $ResourcePath.StartsWith('res://')) { throw "不正な参照です: $ResourcePath" }
    $filePath = [IO.Path]::GetFullPath((Join-Path $projectDirectory $ResourcePath.Substring(6)))
    $rootPrefix = [IO.Path]::GetFullPath($projectDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $filePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "参照先ファイルがありません: $ResourcePath"
    }
    return $filePath
}


function Test-ImportedWorld {
    <#
    機能: CPU データ 4 ファイルと全描画ファイルの整合性を確認します。
    引数: なし。既存ファイルだけを読み、ネットワークにはアクセスしません。
    返り値: PSCustomObject 検査した件数・データの SHA-256 一覧。
    #>
    $cpuFiles = @('ground.json', 'buildings.json', 'elevation.json', 'plateau-manifest.json')
    $cpuData = @{}
    $cpuHashes = @{}
    foreach ($file in $cpuFiles) {
        $cpuPath = Join-Path $projectDirectory "Data/RealWorld/$file"
        if (-not (Test-Path -LiteralPath $cpuPath -PathType Leaf)) { throw "CPU データがありません: $file" }
        $cpuData[$file] = Get-Content -LiteralPath $cpuPath -Encoding UTF8 -Raw | ConvertFrom-Json
        $cpuHashes[$file] = (Get-FileHash -LiteralPath $cpuPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest = $cpuData['plateau-manifest.json']
    $ground = $cpuData['ground.json']
    $elevation = $cpuData['elevation.json']
    if ($manifest.tiles.Count -lt 216) { throw '建物描画タイルが想定の 216 件を下回っています。' }
    if ($ground.rasters.Count -ne 196) { throw '対象範囲の写真タイルが 196 件揃っていません。' }
    if ($elevation.heights.Count -ne $elevation.width * $elevation.height) { throw 'CPU 標高配列の長さが不正です。' }
    if ($cpuData['buildings.json'].buildings.Count -ne $manifest.statistics.buildingCount) { throw '建築物データと明細の件数が一致しません。' }
    foreach ($tile in $manifest.tiles) {
        $modelPath = Resolve-ImportResource $tile.path
        if ((Get-Item -LiteralPath $modelPath).Length -ne $tile.byteLength -or (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $tile.sha256) {
            throw "建物データのサイズまたは SHA-256 が一致しません: $($tile.path)"
        }
        if (-not $tile.featurePath -or $tile.featureSha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "建物の個別編集に必要な地物対応表が明細にありません。.\import-world.ps1 で再生成してください: $($tile.id)"
        }
        $featurePath = Resolve-ImportResource $tile.featurePath
        if ((Get-FileHash -LiteralPath $featurePath -Algorithm SHA256).Hash -ne $tile.featureSha256) {
            throw "地物対応表の SHA-256 が一致しません: $($tile.featurePath)"
        }
    }
    foreach ($raster in $ground.rasters) {
        $null = Resolve-ImportResource $raster.path
        $null = Resolve-ImportResource $raster.terrainPath
    }
    return [PSCustomObject]@{
        'CPUファイル数' = $cpuFiles.Count
        '建物描画タイル数' = $manifest.tiles.Count
        '地物対応タイル数' = $manifest.tiles.Count
        '建築物数' = $manifest.statistics.buildingCount
        '写真タイル数' = $ground.rasters.Count
        '地表タイル数' = $ground.rasters.Count
        'CPUデータSHA256' = $cpuHashes
    }
}


# ----------------------------------------
# 通常取得とオフライン検査
$originalPath = $env:PATH
$originalEncoding = $env:PYTHONIOENCODING
Start-Transcript -LiteralPath $logPath | Out-Null
Push-Location -LiteralPath $projectDirectory
try {
    if ($SkipDownload) {
        Write-Host '既存成果物を検査します。データ取得・依存導入・再生成は行いません。'
    }
    else {
        $env:PYTHONIOENCODING = 'utf-8'
        $nodePath = Get-ImportNode
        $pythonPath = Get-ImportPython
        $venvPython = Initialize-ImportDependencies $nodePath $pythonPath
        Write-Host "Node: $(& $nodePath --version) / Python: $(& $venvPython --version)"
        Invoke-ImportCommand $nodePath @((Join-Path $plateauDirectory 'import.mjs')) 'PLATEAU の建物を取得・変換します。既存の URL キャッシュは再利用します。'
        Invoke-ImportCommand $venvPython @((Join-Path $groundDirectory 'import_ground.py'), '--workers', "$Workers", '--mesh-size', "$MeshSize", '--photo-zoom', "$PhotoZoom") '実地表・写真・標高を取得・変換します。既存キャッシュは再利用します。'
        Invoke-ImportCommand $venvPython @((Join-Path $groundDirectory 'validate_ground.py')) '陸海の代表地点と全地表メッシュを検査します。'
    }
    $result = Test-ImportedWorld
    if (-not $SkipDownload -and -not $SkipTextureCache) {
        $textureCacheDirectory = Join-Path $env:APPDATA 'Godot/app_userdata/都市づくり/cache/urban-textures'
        Invoke-ImportCommand $venvPython @((Join-Path $plateauDirectory 'cache_textures.py'), '--cache-dir', $textureCacheDirectory, '--size', "$TextureCacheSize") '初回起動を短縮するため、建物写真を表示用解像度へ事前変換します。'
    }
    $result | ConvertTo-Json -Depth 4 | Write-Host
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'import-world-result.json') -Encoding UTF8
    Write-Host "実データの確認が完了しました。記録: $logPath"
}
finally {
    $env:PATH = $originalPath
    $env:PYTHONIOENCODING = $originalEncoding
    Pop-Location
    Stop-Transcript | Out-Null
}
