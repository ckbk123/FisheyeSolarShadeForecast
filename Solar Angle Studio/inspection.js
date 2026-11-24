/* Ideal equidistant, 180-degree fisheye; oriented photo axes come from SolarShade. */
(() => {
  "use strict";

  const centerX = 68;
  const centerY = 63;
  const radius = 47;
  const halfPi = Math.PI / 2;
  const precision = 1e-10;
  const mounts = new WeakMap();
  const cardinals = [
    { label: "N", vector: [0, 1, 0] },
    { label: "E", vector: [1, 0, 0] },
    { label: "S", vector: [0, -1, 0] },
    { label: "W", vector: [-1, 0, 0] }
  ];

  function dot(a, b) {
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  }

  function project(vector, frame) {
    const forward = Math.max(-1, Math.min(1, dot(vector, frame.normal)));
    if (forward < -precision) return null;
    const right = dot(vector, frame.right);
    const top = dot(vector, frame.top);
    const lateral = Math.hypot(right, top);
    if (lateral < precision) return { x: centerX, y: centerY };
    const scale = radius * Math.atan2(lateral, forward) / (halfPi * lateral);
    return { x: centerX + right * scale, y: centerY - top * scale };
  }

  function horizonPath(frame) {
    const horizontal = Math.hypot(frame.normal[0], frame.normal[1]);
    const axisBearing = Math.atan2(frame.normal[0], frame.normal[1]);
    const start = horizontal < precision ? 0 : axisBearing - halfPi;
    const span = horizontal < precision ? Math.PI * 2 : Math.PI;
    const points = [];
    for (let i = 0; i <= 90; i++) {
      const a = start + span * i / 90;
      const p = project([Math.sin(a), Math.cos(a), 0], frame);
      if (p) points.push(p.x.toFixed(2) + " " + p.y.toFixed(2));
    }
    return points.length ? "M" + points.join(" L") : "";
  }

  function createView(container) {
    container.innerHTML = `
      <div data-fisheye-card style="display:flex;align-items:center;gap:8px;min-width:0;width:100%;box-sizing:border-box">
        <svg viewBox="0 0 136 126" width="136" height="126" role="img" aria-label="Ideal fisheye preview"
             style="display:block;flex:0 1 48%;min-width:82px;max-width:146px;height:auto;overflow:visible">
          <circle cx="68" cy="63" r="47" fill="#edf5f6" stroke="#9db9bc" stroke-width="1.15"/>
          <g fill="none" stroke="#c5d8db" stroke-width=".8">
            <circle cx="68" cy="63" r="15.67"/>
            <circle cx="68" cy="63" r="31.33"/>
            <path d="M21 63H115 M68 16V110" stroke-dasharray="2.4 3.3"/>
          </g>
          <path data-horizon fill="none" stroke="#56878a" stroke-width="1.3"/>
          <circle cx="68" cy="63" r="1.7" fill="#447779"/>
          <g data-markers fill="#184b50" font-size="10" font-weight="650" text-anchor="middle" dominant-baseline="central"></g>
          <g data-sun>
            <path d="M0 -6V-4.9 M0 6V4.9 M-6 0H-4.9 M6 0H4.9 M-4.2 -4.2l.85 .85 M4.2 -4.2l-.85 .85 M4.2 4.2l-.85 -.85 M-4.2 4.2l.85 -.85"
                  fill="none" stroke="#8b560f" stroke-width="1.2" stroke-linecap="round"/>
            <circle data-sun-disc r="3.4" fill="#f7b944" stroke="#82520e" stroke-width=".9"/>
          </g>
        </svg>
        <div style="display:flex;flex:1;flex-direction:column;gap:4px;min-width:0;font-size:.78rem;line-height:1.36;color:#435b66">
          <strong style="font-size:.9rem;line-height:1.2;color:#1e3641">Phone image</strong>
          <span>Ideal 180° fisheye<br>Lens end at image top</span>
          <span data-sun-status style="margin-top:4px;font-weight:650;color:#725014"></span>
        </div>
      </div>`;
    const svg = container.querySelector("svg");
    const markerGroup = svg.querySelector("[data-markers]");
    const markers = cardinals.map(({ label }) => {
      const group = document.createElementNS(svg.namespaceURI, "g");
      group.setAttribute("data-cardinal", label);
      const point = document.createElementNS(svg.namespaceURI, "circle");
      point.setAttribute("r", "1.65");
      const letter = document.createElementNS(svg.namespaceURI, "text");
      letter.textContent = label;
      group.append(point, letter);
      markerGroup.append(group);
      return { group, point, letter };
    });
    return {
      svg, markers,
      horizon: svg.querySelector("[data-horizon]"),
      sun: svg.querySelector("[data-sun]"),
      sunDisc: svg.querySelector("[data-sun-disc]"),
      status: container.querySelector("[data-sun-status]")
    };
  }

  function placeCardinal(marker, point) {
    marker.group.setAttribute("visibility", point ? "visible" : "hidden");
    if (!point) return;
    marker.point.setAttribute("cx", point.x);
    marker.point.setAttribute("cy", point.y);
    const dx = point.x - centerX;
    const dy = point.y - centerY;
    const r = Math.hypot(dx, dy);
    // Labels sit outside the actual projected positions, so the dots identify the rays.
    marker.letter.setAttribute("x", point.x + (r < 4 ? 9 : dx * 8.5 / r));
    marker.letter.setAttribute("y", point.y + (r < 4 ? 0 : dy * 8.5 / r));
  }

  function update(container, state) {
    let view = mounts.get(container);
    if (!view || !container.contains(view.svg)) {
      view = createView(container);
      mounts.set(container, view);
    }
    const geometry = globalThis.AngleGeometry;
    const frame = geometry.equipmentFrame(state.phone);
    view.horizon.setAttribute("d", horizonPath(frame));
    const visibleCardinals = [];
    cardinals.forEach(({ label, vector }, index) => {
      const p = project(vector, frame);
      if (p) visibleCardinals.push(label);
      placeCardinal(view.markers[index], p);
    });
    const sunPoint = project(geometry.sunDirection(state.sun), frame);
    const belowHorizon = state.sun.elevation < 0;
    view.sun.setAttribute("visibility", sunPoint ? "visible" : "hidden");
    if (sunPoint) {
      view.sun.setAttribute("transform", `translate(${sunPoint.x} ${sunPoint.y})`);
      view.sunDisc.setAttribute("fill", belowHorizon ? "#edf5f6" : "#f7b944");
    }
    const status = sunPoint ? "Sun in frame" : "Sun outside frame";
    view.status.textContent = belowHorizon ? status + " · below horizon" : status;
    view.svg.setAttribute("aria-label", "Ideal fisheye projection. Image top is the lens end. Visible horizon bearings: " +
      visibleCardinals.join(", ") + ". " + view.status.textContent + ".");
  }

  globalThis.AngleInspection = Object.freeze({ update });
})();
