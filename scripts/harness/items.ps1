<#
.SYNOPSIS
  Lists the harness items exactly as the Jellyfin API returns them (Name, OriginalTitle, OriginalLanguage,
  SeriesName), using the API key from .env.local. Pass -UserId to see them as another user does
  (the title wrapper is per user), -AsJson for the raw JSON. Output is forced to UTF-8.
#>
[CmdletBinding()]
param([string]$UserId, [switch]$AsJson)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$envFile = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')) '.env.local'
$cfg = @{}
Get-Content $envFile | ForEach-Object { if ($_ -match '^\s*([A-Z_]+)=(.*)$') { $cfg[$Matches[1]] = $Matches[2] } }
if (-not $UserId) { $UserId = $cfg.USER_ID }
$h = @{ Authorization = "MediaBrowser Token=`"$($cfg.API_KEY)`"" }
$r = Invoke-RestMethod -Headers $h -Uri "$($cfg.BASE_URL)/Users/$UserId/Items?Recursive=true&IncludeItemTypes=Movie,Series,Season,Episode&Fields=OriginalTitle,OriginalLanguage&SortBy=SortName"
if ($AsJson) { $r.Items | ConvertTo-Json -Depth 6; return }
$r.Items | ForEach-Object {
    '{0,-8} {1} Name="{2}" OriginalTitle="{3}" OriginalLanguage="{4}" SeriesName="{5}"' -f $_.Type, $_.Id, $_.Name, $_.OriginalTitle, $_.OriginalLanguage, $_.SeriesName
}
