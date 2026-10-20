param(
    [string]$ProjectName = "rfid-dev",
    [string]$SerialDevice = "/dev/ttyUSB0",
    [string]$DialoutGid = "20",
    [ValidateRange(1, 300)]
    [int]$PollSeconds = 1,
    [string[]]$AuthorizedEpc = @(
        "E28069950000400E45B4F53E",
        "E280689400005010BF75955B"
    )
)

$ErrorActionPreference = "Stop"
$env:RFID_SERIAL_DEVICE = $SerialDevice
$env:RFID_DIALOUT_GID = $DialoutGid

$authorized = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($epc in $AuthorizedEpc) {
    [void]$authorized.Add($epc)
}

Write-Host "RFID continuous scan monitor" -ForegroundColor Cyan
Write-Host "Reader device: $SerialDevice"
Write-Host "Every reader observation is printed, including repeated EPCs. Press Ctrl+C to stop."
Write-Host ""

while ($true) {
    $json = & docker compose -p $ProjectName `
        --env-file docker/dev.env `
        -f docker-compose.yml `
        -f docker-compose.dev.yml `
        -f docker-compose.serial.yml `
        exec -T middleware `
        /app/rfid-middleware reader watch `
        --duration $PollSeconds `
        --config /app/appsettings.json

    if ($LASTEXITCODE -ne 0) {
        throw "reader watch failed with exit code $LASTEXITCODE"
    }

    $reads = $json | ConvertFrom-Json
    foreach ($read in @($reads)) {
        if (-not $read.epc) {
            continue
        }

        $status = if ($authorized.Contains([string]$read.epc)) {
            "AUTHORIZED"
        } else {
            "UNAUTHORIZED"
        }
        $color = if ($status -eq "AUTHORIZED") { "Green" } else { "Red" }
        $time = ([DateTimeOffset]$read.timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz")

        Write-Host ("{0}  {1,-12}  EPC={2}  reader={3}  antenna={4}  RSSI={5}" -f `
            $time, $status, $read.epc, $read.readerId, $read.antennaId, $read.rssi) `
            -ForegroundColor $color
    }
}
