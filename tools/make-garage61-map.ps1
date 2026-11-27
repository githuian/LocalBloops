# Regenerates src\LocalBloops\Assets\Garage61Map.json: iRacing TrackID/CarID -> Garage 61 track/car ID,
# used by the tray's "Search Garage 61 for this track/car". Run it at build time only. The app itself
# never calls Garage 61; it just ships this table.
#
# Needs a Garage 61 personal access token (garage61.net -> Developer portal). Put it in an environment
# variable instead of on the command line so it doesn't end up in shell history:
#
#   $env:GARAGE61_TOKEN = '<your token>'
#   powershell -ExecutionPolicy Bypass -File tools\make-garage61-map.ps1
$ErrorActionPreference = 'Stop'

$token = $env:GARAGE61_TOKEN
if (-not $token) { throw 'Set $env:GARAGE61_TOKEN to a Garage 61 personal access token first.' }

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'src\LocalBloops\Assets\Garage61Map.json'
$headers = @{ Authorization = "Bearer $token" }

function Get-Garage61List([string]$path) {
    $r = Invoke-RestMethod -Uri "https://garage61.net/api/v1/$path" -Headers $headers
    if ($null -ne $r.items) { return @($r.items) }
    return @($r)
}

# Garage 61 objects carry their own "id" plus the sim's ID in "platform_id" (and "platform").
function Get-IracingMap([object[]]$items, [string]$what) {
    $map = [ordered]@{}
    if ($items.Count -eq 0) { throw "Garage 61 returned no $what." }
    if (-not ($items[0].PSObject.Properties.Name -contains 'platform_id')) {
        $fields = ($items[0].PSObject.Properties.Name) -join ', '
        throw "Garage 61 $what have no platform_id field. Fields seen: $fields"
    }
    $rows = foreach ($i in $items) {
        if ($i.platform -and $i.platform -notmatch '^iracing$') { continue }
        $platformId = "$($i.platform_id)"
        if ($platformId -notmatch '^\d+$' -or [int]$i.id -le 0) { continue }
        [pscustomobject]@{ Key = [int]$platformId; Id = [int]$i.id }
    }
    foreach ($row in ($rows | Sort-Object Key)) {
        if ($map.Contains("$($row.Key)")) { continue }
        $map["$($row.Key)"] = $row.Id
    }
    return $map
}

$tracks = Get-IracingMap (Get-Garage61List 'tracks') 'tracks'
$cars = Get-IracingMap (Get-Garage61List 'cars') 'cars'

$doc = [ordered]@{
    source = "garage61.net/api/v1 (tracks, cars), generated $(Get-Date -Format 'yyyy-MM-dd')"
    tracks = $tracks
    cars = $cars
}
$json = $doc | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($out, $json + "`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wrote $out ($($tracks.Count) tracks, $($cars.Count) cars)"
