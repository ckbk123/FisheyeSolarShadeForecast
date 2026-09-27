"""Independent ZIP/XML, Astropy and NumPy validation; never imports the C# implementation."""
from pathlib import Path
from datetime import datetime, timedelta
from zipfile import ZipFile
import xml.etree.ElementTree as ET
import json
import numpy as np
import yaml
from PIL import Image
from astropy.coordinates import EarthLocation, AltAz, get_sun
from astropy.time import Time
from astropy import units as u
from astropy.utils import iers
iers.conf.auto_download=False
root=Path(__file__).resolve().parents[3]
folder=root/'src/SolarPositionAndShading/Validator/results'
ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
def sheet(path):
    with ZipFile(path) as z:
        xml=ET.fromstring(z.read('xl/worksheets/sheet1.xml'))
    result=[]
    for row in xml.findall('.//s:row',ns):
        out={}
        for c in row.findall('s:c',ns):
            col=''.join(ch for ch in c.attrib['r'] if ch.isalpha())
            if c.attrib.get('t')=='inlineStr': out[col]=''.join(e.text or '' for e in c.findall('.//s:t',ns))
            else: out[col]=float(c.find('s:v',ns).text)
        result.append(out)
    return result
metadata=json.loads((folder/'benchmark.json').read_text())
site=metadata['Site']
location=EarthLocation(lat=site['LatitudeDegrees']*u.deg,lon=site['LongitudeDegrees']*u.deg,height=site['ElevationMetres']*u.m)
def sun(times):
    t=Time(times)
    s=get_sun(t).transform_to(AltAz(obstime=t,location=location,pressure=0*u.hPa))
    return np.stack([np.sin(s.zen.rad)*np.sin(s.az.rad),np.sin(s.zen.rad)*np.cos(s.az.rad),np.cos(s.zen.rad)],axis=-1)
reports=[]
for provider,base in [('OpenMeteo','shading-month'),('NasaPower','nasa-shading-month')]:
    original=sheet(root/f'src/IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905/{provider}_20250501_20250601_requested.xlsx')[1:]
    output=sheet(folder/f'{base}.xlsx')[1:]
    positions=sheet(folder/f'{base}-solar-positions.xlsx')[1:]
    assert len(original)==len(output)==len(positions)==744
    assert all(a['A']==b['A']==c['A'] for a,b,c in zip(original,output,positions))
    for a,b,c in zip(original,output,positions):
        assert 0<=b['E']<=b['F']<=1 and 0<=b['G']<=1
        if a['B']==0: assert b['D']==0 and b['H']=='SkippedZeroDirect'
        elif b['G']<1-1e-10: assert 'D' not in b
        else: assert 0<=b['D']<=1
        assert np.isfinite(c['B']) and np.isfinite(c['C'])
    expected=sun([datetime.fromisoformat(r['A']) for r in positions])
    az=np.radians([r['C'] for r in positions]);ze=np.radians([r['B'] for r in positions])
    actual=np.stack([np.sin(ze)*np.sin(az),np.sin(ze)*np.cos(az),np.cos(ze)],axis=-1)
    errors=np.degrees(2*np.arcsin(np.linalg.norm(actual-expected,axis=1)/2))
    assert errors.max()<1/60
    reports.append(dict(Provider=provider,Rows=744,ZeroDirect=sum(r['B']==0 for r in original),OffsetsAndInstantsPreserved=True,
                        SolarMaxErrorDegrees=float(errors.max()),NightSolarPositionsExported=True))

# Independent direct shading reference at 48 spread-out positive-direct hours.
mask=np.array(Image.open(root/'src/SkyPhotoMasking/Validator/example_image-efficientnet-b5.png'))/255.0
cal=yaml.safe_load((root/'artifacts/calibration-validation/ready-to-run/calibration.yml').read_text())
cx,cy=cal['principal_point'];poly=cal['poly_incident_angle_to_radius'];h,w=mask.shape
source=sheet(root/'src/IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905/OpenMeteo_20250501_20250601_requested.xlsx')[1:]
actual=sheet(folder/'shading-month.xlsx')[1:]
selected=np.array([i for i,r in enumerate(source) if r['B']>0])
selected=selected[np.linspace(0,len(selected)-1,48,dtype=int)]
n=2048;i=np.arange(n);qz=1-(1-np.cos(np.radians(.25)))*(i+.5)/n
qr=np.sqrt(1-qz*qz);a=i*np.pi*(3-np.sqrt(5));qx=qr*np.cos(a);qy=qr*np.sin(a)
comparisons=[]
for index in selected:
    label=datetime.fromisoformat(source[index]['A'])
    centers=sun([label+timedelta(minutes=-60+(j+.5)/4) for j in range(240)])
    visible=known=total=0.0
    for center in centers:
        basis=np.array([0,0,1]) if abs(center[2])<.9 else np.array([1,0,0])
        tangent=np.cross(center,basis);tangent/=np.linalg.norm(tangent);other=np.cross(center,tangent)
        rays=qz[:,None]*center+qx[:,None]*tangent+qy[:,None]*other
        weights=np.maximum(0,rays[:,2]);total+=weights.sum()
        theta=np.arctan2(np.linalg.norm(rays[:,:2],axis=1),rays[:,2])
        radius=np.polynomial.polynomial.polyval(theta,poly)
        lateral=np.linalg.norm(rays[:,:2],axis=1)
        x=cx-radius*rays[:,0]/np.maximum(lateral,1e-30);y=cy-radius*rays[:,1]/np.maximum(lateral,1e-30)
        valid=(theta<=np.radians(66.43))&(weights>0)&(x>=0)&(x<=w-1)&(y>=0)&(y<=h-1)&((x-1521.8750635782878)**2+(y-2003.1250317891438)**2<=882.9166889190674**2)
        x=x[valid];y=y[valid];weight=weights[valid]
        ix=np.minimum(x.astype(int),w-2);iy=np.minimum(y.astype(int),h-2);fx=x-ix;fy=y-iy
        brightness=(1-fy)*((1-fx)*mask[iy,ix]+fx*mask[iy,ix+1])+fy*((1-fx)*mask[iy+1,ix]+fx*mask[iy+1,ix+1])
        known+=weight.sum();visible+=np.dot(weight,brightness)
    lower=(known-visible)/total;upper=1-visible/total
    error=max(abs(lower-actual[index]['E']),abs(upper-actual[index]['F']))
    comparisons.append(dict(Timestamp=source[index]['A'],Lower=float(lower),Upper=float(upper),MaxBoundError=float(error)))
maximum=max(r['MaxBoundError'] for r in comparisons)
report=dict(Workbooks=reports,IndependentShading=dict(Samples=48,TemporalSamples=240,DiskSamples=2048,
    MaxBoundError=maximum,Acceptance=0.01,Passed=maximum<.01,Cases=comparisons))
(folder/'independent-validation.json').write_text(json.dumps(report,indent=2))
print(json.dumps(dict(Workbooks=reports,IndependentShadingMaxError=maximum),indent=2))
assert maximum<.01
