"""Independent read-back of native stages. Accepts package, Debug Data, or run directory."""
import base64
import json
import math
import re
import sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path
from zipfile import ZipFile
import xml.etree.ElementTree as ET
from PIL import Image

NS = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}


def instant(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def close(actual, expected):
    assert actual is not None and math.isclose(actual, expected, rel_tol=1e-11, abs_tol=1e-10), (actual, expected)


def column(address):
    value = 0
    for letter in re.match(r"[A-Z]+", address)[0]:
        value = value * 26 + ord(letter) - 64
    return value - 1


def sheets(path):
    """Decode by cell address so undefined factors remain blank without shifting columns."""
    with ZipFile(path) as archive:
        assert archive.testzip() is None, path
        shared = []
        if "xl/sharedStrings.xml" in archive.namelist():
            shared = ["".join(item.itertext()) for item in ET.fromstring(archive.read("xl/sharedStrings.xml"))]
        workbook = ET.fromstring(archive.read("xl/workbook.xml"))
        links = {link.get("Id"): link.get("Target") for link in ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))}
        for sheet in workbook.find("s:sheets", NS):
            target = links[sheet.get("{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id")]
            name = target.lstrip("/") if target.startswith("/") else "xl/" + target
            header, rows = None, []
            with archive.open(name) as stream:
                events = ET.iterparse(stream, events=("end",))
                for _, row in events:
                    if row.tag != "{" + NS["s"] + "}row":
                        continue
                    values = {}
                    for cell in row.findall("s:c", NS):
                        assert cell.find("s:f", NS) is None, (path, "unexpected formula")
                        raw, kind = cell.find("s:v", NS), cell.get("t")
                        value = "".join(cell.itertext()) if kind == "inlineStr" else shared[int(raw.text)] if kind == "s" else float(raw.text) if raw is not None else None
                        if isinstance(value, float):
                            assert math.isfinite(value), (path, value)
                        values[column(cell.get("r"))] = value
                    if header is None:
                        header = [values.get(i) for i in range(max(values) + 1)]
                        assert len(set(header)) == len(header), (path, "duplicate headers")
                    else:
                        rows.append({label: values.get(i) for i, label in enumerate(header)})
                    row.clear()
            yield sheet.get("name"), rows


def first(path):
    return next(sheets(path))[1]


def verify_profile(stage):
    calibration = json.loads((stage / "camera-profile.json").read_text(encoding="utf-8-sig"))["Calibration"]
    yaml = (stage / "calibration.yml").read_text(encoding="utf-8-sig")
    for native, field in [("principal_point", "PrincipalPoint"), ("poly_incident_angle_to_radius", "IncidentAngleToRadiusPolynomial")]:
        match = re.search(r"^" + native + r":\s*(\[[^\n]+\])", yaml, re.M)
        assert match and json.loads(match[1]) == calibration[field], (stage, field)
    size = re.search(r"^image_size:\s*(\[[^\n]+\])", yaml, re.M)
    if size:
        assert json.loads(size[1]) == [calibration["ImageWidth"], calibration["ImageHeight"]]
    return True


def verify_mask(stage):
    saved = json.loads((stage / "mask-details.json").read_text(encoding="utf-8-sig"))["Mask"]
    # Current metadata intentionally omits duplicated PNG bytes; older exports included them.
    if "Png" in saved:
        assert (stage / "sky-mask.png").read_bytes() == base64.b64decode(saved["Png"])
    with Image.open(stage / "sky-mask.png") as image:
        image = image.convert("L")
        assert image.size == (saved["Width"], saved["Height"])
        assert sum(image.histogram()[1:255]) == 0, (stage, "mask is not binary")
        return image.width * image.height


def verify_cardinal_overlay(run):
    """Independent camera-basis projection and native PNG/workbook read-back."""
    stage = run / "02-orientation"
    if (run / "cardinal-directions-details.json").exists():
        stage = run
    details_path = stage / "cardinal-directions-details.json"
    if not details_path.exists():
        return {}
    details = json.loads(details_path.read_text(encoding="utf-8-sig"))
    width, height = details["Width"], details["Height"]
    if (run / "01-calibration/camera-profile.json").exists():
        calibration = json.loads((run / "01-calibration/camera-profile.json").read_text(encoding="utf-8-sig"))["Calibration"]
        disk = json.loads((run / "02-sky-mask/mask-details.json").read_text(encoding="utf-8-sig"))["Mask"]["Disk"]
    else:
        saved = details["Projection"]
        calibration = {"ImageWidth": width, "ImageHeight": height,
                       "PrincipalPoint": [saved["PrincipalPointX"], saved["PrincipalPointY"]],
                       "IncidentAngleToRadiusPolynomial": saved["IncidentAngleToRadiusPolynomial"],
                       "MaximumIncidentAngleDegrees": saved["MaximumIncidentAngleDegrees"]}
        disk = {**saved["ImageDisk"], "Radius": saved["ImageDisk"]["RadiusPixels"]}
    assert (width, height) == (calibration["ImageWidth"], calibration["ImageHeight"])
    pose = details["Pose"]
    heading, tilt, roll = [math.radians(pose[key]) for key in ("ImageTopAzimuthDegrees", "TiltDegrees", "RollDegrees")]
    s, c, st, ct, sr, cr = math.sin(heading), math.cos(heading), math.sin(tilt), math.cos(tilt), math.sin(roll), math.cos(roll)
    west = (-c, s, 0)
    down = (-ct*s, -ct*c, st)
    axes = [tuple(cr*a + sr*b for a, b in zip(west, down)), tuple(-sr*a + cr*b for a, b in zip(west, down)), (st*s, st*c, ct)]

    def project(azimuth, zenith):
        a, z = math.radians(azimuth), math.radians(zenith)
        ray = (math.sin(z)*math.sin(a), math.sin(z)*math.cos(a), math.cos(z))
        x, y, forward = [sum(a*b for a, b in zip(ray, axis)) for axis in axes]
        theta = math.atan2(math.hypot(x, y), forward)
        radius = sum(coefficient * theta**power for power, coefficient in enumerate(calibration["IncidentAngleToRadiusPolynomial"]))
        lateral = math.hypot(x, y)
        px = calibration["PrincipalPoint"][0] + (radius*x/lateral if lateral > 1e-14 else 0)
        py = calibration["PrincipalPoint"][1] + (radius*y/lateral if lateral > 1e-14 else 0)
        valid = (math.degrees(theta) <= calibration["MaximumIncidentAngleDegrees"] + 1e-9 and
                 -1e-7 <= px <= width-1+1e-7 and -1e-7 <= py <= height-1+1e-7 and
                 math.hypot(px-disk["CenterX"], py-disk["CenterY"]) <= disk["Radius"] + 1e-7)
        return px, py, valid

    rows = first(stage / "cardinal-directions.xlsx")
    assert len(rows) == len(details["Markers"]) == 4
    with Image.open(stage / "cardinal-directions-overlay.png") as image:
        assert image.mode == "RGBA" and image.size == (width, height)
        red, green, blue, alpha = image.split()
        assert red.getextrema() == (255, 255) and green.getextrema() == (0, 0) and blue.getextrema() == (180, 180)
        assert alpha.getextrema()[0] == 0 and alpha.getextrema()[1] <= math.ceil(details["Options"]["Opacity"]*255)
        for marker, row, name, azimuth in zip(details["Markers"], rows, "NESW", (0, 90, 180, 270)):
            assert marker["Name"] == row["Direction"] == name
            assert marker["AzimuthDegrees"] == row["True azimuth (degrees)"] == azimuth
            assert str(marker["Visible"]).lower() == str(row["Visible tick"]).lower()
            assert marker["Status"] == row["Status"]
            for field, column_name in (("BoundaryZenithDegrees", "Endpoint zenith (degrees)"), ("BoundaryX", "Endpoint x (native pixels)"), ("BoundaryY", "Endpoint y (native pixels)"), ("LabelX", "Label baseline x"), ("LabelY", "Label baseline y")):
                assert marker[field] == row[column_name]
            if not marker["Visible"]:
                assert all(marker[key] is None for key in ("BoundaryX", "BoundaryY", "BoundaryZenithDegrees", "LabelX", "LabelY"))
                assert not marker["TickPoints"]
                continue
            x, y, valid = project(azimuth, marker["BoundaryZenithDegrees"])
            assert valid
            assert abs(x-marker["BoundaryX"]) < 1e-6 and abs(y-marker["BoundaryY"]) < 1e-6
            if marker["BoundaryZenithDegrees"] < 89.999:
                assert not project(azimuth, marker["BoundaryZenithDegrees"] + .001)[2], (name, "not at coverage boundary")
            assert len(marker["TickPoints"]) >= 2
            assert marker["TickPoints"][0] == {"X": marker["BoundaryX"], "Y": marker["BoundaryY"]}
            for point in marker["TickPoints"]:
                assert 0 <= point["X"] <= width-1 and 0 <= point["Y"] <= height-1
            ix, iy = round(x), round(y)
            assert alpha.crop((max(0, ix-3), max(0, iy-3), min(width, ix+4), min(height, iy+4))).getextrema()[1] > 0
    return {"CardinalMarkers": sum(m["Visible"] for m in details["Markers"]), "CardinalNativePixels": width*height, "CardinalProjectionVerified": True}


def verify_overlay(run, samples):
    stage = run / "04-solar-positions"
    path = stage / "sun-path-overlay.png"
    if not path.exists():
        return {}
    details = json.loads((stage / "sun-path-details.json").read_text(encoding="utf-8-sig"))
    vertices = [row for _, rows in sheets(stage / "sun-path-projection.xlsx") for row in rows]
    assert len(vertices) == details["RenderedVertexCount"]
    image = Image.open(path)
    assert image.mode == "RGBA" and image.size == (details["Width"], details["Height"])
    alpha = image.getchannel("A")
    assert alpha.getextrema()[1] <= math.ceil(details["Options"]["Opacity"] * 255)
    if vertices:
        assert alpha.getbbox() is not None
    calibration = json.loads((run / "01-calibration/camera-profile.json").read_text(encoding="utf-8-sig"))["Calibration"]
    settings = json.loads((run / "run.json").read_text(encoding="utf-8-sig"))["Settings"]
    heading, tilt, roll = (math.radians(v) for v in ((settings["BottomAzimuth"] + 180) % 360, settings["CameraTilt"], settings["CameraRoll"]))
    s, c, st, ct, sr, cr = math.sin(heading), math.cos(heading), math.sin(tilt), math.cos(tilt), math.sin(roll), math.cos(roll)
    xaxis, yaxis, zaxis = (-c, s, 0), (-ct*s, -ct*c, st), (st*s, st*c, ct)
    ax = tuple(cr*x + sr*y for x, y in zip(xaxis, yaxis))
    ay = tuple(-sr*x + cr*y for x, y in zip(xaxis, yaxis))
    previous = {}
    for vertex in vertices:
        timestamp = instant(vertex["Sample timestamp"])
        sample = samples[(vertex["Source interval ID"], timestamp)]
        assert vertex["Selected sample index (zero based)"] == sample["_selected_index"]
        az, zen = math.radians(sample["Azimuth (degrees)"]), math.radians(sample["Apparent zenith (degrees)"])
        assert 0 <= zen < math.pi/2
        ray = (math.sin(zen)*math.sin(az), math.sin(zen)*math.cos(az), math.cos(zen))
        a, b, z = (sum(x*y for x, y in zip(ray, axis)) for axis in (ax, ay, zaxis))
        lateral = math.hypot(a, b)
        theta = math.atan2(lateral, z)
        radius = sum(value * theta**power for power, value in enumerate(calibration["IncidentAngleToRadiusPolynomial"]))
        px = calibration["PrincipalPoint"][0] + (radius*a/lateral if lateral >= 1e-14 else 0)
        py = calibration["PrincipalPoint"][1] + (radius*b/lateral if lateral >= 1e-14 else 0)
        assert abs(vertex["Projected x (native pixels)"] - px) < 1e-7
        assert abs(vertex["Projected y (native pixels)"] - py) < 1e-7
        assert 0 <= px < image.width and 0 <= py < image.height
        track = vertex["Track ID"]
        if track in previous:
            before = previous[track]
            assert timestamp > instant(before["Sample timestamp"])
            assert vertex["Display local date"] == before["Display local date"]
        previous[track] = vertex
    assert len(previous) == details["TrackCount"]
    return {"SunPathVertices": len(vertices), "SunPathTracks": len(previous), "SunPathProjectionVerified": True}


def verify_calculation(run):
    panel = json.loads((run / "06-shading/panel-results.json").read_text(encoding="utf-8-sig"))
    rows = panel["Rows"]
    raw = {r["Interval ID"]: r for r in first(run / "03-irradiance/horizontal-irradiance.xlsx")}
    solar = first(run / "04-solar-positions/solar-positions.xlsx")
    before = first(run / "05-transposition/panel-unshaded.xlsx")
    assert len(rows) == len(solar) == len(before) and rows
    previous_end = None
    for r, b, s in zip(rows, before, solar):
        source = raw[r["IntervalId"]]
        assert b["Interval ID"] == s["Source interval ID"] == r["IntervalId"]
        for field, header in [("SourceStart", "Source start"), ("SourceEnd", "Source end")]:
            assert instant(source[header]) == instant(b[header]) == instant(s[header]) == instant(r[field])
        assert instant(source["Timestamp"]) == instant(b["Timestamp"]) == instant(s["Timestamp (local, UTC offset)"]) == instant(r["Timestamp"])
        start, end = instant(r["Start"]), instant(r["End"])
        assert instant(r["SourceStart"]) <= start < end <= instant(r["SourceEnd"])
        assert instant(b["Selected start"]) == instant(s["Selected start"]) == start
        assert instant(b["Selected end"]) == instant(s["Selected end"]) == end
        assert previous_end is None or previous_end == start, (run, "missing/stretched source interval")
        previous_end = end
        if source["Native cadence minutes"] is not None:
            close((instant(r["SourceEnd"]) - instant(r["SourceStart"])).total_seconds() / 60, source["Native cadence minutes"])
        close(r["Hours"], (end - start).total_seconds() / 3600)
        for key, header in [("BeforeDirect", "Direct on panel (W/m²)"), ("BeforeDiffuse", "Sky diffuse on panel (W/m²)"), ("BeforeTotal", "Total on panel (W/m²)")]:
            assert b[header] == r[key], (run, key)
    close(sum(r["BeforeTotal"] * r["Hours"] for r in rows) / 1000, panel["BeforeEnergy"])
    components, samples = defaultdict(lambda: [0.0, 0.0, 0.0, 0]), {}
    for name, detail in sheets(run / "05-transposition/panel-unshaded.xlsx"):
        if not name.startswith("Integration"):
            continue
        for sample in detail:
            key, weight = sample["Interval ID"], sample["Weight"]
            acc = components[key]
            acc[0] += weight
            acc[1] += sample["Direct (W/m²)"] * weight
            acc[2] += (sample["Isotropic diffuse (W/m²)"] + sample["Circumsolar diffuse (W/m²)"]) * weight
            acc[3] += 1
            identity = (key, instant(sample["Sample timestamp"]))
            assert identity not in samples
            sample["_selected_index"] = acc[3] - 1
            samples[identity] = sample
    assert set(components) == {r["IntervalId"] for r in rows}
    for r in rows:
        weight, direct, diffuse, _ = components[r["IntervalId"]]
        close(weight, 1); close(direct, r["BeforeDirect"]); close(diffuse, r["BeforeDiffuse"])
    solar_seen = set()
    for book in sorted((run / "04-solar-positions").glob("solar-integration-samples*.xlsx")):
        for sample in first(book):
            if sample["Quadrature domain"] != "Selected":
                continue
            key = (sample["Source interval ID"], instant(sample["Sample timestamp"]))
            assert key not in solar_seen
            solar_seen.add(key)
            transposed = samples[key]
            assert sample["Mean quadrature weight"] == transposed["Weight"]
            assert sample["Apparent zenith (degrees)"] == transposed["Apparent zenith (degrees)"]
            assert sample["Azimuth (degrees)"] == transposed["Azimuth (degrees)"]
            assert instant(sample["Selected start"]) <= key[1] < instant(sample["Selected end"])
    assert solar_seen == set(samples), (run, "solar/transposition sample identity mismatch")
    overlay_checks = verify_overlay(run, samples)
    shaded = all(r["AfterTotal"] is not None for r in rows)
    if shaded:
        after = first(run / "06-shading/panel-shaded.xlsx")
        factors = first(run / "06-shading/shading-correction-factors.xlsx")
        assert len(after) == len(factors) == len(rows)
        for r, a, f in zip(rows, after, factors):
            assert a["Interval ID"] == f["Interval ID"] == r["IntervalId"]
            for field, header in [("Timestamp", "Timestamp"), ("SourceStart", "Source start"), ("SourceEnd", "Source end"), ("Start", "Interval start"), ("End", "Interval end")]:
                assert instant(a[header]) == instant(f[header]) == instant(r[field])
            for field, header in [("AfterDirect", "Shaded direct on panel (W/m²)"), ("AfterDiffuse", "Shaded sky diffuse on panel (W/m²)"), ("AfterTotal", "Shaded total on panel (W/m²)"), ("UpperTotal", "Upper total (W/m²)")]:
                assert a[header] == r[field]
            for prefix, header, field in [("Direct", "Direct transmission", "DirectTransmission"), ("Diffuse", "Sky diffuse transmission", "DiffuseTransmission"), ("Total", "Total transmission", "TotalTransmission")]:
                expected = r["After" + prefix] / r["Before" + prefix] if r["Before" + prefix] > 0 else None
                assert f[header] == r[field] == expected
            assert f["Loss fraction"] == r["LossFraction"]
        sums = defaultdict(lambda: [0.0, 0.0, 0.0, 0])
        for book in sorted((run / "06-shading").glob("shading-visibility*.xlsx")):
            for v in first(book):
                key = v["Interval ID"]
                sample = samples[(key, instant(v["Substep timestamp"]))]
                weight = v["Quadrature weight"]
                assert weight == sample["Weight"]
                direct, isotropic, circumsolar = sample["Direct (W/m²)"], sample["Isotropic diffuse (W/m²)"], sample["Circumsolar diffuse (W/m²)"]
                disk, known = v["Direct visible fraction"], v["Direct observed fraction"]
                dome, covered = v["Isotropic visible fraction"], v["Isotropic observed fraction"]
                assert 0 <= disk <= known + 1e-10 <= 1 + 1e-10
                assert 0 <= dome <= covered + 1e-10 <= 1 + 1e-10
                sums[key][0] += direct * disk * weight
                sums[key][1] += (isotropic * dome + circumsolar * disk) * weight
                sums[key][2] += (direct * (disk + 1 - known) + isotropic * (dome + 1 - covered) + circumsolar * (disk + 1 - known)) * weight
                sums[key][3] += 1
        for r in rows:
            direct, diffuse, upper, count = sums[r["IntervalId"]]
            assert count == components[r["IntervalId"]][3]
            close(direct, r["AfterDirect"]); close(diffuse, r["AfterDiffuse"]); close(upper, r["UpperTotal"])
        close(sum(r["AfterTotal"] * r["Hours"] for r in rows) / 1000, panel["AfterEnergy"])
        transmission = panel["AfterEnergy"] / panel["BeforeEnergy"] if panel["BeforeEnergy"] > 0 else None
        assert transmission == panel["EnergyTransmission"]
        if transmission is not None:
            close(100 * (1 - transmission), panel["LossPercent"])
    else:
        assert panel["AfterEnergy"] is None and panel["LossPercent"] is None
        assert not (run / "06-shading/panel-shaded.xlsx").exists()
    return {"ExactResultRows": len(rows), "IntegrationSamples": len(samples), "ShadingAvailable": shaded, **overlay_checks}


def main(root):
    root = root.resolve()
    directory = root if root.name == "Debug Data" or (root / "run.json").exists() else root / "Debug Data"
    runs = [directory] if (directory / "run.json").exists() else sorted(p.parent for p in directory.glob("*/run.json"))
    assert runs, (directory, "no run manifests")
    checks = []
    for run in runs:
        manifest = json.loads((run / "run.json").read_text(encoding="utf-8-sig"))
        entry, listed = {"Run": run.name, "Status": manifest["Status"]}, set()
        for stage in manifest["Stages"].values():
            for artifact in stage["Artifacts"]:
                path = (run / artifact).resolve()
                assert path.is_relative_to(run.resolve()) and path.is_file(), (run, artifact)
                listed.add(path)
        books = sorted(run.rglob("*.xlsx"))
        for book in books:
            # Numerical workbooks are fully read during value checks below; don't parse giant diagnostics twice.
            if manifest["Status"] != "Complete" or not run.name.startswith("calculation-") or book.parent.name == "01-calibration":
                list(sheets(book))
            if manifest["Status"] == "Complete":
                assert book.resolve() in listed, (run, "unlisted workbook", book)
        entry["Workbooks"] = len(books)
        if manifest["Status"] == "Complete":
            if (run / "06-shading/panel-results.json").exists():
                entry.update(verify_calculation(run))
            entry.update(verify_cardinal_overlay(run))
            if (run / "01-calibration/camera-profile.json").exists():
                entry["NativeCalibrationVerified"] = verify_profile(run / "01-calibration")
            if (run / "02-sky-mask/sky-mask.png").exists():
                entry["BinaryMaskPixels"] = verify_mask(run / "02-sky-mask")
        checks.append(entry)
    completed = sum(c["Status"] == "Complete" for c in checks)
    assert completed, "No completed native runs verified"
    report = {"Passed": True, "CompletedRuns": completed, "Checks": checks}
    (root / "workbook-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main(Path(sys.argv[1]))
