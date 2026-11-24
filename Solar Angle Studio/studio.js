(() => {
  "use strict";

  const G = window.AngleGeometry;
  const $ = id => document.getElementById(id);
  const scenarios = [
    { id: "south", name: "South-facing · 28°", topHeading: 0, tilt: 28 },
    { id: "east", name: "East-facing · 28°", topHeading: 270, tilt: 28 },
    { id: "west", name: "West-facing · 28°", topHeading: 90, tilt: 28 },
    { id: "north", name: "North-facing · 28°", topHeading: 180, tilt: 28 },
    { id: "flat", name: "Flat · tops point east", topHeading: 90, tilt: 0 },
    { id: "vertical", name: "Vertical panel · 90°", topHeading: 0, tilt: 90 }
  ];
  const options = {active: "panel", angles: true, labels: true, placed: true};
  const colors = {panel: "var(--panel)", phone: "var(--phone)", sun: "var(--sun)"};
  const compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
  const cardinal = bearing => compass[Math.round(G.wrap360(bearing) / 45) % 8];
  const numberText = value => Number(value.toFixed(5)).toString();
  const degrees = value => numberText(value).replace("-", "−") + "°";
  const measure = (value, label) => '<span class="measure"><b>' + value + '</b><small>' + label + '</small></span>';
  let state, scenarioId, fieldKeys;
  const scene = window.AngleScene.create($("scene"), setViewButton);

  if (new URLSearchParams(location.search).get("embedded") === "1") {
    document.documentElement.classList.add("embedded");
  }

  function setViewButton(view) {
    document.querySelectorAll("[data-view]").forEach(button =>
      button.setAttribute("aria-pressed", button.dataset.view === view));
  }

  function selectPreset(id) {
    const preset = scenarios.find(item => item.id === id);
    if (!preset) return;
    scenarioId = id;
    const panel = {topHeading: preset.topHeading, tilt: preset.tilt};
    state = {panel, phone: G.copyPanelPose(panel), sun: {azimuth: 135, elevation: 42}};
    options.placed = true;
    options.active = id === "flat" ? "phone" : "panel";
    $("preset").value = id;
    $("custom-scenario").hidden = true;
    scene.resetView();
    render();
  }

  function markCustom() {
    $("custom-scenario").hidden = false;
    $("preset").value = "custom";
  }

  const compassLimits = ["0° N", "360° N"];
  const tiltLimits = ["0° flat", "90° vertical"];
  const field = (id, name, min, max, value, limits, wraps = false) =>
    ({id, name, min, max, value, limits, wraps});

  function controls(v) {
    const panel = state.panel, phone = state.phone, sun = state.sun;
    if (options.active === "panel") return [
      field("panelTilt", "Panel tilt from horizontal, °", 0, 90, panel.tilt, tiltLimits),
      field("panelTopHeading", "Panel top heading, ° clockwise from true north", 0, 360, panel.topHeading, compassLimits, true)
    ];
    if (options.active === "sun") return [
      field("sunAz", "Solar azimuth, ° clockwise from true north", 0, 360,
        v.sunBearing == null ? null : sun.azimuth, compassLimits, true),
      field("elevation", "Solar elevation from horizontal, °", -90, 90, sun.elevation, ["−90° nadir", "+90° zenith"]),
      field("zenith", "Solar zenith from vertical up, °", 0, 180, v.zenith, ["0° zenith", "180° nadir"])
    ];
    return [
      field("phoneTilt", "Phone tilt from horizontal, °", 0, 90, phone.tilt, tiltLimits),
      field("phoneTopHeading", "Phone top heading, ° clockwise from true north", 0, 360, phone.topHeading, compassLimits, true)
    ];
  }

  function facing(bearing) {
    return bearing == null ? "undefined (faces straight up)" :
      degrees(G.signedAzimuth(bearing - 180)) + " from south · " + cardinal(bearing);
  }

  function note(v) {
    if (options.active === "sun") {
      if (v.sunBearing == null) return "At zenith or nadir, solar azimuth is undefined.";
      return state.sun.elevation < 0 ? "Sun below the horizon." : "Elevation and zenith add to 90°.";
    }
    const panel = options.active === "panel";
    const readout = (panel ? "Panel front azimuth · " : "Lens azimuth · ") + facing(panel ? v.panelBearing : v.phoneBearing);
    if (state[options.active].tilt === 90)
      return readout + ". The top points straight up. Its heading is measured with the device laid flat.";
    return readout;
  }

  function writeAngle(id, value) {
    const panel = state.panel, phone = state.phone, sun = state.sun;
    switch (id) {
      case "panelTopHeading": panel.topHeading = G.wrap360(value); break;
      case "panelTilt": panel.tilt = value; break;
      case "phoneTopHeading": phone.topHeading = G.wrap360(value); break;
      case "phoneTilt": phone.tilt = value; break;
      case "sunAz": sun.azimuth = G.wrap360(value); break;
      case "elevation": sun.elevation = value; break;
      case "zenith": sun.elevation = 90 - value; break;
      default: return;
    }
    if (options.placed && (panel.tilt !== phone.tilt || panel.topHeading !== phone.topHeading))
      options.placed = false;
    markCustom();
    render(id, value);
  }

  function makeField(f) {
    const group = document.createElement("div");
    group.className = "angle-field";
    group.id = "group-" + f.id;
    group.innerHTML = '<div class="field-head"><label for="num-' + f.id + '">' + f.name + '</label>' +
      '<div class="number-box"><input type="number" id="num-' + f.id + '" step="any"><span aria-hidden="true">°</span></div></div>' +
      '<input class="range" type="range" step="0.1" id="range-' + f.id + '" aria-label="' + f.name + ' slider">' +
      '<div class="limits"><span>' + f.limits[0] + '</span><span>' + f.limits[1] + '</span></div>';
    const range = group.querySelector(".range"), input = group.querySelector("input[type=number]");
    range.min = f.min;
    range.max = f.max;
    if (!f.wraps) { input.min = f.min; input.max = f.max; }
    input.required = true;
    range.addEventListener("input", () => writeAngle(f.id, range.valueAsNumber));
    input.addEventListener("change", () => {
      if (!Number.isFinite(input.valueAsNumber) || !input.reportValidity()) { render(); return; }
      writeAngle(f.id, input.valueAsNumber);
    });
    input.addEventListener("keydown", event => { if (event.key === "Enter") input.blur(); });
    return group;
  }

  function renderFields(v, editedId, editedValue) {
    const fields = controls(v);
    const keys = fields.map(f => f.id).join(" ");
    if (keys !== fieldKeys) {
      const host = $("fields");
      host.replaceChildren(...fields.map(makeField));
      fieldKeys = keys;
    }
    for (const f of fields) {
      const range = $("range-" + f.id), num = $("num-" + f.id);
      const disabled = f.value == null;
      const value = f.id === editedId && f.max === 360 && f.value === 0 && editedValue === 360 ? 360 : f.value;
      num.value = disabled ? "" : numberText(value);
      num.placeholder = disabled ? "—" : "";
      range.value = disabled ? (f.min + f.max) / 2 : value;
      range.setAttribute("aria-valuetext", disabled ? "Undefined" : degrees(value));
      range.style.setProperty("--fill", (100 * (range.valueAsNumber - f.min) / (f.max - f.min)) + "%");
      range.disabled = num.disabled = disabled;
      $("group-" + f.id).classList.toggle("disabled", disabled);
    }
    $("control-note").textContent = note(v);
    $("copy-panel").hidden = options.active !== "phone";
    $("photo-inspection").hidden = options.active !== "phone";
    if (options.active === "phone") window.AngleInspection.update($("photo-inspection"), state);
  }

  function renderReadout(v) {
    const row = (id, name, heading, tilt, az) => '<tr><th scope="row"><span class="object-label"><i class="' + id +
      '"></i>' + name + '</span></th><td>' + heading + '</td><td>' + tilt + '</td><td>' + az + '</td></tr>';
    const top = device => measure(degrees(device.topHeading) + " " + cardinal(device.topHeading),
      device.tilt === 90 ? "when laid flat" : "from true north");
    const azimuth = (bearing, name) => measure(bearing == null ? "Undefined" :
      degrees(G.signedAzimuth(bearing - 180)) + " " + cardinal(bearing), name + " · from south");
    $("readout").innerHTML =
      row("panel", "Panel", top(state.panel), measure(degrees(state.panel.tilt), "tilt"), azimuth(v.panelBearing, "panel front")) +
      row("phone", "Phone", top(state.phone), measure(degrees(state.phone.tilt), "tilt"), azimuth(v.phoneBearing, "lens")) +
      row("sun", "Sun", "—", measure(degrees(state.sun.elevation) + " / " + degrees(v.zenith), "elevation / zenith"),
        measure(v.sunBearing == null ? "Undefined" : degrees(v.sunBearing) + " " + cardinal(v.sunBearing), "solar · from true north"));
    $("incidence").textContent = degrees(v.incidence);
    $("separation").textContent = degrees(v.separation);
  }

  function render(editedId, editedValue) {
    const values = G.sceneValues(state);
    document.documentElement.style.setProperty("--accent", colors[options.active]);
    document.querySelectorAll("[data-active]").forEach(button =>
      button.setAttribute("aria-pressed", button.dataset.active === options.active));
    renderFields(values, editedId, editedValue);
    renderReadout(values);
    scene.update(state, options);
  }

  for (const preset of scenarios) $("preset").add(new Option(preset.name, preset.id));
  const custom = new Option("Custom angles", "custom");
  custom.id = "custom-scenario";
  custom.disabled = custom.hidden = true;
  $("preset").add(custom);
  $("preset").addEventListener("change", event => selectPreset(event.target.value));
  $("reset-preset").addEventListener("click", () => selectPreset(scenarioId));
  document.querySelectorAll("[data-active]").forEach(button => button.addEventListener("click", () => {
    options.active = button.dataset.active;
    render();
  }));
  $("copy-panel").addEventListener("click", () => {
    state.phone = G.copyPanelPose(state.panel);
    options.placed = true;
    markCustom();
    render();
  });
  for (const key of ["angles", "labels"]) $("show-" + key).addEventListener("change", event => {
    options[key] = event.target.checked;
    scene.update(state, options);
  });
  document.querySelectorAll("[data-view]").forEach(button =>
    button.addEventListener("click", () => scene.setView(button.dataset.view)));
  $("zoom-out").addEventListener("click", () => scene.zoomBy(1 / 1.18));
  $("zoom-in").addEventListener("click", () => scene.zoomBy(1.18));
  $("fit-view").addEventListener("click", () => scene.resetView());

  const guide = $("guide");
  $("open-guide").addEventListener("click", () => guide.showModal());
  $("close-guide").addEventListener("click", () => guide.close());
  guide.addEventListener("click", event => {
    if (event.target !== guide) return;
    const r = guide.getBoundingClientRect();
    if (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom)
      guide.close();
  });
  selectPreset("south");
})();
