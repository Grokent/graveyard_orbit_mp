#requires -version 3
$ErrorActionPreference = "SilentlyContinue"

$game = $PSScriptRoot
if (-not $game) { $game = Split-Path -Parent $MyInvocation.MyCommand.Path }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$desktop = [Environment]::GetFolderPath("Desktop")
if (-not $desktop -or -not (Test-Path $desktop)) { $desktop = $env:TEMP }

$folder = Join-Path $desktop ("GraveyardOrbit-Co-op-Report-" + $stamp)
New-Item -ItemType Directory -Path $folder -Force | Out-Null

$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$t) { $lines.Add($t) }

Add-Line "GRAVEYARD ORBIT CO-OP - DIAGNOSTIC REPORT"
Add-Line ("Generated : " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))
Add-Line ("Computer  : " + $env:COMPUTERNAME + "  /  user " + $env:USERNAME)
Add-Line ("Game      : " + $game)
Add-Line ""

# ---------------------------------------------------------------- system
$os = Get-CimInstance Win32_OperatingSystem
if ($os) {
    Add-Line ("OS        : " + $os.Caption + " " + $os.Version + " (" + $os.OSArchitecture + ")")
    Add-Line ("RAM       : " + [math]::Round($os.TotalVisibleMemorySize / 1MB, 1) + " GB")
}
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
if ($cpu) { Add-Line ("CPU       : " + $cpu.Name) }
$gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1
if ($gpu) { Add-Line ("GPU       : " + $gpu.Name + "  driver " + $gpu.DriverVersion) }
Add-Line ""

# ---------------------------------------------------------------- build
$exe = Join-Path $game "GraveyardOrbit.exe"
if (Test-Path $exe) {
    $v = (Get-Item $exe).VersionInfo
    Add-Line ("Game exe  : " + $v.FileVersion)
} else {
    Add-Line "Game exe  : NOT FOUND"
}

$log = Join-Path $game "BepInEx\LogOutput.log"
if (Test-Path $log) {
    Add-Line ("BepInEx   : " + (Get-Content $log -TotalCount 1))
    Copy-Item $log (Join-Path $folder "LogOutput.log") -Force
    $patterns = "\[coop\]|\[steam\]|\[contract\]|\[join\]|\[flow\]|\[spawnfan\]|Error|Exception|Crash|Fatal"
    Select-String -Path $log -Pattern $patterns | ForEach-Object { $_.Line } |
        Set-Content (Join-Path $folder "log-highlights.txt") -Encoding UTF8
    Get-Content $log -Tail 80 | Set-Content (Join-Path $folder "log-tail.txt") -Encoding UTF8
} else {
    Add-Line "BepInEx   : LogOutput.log NOT FOUND"
}

$mod = Join-Path $game "BepInEx\plugins\GraveyardOrbit.Coop\GraveyardOrbit.Coop.dll"
if (Test-Path $mod) {
    $fi = Get-Item $mod
    Add-Line ("Mod DLL   : " + $fi.Length + " bytes, " + $fi.LastWriteTime)
    Add-Line ("Mod SHA256: " + (Get-FileHash $mod -Algorithm SHA256).Hash)
} else {
    Add-Line "Mod DLL   : NOT FOUND"
}

$map = Join-Path $game "BepInEx\plugins\GraveyardOrbit.Coop\maps\wrecked_space_rock.json"
if (Test-Path $map) {
    $mi = Get-Item $map
    Add-Line ("Map file  : " + $mi.Length + " bytes")
    Add-Line ("Map SHA256: " + (Get-FileHash $map -Algorithm SHA256).Hash)
    $flat = (Get-Content $map -Raw) -replace "\s", ""
    $countText = "?"
    if ($flat -match '"count":(\d+)') { $countText = $matches[1] }
    $boundsText = ""
    if ($flat -match '"bounds":\{"q":\[(-?\d+),(-?\d+)\],"r":\[(-?\d+),(-?\d+)\]') {
        $boundsText = "q[" + $matches[1] + "," + $matches[2] + "] r[" + $matches[3] + "," + $matches[4] + "]"
    }
    Add-Line ("Map stats : " + $countText + " cells " + $boundsText)
} else {
    Add-Line "Map file  : not present (the mod will regenerate it)"
}

$cfg = Join-Path $game "BepInEx\config\agentbox.graveyardorbit.coop.cfg"
if (Test-Path $cfg) {
    Copy-Item $cfg (Join-Path $folder "coop-config.cfg") -Force
}

# ---------------------------------------------------------------- save / seeds
$saveDir = Join-Path $env:USERPROFILE "AppData\LocalLow\AnchorheadGames\Graveyard Orbit"
$save = Join-Path $saveDir "graveyard_orbit_save.json"
if (Test-Path $save) {
    $s = Get-Content $save -Raw | ConvertFrom-Json
    $anomaly = $null
    if ($s.profile) { $anomaly = $s.profile.anomaly_seed }
    Add-Line ""
    Add-Line "SAVE"
    Add-Line ("  structure    : " + $s.current_structure)
    Add-Line ("  money / day  : " + $s.money + " / " + $s.current_day)
    Add-Line ("  sorties      : " + $s.sorties)
    Add-Line ("  save_gen     : " + $s.save_gen)
    Add-Line ("  level_seed   : " + $s.level_seed)
    Add-Line ("  anomaly_seed : " + $anomaly)

    $summary = [pscustomobject]@{
        current_structure = $s.current_structure
        current_day       = $s.current_day
        money             = $s.money
        sorties           = $s.sorties
        save_gen          = $s.save_gen
        level_seed        = $s.level_seed
        anomaly_seed      = $anomaly
    }
    $summary | ConvertTo-Json | Set-Content (Join-Path $folder "save-summary.json") -Encoding UTF8

    $backup = $save + ".coop-backup"
    if (Test-Path $backup) {
        $b = Get-Content $backup -Raw | ConvertFrom-Json
        $banomaly = $null
        if ($b.profile) { $banomaly = $b.profile.anomaly_seed }
        Add-Line ("  coop-backup  : level_seed " + $b.level_seed + ", anomaly_seed " + $banomaly)
    }
} else {
    Add-Line ""
    Add-Line "SAVE: not found at $save"
}

# ---------------------------------------------------------------- unity logs
$player = Join-Path $saveDir "Player.log"
if (Test-Path $player) { Copy-Item $player (Join-Path $folder "Player.log") -Force }

$crashDir = Join-Path $saveDir "Crashes"
if (Test-Path $crashDir) {
    $latest = Get-ChildItem $crashDir -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        $crashLog = Join-Path $latest.FullName "Player.log"
        if (Test-Path $crashLog) {
            Copy-Item $crashLog (Join-Path $folder ("crash-" + $latest.Name + "-Player.log")) -Force
            Add-Line ("Crash log : " + $latest.Name)
        }
    }
}

Add-Line ""
Add-Line "Files in this report: report.txt, LogOutput.log, log-highlights.txt, log-tail.txt,"
Add-Line "coop-config.cfg, save-summary.json, Player.log (if present), crash log (if present)."

$lines | Set-Content (Join-Path $folder "report.txt") -Encoding UTF8

$zip = Join-Path $desktop ("GraveyardOrbit-Co-op-Report-" + $stamp + ".zip")
try {
    Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip -Force
    Write-Host ""
    Write-Host "Report created:"
    Write-Host "  $zip"
    Write-Host ""
    Write-Host "Send that .zip file to the developer."
    Start-Process explorer.exe ("/select," + $zip)
} catch {
    Write-Host ""
    Write-Host "Could not create the zip. Send this folder instead:"
    Write-Host "  $folder"
}
