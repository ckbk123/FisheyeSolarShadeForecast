"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const G = require("../geometry.js");

function near(actual, expected) {
  assert.ok(Math.abs(actual - expected) < 1e-10, actual + " should equal " + expected);
}

function vectorNear(actual, expected) {
  expected.forEach((value, i) => near(actual[i], value));
}

test("marked top headings and outward normal azimuths use their respective geographic references", () => {
  for (const [topHeading, expectedBearing, fromSouth] of [[0, 180, 0], [270, 90, -90], [90, 270, 90], [180, 0, 180], [334, 154, -26]]) {
    const panel = {topHeading, tilt: 28}, phone = G.copyPanelPose(panel);
    const pose = G.sceneValues({panel, phone, sun: {azimuth: 0, elevation: 20}});
    near(pose.panelBearing, expectedBearing);
    near(pose.phoneBearing, expectedBearing);
    near(G.bearing(pose.panelNormal), expectedBearing);
    near(G.bearing(pose.phoneNormal), expectedBearing);
    near(G.signedAzimuth(pose.panelBearing - 180), fromSouth);
    near(pose.phoneTilt, 28);
    near(pose.separation, 0);
    near(G.angleBetween(pose.phoneNormal, [0, 0, 1]), 28);
    assert.ok(pose.imageTop[2] > 0);
    near(pose.actualImageTopBearing, G.wrap360(expectedBearing + 180));
  }
  assert.equal(G.signedAzimuth(270), -90);
});

test("lens end points uphill and the oriented image has the desktop right axis", () => {
  const {top, right, normal} = G.equipmentFrame({topHeading: 0, tilt: 28});
  const a = Math.PI * 28 / 180;
  vectorNear(normal, [0, -Math.sin(a), Math.cos(a)]);
  vectorNear(top, [0, Math.cos(a), Math.sin(a)]);
  vectorNear(right, [-1, 0, 0]);
});

test("both marked edges turn independently while flat without changing front or lens direction", () => {
  for (const heading of [0, 90, 180, 270]) {
    const panel = {topHeading: heading, tilt: 0}, phone = {topHeading: G.wrap360(heading + 45), tilt: 0};
    const result = G.sceneValues({panel, phone, sun: {azimuth: 40, elevation: 0}});
    near(result.actualImageTopBearing, phone.topHeading);
    assert.equal(result.phoneBearing, null);
    assert.equal(result.panelBearing, null);
    vectorNear(result.phoneNormal, [0, 0, 1]);
    vectorNear(result.panelNormal, [0, 0, 1]);
    near(G.bearing(G.equipmentFrame(panel).top), heading);
    near(G.bearing(G.equipmentFrame(phone).right), G.wrap360(phone.topHeading - 90));
    const copy = G.copyPanelPose(panel);
    near(G.bearing(G.equipmentFrame(copy).top), heading);
    for (const tilt of [28, 90, 0.1]) {
      const frame = G.equipmentFrame({...copy, tilt});
      near(G.bearing(frame.normal), G.wrap360(heading + 180));
      if (tilt < 90) near(G.bearing(frame.top), heading);
    }
  }
});

test("vertical phone has a horizontal optical axis and no image-top bearing", () => {
  const panel = {topHeading: 270, tilt: 90};
  const phone = G.copyPanelPose(panel);
  const pose = G.sceneValues({panel, phone, sun: {azimuth: 90, elevation: 0}});
  vectorNear(pose.imageTop, [0, 0, 1]);
  vectorNear(pose.phoneNormal, [1, 0, 0]);
  assert.equal(pose.actualImageTopBearing, null);
  near(pose.incidence, 0);
  vectorNear(G.equipmentFrame({...phone, topHeading: 90}).normal, [-1, 0, 0]);
  near(G.bearing(G.equipmentFrame({...phone, tilt: 0}).top), 270);
});

test("manual solar azimuth is clockwise from north and zenith complements elevation", () => {
  const panel = {topHeading: 0, tilt: 28}, phone = {...panel};
  for (const [azimuth, elevation, incidence, sunBearing] of [
    [180, 62, 0, 180], [0, 62, 56, 0], [0, -62, 180, 0], [90, 90, 28, null], [270, -90, 152, null]
  ]) {
    const pose = G.sceneValues({panel, phone, sun: {azimuth, elevation}});
    near(pose.incidence, incidence);
    near(pose.zenith, 90 - elevation);
    assert.equal(pose.sunBearing, sunBearing);
  }
});

test("copy panel pose is a snapshot and independent edits preserve the original object", () => {
  const panel = {tilt: 28, topHeading: 90};
  const copy = G.copyPanelPose(panel);
  panel.tilt = 45;
  near(copy.tilt, 28);
  copy.topHeading = 45;
  near(panel.topHeading, 90);
});
