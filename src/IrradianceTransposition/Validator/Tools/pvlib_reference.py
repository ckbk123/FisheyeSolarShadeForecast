"""Independent oracle, development only: pvlib==0.15.2, openpyxl. No production dependency.

generate: fixed daylight cases plus spline-knot neighbourhoods and deterministic random cases.
verify: compare all C# interval audit rows to vectorized pvlib and read every XLSX cell independently.
"""
import argparse
import gzip
from datetime import datetime, timezone
import json
from pathlib import Path
import numpy as np
import pvlib
import openpyxl
import pandas as pd


def generate(path):
    assert pvlib.__version__ == "0.15.2"
    cases = []

    def add(tilt, azimuth, zenith, sun_azimuth, dni, dhi, extra):
        args = (tilt, azimuth, dhi, dni, extra, zenith, sun_azimuth)
        cases.append(dict(tilt=float(tilt), azimuth=float(azimuth), zenith=float(zenith),
                          sunAzimuth=float(sun_azimuth), dni=float(dni), dhi=float(dhi), extra=float(extra),
                          direct=float(pvlib.irradiance.beam_component(tilt, azimuth, zenith, sun_azimuth, dni)),
                          hay=float(pvlib.irradiance.haydavies(*args)),
                          perez=float(pvlib.irradiance.perez_driesse(*args))))

    for tilt in [0, 30, 60, 90]:
        for zenith in [0, 30, 60, 84.999, 85, 85.001, 89, 89.999]:
            for azimuth in [0, 90, 180, 270, 360]:
                for dni, dhi in [(0, 0), (0, 200), (800, 0), (800, 100), (150, 600)]:
                    add(tilt, azimuth, zenith, 180, dni, dhi, 1366.1)
    # Solve the zeta transform for DNI fraction to exercise each spline knot and continuity.
    for z in [0, 30, 70, 89]:
        k = 1.041 * np.radians(z)**3
        for knot in [0, .061, .187, .333, .487, .643, .778, .839, 1]:
            for epsilon in [-1e-10, 0, 1e-10]:
                zeta = np.clip(knot + epsilon, 0, 1)
                fraction = zeta * (1 + k) / (1 + zeta * k)
                add(42, 211, z, 135, 900 * fraction, 900 * (1 - fraction), 1350)
    rng = np.random.default_rng(20260906)
    for _ in range(400):
        add(rng.uniform(0, 90), rng.uniform(0, 360), rng.uniform(0, 89.99),
            rng.uniform(0, 360), rng.uniform(0, 1200), rng.uniform(0, 800), rng.uniform(1320, 1420))
    path.parent.mkdir(parents=True, exist_ok=True)
    # ephemeris uses NOAA's refraction branches with unit scaling at 10 C and 101325 Pa.
    # Compare refraction alone, not its independent underlying solar ephemeris. Below -1 degree,
    # pvlib truncates refraction whereas NOAA retains it; those invisible directions are excluded here.
    ephemeris = pvlib.solarposition.ephemeris(pd.date_range("2025-05-01", periods=288, freq="5min", tz="UTC"),
                                            10.8, 106.7, temperature=10, pressure=101325)
    refraction = [dict(geometric=float(r.zenith), apparent=float(r.apparent_zenith), azimuth=float(r.azimuth))
                  for r in ephemeris.itertuples() if r.elevation > -1]
    path.write_text(json.dumps(dict(pvlib=pvlib.__version__, cases=cases, refraction=refraction), separators=(",", ":")), encoding="utf-8")
    print(f"Generated {len(cases)} pvlib cases: {path}")


def verify(directory):
    errors, summaries = [], []
    for audit_path in sorted(directory.glob("*_audit.json.gz")):
        with gzip.open(audit_path, "rt", encoding="utf-8-sig") as f:
            audit = json.load(f)
        tilt, azimuth = audit["Tilt"], audit["Azimuth"]
        output = directory / audit["Workbook"]
        wb = openpyxl.load_workbook(output, data_only=True, read_only=True)
        sheet = wb.active
        cells = list(sheet.values)
        assert len(cells) == len(audit["Rows"]) + 1 and sheet.max_column == 3
        assert cells[0] == ("Timestamp (local, UTC offset)", "Direct on panel (W/m²)", "Sky diffuse on panel (W/m²)")
        assert audit["Model"] in wb.properties.description
        largest = 0
        for row, actual in zip(audit["Rows"], cells[1:]):
            timestamp, direct, sky = actual
            assert datetime.fromisoformat(timestamp) == datetime.fromisoformat(row["Timestamp"])
            assert datetime.fromisoformat(timestamp).utcoffset().total_seconds() == row["ExportUtcOffsetSeconds"]
            assert direct == row["Direct"] and sky == row["SkyDiffuse"]
            if tilt == 0:
                assert direct == row["Bhi"] and sky == row["Dhi"]
                continue
            positions = row["Positions"]
            if not positions:
                assert direct == 0 and sky == 0
                continue
            zenith = np.array([p["ZenithDegrees"] for p in positions])
            sun_azimuth = np.array([p["AzimuthDegrees"] for p in positions])
            daylight = zenith < 90
            mean_cosine = np.maximum(np.where(daylight, np.cos(np.radians(zenith)), 0), 0).mean()
            dni = row["Bhi"] / mean_cosine if row["Bhi"] > 0 else 0
            # Independently compute Spencer correction using pvlib, at the same UTC instants.
            extra = np.array([float(pvlib.irradiance.get_extra_radiation(
                datetime.fromisoformat(t).astimezone(timezone.utc).timetuple().tm_yday)) for t in row["Times"]])
            expected_direct = np.zeros(len(positions))
            expected_sky = np.full(len(positions), row["Dhi"] * (1 + np.cos(np.radians(tilt))) / 2)
            expected_direct[daylight] = pvlib.irradiance.beam_component(tilt, azimuth, zenith[daylight], sun_azimuth[daylight], dni)
            model = pvlib.irradiance.haydavies if audit["Model"] == "HayDavies" else pvlib.irradiance.perez_driesse
            expected_sky[daylight] = model(tilt, azimuth, row["Dhi"], dni, extra[daylight], zenith[daylight], sun_azimuth[daylight])
            error = max(abs(direct - expected_direct.mean()), abs(sky - expected_sky.mean()))
            largest = max(largest, error)
            if error > 1e-8:
                errors.append((audit_path.name, timestamp, error))
        summaries.append(dict(workbook=output.name, rows=len(audit["Rows"]), maximumErrorWm2=largest))
        wb.close()
    if not summaries:
        raise RuntimeError("No audit files found")
    if errors:
        raise AssertionError(errors[:10])
    report = dict(pvlib=pvlib.__version__, openpyxl=openpyxl.__version__, workbooks=summaries,
                  note="Checks implementation and export, not measured accuracy of the irradiance model or solar ephemeris.")
    (directory / "pvlib_verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=["generate", "verify"])
    parser.add_argument("path", type=Path)
    options = parser.parse_args()
    (generate if options.operation == "generate" else verify)(options.path)
