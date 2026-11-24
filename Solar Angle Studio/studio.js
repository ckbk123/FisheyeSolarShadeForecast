(() => {
  "use strict";

  const G = window.AngleGeometry;
  const $ = id => document.getElementById(id);
  const scenarios = [
    { id: "south", name: "South-facing · 28°", azimuth: 0, tilt: 28 },
    { id: "east", name: "East-facing · 28°", azimuth: -90, tilt: 28 },
    { id: "west", name: "West-facing · 28°", azimuth: 90, tilt: 28 },
    { id: "north", name: "North-facing · 28°", azimuth: 180, tilt: 28 },
    { id: "flat", name: "Flat phone · image top east", azimuth: 0, tilt: 0, flatHeading: 90 },
    { id: "vertical", name: "Vertical panel · 90°", azimuth: 0, tilt: 90 }
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
    const panel = {azimuth: preset.azimuth, tilt: preset.tilt};
    state = {panel, phone: {...panel, imageTopBearing: preset.flatHeading ?? 0}, sun: {azimuth: 135, elevation: 42}};
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

  const equipmentLimits = ["−180° N", "+180° N"];
  const compassLimits = ["0° N", "360° N"];
  const tiltLimits = ["0° flat", "90° vertical"];
  const field = (id, name, min, max, value, limits, wraps = false) =>
    ({id, name, min, max, value, limits, wraps});

  function controls(v) {
    const panel = state.panel, phone = state.phone, sun = state.sun;
    if (options.active === "panel") return [
      field("panelTilt", "Panel tilt from horizontal, °", 0, 90, panel.tilt, tiltLimits),
      field("panelAz", "Panel front azimuth, ° clockwise from south", -180, 180,
        v.panelBearing == null ? null : panel.azimuth, equipmentLimits, true)
    ];
    if (options.active === "sun") return [
      field("sunAz", "Solar azimuth, ° clockwise from true north", 0, 360,
        v.sunBearing == null ? null : sun.azimuth, compassLimits, true),
      field("elevation", "Solar elevation from horizontal, °", -90, 90, sun.elevation, ["−90° nadir", "+90° zenith"]),
      field("zenith", "Solar zenith from vertical up, °", 0, 180, v.zenith, ["0° zenith", "180° nadir"])
    ];
    const fields = [
      field("phoneTilt", "Phone tilt from horizontal, °", 0, 90, phone.tilt, tiltLimits),
      field("lensAz", "Lens azimuth, ° clockwise from south", -180, 180,
        v.phoneBearing == null ? null : phone.azimuth, equipmentLimits, true)
    ];
    if (phone.tilt === 0) fields.push(field("flatHeading", "Image-top bearing, ° clockwise from true north",
      0, 360, v.actualImageTopBearing, compassLimits, true));
    return fields;
  }

  function note(v) {
    if (options.active === "sun") {
      if (v.sunBearing == null) return "At zenith or nadir, solar azimuth is undefined.";
      return state.sun.elevation < 0 ? "Sun below the horizon." : "Elevation and zenith add to 90°.";
    }
    if (options.active === "panel" && v.panelBearing == null) return "Flat panel · front azimuth is undefined.";
    if (options.active === "phone" && v.phoneBearing == null) return "Flat phone · lens azimuth is undefined.";
    return "S 0°  ·  W +90°  ·  N ±180°  ·  E −90°";
  }

  function writeAngle(id, value) {
    const panel = state.panel, phone = state.phone, sun = state.sun;
    switch (id) {
      case "panelAz": panel.azimuth = G.signedAzimuth(value); break;
      case "panelTilt": panel.tilt = value; break;
      case "lensAz": phone.azimuth = G.signedAzimuth(value); break;
      case "phoneTilt": state.phone = G.setPhoneTilt(phone, value); break;
      case "flatHeading": phone.imageTopBearing = G.wrap360(value); break;
      case "sunAz": sun.azimuth = G.wrap360(value); break;
      case "elevation": sun.elevation = value; break;
      case "zenith": sun.elevation = 90 - value; break;
      default: return;
    }
    // A copied pose is a snapshot. Show the phone beside the panel when their planes diverge.
    if (options.placed && G.angleBetween(G.framePanel(panel).normal, G.framePhone(state.phone).normal) > 1e-10)
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
      const activeIds = new Set(fields.map(f => "group-" + f.id));
      for (const group of [...host.children]) {
        if (!activeIds.has(group.id)) group.remove();
      }
      // A flat phone adds a heading row. Keep the tilt slider mounted through
      // that transition so an ongoing pointer drag or keyboard edit continues.
      for (const f of fields) {
        if (!$("group-" + f.id)) host.append(makeField(f));
      }
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
    const row = (id, name, az, tilt, extra) => '<tr><th scope="row"><span class="object-label"><i class="' + id +
      '"></i>' + name + '</span></th><td>' + az + '</td><td>' + tilt + '</td><td>' + extra + '</td></tr>';
    const azimuth = (az, bearing, origin) => bearing == null ? "Undefined" :
      measure(degrees(az) + " " + cardinal(bearing), "from " + origin);
    const heading = v.actualImageTopBearing == null ? "Undefined" :
      degrees(v.actualImageTopBearing) + " " + cardinal(v.actualImageTopBearing);
    $("readout").innerHTML =
      row("panel", "Panel", azimuth(state.panel.azimuth, v.panelBearing, "south"),
        measure(degrees(state.panel.tilt), "tilt"), "—") +
      row("phone", "Phone", azimuth(state.phone.azimuth, v.phoneBearing, "south"),
        measure(degrees(state.phone.tilt), "tilt"), measure(heading, "image top · true north")) +
      row("sun", "Sun", azimuth(state.sun.azimuth, v.sunBearing, "true north"),
        measure(degrees(state.sun.elevation), "elevation"), measure(degrees(v.zenith), "zenith"));
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
    state.phone = G.copyPanelPose(state.panel, state.phone);
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
