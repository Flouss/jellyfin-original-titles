<#
.SYNOPSIS
  Seeds the local Original Titles harness (docker-compose.debug.yml) with a throwaway admin, two libraries,
  an API key and three fixture videos (one movie, one series with two episodes).

.DESCRIPTION
  Idempotent: re-running reuses .env.local (generated here, gitignored, never commit it), skips the startup
  wizard when it is already done and re-applies the fixture titles.
  Only talks to the harness on 127.0.0.1:18096. Works on Windows PowerShell 5.1 and PowerShell 7
  (this file is ASCII on purpose; the Arabic title is built from code points).

  What POST /Items/{id} can set (verified against Jellyfin v12.2 ItemUpdateController.UpdateItem and on the
  running harness): Name, OriginalTitle and OriginalLanguage are all copied from the posted BaseItemDto.
  Empty/whitespace OriginalTitle/OriginalLanguage become null. Name is copied as-is, so ALWAYS GET the item
  first and post the whole object back with your changes (a body without Name nulls it).
  See README.md, "Local harness", for the full table and the gotchas this script works around.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:18096'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$mediaDir = Join-Path $PSScriptRoot 'media'
$envFile = Join-Path $root '.env.local'

if ($BaseUrl -notmatch '^http://(127\.0\.0\.1|localhost):') {
    throw "Refusing to talk to '$BaseUrl': the harness only ever targets the local container."
}

function U([string]$hexCodePoints) {
    -join ($hexCodePoints.Split(' ') | ForEach-Object { [char][Convert]::ToInt32($_, 16) })
}

# Fixture content. Original titles are Arabic, translated Name is the Latin one Jellyfin shows by default.
$movieOriginal = U '0645 0648 0634 0020 0641 064A 0020 062B 0646 064A 062A 064A'   # "<mouch fi thniti>" in Arabic script
$seriesOriginal = U '0628 0631 0646 0627 0645 062C 0020 0627 0644 0627 062E 062A 0628 0627 0631'
$fixture = @{
    Movie   = @{ Name = 'Mouch Fi Thniti'; OriginalTitle = $movieOriginal; OriginalLanguage = 'ar' }
    Series  = @{ Name = 'Harness Show'; OriginalTitle = $seriesOriginal; OriginalLanguage = 'ar' }
    Episode = @{ Name = 'Pilot' }   # episode keeps its own title; language is inherited from the series
}

function Wait-Server {
    for ($i = 0; $i -lt 90; $i++) {
        try { return Invoke-RestMethod -Uri "$BaseUrl/System/Info/Public" -TimeoutSec 5 } catch { Start-Sleep -Seconds 2 }
    }
    throw "Jellyfin did not answer on $BaseUrl within 3 minutes (is 'docker compose -f docker-compose.debug.yml watch' running?)"
}

function Invoke-Jf {
    param([string]$Method = 'GET', [string]$Path, $Body = $null, [string]$Token = $script:Token)
    $headers = @{}
    if ($Token) {
        $headers['Authorization'] = "MediaBrowser Token=`"$Token`""   # API-key / access-token auth on 12.x
    } else {
        $headers['Authorization'] = 'MediaBrowser Client="harness-seed", Device="seed", DeviceId="harness-seed", Version="1"'
    }
    $p = @{ Method = $Method; Uri = "$BaseUrl$Path"; Headers = $headers; TimeoutSec = 60 }
    if ($null -ne $Body) {
        $p.ContentType = 'application/json; charset=utf-8'
        $p.Body = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 30))
    }
    Invoke-RestMethod @p
}

function Read-EnvLocal {
    $h = @{}
    if (Test-Path $envFile) {
        Get-Content $envFile | ForEach-Object { if ($_ -match '^\s*([A-Z_]+)=(.*)$') { $h[$Matches[1]] = $Matches[2] } }
    }
    $h
}
function Write-EnvLocal([hashtable]$h) {
    $lines = $h.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }
    [System.IO.File]::WriteAllLines($envFile, [string[]]$lines)
}

# ---------------------------------------------------------------- 1. fixture videos (never committed)
function New-Video([string]$path) {
    if (Test-Path $path) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $ff = @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc=size=160x120:rate=10:duration=2',
        '-f', 'lavfi', '-i', 'sine=frequency=440:duration=2', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest')
    if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
        & ffmpeg @ff $path
    } else {
        # No local ffmpeg: use the ffmpeg shipped in the Jellyfin image itself.
        $dir = Split-Path $path; $name = Split-Path $path -Leaf
        & docker run --rm --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg -v "${dir}:/out" jellyfin/jellyfin:12.2 @ff "/out/$name"
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $path)) { throw "ffmpeg failed to create $path" }
}
New-Video (Join-Path $mediaDir 'Movies\Mouch Fi Thniti (2020)\Mouch Fi Thniti (2020).mp4')
New-Video (Join-Path $mediaDir 'Shows\Harness Show (2021)\Season 01\Harness Show S01E01.mp4')
New-Video (Join-Path $mediaDir 'Shows\Harness Show (2021)\Season 01\Harness Show S01E02.mp4')

# ---------------------------------------------------------------- 2. startup wizard + admin
$info = Wait-Server
Write-Host "Jellyfin $($info.Version) is up (wizard completed: $($info.StartupWizardCompleted))"
$cfg = Read-EnvLocal
if (-not $info.StartupWizardCompleted) {
    $bytes = New-Object byte[] 18
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $cfg = @{
        BASE_URL = $BaseUrl
        ADMIN_USER = 'harness-admin'
        ADMIN_PASSWORD = ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', 'x')
    }
    Write-EnvLocal $cfg   # write before using, so a half-failed run never loses the password
    $script:Token = $null
    Invoke-Jf POST '/Startup/Configuration' @{ UICulture = 'en-US'; MetadataCountryCode = 'US'; PreferredMetadataLanguage = 'en' } | Out-Null
    Invoke-Jf GET '/Startup/User' | Out-Null   # initialises the default user row before it is renamed
    Invoke-Jf POST '/Startup/User' @{ Name = $cfg.ADMIN_USER; Password = $cfg.ADMIN_PASSWORD } | Out-Null
    Invoke-Jf POST '/Startup/RemoteAccess' @{ EnableRemoteAccess = $true; EnableAutomaticPortMapping = $false } | Out-Null
    Invoke-Jf POST '/Startup/Complete' | Out-Null
    Write-Host 'Startup wizard completed.'
} elseif (-not $cfg.ADMIN_PASSWORD) {
    throw "The wizard is already done but $envFile has no credentials. Run 'docker compose -f docker-compose.debug.yml down -v' and start again."
}

$script:Token = $null
$auth = Invoke-Jf POST '/Users/AuthenticateByName' @{ Username = $cfg.ADMIN_USER; Pw = $cfg.ADMIN_PASSWORD }
$adminToken = $auth.AccessToken
$uid = $auth.User.Id
$cfg.USER_ID = $uid

# ---------------------------------------------------------------- 3. API key
$keys = Invoke-Jf GET '/Auth/Keys' -Token $adminToken
$key = $keys.Items | Where-Object { $_.AppName -eq 'original-titles-harness' } | Select-Object -First 1
if (-not $key) {
    Invoke-Jf POST '/Auth/Keys?app=original-titles-harness' -Token $adminToken | Out-Null
    $keys = Invoke-Jf GET '/Auth/Keys' -Token $adminToken
    $key = $keys.Items | Where-Object { $_.AppName -eq 'original-titles-harness' } | Select-Object -First 1
}
$cfg.API_KEY = $key.AccessToken
Write-EnvLocal $cfg
$script:Token = $cfg.API_KEY

# ---------------------------------------------------------------- 4. libraries (no metadata providers: nothing leaves the box)
function New-Library([string]$name, [string]$type, [string]$path, [string[]]$itemTypes) {
    $existing = Invoke-Jf GET '/Library/VirtualFolders'
    if ($existing | Where-Object { $_.Name -eq $name }) { return }
    $opts = @{
        Enabled = $true
        EnableRealtimeMonitor = $false
        EnableEmbeddedTitles = $false
        PathInfos = @(@{ Path = $path })
        TypeOptions = @($itemTypes | ForEach-Object { @{ Type = $_; MetadataFetchers = @(); ImageFetchers = @() } })
    }
    $q = "name=$([uri]::EscapeDataString($name))&collectionType=$type&refreshLibrary=true"
    Invoke-Jf POST "/Library/VirtualFolders?$q" @{ LibraryOptions = $opts } | Out-Null
    Write-Host "Library '$name' created."
}
New-Library 'Movies' 'movies' '/media/Movies' @('Movie')
New-Library 'Shows' 'tvshows' '/media/Shows' @('Series', 'Season', 'Episode')

# ---------------------------------------------------------------- 5. wait for the scan
function Get-Items([string]$types) {
    (Invoke-Jf GET "/Users/$uid/Items?Recursive=true&IncludeItemTypes=$types&Fields=OriginalTitle,OriginalLanguage").Items
}
$movie = $null; $series = $null; $episodes = @()
for ($i = 0; $i -lt 60; $i++) {
    $movie = Get-Items 'Movie' | Select-Object -First 1
    $series = Get-Items 'Series' | Select-Object -First 1
    $episodes = @(Get-Items 'Episode')
    if ($movie -and $series -and $episodes.Count -ge 2) { break }
    Start-Sleep -Seconds 2
}
if (-not ($movie -and $series -and $episodes.Count -ge 2)) { throw 'Library scan did not produce the movie, series and 2 episodes in 2 minutes.' }

# ---------------------------------------------------------------- 6. set Name / OriginalTitle / OriginalLanguage
function Set-ItemTitles($id, [hashtable]$values) {
    $item = Invoke-Jf GET "/Users/$uid/Items/$id"          # GET /Items/{id} 400s without a user
    $item.PSObject.Properties.Remove('Trickplay')            # posting Trickplay back 500s
    foreach ($k in $values.Keys) {
        if ($item.PSObject.Properties[$k]) { $item.$k = $values[$k] } else { $item | Add-Member -NotePropertyName $k -NotePropertyValue $values[$k] }
    }
    Invoke-Jf POST "/Items/$id" $item | Out-Null
}
Set-ItemTitles $movie.Id $fixture.Movie
Set-ItemTitles $series.Id $fixture.Series
$ep1 = $episodes | Sort-Object IndexNumber, Name | Select-Object -First 1
Set-ItemTitles $ep1.Id $fixture.Episode

Write-Host ''
Write-Host 'Seeded. Items as the API (with the API key) returns them:'
foreach ($t in 'Movie', 'Series', 'Season', 'Episode') {
    foreach ($it in (Get-Items $t)) {
        '{0,-8} Id={1} Name={2} OriginalTitle={3} OriginalLanguage={4} SeriesName={5}' -f $t, $it.Id, $it.Name, $it.OriginalTitle, $it.OriginalLanguage, $it.SeriesName
    }
}
Write-Host ''
Write-Host "Admin '$($cfg.ADMIN_USER)' / API key are in $envFile (gitignored). Web UI: $BaseUrl"
