[CmdletBinding()]
param(
    [string]$GodotPath = 'S:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe',
    [string]$DotnetPath = '',
    [ValidateRange(15, 600)]
    [int]$TimeoutSeconds = 120,
    [ValidateRange(1024, 65535)]
    [int]$NetworkPort = 24567,
    [switch]$Play
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'BayfrontSandbox.csproj'
$logRoot = Join-Path $projectRoot ('.godot\test-logs\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
$runningProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

<#
機能: 実行可能な .NET SDK の場所を探します。
引数: PreferredPath は string 型の優先する dotnet.exe のパスです。
返り値: string 型の dotnet.exe の絶対パスです。
#>
function Resolve-DotnetPath {
    param([string]$PreferredPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($PreferredPath) { $candidates.Add($PreferredPath) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) }
    if ($env:USERPROFILE) { $candidates.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) }
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) { $candidates.Add($installed.Source) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw '.NET SDK が見つかりません。-DotnetPath に dotnet.exe のパスを指定してください。'
}

<#
機能: Windows プロセスのコマンドライン引数を引用符で保護します。
引数: Value は string 型の単一引数です。
返り値: string 型の保護済み引数です。
#>
function ConvertTo-ProcessArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

<#
機能: 標準出力と標準エラーを記録する非表示の試験プロセスを開始します。
引数: Executable は string 型の実行ファイル、Arguments は string[] 型の引数、Name は string 型のログ名です。
返り値: PSCustomObject 型のプロセスとログパスです。
#>
function Start-LoggedProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$Name)

    $stdout = Join-Path $logRoot ($Name + '.stdout.log')
    $stderr = Join-Path $logRoot ($Name + '.stderr.log')
    $commandArguments = ($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $process = Start-Process -FilePath $Executable -ArgumentList $commandArguments `
        -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $runningProcesses.Add($process)
    return [PSCustomObject]@{ Process = $process; Stdout = $stdout; Stderr = $stderr; Name = $Name }
}

<#
機能: プロセスのログ末尾を失敗内容として表示します。
引数: Job は PSCustomObject 型のプロセス情報です。
返り値: なし（void 型）。
#>
function Show-ProcessLog {
    param([PSCustomObject]$Job)

    foreach ($path in @($Job.Stdout, $Job.Stderr)) {
        if (Test-Path -LiteralPath $path) {
            Write-Host ('ログ: ' + $path)
            Get-Content -LiteralPath $path -Encoding UTF8 -Tail 45 | ForEach-Object { Write-Host $_ }
        }
    }
}

<#
機能: 試験プロセスの終了と終了コード、必要な成功マーカーを確認します。
引数: Job は PSCustomObject 型のプロセス情報、Marker は string 型の任意の成功マーカーです。
返り値: なし（void 型）。
#>
function Wait-CheckedProcess {
    param([PSCustomObject]$Job, [string]$Marker = '')

    if (-not $Job.Process.WaitForExit($TimeoutSeconds * 1000)) {
        Show-ProcessLog $Job
        throw ($Job.Name + ' が制限時間内に終了しませんでした。')
    }
    $Job.Process.Refresh()
    $text = if (Test-Path -LiteralPath $Job.Stdout) { Get-Content -LiteralPath $Job.Stdout -Raw -Encoding UTF8 } else { '' }
    if ($Job.Process.ExitCode -ne 0 -or ($Marker -and -not $text.Contains($Marker))) {
        Show-ProcessLog $Job
        throw ($Job.Name + ' が失敗しました。終了コード: ' + $Job.Process.ExitCode)
    }
    if (Test-Path -LiteralPath $Job.Stderr) {
        $errorText = Get-Content -LiteralPath $Job.Stderr -Raw -Encoding UTF8
        if ($errorText -match '(?m)^(?:SCRIPT ERROR|ERROR):') {
            Show-ProcessLog $Job
            throw ($Job.Name + ' の実行中に Godot エラーが発生しました。')
        }
    }
}

<#
機能: 通信ホストが初期都市を確定して待機状態になるまで待ちます。
引数: Job は PSCustomObject 型のホストプロセス情報、Marker は string 型の準備完了マーカーです。
返り値: なし（void 型）。
#>
function Wait-NetworkHost {
    param([PSCustomObject]$Job, [string]$Marker = 'BAYFRONT_NETWORK_HOST_READY')

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 20) {
        $Job.Process.Refresh()
        if ($Job.Process.HasExited) {
            Show-ProcessLog $Job
            throw '共同編集ホストが準備完了前に終了しました。'
        }
        if (Test-Path -LiteralPath $Job.Stdout) {
            $text = Get-Content -LiteralPath $Job.Stdout -Raw -Encoding UTF8
            if ($text -and $text.Contains($Marker)) { return }
        }
        Start-Sleep -Milliseconds 150
    }
    Show-ProcessLog $Job
    throw '共同編集ホストの準備が制限時間内に完了しませんでした。'
}

<#
機能: 不正な保存データを持つ専用サーバーが失敗して停止することを検証します。
引数: Job は PSCustomObject 型のサーバープロセス情報です。
返り値: なし（void 型）。
#>
function Wait-RejectedServer {
    param([PSCustomObject]$Job)

    if (-not $Job.Process.WaitForExit($TimeoutSeconds * 1000)) {
        Show-ProcessLog $Job
        throw '不正保存データの専用サーバーが停止しませんでした。'
    }
    $Job.Process.Refresh()
    if ($Job.Process.ExitCode -ne 1) {
        Show-ProcessLog $Job
        throw ('不正保存データの専用サーバーの終了コードが不正です: ' + $Job.Process.ExitCode)
    }
}

# ----------------------------------------

try {
    if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) {
        throw ('Godot .NET 版が見つかりません: ' + $GodotPath)
    }
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw ('プロジェクトが見つかりません: ' + $projectFile)
    }
    $DotnetPath = Resolve-DotnetPath $DotnetPath
    $env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
    $env:PATH = $env:DOTNET_ROOT + [System.IO.Path]::PathSeparator + $env:PATH
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    Write-Host ('試験ログ: ' + $logRoot)

    # ----------------------------------------

    Write-Host 'C# プロジェクトをビルドしています。'
    $build = Start-LoggedProcess $DotnetPath @('build', $projectFile, '--nologo') 'build'
    Wait-CheckedProcess $build
    Write-Host 'Godot のリソースを取り込んでいます。'
    $import = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--editor', '--import') 'import'
    Wait-CheckedProcess $import
    Write-Host '都市モデル・保存復元・地図・アセットを検証しています。'
    $smoke = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--smoke-test') 'smoke'
    Wait-CheckedProcess $smoke 'BAYFRONT_SMOKE_PASS'
    Write-Host '保存時計・編集版との分離・停止・旧保存の移行を検証しています。'
    $clockScene = 'res://Tests/SimulationClock.tscn'
    $clockModel = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, $clockScene) 'clock-model'
    Wait-CheckedProcess $clockModel 'BAYFRONT_CLOCK_PASS'
    Write-Host '壁・屋根・道路・信号の選択と、地形による遮蔽を検証しています。'
    $picking = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, 'res://Tests/UrbanPicking.tscn') 'urban-picking'
    Wait-CheckedProcess $picking 'BAYFRONT_PICKING_PASS'
    Write-Host '更地・交通街区での原典読込休止と、原典有効化時の再開を検証しています。'
    $sourceLoading = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, 'res://Tests/SourceLoading.tscn') 'source-loading'
    Wait-CheckedProcess $sourceLoading 'BAYFRONT_SOURCE_LOADING_PASS'
    Write-Host '移動・回転・上下操作と、複数の都市名を持つ保存データを検証しています。'
    $transform = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, 'res://Tests/EditTransform.tscn') 'edit-transform'
    Wait-CheckedProcess $transform 'BAYFRONT_EDIT_TRANSFORM_PASS'
    Write-Host '住民・車・電車・船の表示位置と、側面からの個体選択を検証しています。'
    $agentRendering = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, 'res://Tests/AgentRendering.tscn') 'agent-rendering'
    Wait-CheckedProcess $agentRendering 'BAYFRONT_AGENT_RENDERING_PASS'
    Write-Host '停止中の交通一覧と、状態更新中の個体選択ボタン維持を検証しています。'
    $trafficList = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, 'res://Tests/TrafficList.tscn') 'traffic-list'
    Wait-CheckedProcess $trafficList 'BAYFRONT_TRAFFIC_LIST_PASS'
    Write-Host '目的地を持つ歩行・車・鉄道・船の移動、経路切断、保存再開を検証しています。'
    $trafficScene = 'res://Tests/PurposeSimulation.tscn'
    $purpose = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, $trafficScene) 'purpose-simulation'
    Wait-CheckedProcess $purpose 'BAYFRONT_PURPOSE_SIMULATION_PASS'
    Write-Host '実地形への交通街区配置、住宅の非重複、四交通手段の目的地到着を検証しています。'
    $trafficCity = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, 'res://Tests/TrafficCity.tscn') 'traffic-city'
    Wait-CheckedProcess $trafficCity 'BAYFRONT_TRAFFIC_CITY_PASS'
    Write-Host '通常画面の建設入力・屋上選択・撤去・保存読込を検証しています。'
    $interaction = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--interaction-test') 'interaction'
    Wait-CheckedProcess $interaction 'BAYFRONT_INTERACTION_PASS'
    Write-Host '全地物の設定画面・形状編集・地形操作・保存読込を検証しています。'
    $urbanInteraction = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--urban-interaction-test') 'urban-interaction'
    Wait-CheckedProcess $urbanInteraction 'BAYFRONT_URBAN_INTERACTION_PASS'

    # ----------------------------------------

    $portArgument = '--network-port=' + $NetworkPort
    Write-Host '2 プロセスで時計同期・同じ編集版の断片転送・切断後の時計復元を検証しています。'
    $clockHost = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, $clockScene, '--', '--clock-test-host', $portArgument) 'clock-network-host'
    Wait-NetworkHost $clockHost 'BAYFRONT_CLOCK_HOST_READY'
    $clockClient = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, $clockScene, '--', '--clock-test-client', $portArgument) 'clock-network-client'
    Wait-CheckedProcess $clockClient 'BAYFRONT_CLOCK_CLIENT_PASS'
    Wait-CheckedProcess $clockHost 'BAYFRONT_CLOCK_HOST_PASS'

    Write-Host '2 プロセスで目的地・個体・都市名の同期と、参加者の所有権制限を検証しています。'
    $trafficHost = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, $trafficScene, '--', '--traffic-test-host', $portArgument) 'traffic-network-host'
    Wait-NetworkHost $trafficHost 'BAYFRONT_TRAFFIC_HOST_READY'
    $trafficClient = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, $trafficScene, '--', '--traffic-test-client', $portArgument) 'traffic-network-client'
    Wait-CheckedProcess $trafficClient 'BAYFRONT_TRAFFIC_CLIENT_PASS'
    Wait-CheckedProcess $trafficHost 'BAYFRONT_TRAFFIC_HOST_PASS'

    # ----------------------------------------

    Write-Host '2 プロセスで共同編集と切断後の自都市復帰を検証しています。'
    $hostJob = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--network-test-host', $portArgument) 'network-host'
    Wait-NetworkHost $hostJob
    $differentMapClient = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--network-test-map-mismatch', $portArgument) 'network-different-map'
    Wait-CheckedProcess $differentMapClient 'BAYFRONT_NETWORK_MAP_MISMATCH_PASS'
    $clientJob = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--network-test-client', $portArgument) 'network-client'
    Wait-CheckedProcess $clientJob 'BAYFRONT_NETWORK_CLIENT_PASS'
    $urbanNetworkOutput = Get-Content -LiteralPath $clientJob.Stdout -Raw -Encoding UTF8
    if (-not $urbanNetworkOutput.Contains('BAYFRONT_URBAN_NETWORK_PASS')) {
        throw '詳細地物・地形・信号・競合拒否・履歴の共同編集が確認できませんでした。'
    }
    Wait-CheckedProcess $hostJob 'BAYFRONT_NETWORK_HOST_PASS'

    # ----------------------------------------

    Write-Host '描画を使わない専用サーバーの建設・保存・再起動を検証しています。'
    $serverSave = Join-Path $logRoot 'dedicated-city.json'
    $serverPortArgument = '--port=' + $NetworkPort
    $serverSaveArgument = '--save-path=' + $serverSave
    $serverJob = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--server', $serverPortArgument, $serverSaveArgument, '--autosave-seconds=0.25', '--max-seconds=6') 'dedicated-server'
    Wait-NetworkHost $serverJob 'BAYFRONT_SERVER_READY'
    $serverClient = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--dedicated-test-add', $portArgument) 'dedicated-client'
    Wait-CheckedProcess $serverClient 'BAYFRONT_DEDICATED_BUILD_PASS'
    Wait-CheckedProcess $serverJob 'BAYFRONT_SERVER_SAVED'
    $serverOutput = Get-Content -LiteralPath $serverJob.Stdout -Raw -Encoding UTF8
    if (-not $serverOutput.Contains('表示=headless') -or -not $serverOutput.Contains('専用サーバーの描画ノード数: 0')) {
        throw '専用サーバーが描画なしで動作していることを確認できませんでした。'
    }
    if (-not (Test-Path -LiteralPath $serverSave)) { throw '専用サーバーの保存ファイルが生成されませんでした。' }
    $serverCity = Get-Content -LiteralPath $serverSave -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $serverCity.MapId -or $serverCity.MapVersion -lt 1) {
        throw '専用サーバーの保存データに地図の識別情報がありません。'
    }
    if (-not @($serverCity.Edits.Objects | Where-Object { $_.Id -eq 'dedicated-persisted-housing' -and $_.Floors -eq 6 }).Count) {
        throw '専用サーバーの保存データに参加者の建設が残っていません。'
    }
    if (-not @($serverCity.Edits.Objects | Where-Object { $_.Floors -eq 12 }).Count -or
        -not @($serverCity.Edits.Terrain | Where-Object { $_.Id -eq 'dedicated-urban-terrain' }).Count -or
        $serverCity.Edits.SeaLevelMeters -ne 36 -or $serverCity.Edits.IncludeSource) {
        throw '専用サーバーに原典建物・地形・水面・初期都市の変更が保存されていません。'
    }
    $restartJob = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--server', $serverPortArgument, $serverSaveArgument, '--autosave-seconds=0.25', '--max-seconds=4') 'dedicated-restart'
    Wait-NetworkHost $restartJob 'BAYFRONT_SERVER_READY'
    $checkClient = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--dedicated-test-check', $portArgument) 'dedicated-restart-client'
    Wait-CheckedProcess $checkClient 'BAYFRONT_DEDICATED_RESTART_PASS'
    Wait-CheckedProcess $restartJob 'BAYFRONT_SERVER_READY'

    # ----------------------------------------

    Write-Host '専用サーバーが壊れた保存ファイルを上書きせず停止することを検証しています。'
    $invalidSave = Join-Path $logRoot 'dedicated-invalid.json'
    [System.IO.File]::WriteAllText($invalidSave, '{破損した都市データ', [System.Text.UTF8Encoding]::new($false))
    $invalidHash = (Get-FileHash -LiteralPath $invalidSave -Algorithm SHA256).Hash
    $invalidSaveArgument = '--save-path=' + $invalidSave
    $invalidServer = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--server', $serverPortArgument, $invalidSaveArgument, '--max-seconds=1') 'dedicated-invalid'
    Wait-RejectedServer $invalidServer
    if ((Get-FileHash -LiteralPath $invalidSave -Algorithm SHA256).Hash -ne $invalidHash) {
        throw '専用サーバーが不正な保存データを書き換えました。'
    }

    # ----------------------------------------

    Write-Host '専用サーバーが別の地図バージョンの保存を上書きせず停止することを検証しています。'
    $differentMapSave = Join-Path $logRoot 'dedicated-different-map.json'
    $serverCity.MapVersion = [int]$serverCity.MapVersion + 1
    [System.IO.File]::WriteAllText($differentMapSave, ($serverCity | ConvertTo-Json -Depth 16), [System.Text.UTF8Encoding]::new($false))
    $differentMapHash = (Get-FileHash -LiteralPath $differentMapSave -Algorithm SHA256).Hash
    $differentMapArgument = '--save-path=' + $differentMapSave
    $differentMapServer = Start-LoggedProcess $GodotPath @('--headless', '--path', $projectRoot, '--', '--server', $serverPortArgument, $differentMapArgument, '--max-seconds=1') 'dedicated-different-map'
    Wait-RejectedServer $differentMapServer
    if ((Get-FileHash -LiteralPath $differentMapSave -Algorithm SHA256).Hash -ne $differentMapHash) {
        throw '専用サーバーが別の地図バージョンの保存データを書き換えました。'
    }
    Write-Host 'すべての自動試験に成功しました。'

    # ----------------------------------------

    if ($Play) {
        $graphicalGodot = $GodotPath -replace '_console\.exe$', '.exe'
        if (-not (Test-Path -LiteralPath $graphicalGodot)) { $graphicalGodot = $GodotPath }
        $playArguments = (@('--path', $projectRoot) | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
        Start-Process -FilePath $graphicalGodot -ArgumentList $playArguments `
            -WorkingDirectory $projectRoot -WindowStyle Normal | Out-Null
        Write-Host '試作ゲームを起動しました。'
    }
}
catch {
    Write-Host ('自動試験失敗: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('ログの保存先: ' + $logRoot)
    exit 1
}
finally {
    foreach ($process in $runningProcesses) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            $process.Dispose()
        }
        catch {
            Write-Verbose '終了済みプロセスの後処理を省略しました。'
        }
    }
}
