/* Directions are [east, north, up]. Angles are in degrees. */
(function () {
  "use strict";

  const rad = Math.PI / 180;

  function wrap360(angle) {
    const wrapped = ((angle % 360) + 360) % 360;
    return wrapped === 0 ? 0 : wrapped;
  }

  function signedAzimuth(angle) {
    const wrapped = wrap360(angle);
    return wrapped > 180 ? wrapped - 360 : wrapped;
  }

  function bearing([east, north]) {
    if (Math.abs(east) < 1e-14 && Math.abs(north) < 1e-14) return null;
    return wrap360(Math.atan2(east, north) / rad);
  }

  function angleBetween(a, b) {
    const cross = [
      a[1] * b[2] - a[2] * b[1],
      a[2] * b[0] - a[0] * b[2],
      a[0] * b[1] - a[1] * b[0]
    ];
    const dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    return Math.atan2(Math.hypot(...cross), dot) / rad;
  }

  // Start flat with the marked top at topHeading, then lift that edge about the
  // cross-slope axis. For the sky-facing image, right is west when top is north.
  function equipmentFrame({topHeading, tilt}) {
    const angle = wrap360(topHeading) * rad;
    const s = Math.sin(angle), c = Math.cos(angle);
    const rise = Math.sin(tilt * rad), run = tilt === 90 ? 0 : Math.cos(tilt * rad);
    return {
      normal: [-s * rise, -c * rise, run],
      top: [s * run, c * run, rise],
      right: [-c, s, 0]
    };
  }

  function sunDirection({azimuth, elevation}) {
    const horizontal = Math.abs(elevation) === 90 ? 0 : Math.cos(elevation * rad);
    return [horizontal * Math.sin(azimuth * rad), horizontal * Math.cos(azimuth * rad), Math.sin(elevation * rad)];
  }

  function copyPanelPose(panel) {
    return {tilt: panel.tilt, topHeading: panel.topHeading};
  }

  function sceneValues({panel, phone, sun}) {
    const panelFrame = equipmentFrame(panel);
    const phoneFrame = equipmentFrame(phone);
    const sunVector = sunDirection(sun);
    return {
      panelNormal: panelFrame.normal,
      phoneNormal: phoneFrame.normal,
      imageTop: phoneFrame.top,
      sunVector,
      incidence: angleBetween(panelFrame.normal, sunVector),
      separation: angleBetween(panelFrame.normal, phoneFrame.normal),
      zenith: 90 - sun.elevation,
      phoneTilt: phone.tilt,
      actualImageTopBearing: phone.tilt === 90 ? null : wrap360(phone.topHeading),
      phoneBearing: phone.tilt === 0 ? null : wrap360(phone.topHeading + 180),
      panelBearing: panel.tilt === 0 ? null : wrap360(panel.topHeading + 180),
      sunBearing: Math.abs(sun.elevation) === 90 ? null : wrap360(sun.azimuth)
    };
  }

  const api = {wrap360, signedAzimuth, bearing, angleBetween, equipmentFrame, sunDirection, copyPanelPose, sceneValues};
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  globalThis.AngleGeometry = api;
})();
