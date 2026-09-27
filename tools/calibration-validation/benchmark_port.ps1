param(
    [Parameter(Mandatory=$true)][string]$ImageDirectory,
    [string]$OutputDirectory = 'artifacts/calibration-validation/optimized',
    [ValidateRange(1,50)][int]$Runs = 10,
    [ValidateRange(1,20)][int]$FreshProcesses = 3
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cliProject = Join-Path $repo 'src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli'
& dotnet build $cliProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$cli = Join-Path $cliProject 'bin/Release/net10.0-windows/SolarShade.Calibration.Cli.dll'
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
& dotnet $cli hardware | Set-Content (Join-Path $output 'hardware.json')
if ($LASTEXITCODE -ne 0) { throw 'Hardware probe failed' }
& dotnet $cli benchmark-pipeline $ImageDirectory $output $Runs
if ($LASTEXITCODE -ne 0) { throw 'Pipeline benchmark failed' }
$processRuns = @()
for ($i = 0; $i -lt $FreshProcesses; $i++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $json = & dotnet $cli calibrate $ImageDirectory $output
    $elapsed = $timer.Elapsed.TotalMilliseconds
    if ($LASTEXITCODE -ne 0) { throw 'Fresh-process calibration failed' }
    $processRuns += [pscustomobject]@{ wall_ms = $elapsed; pipeline = ($json -join "`n" | ConvertFrom-Json) }
}
$processRuns | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'fresh-processes.json')
& dotnet $cli validate (Join-Path $output 'observations.json') (Join-Path $output 'result.json') |
    Set-Content (Join-Path $output 'independent-validation.json')
if ($LASTEXITCODE -ne 0) { throw 'Independent validation failed' }
Write-Output "Reports written to $output. Filesystem caches were not purged. Fresh-process times include dotnet process startup."
