$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot
$repoRoot = Split-Path (Split-Path $sourceRoot)
$packageRoot = Join-Path $env:USERPROFILE '.nuget/packages'
$items = @(
  @('FishEyes / CNRS-LAAS', (Join-Path $PSScriptRoot 'Licenses/FishEyes-LICENSE.txt')),
  @('Poenitz py-omnicalib', (Join-Path $repoRoot 'THIRD_PARTY_LICENSES/py-omnicalib-LICENSE.txt')),
  @('pvlib-python', (Join-Path $repoRoot 'src/IrradianceTransposition/THIRD_PARTY_NOTICES.md')),
  @('ONNX Runtime', (Join-Path $packageRoot 'microsoft.ml.onnxruntime.directml/1.24.4/LICENSE')),
  @('ONNX Runtime third-party notices', (Join-Path $packageRoot 'microsoft.ml.onnxruntime.directml/1.24.4/ThirdPartyNotices.txt')),
  @('Microsoft DirectML', (Join-Path $packageRoot 'microsoft.ai.directml/1.15.4/LICENSE.txt')),
  @('Microsoft.Extensions.Logging.Abstractions 8.0.3', (Join-Path $packageRoot 'microsoft.extensions.logging.abstractions/8.0.3/LICENSE.TXT')),
  @('Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2', (Join-Path $packageRoot 'microsoft.extensions.dependencyinjection.abstractions/8.0.2/LICENSE.TXT')),
  @('DirectML code licence', (Join-Path $packageRoot 'microsoft.ai.directml/1.15.4/LICENSE-CODE.txt')),
  @('DirectML third-party notices', (Join-Path $packageRoot 'microsoft.ai.directml/1.15.4/ThirdPartyNotices.txt')),
  @('.NET runtime', (Join-Path $packageRoot 'microsoft.netcore.app.runtime.win-x64/10.0.8/LICENSE.TXT')),
  @('.NET third-party notices', (Join-Path $packageRoot 'microsoft.netcore.app.runtime.win-x64/10.0.8/THIRD-PARTY-NOTICES.TXT'))
)
$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('SolarShade test application 0.1 — bundled component notices')
foreach ($item in $items) {
  [void]$builder.AppendLine("`r`n--- " + $item[0] + " ---`r`n")
  [void]$builder.AppendLine([System.IO.File]::ReadAllText($item[1]))
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Licenses') -Filter '*.txt' | Where-Object Name -ne 'FishEyes-LICENSE.txt') {
  [void]$builder.AppendLine("`r`n--- " + $file.Name + " ---`r`n")
  [void]$builder.AppendLine([System.IO.File]::ReadAllText($file.FullName))
}
[System.IO.File]::WriteAllText((Join-Path $sourceRoot 'ThirdPartyNotices.txt'), $builder.ToString())
