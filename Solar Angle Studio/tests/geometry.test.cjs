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

test("signed equipment azimuth follows the panel front and lens normal", () => {
  for (const [azimuth, expectedBearing] of [[0, 180], [-90, 90], [90, 270], [180, 0], [-180, 0], [270, 90], [-26, 154]]) {
    const panel = {azimuth, tilt: 28}, phone = G.copyPanelPose(panel, {tilt: 0, imageTopBearing: 75});
    const pose = G.sceneValues({panel, phone, sun: {azimuth: 0, elevation: 20}});
    near(pose.panelBearing, expectedBearing);
    near(pose.phoneBearing, expectedBearing);
    near(G.bearing(pose.panelNormal), expectedBearing);
    near(G.bearing(pose.phoneNormal), expectedBearing);
    near(pose.phoneTilt, 28);
    near(pose.separation, 0);
    near(G.angleBetween(pose.phoneNormal, [0, 0, 1]), 28);
    assert.ok(pose.imageTop[2] > 0);
    near(pose.actualImageTopBearing, G.wrap360(expectedBearing + 180));
  }
  assert.equal(G.signedAzimuth(270), -90);
});

test("lens end points uphill and the oriented image has the desktop right axis", () => {
  const {top, right, normal} = G.framePhone({azimuth: 0, tilt: 28});
  const a = Math.PI * 28 / 180;
  vectorNear(normal, [0, -Math.sin(a), Math.cos(a)]);
  vectorNear(top, [0, Math.cos(a), Math.sin(a)]);
  vectorNear(right, [-1, 0, 0]);
});

test("flat phone heading is independent of the panel's undefined front azimuth", () => {
  const panel = {azimuth: -33, tilt: 0};
  for (const heading of [0, 90, 180, 270]) {
    const phone = {azimuth: 12, tilt: 0, imageTopBearing: heading};
    const result = G.sceneValues({panel, phone: G.copyPanelPose(panel, phone), sun: {azimuth: 40, elevation: 0}});
    near(result.actualImageTopBearing, heading);
    assert.equal(result.phoneBearing, null);
    assert.equal(result.panelBearing, null);
    vectorNear(result.phoneNormal, [0, 0, 1]);
    near(G.bearing(G.framePhone(phone).right), G.wrap360(heading - 90));
    const tilted = G.setPhoneTilt(phone, 28);
    near(G.bearing(G.framePhone(tilted).normal), G.wrap360(heading + 180));
    near(G.setPhoneTilt(tilted, 0).imageTopBearing, heading);
  }
});

test("vertical phone has a horizontal optical axis and no image-top bearing", () => {
  const panel = {azimuth: -90, tilt: 90};
  const phone = G.copyPanelPose(panel, {tilt: 0, azimuth: 35, imageTopBearing: 60});
  const pose = G.sceneValues({panel, phone, sun: {azimuth: 90, elevation: 0}});
  vectorNear(pose.imageTop, [0, 0, 1]);
  vectorNear(pose.phoneNormal, [1, 0, 0]);
  assert.equal(pose.actualImageTopBearing, null);
  near(pose.incidence, 0);
  near(G.copyPanelPose({...panel, tilt: 0}, phone).imageTopBearing, 270);
});

test("manual solar azimuth is clockwise from north and zenith complements elevation", () => {
  const panel = {azimuth: 0, tilt: 28}, phone = {...panel};
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
  const panel = {tilt: 28, azimuth: 90}, phone = {tilt: 0, azimuth: -45, imageTopBearing: 180};
  const copy = G.copyPanelPose(panel, phone);
  panel.tilt = 45;
  near(copy.tilt, 28);
  assert.equal(phone.tilt, 0);
  assert.throws(() => G.setPhoneTilt(phone, -1), RangeError);
  assert.throws(() => G.setPhoneTilt(phone, 91), RangeError);
});
