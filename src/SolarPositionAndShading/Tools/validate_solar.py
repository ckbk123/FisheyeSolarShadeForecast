"""Independent development-only Astropy oracle. No network, refraction disabled."""
import json
from pathlib import Path
import numpy as np
import astropy
from astropy.coordinates import EarthLocation, AltAz, get_sun
from astropy.time import Time
from astropy import units as u
from astropy.utils import iers
iers.conf.auto_download = False
folder = Path(__file__).resolve().parents[1] / 'Validator' / 'results'
cases = json.loads((folder / 'solar-oracle-input.json').read_text())
errors = []
for case in cases:
    s = case['Site']
    from datetime import datetime
    t = Time(datetime.fromisoformat(case['Timestamp']))
    location = EarthLocation(lat=s['LatitudeDegrees']*u.deg, lon=s['LongitudeDegrees']*u.deg, height=s['ElevationMetres']*u.m)
    sun = get_sun(t).transform_to(AltAz(obstime=t, location=location, pressure=0*u.hPa))
    az, ze = sun.az.rad, sun.zen.rad
    expected = np.array([np.sin(ze)*np.sin(az), np.sin(ze)*np.cos(az), np.cos(ze)])
    actual = case['Position']['Direction']
    vector = np.array([actual['East'],actual['North'],actual['Up']])
    error = np.degrees(2*np.arcsin(np.clip(np.linalg.norm(expected-vector)/2,0,1)))
    errors.append(float(error))
    case['AstropyAzimuthDegrees'] = float(sun.az.deg)
    case['AstropyZenithDegrees'] = float(sun.zen.deg)
    case['AngularErrorDegrees'] = float(error)
report = dict(AstropyVersion=astropy.__version__, Samples=len(errors), MaxAngularErrorDegrees=max(errors),
              P95AngularErrorDegrees=float(np.percentile(errors,95)), MeanAngularErrorDegrees=float(np.mean(errors)),
              AcceptanceDegrees=1/60, Passed=max(errors)<1/60, Cases=cases)
(folder/'solar-astropy-validation.json').write_text(json.dumps(report,indent=2))
print(json.dumps({k:v for k,v in report.items() if k!='Cases'},indent=2))
assert report['Passed']
