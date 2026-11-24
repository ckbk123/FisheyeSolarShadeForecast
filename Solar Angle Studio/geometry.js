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
    if (wrapped === 180 && angle < 0) return -180;
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

  // The lens end is straight uphill. In a tilted plane this edge projects
  // opposite the outward normal, so its north bearing equals the numeric
  // south-zero equipment azimuth. Image right is west when image top is north.
  function equipmentFrame(topHeading, tilt) {
    const angle = wrap360(topHeading) * rad;
    const s = Math.sin(angle), c = Math.cos(angle);
    const rise = Math.sin(tilt * rad), run = tilt === 90 ? 0 : Math.cos(tilt * rad);
    return {
      normal: [-s * rise, -c * rise, run],
      top: [s * run, c * run, rise],
      right: [-c, s, 0]
    };
  }

  function framePhone(phone) {
    return equipmentFrame(phone.tilt === 0 ? phone.imageTopBearing ?? 0 : phone.azimuth, phone.tilt);
  }

  function framePanel(panel) {
    return equipmentFrame(panel.azimuth, panel.tilt);
  }

  function sunDirection({azimuth, elevation}) {
    const horizontal = Math.abs(elevation) === 90 ? 0 : Math.cos(elevation * rad);
    return [horizontal * Math.sin(azimuth * rad), horizontal * Math.cos(azimuth * rad), Math.sin(elevation * rad)];
  }

  function setPhoneTilt(phone, tilt) {
    if (!Number.isFinite(tilt) || tilt < 0 || tilt > 90) throw new RangeError("Phone tilt must be between 0° and 90°.");
    if (tilt === phone.tilt) return {...phone};
    if (tilt === 0) return {...phone, tilt, imageTopBearing: wrap360(phone.azimuth)};
    if (phone.tilt === 0) return {...phone, tilt, azimuth: signedAzimuth(phone.imageTopBearing ?? 0)};
    return {...phone, tilt};
  }

  function copyPanelPose(panel, phone) {
    if (panel.tilt === 0) return setPhoneTilt(phone, 0);
    return {...phone, tilt: panel.tilt, azimuth: panel.azimuth};
  }

  function sceneValues({panel, phone, sun}) {
    const panelFrame = framePanel(panel);
    const phoneFrame = framePhone(phone);
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
      actualImageTopBearing: phone.tilt === 90 ? null :
        wrap360(phone.tilt === 0 ? phone.imageTopBearing ?? 0 : phone.azimuth),
      phoneBearing: phone.tilt === 0 ? null : wrap360(phone.azimuth + 180),
      panelBearing: panel.tilt === 0 ? null : wrap360(panel.azimuth + 180),
      sunBearing: Math.abs(sun.elevation) === 90 ? null : wrap360(sun.azimuth)
    };
  }

  const api = {wrap360, signedAzimuth, bearing, angleBetween, framePhone, framePanel, sunDirection,
    setPhoneTilt, copyPanelPose, sceneValues};
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  globalThis.AngleGeometry = api;
})();
