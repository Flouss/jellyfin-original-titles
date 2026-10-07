<#
.SYNOPSIS
  End-to-end check of the editor guard on the local harness (run seed.ps1 first).

.DESCRIPTION
  Enables "ar" for harness-admin ONLY (overwrites the plugin's user list), creates the control user harness-user if missing,
  then posts the items harness-admin sees (wrapper on: Arabic titles) back through POST /Items/{id} UNCHANGED, with the API key
  and with an admin session token, and reads the stored values back through the control user (empty language list, so the
  wrapper never rewrites what it sees). Covers movie Name, series Name (and the controller's cascade into season/episode
  SeriesName), episode and season SeriesName, and a genuine rename (kept). Resets the fixture titles after each case.
  -Expect guard (default): the translated titles must stay. -Expect bug: what Jellyfin does WITHOUT the guard (build the plugin
  without Guard/EditorGuardFilter registered): the original title is stored. Prints PASS/FAIL per check and FAILS=<n>.
  Uses the credentials in .env.local (written by seed.ps1, never printed). Talks only to the loopback harness.
#>
[CmdletBinding()]
param(
    [ValidateSet('bug', 'guard')][string]$Expect = 'guard'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$cfg = @{}
$envFile = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')) '.env.local'
Get-Content $envFile | ForEach-Object { if ($_ -match '^\s*([A-Z_]+)=(.*)$') { $cfg[$Matches[1]] = $Matches[2] } }
$B = $cfg.BASE_URL
# Parses the URL and compares the host exactly: a regex on the string also accepts 'http://127.0.0.1:80@evil.host/' (userinfo),
# whose real host is evil.host.
function Assert-LoopbackUrl([string]$Url) {
    $u = $null
    if (-not [uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$u) -or $u.Scheme -ne 'http' -or $u.UserInfo -or
        ($u.Host -ne '127.0.0.1' -and $u.Host -ne 'localhost')) {
        throw "Refusing to talk to '$Url': the harness only ever targets the local container."
    }
}
Assert-LoopbackUrl $B
$pluginId = '7c1e5a3d-92b4-4f6e-8d0a-3b5f1c2e9a47'

function U([string]$hex) { -join ($hex.Split(' ') | ForEach-Object { [char][Convert]::ToInt32($_, 16) }) }
# Same code points as seed.ps1.
$movieOriginal = U '0645 0648 0634 0020 0641 064A 0020 062B 0646 064A 062A 064A'
$seriesOriginal = U '0628 0631 0646 0627 0645 062C 0020 0627 0644 0627 062E 062A 0628 0627 0631'
$movieReal = 'Mouch Fi Thniti'
$seriesReal = 'Harness Show'

function Jf([string]$Method, [string]$Path, $Body = $null, [string]$Token = $cfg.API_KEY) {
    $h = @{ Authorization = "MediaBrowser Token=`"$Token`"" }
    $p = @{ Method = $Method; Uri = "$B$Path"; Headers = $h; TimeoutSec = 60 }
    if ($null -ne $Body) {
        $p.ContentType = 'application/json; charset=utf-8'
        $p.Body = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 30))
    }
    Invoke-RestMethod @p
}

# Control user (language list stays empty).
$users = Jf GET '/Users'
$control = $users | Where-Object { $_.Name -eq 'harness-user' }
if (-not $control) {
    # Random password, kept (like the admin's) in the untracked .env.local and never printed. Nothing here logs the control user in;
    # the password exists so the account is not left with a guessable one.
    $bytes = New-Object byte[] 18
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $cfg.CONTROL_PASSWORD = ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', 'x')
    $lines = $cfg.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }
    [System.IO.File]::WriteAllLines($envFile, [string[]]$lines)   # write before using, so the password is never lost
    $control = Jf POST '/Users/New' @{ Name = 'harness-user'; Password = $cfg.CONTROL_PASSWORD }
}
$admin = $cfg.USER_ID
$ctl = $control.Id

# Admin session token (a real user claim, like the web metadata editor).
$authHeader = @{ Authorization = 'MediaBrowser Client="t5-e2e", Device="t5", DeviceId="t5-e2e", Version="1"' }
$auth = Invoke-RestMethod -Method POST -Uri "$B/Users/AuthenticateByName" -Headers $authHeader -ContentType 'application/json' `
    -Body (@{ Username = $cfg.ADMIN_USER; Pw = $cfg.ADMIN_PASSWORD } | ConvertTo-Json)
$sessionToken = $auth.AccessToken

# Enable ar for the admin only.
$pc = Jf GET "/Plugins/$pluginId/Configuration"
$pc.Users = @(@{ UserId = $admin; Languages = @('ar') })
Jf POST "/Plugins/$pluginId/Configuration" $pc | Out-Null

function Ids() {
    $all = (Jf GET "/Users/$ctl/Items?Recursive=true&IncludeItemTypes=Movie,Series,Season,Episode").Items
    @{
        Movie    = ($all | Where-Object Type -eq 'Movie').Id
        Series   = ($all | Where-Object Type -eq 'Series').Id
        Season   = ($all | Where-Object Type -eq 'Season').Id
        Episodes = @($all | Where-Object Type -eq 'Episode' | ForEach-Object Id)
    }
}
$ids = Ids

function PostBack([string]$id, [string]$viewAs, [string]$token, [hashtable]$changes = @{}) {
    $item = Jf GET "/Users/$viewAs/Items/$id"
    $item.PSObject.Properties.Remove('Trickplay')
    foreach ($k in $changes.Keys) { $item.$k = $changes[$k] }
    Jf POST "/Items/$id" $item $token | Out-Null
    return $item
}
function Stored([string]$id) { Jf GET "/Users/$ctl/Items/$id" }
function Reset() {
    PostBack $ids.Movie $ctl $cfg.API_KEY @{ Name = $movieReal } | Out-Null
    PostBack $ids.Series $ctl $cfg.API_KEY @{ Name = $seriesReal } | Out-Null   # cascades SeriesName to season + episodes
    foreach ($e in $ids.Episodes) { PostBack $e $ctl $cfg.API_KEY @{ SeriesName = $seriesReal } | Out-Null }
}
$fails = 0
function Check([string]$label, [string]$actual, [string]$expected) {
    $ok = $actual -ceq $expected
    if (-not $ok) { $script:fails++ }
    '{0} {1}: actual="{2}" expected="{3}"' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $label, $actual, $expected
}

Reset
'--- before: what the enabled admin sees vs the control user'
Check 'admin sees movie Name (wrapper on)' (Jf GET "/Users/$admin/Items/$($ids.Movie)").Name $movieOriginal
Check 'control sees movie Name (stored)' (Stored $ids.Movie).Name $movieReal

$mv = if ($Expect -eq 'bug') { $movieOriginal } else { $movieReal }
$sv = if ($Expect -eq 'bug') { $seriesOriginal } else { $seriesReal }

'--- A: movie posted back unchanged with the API key (no user claim)'
$posted = PostBack $ids.Movie $admin $cfg.API_KEY
Check 'A posted Name' $posted.Name $movieOriginal
Check 'A stored Name' (Stored $ids.Movie).Name $mv
Reset

'--- B: movie posted back unchanged with the admin session token (user claim = admin)'
$posted = PostBack $ids.Movie $admin $sessionToken
Check 'B posted Name' $posted.Name $movieOriginal
Check 'B stored Name' (Stored $ids.Movie).Name $mv
Reset

'--- C: series posted back unchanged (API key); the controller cascades Name into season/episode SeriesName'
$posted = PostBack $ids.Series $admin $cfg.API_KEY
Check 'C posted Name' $posted.Name $seriesOriginal
Check 'C stored series Name' (Stored $ids.Series).Name $sv
Check 'C stored season SeriesName' (Stored $ids.Season).SeriesName $sv
foreach ($e in $ids.Episodes) { Check "C stored episode $e SeriesName" (Stored $e).SeriesName $sv }
Reset

'--- D: episode posted back unchanged (session); SeriesName is copied when not null'
$ep = $ids.Episodes[0]
$posted = PostBack $ep $admin $sessionToken
Check 'D posted SeriesName' $posted.SeriesName $seriesOriginal
Check 'D stored episode SeriesName' (Stored $ep).SeriesName $sv
Check 'D stored episode Name unchanged' (Stored $ep).Name 'Pilot'
Reset

'--- E: companion - a genuine rename by the enabled admin is kept (both modes)'
PostBack $ids.Movie $admin $sessionToken @{ Name = 'Mouch Edited' } | Out-Null
Check 'E stored Name' (Stored $ids.Movie).Name 'Mouch Edited'
Reset

'--- F: season posted back unchanged (API key)'
PostBack $ids.Season $admin $cfg.API_KEY | Out-Null
Check 'F stored season SeriesName' (Stored $ids.Season).SeriesName $sv
Reset

'--- after reset'
Check 'movie stored' (Stored $ids.Movie).Name $movieReal
Check 'series stored' (Stored $ids.Series).Name $seriesReal
"FAILS=$fails"
