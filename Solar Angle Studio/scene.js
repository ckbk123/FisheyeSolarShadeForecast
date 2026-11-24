/* Orthographic 3D scene. World space is east, north, up, in the geometry module's order. */
(function() {
"use strict";

const G = globalThis.AngleGeometry;
const rad = Math.PI / 180;
const origin = [0, 0, 0];
const up = [0, 0, 1];
const colors = {
  panel: "#3d62b2",
  phone: "#087e77",
  sun: "#af6b0c"
};
const ringRadius = 3.45;
const sunDistance = 3.82;
const plus = (a, b) => a.map((v, i) => v + b[i]);
const times = (a, k) => a.map(v => v * k);
const move = (point, direction, distance) => plus(point, times(direction, distance));
const dot = (a, b) => a.reduce((sum, value, i) => sum + value * b[i], 0);
const clamp = (n, low, high) => Math.max(low, Math.min(high, n));
const heading = degrees => [Math.sin(degrees * rad), Math.cos(degrees * rad), 0];
const onRing = (radius, degrees, height = 0) => [...heading(degrees).slice(0, 2).map(n => n * radius), height];
const onFace = (center, frame, x, y, z = 0) =>
    move(move(move(center, frame.right, x), frame.top, y), frame.normal, z);
const angleText = n => (Math.abs(n) < 0.05 ? "0" : String(Number(n.toFixed(1))).replace("-", "−")) + "°";

function samples(start, end, makePoint, step = 4) {
  const count = Math.max(1, Math.ceil(Math.abs(end - start) / step));
  return Array.from({length: count + 1}, (_, i) => makePoint(start + (end - start) * i / count));
}

function create(container, onViewChange) {
  const canvas = document.createElement("canvas");
  canvas.setAttribute("role", "img");
  canvas.setAttribute(
      "aria-label",
      "3D solar orientation scene. Drag or use arrow keys to orbit. Scroll or use plus and minus to zoom. Home resets the view.");
  canvas.tabIndex = 0;
  Object.assign(
      canvas.style, {display: "block", width: "100%", height: "100%", cursor: "grab", touchAction: "none"});
  container.appendChild(canvas);
  const ctx = canvas.getContext("2d");
  let state = {
    panel: {azimuth: 0, tilt: 28},
    phone: {azimuth: 0, tilt: 28, imageTopBearing: 0},
    sun: {azimuth: 135, elevation: 42}
  };
  let options = {active: "panel", labels: true, angles: true, placed: true};
  let yaw = 221, pitch = 28, zoom = 1, queued = false;
  let width, height, scale, centerX, centerY, eye, screenRight, screenUp;
  let labels = [], occupied = [];

  function project(v) {
    return {x: centerX + scale * dot(v, screenRight), y: centerY - scale * dot(v, screenUp)};
  }

  function path(points, close = false) {
    ctx.beginPath();
    points.forEach((p, i) => {
      const s = project(p);
      i ? ctx.lineTo(s.x, s.y) : ctx.moveTo(s.x, s.y);
    });
    if (close)
      ctx.closePath();
  }

  function line(points, color, thickness = 1, dash = []) {
    path(points);
    ctx.strokeStyle = color;
    ctx.lineWidth = thickness;
    ctx.setLineDash(dash);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  function polygon(points, fill, outline = null, thickness = 1) {
    path(points, true);
    ctx.fillStyle = fill;
    ctx.fill();
    if (outline) {
      ctx.strokeStyle = outline;
      ctx.lineWidth = thickness;
      ctx.stroke();
    }
  }

  function dotAt(point, radius, fill) {
    const s = project(point);
    ctx.beginPath();
    ctx.arc(s.x, s.y, radius, 0, Math.PI * 2);
    ctx.fillStyle = fill;
    ctx.fill();
  }

  function tag(text, worldAnchor, color, dx = 8, dy = -14, compact = false) {
    labels.push({text, anchor: project(worldAnchor), color, dx, dy, compact});
  }

  function paintTags() {
    const overlap = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) *
        Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
    labels.forEach(({text, anchor, color, dx, dy, compact}) => {
      ctx.font = compact ? "600 11px 'Segoe UI', sans-serif" : "600 12px 'Segoe UI', sans-serif";
      const w = Math.ceil(ctx.measureText(text).width + (compact ? 12 : 16)), h = compact ? 19 : 22;
      const baseX = dx >= 0 ? anchor.x + dx : anchor.x + dx - w;
      const baseY = anchor.y + dy - h / 2;
      const opposite = dx >= 0 ? -w - 14 : w + 14;
      const candidates = [
        [0, 0], [0, -23], [0, 23], [opposite, 0], [opposite, -23], [opposite, 23], [0, -44], [0, 44], [24, -20],
        [-24, 18], [opposite, -44], [opposite, 44], [36, 0], [-36, 0]
      ];
      let best = null, bestCost = Infinity;
      for (const [ox, oy] of candidates) {
        const x = clamp(baseX + ox, 6, width - w - 6), y = clamp(baseY + oy, 6, height - h - 6);
        const box = {x: x - 3, y: y - 3, w: w + 6, h: h + 6};
        const collision =
            occupied.reduce((sum, obstacle) => sum + overlap(box, obstacle) * (obstacle.weight ?? 1), 0);
        const cost = collision * 15 + Math.hypot(x - baseX, y - baseY);
        if (cost < bestCost) {
          bestCost = cost;
          best = {x, y, w, h};
        }
      }
      const {x, y} = best;
      const bx = clamp(anchor.x, x, x + w), by = clamp(anchor.y, y, y + h);
      if (Math.hypot(anchor.x - bx, anchor.y - by) > 5) {
        ctx.strokeStyle = color + "98";
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(anchor.x, anchor.y);
        ctx.lineTo(bx, by);
        ctx.stroke();
      }
      ctx.beginPath();
      ctx.roundRect(x, y, w, h, compact ? 4 : 5);
      ctx.fillStyle = compact ? "#ffffffed" : "#f9fcffed";
      ctx.fill();
      ctx.strokeStyle = color + "44";
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.fillStyle = color;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(text, x + w / 2, y + h / 2 - .25);
      occupied.push({x: x - 2, y: y - 2, w: w + 4, h: h + 4});
    });
  }

  function arrow(points, color, weight, headSize) {
    line(points, color, weight);
    const tip = project(points.at(-1));
    let base = project(points.at(-2)), i = points.length - 2;
    while (i > 0 && Math.hypot(tip.x - base.x, tip.y - base.y) < headSize)
      base = project(points[--i]);
    const dx = tip.x - base.x, dy = tip.y - base.y, len = Math.hypot(dx, dy);
    if (len < 1) {
      dotAt(points.at(-1), headSize / 2, color);
      return;
    }
    const ux = dx / len, uy = dy / len;
    ctx.beginPath();
    ctx.moveTo(tip.x, tip.y);
    ctx.lineTo(tip.x - headSize * ux + headSize * .43 * uy, tip.y - headSize * uy - headSize * .43 * ux);
    ctx.lineTo(tip.x - headSize * ux - headSize * .43 * uy, tip.y - headSize * uy + headSize * .43 * ux);
    ctx.closePath();
    ctx.fillStyle = color;
    ctx.fill();
  }

  function grid() {
    // Distance fading keeps the working circle legible, including in the plan view.
    for (let index = -18; index <= 18; index++) {
      const v = index * .5, major = index % 2 === 0;
      const opacity = Math.max(.025, 1 - Math.abs(v) / 10) * (major ? .23 : .14);
      for (const points of [[[v, -10, 0], [v, 10, 0]], [[-10, v, 0], [10, v, 0]]]) {
        const a = project(points[0]), b = project(points[1]);
        const grad = ctx.createLinearGradient(a.x, a.y, b.x, b.y);
        grad.addColorStop(0, "rgba(117,142,161,0)");
        grad.addColorStop(.4, "rgba(117,142,161," + opacity + ")");
        grad.addColorStop(.6, "rgba(117,142,161," + opacity + ")");
        grad.addColorStop(1, "rgba(117,142,161,0)");
        line(points, grad, major ? .9 : .7);
      }
    }
    const ring = samples(0, 360, a => onRing(ringRadius, a), 2);
    line(ring, "#91a4b4", 1.3);
    line(samples(0, 360, a => onRing(ringRadius - .14, a)), "#bdcad4", .8);
    for (let a = 0; a < 360; a += 5) {
      const major = a % 30 === 0;
      line(
          [onRing(ringRadius - (major ? .16 : .075), a), onRing(ringRadius + (major ? .06 : 0), a)],
          major ? "#718695" : "#a4b5c1", major ? 1.2 : .85);
    }
    line([onRing(ringRadius, 0), onRing(ringRadius, 180)], "#8fa6b6", 1, [5, 5]);
    line([onRing(ringRadius, 90), onRing(ringRadius, 270)], "#8fa6b6", 1, [5, 5]);
    line([origin, [0, 0, 2.92]], "#92a5b2", 1.15, [4, 4]);
    if (options.labels && Math.abs(pitch) < 79) {
      const s = project([0, 0, 2.93]);
      ctx.font = "600 10px 'Segoe UI', sans-serif";
      ctx.textAlign = "center";
      ctx.textBaseline = "bottom";
      ctx.fillStyle = "#5c7484";
      ctx.fillText("UP", s.x, s.y - 3);
    }
    ["N", "E", "S", "W"].forEach((letter, i) => {
      const angle = i * 90;
      if (i === 0)
        polygon(
            [onRing(ringRadius + .14, 0), onRing(ringRadius - .08, -1.6), onRing(ringRadius - .08, 1.6)],
            "#385163");
      const s = project(onRing(ringRadius + .31, angle));
      // In elevation view east and west coincide at the ground origin.
      if (Math.abs(pitch) < 2 && Math.abs(dot(heading(angle), eye)) > .995)
        return;
      ctx.font = "700 15px 'Segoe UI', sans-serif";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.lineWidth = 3;
      ctx.strokeStyle = "#edf3f7";
      ctx.strokeText(letter, s.x, s.y);
      ctx.fillStyle = i === 0 ? "#233d50" : "#526c7e";
      ctx.fillText(letter, s.x, s.y);
      occupied.push({x: s.x - 8, y: s.y - 9, w: 16, h: 18});
    });
  }

  function groundArc(values) {
    const active = options.active;
    const flatHeading = active === "phone" && state.phone.tilt === 0;
    const fromNorth = active === "sun" || flatHeading;
    const bearing = flatHeading ? values.actualImageTopBearing :
        active === "phone" ? values.phoneBearing : active === "panel" ? values.panelBearing : values.sunBearing;
    if (bearing == null) return;
    const start = fromNorth ? 0 : 180;
    const delta = fromNorth ? bearing : state[active].azimuth;
    const color = colors[active], radius = 2.95;
    const arc = samples(start, start + delta, a => onRing(radius, a, .028), 2.5);
    if (Math.abs(delta) > .02) {
      polygon([[0, 0, .028], ...arc], color + "0b");
      arrow(arc, color + "bc", 1.7, 6.5);
    } else
      dotAt(arc[0], 3, color);
    line([[0, 0, .028], onRing(radius + .12, start, .028)], color + "78", 1, [3, 4]);
    line([[0, 0, .03], onRing(radius + .12, bearing, .03)], color + "9c", 1.4);
    const mid = onRing(radius, start + delta / 2, .03);
    tag((flatHeading ? "Image top " : "Az ") + angleText(delta), mid, color, 8, 9, true);
  }

  function contactShadow(center, frame, w, h) {
    const points = [[-w, -h], [w, -h], [w, h], [-w, h]].map(([x, y]) => {
      const p = onFace(center, frame, x * .52, y * .52);
      return [p[0], p[1], 0];
    });
    ctx.save();
    ctx.filter = "blur(" + Math.max(2, scale * .06) + "px)";
    polygon(points, "#35454b20");
    ctx.restore();
  }

  function localFace(center, frame, paint) {
    const p = project(center), x = project(move(center, frame.right, 1)),
          y = project(move(center, frame.top, 1));
    ctx.save();
    ctx.transform(x.x - p.x, x.y - p.y, y.x - p.x, y.y - p.y, p.x, p.y);
    paint();
    ctx.restore();
  }

  function reserveModel(center, frame, w, h, weight) {
    const corners =
        [[-w, -h], [w, -h], [w, h], [-w, h]].map(([x, y]) => project(onFace(center, frame, x / 2, y / 2)));
    const xs = corners.map(p => p.x), ys = corners.map(p => p.y);
    const x = Math.min(...xs), y = Math.min(...ys);
    occupied.push({x, y, w: Math.max(...xs) - x, h: Math.max(...ys) - y, weight});
  }

  function slab(center, frame, w, h, depth, edgeColor, underside, front) {
    const outline = [[-w / 2, -h / 2], [w / 2, -h / 2], [w / 2, h / 2], [-w / 2, h / 2]];
    const sideNormals = [times(frame.top, -1), frame.right, frame.top, times(frame.right, -1)];
    outline.forEach(([x, y], i) => {
      if (dot(sideNormals[i], eye) <= 0)
        return;
      const [nx, ny] = outline[(i + 1) % 4];
      polygon(
          [
            onFace(center, frame, x, y, -depth), onFace(center, frame, nx, ny, -depth),
            onFace(center, frame, nx, ny), onFace(center, frame, x, y)
          ],
          edgeColor, "#455667", .65);
    });
    if (dot(frame.normal, eye) >= 0)
      localFace(center, frame, front);
    else
      polygon(outline.map(([x, y]) => onFace(center, frame, x, y, -depth)), underside, "#687b88", 1.3);
  }

  function panelStand(center, frame) {
    const supports = [];
    [-1, 1].forEach(side => {
      const footPoints = [-.62, .62].map(y => onFace(center, frame, side * .94, y, -.09));
      const feet = footPoints.map(p => [p[0], p[1], .04]);
      supports.push(
          [feet[0], footPoints[0]], [feet[1], footPoints[1]], [feet[0], footPoints[1]], [feet[0], feet[1]]);
    });
    supports.sort((a, b) => dot(a[0], eye) - dot(b[0], eye)).forEach(points => {
      line(points, "#80949e", Math.max(2.2, scale * .056));
      line(points, "#b7c7ce", Math.max(.9, scale * .021));
    });
  }

  function panelBody(center, frame) {
    const w = 2.72, h = 1.7;
    slab(center, frame, w, h, .09, "#9baebc", "#becbd2", () => {
      ctx.fillStyle = "#b5c3cf";
      ctx.fillRect(-w / 2, -h / 2, w, h);
      ctx.strokeStyle = "#4c6577";
      ctx.lineWidth = .017;
      ctx.strokeRect(-w / 2, -h / 2, w, h);
      const innerW = w - .095, innerH = h - .095, nx = 9, ny = 5, cw = innerW / nx, ch = innerH / ny;
      ctx.fillStyle = "#96adc1";
      ctx.fillRect(-innerW / 2, -innerH / 2, innerW, innerH);
      const cellColors = ["#294765", "#304f70", "#294968", "#355577", "#294968"];
      for (let ix = 0; ix < nx; ix++)
        for (let iy = 0; iy < ny; iy++) {
          const x = -innerW / 2 + ix * cw + .006, y = -innerH / 2 + iy * ch + .006;
          const cellW = cw - .012, cellH = ch - .012, cut = .024;
          ctx.beginPath();
          [[cut, 0], [cellW - cut, 0], [cellW, cut], [cellW, cellH - cut], [cellW - cut, cellH], [cut, cellH],
           [0, cellH - cut], [0, cut]]
              .forEach(([px, py], i) => i ? ctx.lineTo(x + px, y + py) : ctx.moveTo(x + px, y + py));
          ctx.closePath();
          ctx.fillStyle = cellColors[(ix * 7 + iy * 3) % 5];
          ctx.fill();
          ctx.strokeStyle = "#95adc570";
          ctx.lineWidth = .005;
          ctx.beginPath();
          for (let bus = 1; bus <= 3; bus++) {
            const px = x + cellW * bus / 4;
            ctx.moveTo(px, y + .01);
            ctx.lineTo(px, y + cellH - .01);
          }
          ctx.stroke();
        }
      const gloss = ctx.createLinearGradient(0, -h / 2, 0, h / 2);
      gloss.addColorStop(0, "#829bbe03");
      gloss.addColorStop(1, "#c0e5ff2b");
      ctx.fillStyle = gloss;
      ctx.fillRect(-innerW / 2, -innerH / 2, innerW, innerH);
    });
  }

  function phoneBody(center, frame) {
    const w = .50, h = .89;
    slab(center, frame, w, h, .052, "#418a88", "#537778", () => {
      const round = (x, y, ww, hh, r, fill) => {
        ctx.beginPath();
        ctx.roundRect(x, y, ww, hh, r);
        ctx.fillStyle = fill;
        ctx.fill();
      };
      const circle = (x, y, r, fill) => {
        ctx.beginPath();
        ctx.arc(x, y, r, 0, 2 * Math.PI);
        ctx.fillStyle = fill;
        ctx.fill();
      };
      round(-w / 2, -h / 2, w, h, .06, "#193e49");
      round(-w / 2 + .018, -h / 2 + .018, w - .036, h - .036, .05, "#b3e1da");
      round(-w / 2 + .035, -h / 2 + .035, w - .07, h - .07, .04, "#248780");
      round(-.139, .065, .278, .292, .047, "#184d50");
      circle(0, .22, .121, "#72ada8");
      circle(0, .22, .096, "#123c43");
      circle(0, .22, .072, "#1a505e");
      circle(-.017, .24, .04, "#173444");
      circle(-.036, .252, .018, "#bee8e6");
      ctx.strokeStyle = "#a8e3da";
      ctx.lineWidth = .015;
      ctx.beginPath();
      ctx.moveTo(-.044, -.12);
      ctx.lineTo(0, -.059);
      ctx.lineTo(.044, -.12);
      ctx.stroke();
    });
  }

  function sunBody(position, vector) {
    const floor = [position[0], position[1], 0];
    line([origin, position], "#b57811bd", 1.8, [6, 4]);
    line([floor, position], "#ba934b9e", 1.15, [3, 4]);
    if (Math.abs(state.sun.elevation) > .01 && Math.abs(state.sun.elevation) < 89.99) {
      dotAt(floor, 2.5, "#c7943f");
    }
    const s = project(position), r = clamp(scale * .15, 7, 14);
    occupied.push({x: s.x - r * 1.4, y: s.y - r * 1.4, w: r * 2.8, h: r * 2.8});
    const halo = ctx.createRadialGradient(s.x, s.y, r * .55, s.x, s.y, r * 2.4);
    halo.addColorStop(0, "#ffce5f63");
    halo.addColorStop(1, "#ffda7100");
    ctx.fillStyle = halo;
    ctx.fillRect(s.x - r * 2.4, s.y - r * 2.4, r * 4.8, r * 4.8);
    for (let i = 0; i < 8; i++) {
      const a = (i * 45 + 22.5) * rad, c = Math.cos(a), sn = Math.sin(a);
      ctx.beginPath();
      ctx.moveTo(s.x + c * r * 1.35, s.y + sn * r * 1.35);
      ctx.lineTo(s.x + c * r * 1.70, s.y + sn * r * 1.70);
      ctx.lineWidth = 1.6;
      ctx.strokeStyle = "#c28b26";
      ctx.stroke();
    }
    const light = ctx.createRadialGradient(s.x - r * .38, s.y - r * .42, r * .08, s.x, s.y, r);
    light.addColorStop(0, "#fff3b9");
    light.addColorStop(.55, "#ffd66f");
    light.addColorStop(1, "#e9a731");
    dotAt(position, r, light);
    ctx.lineWidth = 1;
    ctx.strokeStyle = "#b8862c";
    ctx.stroke();
    if (options.labels)
      tag(vector[2] < -.000001 ? "Sun · below horizon" : "Sun", position, colors.sun, r + 8, -16);
  }

  function tiltArc({base, zero, toward, angle, radius, color, prefix, dx, dy}) {
    const point = a => move(move(base, zero, Math.cos(a * rad) * radius), toward, Math.sin(a * rad) * radius);
    const arc = samples(0, angle, point, 2.5);
    line([base, point(0)], color + "bd", 1.3, [3, 3]);
    line([base, point(angle)], color + "bc", 1.3);
    if (Math.abs(angle) > .03) {
      polygon([base, ...arc], color + "1c");
      line(arc, "#f4f8fab0", 3.5);
      arrow(arc, color, 1.9, 6.5);
    }
    tag(prefix + " " + angleText(angle), point(angle / 2), color, dx, dy, true);
  }

  function angleGuide(values, panels, phones, pc, fc) {
    const active = options.active;
    if (active === "sun") {
      if (values.sunBearing == null) {
        // The elevation and zenith remain defined at the poles. No azimuth plane is distinguished.
        tag("Elev " + angleText(state.sun.elevation) + " · Zenith " + angleText(values.zenith),
            times(values.sunVector, sunDistance * .65), colors.sun, 13, -4, true);
        return;
      }
      const horizon = heading(state.sun.azimuth);
      tiltArc({
        base: origin,
        zero: horizon,
        toward: up,
        angle: state.sun.elevation,
        radius: 2.18,
        color: colors.sun,
        prefix: "Elev",
        dx: 8,
        dy: 12
      });
      tiltArc({
        base: origin,
        zero: up,
        toward: horizon,
        angle: values.zenith,
        radius: 1.45,
        color: colors.sun,
        prefix: "Zenith",
        dx: -6,
        dy: -13
      });
    } else {
      const isPanel = active === "panel";
      const trueBearing = (isPanel ? values.panelBearing : values.phoneBearing) ??
          ((isPanel ? state.panel.azimuth : values.actualImageTopBearing) + 180);
      const planeTilt = isPanel ? state.panel.tilt : values.phoneTilt;
      // The selected plane meets its horizontal reference along the level (cross-slope) direction.
      const uphill = heading(trueBearing + 180);
      const slope = plus(times(uphill, Math.cos(planeTilt * rad)), times(up, Math.sin(planeTilt * rad)));
      const f = isPanel ? panels : phones, c = isPanel ? pc : fc;
      const base = move(move(c, f.right, isPanel ? -1.67 : -.37), slope, isPanel ? -.78 : -.33);
      tiltArc({
        base,
        zero: uphill,
        toward: up,
        angle: planeTilt,
        radius: isPanel ? 1.22 : .72,
        color: colors[active],
        prefix: "Tilt",
        dx: 11,
        dy: 17
      });
    }
  }

  function setProjection(sun) {
    const a = yaw * rad, e = pitch * rad;
    eye = [Math.sin(a) * Math.cos(e), Math.cos(a) * Math.cos(e), Math.sin(e)];
    screenRight = [-Math.cos(a), Math.sin(a), 0];
    screenUp = [-Math.sin(a) * Math.sin(e), -Math.cos(a) * Math.sin(e), Math.cos(e)];
    // Fit the whole compass and sky marker before applying any user-controlled zoom.
    const bounds = [...samples(0, 360, v => onRing(ringRadius + .34, v)), sun, [0, 0, 3.03]];
    const horizontal = bounds.map(p => dot(p, screenRight)), vertical = bounds.map(p => dot(p, screenUp));
    const xMin = Math.min(...horizontal), xMax = Math.max(...horizontal);
    const yMin = Math.min(...vertical), yMax = Math.max(...vertical);
    const horizontalMargin = width < 450 ? 29 : 57;
    const verticalMargin = height < 340 ? 28 : 34;
    scale = Math.max(
                1,
                Math.min(
                    (width - 2 * horizontalMargin) / (xMax - xMin),
                    (height - 2 * verticalMargin) / Math.max(4.2, yMax - yMin))) *
        zoom;
    centerX = width / 2 - (xMin + xMax) / 2 * scale;
    centerY = height / 2 + (yMin + yMax) / 2 * scale;
  }

  function render() {
    queued = false;
    const box = canvas.getBoundingClientRect();
    width = box.width;
    height = box.height;
    if (!width || !height)
      return;
    const ratio = Math.min(window.devicePixelRatio || 1, 2.5);
    const pixelsW = Math.round(width * ratio), pixelsH = Math.round(height * ratio);
    if (canvas.width !== pixelsW || canvas.height !== pixelsH) {
      canvas.width = pixelsW;
      canvas.height = pixelsH;
    }
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    const gradient = ctx.createLinearGradient(0, 0, 0, height);
    gradient.addColorStop(0, "#f5f8fa");
    gradient.addColorStop(.67, "#edf3f6");
    gradient.addColorStop(1, "#e7eff4");
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, width, height);
    ctx.lineJoin = "round";
    ctx.lineCap = "round";
    labels = [];
    occupied = [];
    const values = G.sceneValues(state), panels = G.framePanel(state.panel), phones = G.framePhone(state.phone);
    const sun = times(values.sunVector, sunDistance);
    setProjection(sun);
    const pc = options.placed ? [0, 0, .91] : [-.56, .17, .91];
    const fc = options.placed ? onFace(pc, panels, .78, .035, .077) : [1.42, -.42, .73];
    grid();
    if (options.angles)
      groundArc(values);
    contactShadow(pc, panels, 2.72, 1.7);
    if (!options.placed)
      contactShadow(fc, phones, .50, .89);
    sunBody(sun, values.sunVector);
    panelStand(pc, panels);
    const models = [
      {depth: dot(pc, eye), draw: () => panelBody(pc, panels)},
      {depth: dot(fc, eye), draw: () => phoneBody(fc, phones)}
    ];
    if (options.placed) {
      panelBody(pc, panels);
      if (dot(panels.normal, eye) >= -.045)
        phoneBody(fc, phones);
    } else
      models.sort((a, b) => a.depth - b.depth).forEach(m => m.draw());
    reserveModel(pc, panels, 2.42, 1.43, .12);
    reserveModel(fc, phones, .50, .89, .85);
    const panelBase = onFace(pc, panels, -.60, .00, .018), phoneBase = onFace(fc, phones, 0, .22, .022);
    const panelTip = move(panelBase, panels.normal, 1.67), phoneTip = move(phoneBase, phones.normal, 1.30);
    arrow([panelBase, panelTip], colors.panel, 2.8, 10);
    arrow([phoneBase, phoneTip], colors.phone, 2.8, 10);
    dotAt(panelBase, 2.3, colors.panel);
    dotAt(phoneBase, 2.3, colors.phone);
    if (options.labels) {
      tag("Panel normal", panelTip, colors.panel, 9, -14);
      tag("Lens axis", phoneTip, colors.phone, -9, -14);
    }
    if (options.active === "phone") {
      const begin = onFace(fc, phones, 0, -.22, .035), end = onFace(fc, phones, 0, .74, .035);
      arrow([begin, end], "#087e77", 1.8, 7);
      if (options.labels)
        tag("Image top", end, colors.phone, -8, 12);
    }
    if (options.angles)
      angleGuide(values, panels, phones, pc, fc);
    paintTags();
  }

  function invalidate() {
    if (!queued) {
      queued = true;
      requestAnimationFrame(render);
    }
  }
  function changedView(name) {
    if (onViewChange)
      onViewChange(name);
    invalidate();
  }
  function setView(name) {
    if (name === "top") {
      yaw = 180;
      pitch = 90;
    } else if (name === "side") {
      yaw = 90;
      pitch = 0;
    } else {
      yaw = 221;
      pitch = 28;
      name = "orbit";
    }
    zoom = 1;
    changedView(name);
  }
  function zoomBy(factor) {
    if (Number.isFinite(factor) && factor > 0) {
      zoom = clamp(zoom * factor, .55, 2.7);
      invalidate();
    }
  }
  const touches = new Map();
  function distance() {
    const [a, b] = [...touches.values()];
    return Math.hypot(a.x - b.x, a.y - b.y);
  }
  canvas.addEventListener("pointerdown", e => {
    if (e.button !== 0 && e.pointerType === "mouse")
      return;
    touches.set(e.pointerId, {x: e.clientX, y: e.clientY});
    canvas.setPointerCapture(e.pointerId);
    canvas.style.cursor = "grabbing";
  });
  canvas.addEventListener("pointermove", e => {
    const prev = touches.get(e.pointerId);
    if (!prev)
      return;
    const oldDistance = touches.size === 2 ? distance() : 0;
    touches.set(e.pointerId, {x: e.clientX, y: e.clientY});
    if (touches.size === 2) {
      const next = distance();
      if (oldDistance > 0)
        zoomBy(next / oldDistance);
    } else if (touches.size === 1) {
      yaw -= .43 * (e.clientX - prev.x);
      pitch = clamp(pitch + .34 * (e.clientY - prev.y), -20, 90);
      changedView("orbit");
    }
  });
  function release(e) {
    touches.delete(e.pointerId);
    if (!touches.size)
      canvas.style.cursor = "grab";
  }
  canvas.addEventListener("pointerup", release);
  canvas.addEventListener("pointercancel", release);
  canvas.addEventListener("lostpointercapture", release);
  canvas.addEventListener("wheel", e => {
    e.preventDefault();
    zoomBy(Math.exp(-clamp(e.deltaY, -150, 150) * .0017));
  }, {passive: false});
  canvas.addEventListener("keydown", e => {
    if (e.key === "Home")
      setView("orbit");
    else if (e.key === "+" || e.key === "=")
      zoomBy(1.15);
    else if (e.key === "-")
      zoomBy(1 / 1.15);
    else if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(e.key)) {
      if (e.key === "ArrowLeft")
        yaw += 8;
      if (e.key === "ArrowRight")
        yaw -= 8;
      if (e.key === "ArrowUp")
        pitch = clamp(pitch + 6, -20, 90);
      if (e.key === "ArrowDown")
        pitch = clamp(pitch - 6, -20, 90);
      changedView("orbit");
    } else
      return;
    e.preventDefault();
  });
  new ResizeObserver(invalidate).observe(container);
  invalidate();
  return {
    update(next, settings) {
      state = next;
      options = {...options, ...settings};
      invalidate();
    },
    setView,
    zoomBy,
    resetView() {
      setView("orbit");
    }
  };
}
globalThis.AngleScene = {create};
})();
