#requires -Version 7.6
# After a Windows Release build, compare the web map against the actual desktop assemblies.
param([string] $DesktopDirectory = (Join-Path $PSScriptRoot '../../src/ApplicationFrontend/bin/Release/net10.0-windows'))
$ErrorActionPreference = 'Stop'
foreach ($name in 'SolarShade.Shading', 'APPLICATION') {
    $assemblyPath = (Get-Item -LiteralPath (Join-Path $DesktopDirectory ($name + '.dll'))).FullName
    [Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
}

function Vector($direction) { @($direction.East, $direction.North, $direction.Up) }

function Camera($settings) {
    $pose = [SolarShade.Desktop.EquipmentOrientation]::ToCameraPose($settings)
    $axes = $pose.Axes()
    $angles = [SolarShade.Desktop.EquipmentOrientation]::Describe($settings)
    @{
        right = Vector $axes.Item1
        top = Vector ([SolarShade.Shading.Direction]::op_Multiply(-1, $axes.Item2))
        normal = Vector $axes.Item3
        topHeading = $settings.PhoneTopHeading
        imageTopBearing = $angles.ImageTopBearing
        lensBearing = if ($null -ne $angles.PhoneAzimuth) { $settings.BottomAzimuth }
        lensAzimuthFromSouth = $angles.PhoneAzimuth
    }
}

# Independent panel and camera inputs exercise both objects even away from a mounted pose.
$cases = foreach ($heading in 0, 90, -90, 270, 180, -180, -26, 36.75) {
    foreach ($tilt in 0, 6.8, 28, 90) {
        foreach ($sunElevation in -90, -17, 0, 42, 90) {
            $settings = [SolarShade.Desktop.UserSettings]::new()
            $settings.PanelTilt = $tilt
            $settings.PanelTopHeading = $heading
            $phone = @{ topHeading = (45 - $heading + 360) % 360; tilt = 90 - $tilt }
            $settings.PhoneInclination = $phone.tilt
            $settings.PhoneTopHeading = $phone.topHeading

            $sunAzimuth = (317.25 + $heading) % 360
            $sunDirection = [SolarShade.Shading.Direction]::FromAngles($sunAzimuth, 90 - $sunElevation)
            $panelDirection = [SolarShade.Shading.Direction]::FromAngles($settings.PanelAzimuth, $tilt)
            $lensDirection = [SolarShade.Desktop.EquipmentOrientation]::ToCameraPose($settings).Axes().Item3
            $case = @{
                state = @{
                    panel = @{ topHeading = (360 + $heading) % 360; tilt = $tilt }
                    phone = $phone
                    sun = @{ azimuth = $sunAzimuth; elevation = $sunElevation }
                }
                camera = Camera $settings
                panel = Vector $panelDirection
                panelTopHeading = $settings.PanelTopHeading
                panelAzimuthFromSouth = [SolarShade.Desktop.EquipmentOrientation]::Describe($settings).PanelAzimuth
                panelBearing = if ($tilt -ne 0) { $settings.PanelAzimuth }
                sun = Vector $sunDirection
                incidenceCosine = $panelDirection.Dot($sunDirection)
                separationCosine = $panelDirection.Dot($lensDirection)
            }
            $originalTilt = $settings.PhoneInclination
            [SolarShade.Desktop.EquipmentOrientation]::PlacePhoneOnPanel($settings)
            $case.mounted = Camera $settings
            $settings.PhoneInclination = $originalTilt
            $case.retilted = Camera $settings
            $case
        }
    }
}

$compare = @'
const assert = require("node:assert/strict");
const fs = require("node:fs");
const G = require(process.argv[1]);
const cases = JSON.parse(fs.readFileSync(0, "utf8"));
function close(a, b, key) {
  assert.ok(Number.isFinite(a) && Number.isFinite(b) && Math.abs(a - b) < 1e-10, key + ": " + a + " vs " + b);
}
function vector(a, b, key) {
  assert.equal(a.length, 3, key);
  b.forEach((component, i) => close(a[i], component, key + "[" + i + "]"));
}
function bearing(a, b, key) {
  if (b === null) assert.equal(a, null, key);
  else {
    assert.ok(Number.isFinite(a), key);
    close(((a - b + 540) % 360) - 180, 0, key);
  }
}
function camera(actual, expected) {
  for (const axis of ["right", "top", "normal"]) vector(actual[axis], expected[axis], "camera " + axis);
}
function southAzimuth(bearing) {
  return bearing === null ? null : G.signedAzimuth(bearing - 180);
}
for (const c of cases) {
  try {
    const {panel, phone, sun} = c.state;
    const values = G.sceneValues(c.state);
    camera(G.equipmentFrame(phone), c.camera);
    vector(G.equipmentFrame(panel).normal, c.panel, "panel front");
    bearing(panel.topHeading, c.panelTopHeading, "panel top heading");
    bearing(phone.topHeading, c.camera.topHeading, "phone top heading");
    bearing(southAzimuth(values.phoneBearing), c.camera.lensAzimuthFromSouth, "lens azimuth from south");
    bearing(southAzimuth(values.panelBearing), c.panelAzimuthFromSouth, "front azimuth from south");
    vector(values.sunVector, c.sun, "solar direction");
    bearing(values.phoneBearing, c.camera.lensBearing, "lens azimuth");
    bearing(values.actualImageTopBearing, c.camera.imageTopBearing, "image-top bearing");
    bearing(values.panelBearing, c.panelBearing, "panel front azimuth");
    bearing(values.sunBearing, Math.abs(sun.elevation) === 90 ? null : sun.azimuth, "solar azimuth");
    close(values.zenith, 90 - sun.elevation, "zenith");
    close(values.phoneTilt, phone.tilt, "phone tilt");
    close(Math.cos(values.incidence * Math.PI / 180), c.incidenceCosine, "incidence cosine");
    close(Math.cos(values.separation * Math.PI / 180), c.separationCosine, "lens–panel cosine");
    const mounted = G.copyPanelPose(panel);
    camera(G.equipmentFrame(mounted), c.mounted);
    bearing(mounted.topHeading, c.mounted.topHeading, "aligned top heading");
    camera(G.equipmentFrame({...mounted, tilt: phone.tilt}), c.retilted);
    bearing(mounted.topHeading, c.retilted.topHeading, "top heading after changing tilt");
  } catch (error) {
    throw new Error("Parity failure for " + JSON.stringify(c.state), {cause: error});
  }
}
console.log(cases.length + " orientation map cases agree with the desktop camera and world directions.");
'@
$geometry = (Get-Item -LiteralPath (Join-Path $PSScriptRoot '../geometry.js')).FullName
ConvertTo-Json -InputObject $cases -Compress -Depth 6 | node -e $compare $geometry
if ($LASTEXITCODE -ne 0) { throw 'Orientation map parity check failed.' }
