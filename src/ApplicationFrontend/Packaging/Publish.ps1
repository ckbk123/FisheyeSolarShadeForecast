param([switch]$SkipRestore, [switch]$StageOnly, [string]$ExistingStage, [string]$DeliveryName = 'Deliverable')
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot
$repoRoot = Split-Path (Split-Path $sourceRoot)
$project = Join-Path $sourceRoot 'ApplicationFrontend.csproj'
$publishRoot = if ($ExistingStage) { [IO.Path]::GetFullPath($ExistingStage) } else { Join-Path $repoRoot ('artifacts/application-publish-' + [Guid]::NewGuid().ToString('N')) }
if ([IO.Path]::GetFileName($DeliveryName) -ne $DeliveryName -or $DeliveryName -in @('.', '..')) { throw 'DeliveryName must be a directory name.' }
$deliveryRoot = Join-Path $repoRoot $DeliveryName
if (-not $ExistingStage) {
& (Join-Path $PSScriptRoot 'Collect-Notices.ps1')
if (-not $SkipRestore) {
  dotnet restore $project -r win-x64 --configfile (Join-Path $sourceRoot 'NuGet.Config') -p:SelfContained=true -p:PublishSingleFile=true -p:NuGetAudit=false
  if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
}
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:BundleModels=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugSymbols=false -p:DebugType=None -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
$published = Join-Path $publishRoot 'APPLICATION.exe'
if (-not (Test-Path -LiteralPath $published)) { throw 'Executable missing.' }
if ($StageOnly) { Write-Output $publishRoot; return }
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'Example/Debug Data/reference-run/run.json'))) { throw 'Native example run missing. Generate and validate it before packaging.' }
$referenceRun = Get-Content -LiteralPath (Join-Path $publishRoot 'Example/Debug Data/reference-run/run.json') -Raw | ConvertFrom-Json
if ($referenceRun.Status -ne 'Complete') { throw 'Native example run is incomplete.' }
# The ZIP is always a clean first-run package, never a copy of the developer's saved session.
$stageData = Join-Path $publishRoot 'Data'
New-Item -ItemType Directory -Force -Path $stageData | Out-Null
if (@(Get-ChildItem -LiteralPath $stageData -Force | Where-Object Name -ne 'settings.json').Count -gt 0) { throw 'Publish stage contains session data. Run smoke tests with external Data/Debug Data directories.' }
if (@(Get-ChildItem -LiteralPath (Join-Path $publishRoot 'Debug Data') -Force | Where-Object Name -ne 'Read me.txt').Count -gt 0) { throw 'Publish stage contains runtime debug runs. Use an external Debug Data directory for smoke tests.' }
Copy-Item -LiteralPath (Join-Path $publishRoot 'Example/settings.json') -Destination (Join-Path $stageData 'settings.json')
$extras = Get-ChildItem -LiteralPath $publishRoot | Where-Object Name -notin @('APPLICATION.exe', 'Example', 'Data', 'Debug Data')
if ($extras.Count -gt 0) { throw ('Unexpected publish files: ' + (($extras | Select-Object -ExpandProperty Name) -join ', ')) }
New-Item -ItemType Directory -Force -Path $deliveryRoot | Out-Null
Copy-Item -LiteralPath $published -Destination (Join-Path $deliveryRoot 'APPLICATION.exe') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $deliveryRoot 'Debug Data') | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRoot 'Debug Data/Read me.txt') -Destination (Join-Path $deliveryRoot 'Debug Data/Read me.txt') -Force
foreach ($sourceFile in Get-ChildItem -LiteralPath (Join-Path $publishRoot 'Example') -File -Recurse) {
  $relative = [IO.Path]::GetRelativePath($publishRoot, $sourceFile.FullName)
  $destination = Join-Path $deliveryRoot $relative
  New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
  Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force
}
# Updates preserve the local portable session; new recipients get the example defaults.
$deliveryData = Join-Path $deliveryRoot 'Data'
New-Item -ItemType Directory -Force -Path $deliveryData | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $deliveryData 'settings.json'))) {
  Copy-Item -LiteralPath (Join-Path $stageData 'settings.json') -Destination (Join-Path $deliveryData 'settings.json')
}
# DeliveryName selects the installed folder; Deliverable.zip is the canonical clean package.
$zipPath = Join-Path $repoRoot 'Deliverable.zip'
$temporaryZip = Join-Path $repoRoot ('artifacts/delivery-' + [Guid]::NewGuid().ToString('N') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
$manifest = [ordered]@{
  Stage = $publishRoot
  ExecutableBytes = (Get-Item -LiteralPath $published).Length
  PackageBytes = (Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Measure-Object Length -Sum).Sum
  ZipBytes = (Get-Item -LiteralPath $zipPath).Length
  ExecutableSha256 = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash
  Files = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse | ForEach-Object { [ordered]@{ Path = [IO.Path]::GetRelativePath($publishRoot, $_.FullName); Bytes = $_.Length } })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/application-package.json') -Encoding utf8
[pscustomobject]$manifest | Select-Object Stage, ExecutableBytes, PackageBytes, ZipBytes, ExecutableSha256
