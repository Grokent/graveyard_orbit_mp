<#
  Graveyard Orbit Co-op — installer / updater (Windows)

  Downloads the latest GitHub release and copies it into the game folder.
  Existing BepInEx settings are left alone.

  Usage:
    Update.bat                          (double-click, auto-detects Steam)
    Update.bat --force                  reinstall even if already current
    Update.bat --game-dir "D:\SteamLibrary\steamapps\common\Graveyard Orbit"
#>

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Owner       = 'Grokent'
$Repo        = 'graveyard_orbit_mp'
$ModVersion  = '0.1.0'
$GameFolder  = 'Graveyard Orbit'
$ExeName     = 'GraveyardOrbit.exe'

$GameDir = $null
$force   = $false

function Show-Usage {
    Write-Host @"
Graveyard Orbit Co-op updater

  Update.bat                          auto-detect the Steam install
  Update.bat --force                  reinstall even if already current
  Update.bat --game-dir "<path>"      use a specific game folder
"@
}

function Fail([string]$message) {
    Write-Host ''
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

# ---- arguments -----------------------------------------------------------
for ($i = 0; $i -lt $args.Count; $i++) {
    $a = [string]$args[$i]
    if ($a -match '^(-GameDir|--game-dir)$') {
        $i++
        if ($i -ge $args.Count) { Fail 'Missing value for --game-dir.' }
        $GameDir = [string]$args[$i]
    }
    elseif ($a -match '^(-GameDir|--game-dir)=(.*)$') {
        $GameDir = $Matches[2]
    }
    elseif ($a -match '^(-Force|--force|-f)$') {
        $force = $true
    }
    elseif ($a -match '^(-h|--help)$') {
        Show-Usage
        exit 0
    }
    else {
        Fail "Unknown argument: $a"
    }
}

# ---- locate the game -----------------------------------------------------
function Find-GameDir {
    $libraries = New-Object System.Collections.Generic.List[string]

    $steam = $null
    foreach ($key in @('HKCU:\Software\Valve\Steam',
                       'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
                       'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $props = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($props.SteamPath) { $steam = $props.SteamPath; break }
            if ($props.InstallPath) { $steam = $props.InstallPath; break }
        }
        catch { }
    }
    if (-not $steam) { $steam = 'C:\Program Files (x86)\Steam' }
    $libraries.Add($steam)

    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        try {
            $text = Get-Content -LiteralPath $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $libraries.Add(($m.Groups[1].Value -replace '\\\\', '\'))
            }
        }
        catch { }
    }

    foreach ($lib in $libraries) {
        $candidate = Join-Path $lib "steamapps\common\$GameFolder"
        if (Test-Path -LiteralPath (Join-Path $candidate $ExeName)) {
            return $candidate
        }
    }
    return $null
}

if (-not $GameDir) { $GameDir = $env:GRAVEYARD_ORBIT_DIR }
if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir) {
    Fail "Could not find Graveyard Orbit. Pass the folder explicitly, e.g.`n  Update.bat --game-dir `"D:\SteamLibrary\steamapps\common\Graveyard Orbit`""
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir $ExeName))) {
    Fail "'$GameDir' does not look like the Graveyard Orbit folder (no $ExeName)."
}

Write-Host "Graveyard Orbit Co-op updater" -ForegroundColor Cyan
Write-Host "Game folder: $GameDir"

if (Get-Process -Name 'GraveyardOrbit' -ErrorAction SilentlyContinue) {
    Fail 'Graveyard Orbit is running. Close the game, then run this again.'
}

# ---- latest release ------------------------------------------------------
$headers = @{
    'User-Agent' = "$Owner-$Repo-updater"
    'Accept'     = 'application/vnd.github+json'
}

Write-Host 'Checking GitHub for the latest build...'
$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/latest" `
        -Headers $headers
}
catch {
    Fail "Could not reach GitHub: $($_.Exception.Message)"
}

$tag = [string]$release.tag_name
if (-not $tag) { Fail 'GitHub returned no release tag.' }

$assetUrl = $null
foreach ($asset in $release.assets) {
    if ($asset.name -like '*.zip') { $assetUrl = [string]$asset.browser_download_url; break }
}
if (-not $assetUrl) {
    $assetUrl = "https://github.com/$Owner/$Repo/releases/download/$tag/GraveyardOrbit-Co-op-$ModVersion-$tag.zip"
}

$installed = $null
$versionFile = Join-Path $GameDir 'VERSION.txt'
if (Test-Path -LiteralPath $versionFile) {
    $installed = (Get-Content -LiteralPath $versionFile -TotalCount 1).Trim()
}

if (-not $force -and $installed -eq $tag) {
    Write-Host "Already up to date ($tag). Use --force to reinstall." -ForegroundColor Green
    exit 0
}

if ($installed) { Write-Host "Installed: $installed  ->  Latest: $tag" }
else { Write-Host "Latest: $tag" }

# ---- download and install ------------------------------------------------
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('go-coop-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    $zip = Join-Path $tmp 'update.zip'
    Write-Host "Downloading $tag ..."
    try {
        Invoke-WebRequest -Uri $assetUrl -OutFile $zip -UseBasicParsing
    }
    catch {
        Fail "Download failed: $assetUrl`n$($_.Exception.Message)"
    }

    $pkg = Join-Path $tmp 'package'
    Write-Host 'Extracting...'
    Expand-Archive -LiteralPath $zip -DestinationPath $pkg -Force

    # Keep the previous plugin DLL next to the new one for easy rollback.
    $dll = Join-Path $GameDir 'BepInEx\plugins\GraveyardOrbit.Coop\GraveyardOrbit.Coop.dll'
    if (Test-Path -LiteralPath $dll) {
        Copy-Item -LiteralPath $dll -Destination ($dll + '.bak') -Force
    }

    Write-Host 'Installing...'
    Copy-Item -Path (Join-Path $pkg '*') -Destination $GameDir -Recurse -Force

    $after = $tag
    if (Test-Path -LiteralPath $versionFile) {
        $after = (Get-Content -LiteralPath $versionFile -TotalCount 1).Trim()
    }

    Write-Host ''
    Write-Host "Installed $after. Launch the game through Steam." -ForegroundColor Green
    Write-Host 'Both players must run the same build; the lobby blocks START on a mismatch.'
}
finally {
    try { Remove-Item -LiteralPath $tmp -Recurse -Force } catch { }
}
