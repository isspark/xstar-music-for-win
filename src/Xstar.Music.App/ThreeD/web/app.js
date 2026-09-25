/* XSTAR 3D mode entry point: wires the bridge, stage, starfield, cover cloud, shelf, lyrics and chrome. */

import * as THREE from "three";
import { onMessage, action } from "./bridge.js";
import { audio, feedAudio, updateAudio } from "./audio.js";
import { createStage, QUALITY } from "./scene.js";
import { createStarfield } from "./starfield.js";
import { createCyberCity } from "./cybercity.js";
import { createWormhole } from "./wormhole.js";
import { createMinimalField } from "./minimal.js";
import { createCover, COVER_PRESETS } from "./cover.js";
import { createShelf } from "./shelf.js";
import { createLyrics } from "./lyrics.js";
import { initChrome } from "./chrome.js";

window.addEventListener("error", (event) => {
  action("clientError", { message: `${event.message} @ ${event.filename}:${event.lineno}` });
});
window.addEventListener("unhandledrejection", (event) => {
  action("clientError", { message: `unhandled rejection: ${event.reason}` });
});

// Desktop-wallpaper mode: the C# host loads index.html?wallpaper=1 and reparents the window
// behind the desktop icons. All self-drawn UI is hidden; only scene + lyrics remain.
const WALLPAPER = new URLSearchParams(location.search).get("wallpaper") === "1";
if (WALLPAPER) {
  document.body.classList.add("wallpaper");
  // Esc leaves the interactive (above-desktop) mode and returns to the plain wallpaper.
  window.addEventListener("keydown", (event) => {
    if (event.key === "Escape") action("setSetting", { key: "wallpaperInteractive", value: false });
  });
}

const canvas = document.getElementById("stage");
const stage = createStage(canvas, "balanced");

const stars = createStarfield(stage.scene, stage.tier.stars);

let presetIndex = 0;
// The last two presets are full replacement scenes rather than particle shapes.
const CYBER_PRESET = 5;
const WORMHOLE_PRESET = 6;
const MINIMAL_PRESET = 7;
const CORRIDOR_SPACING = 12;
// How far to slide the cover cloud left (world units) when the shelf is fully shown, so the
// shelf lane on the right never overlaps the cover art.
const COVER_SHELF_SHIFT = 1.4;
// How far to lift the cover (world units) when the stage-mode shelf row is shown along the bottom.
const COVER_SHELF_LIFT = 1.7;
// Current shelf horizontal offset (world units); the cover shift compensates so the gap is stable.
let shelfOffsetX = 0;
// Shelf layout mode: "side" (cards on the right) or "stage" (cards along the bottom).
let shelfMode = "side";
const cover = createCover(stage.scene, stage.tier.grid, "#22c55e");
cover.setPreset(presetIndex);

/** No-op stand-in for the 3D shelf when running as a wallpaper (cover + stars + lyrics only). */
function createNullShelf() {
  return {
    get detailOpen() { return false; },
    setViewport() {},
    isPointerOverContent() { return false; },
    setAccent() {},
    setLibrary() {},
    setArtwork() {},
    setPresence() {},
    setCameraMode() {},
    setMode() {},
    setLayout() {},
    openDetail() {},
    setDetail() {},
    closeDetail() {},
    getKind() { return "albums"; },
    update() {},
  };
}

let shelf;
if (WALLPAPER) {
  shelf = createNullShelf();
} else {
  const shelfScene = new THREE.Scene();
  shelf = createShelf({
    scene: shelfScene,
    camera: stage.camera,
    canvas,
    requestDetail: (id) => action("getDetail", { id }),
    requestCardArtwork: (id) => action("getCardArtwork", { id }),
    play: (id) => action("playTrack", { id }),
  });
  // Rendered after bloom so the card titles keep their edge instead of blowing out white.
  stage.addOverlayScene(shelfScene);
  shelf.setPresence("auto"); // 默认自动隐藏，控制栏可切换为常驻显示
}

/* ---------------- camera orbit (drag to rotate, wheel to zoom) ---------------- */

const orbit = {
  theta: 0,
  phi: 0,
  targetTheta: 0,
  targetPhi: 0,
  radius: 7,
  radiusTarget: 7,
  dragging: false,
  lastX: 0,
  lastY: 0,
};

canvas.addEventListener("pointerdown", (event) => {
  if (event.button !== 0) return;
  if (shelf.isPointerOverContent(event)) return; // let the shelf handle card / detail drags
  orbit.dragging = true;
  orbit.lastX = event.clientX;
  orbit.lastY = event.clientY;
  canvas.style.cursor = "grabbing";
  try { canvas.setPointerCapture(event.pointerId); } catch { /* ignore */ }
});

canvas.addEventListener("pointermove", (event) => {
  if (!orbit.dragging) return;
  const dx = event.clientX - orbit.lastX;
  const dy = event.clientY - orbit.lastY;
  orbit.lastX = event.clientX;
  orbit.lastY = event.clientY;
  orbit.targetTheta += dx * 0.005;
  orbit.targetPhi = Math.max(-1.15, Math.min(1.15, orbit.targetPhi + dy * 0.005));
});

function endOrbit(event) {
  if (!orbit.dragging) return;
  orbit.dragging = false;
  canvas.style.cursor = "";
  try { canvas.releasePointerCapture(event.pointerId); } catch { /* ignore */ }
}
canvas.addEventListener("pointerup", endOrbit);
canvas.addEventListener("pointercancel", endOrbit);

canvas.addEventListener(
  "wheel",
  (event) => {
    if (event.defaultPrevented) return; // shelf / detail took the wheel
    // The minimal field lives far outside the cover cloud, so it zooms over a wider range.
    const min = minimalActive ? 22 : 4.5;
    const max = minimalActive ? 140 : 12;
    const step = minimalActive ? 0.02 : 0.0025;
    orbit.radiusTarget = Math.max(min, Math.min(max, orbit.radiusTarget + event.deltaY * step));
  },
  { passive: true }
);

const lyricsScene = new THREE.Scene();
const lyricsStage = createLyrics(lyricsScene);
stage.addOverlayScene(lyricsScene);

let active = true; // host-gated render loop; a hidden 3D view must not keep drawing
let bgColorSetting = "#05060a"; // last configured 3D background, restored when leaving the night city
let bgImageSetting = null;
let accentHexCurrent = "#22c55e"; // last accent, applied to the city when it is created lazily
let lyricsEnabled = true;
let lyricMode = "default"; // "default" = 3D text stage; "particle" = current line shapes the cloud
let lyricLines = [];
let lyricIndex = 0;
let bloomEnabled = false;
let glowMultiplier = 1;
let particleColorOverride = "";
let reduceMotion = false;
let currentQuality = "balanced";
let baseBloom = stage.tier.bloomStrength;

lyricsStage.setVisible(true);

function currentLyricLine() {
  const text = (lyricLines[lyricIndex] || "").trim();
  return text === "♪" ? "" : text;
}

/* The city / wormhole are the whole scene: the cover cloud (album art or particle lyrics) is hidden. */
function syncCoverVisibility() {
  cover.group.visible = !(cyberActive || wormholeActive || minimalActive);
  cover.group.position.z = 0;
}

/* Reconciles the two lyric renderers: the 3D text stage (default) and the particle mask (粒子). */
function syncLyricsUi() {
  const hasLyrics = lyricLines.length > 0;
  lyricsStage.setVisible(lyricsEnabled && lyricMode === "default");
  cover.setLyricText(lyricsEnabled && lyricMode === "particle" ? currentLyricLine() : null);
  syncCoverVisibility();
  const on = lyricsEnabled && hasLyrics;
  chrome.setLyricsActive(on);
  chrome.setNowPlayingVisible(!on);
}

const chrome = initChrome({
  onPreset(index) {
    presetIndex = Math.max(0, Math.min(COVER_PRESETS.length - 1, index | 0));
    cover.setPreset(presetIndex);
    chrome.setPreset(presetIndex);
    applyPresetMode();
    action("setSetting", { key: "coverPreset", value: presetIndex });
  },
  onLyricsToggle() {
    lyricsEnabled = !lyricsEnabled;
    syncLyricsUi();
    action("setSetting", { key: "lyrics", value: lyricsEnabled });
  },
  onLyricMode(value) {
    lyricMode = value === "particle" ? "particle" : "default";
    syncLyricsUi();
    action("setSetting", { key: "lyricMode", value: lyricMode });
  },
  onSource(kind) {
    action("getLibrary", { kind });
    action("setSetting", { key: "libraryKind", value: kind });
  },
  onPresence(value) {
    shelf.setPresence(value);
  },
  onShelfMode(value) {
    shelfMode = value === "stage" ? "stage" : "side";
    shelf.setMode(value);
  },
  onCamera(value) {
    shelf.setCameraMode(value);
  },
  onQuality(value) {
    currentQuality = value;
    applyRenderQuality();
  },
  onBloom(value) {
    bloomEnabled = value;
    applyRenderQuality();
  },
  onShelfLayout(layout) {
    if (layout && typeof layout.offsetX === "number") shelfOffsetX = layout.offsetX;
    shelf.setLayout(layout);
  },
  onBackgroundColor(hex) {
    stage.setBackgroundColor(hex);
  },
  onPickBackgroundImage() {
    action("pickBackgroundImage");
  },
  onClearBackgroundImage() {
    stage.clearBackgroundImage();
    action("clearBackgroundImage");
  },
  onParticleSize(value) {
    cover.setSizeMultiplier(value);
    if (minimal) minimal.setSizeMultiplier(value);
  },
  onGlow(value) {
    glowMultiplier = value;
  },
  onParticleCount(value) {
    cover.setGridMultiplier(value);
    if (minimal) minimal.setGridMultiplier(value);
  },
  onColorMode(value) {
    cover.setColorMode(value);
    if (minimal) minimal.setColorMode(value);
  },
  onParticleColor(value) {
    particleColorOverride = value || "";
    cover.setMonoColor(particleColorOverride);
    if (minimal) minimal.setMonoColor(particleColorOverride);
  },
  onCoverParticles(on) {
    cover.setPreferCover(on);
  },
  // All settings-panel controls funnel here so their changes reach the host (and get persisted).
  onSetting(key, value) {
    action("setSetting", { key, value });
  },
});

function applyRenderQuality() {
  const tier = QUALITY[currentQuality] || QUALITY.balanced;
  stage.setBloomEnabled(bloomEnabled && tier.bloom);
  stage.setPixelRatioMax(tier.dpr);
  baseBloom = tier.bloomStrength;
}

/* Two presets are full replacement scenes: the neon night city ("赛博") and the hyperspace
   wormhole ("虫洞"). The cover cloud is hidden while either is active. */
let cyberActive = false;
let wormholeActive = false;
let minimalActive = false;
let city = null;
let wormhole = null;
let minimal = null;

function ensureCity() {
  if (!city) {
    city = createCyberCity(stage.scene);
    city.setAccent(accentHexCurrent);
  }
  return city;
}
function ensureWormhole() {
  if (!wormhole) {
    wormhole = createWormhole(stage.scene);
    wormhole.setAccent(accentHexCurrent);
  }
  return wormhole;
}
function ensureMinimal() {
  if (!minimal) {
    minimal = createMinimalField(stage.scene, stage.tier, accentHexCurrent);
    minimal.setHeight(stage.pixelsPerWorldAtUnit());
  }
  return minimal;
}

/* Both replacement scenes want a pure black backdrop; their own glow carries the frame. */
function applyBackground() {
  if (cyberActive || wormholeActive || minimalActive) {
    stage.setBackgroundColor("#000000");
    stage.clearBackgroundImage();
    return;
  }
  stage.setBackgroundColor(bgColorSetting);
  if (bgImageSetting) stage.setBackgroundImage(bgImageSetting);
  else stage.clearBackgroundImage();
}

function applyPresetMode() {
  const cyber = presetIndex === CYBER_PRESET && !reduceMotion;
  const worm = presetIndex === WORMHOLE_PRESET && !reduceMotion;
  const minimalMode = presetIndex === MINIMAL_PRESET && !reduceMotion;

  if (cyber) ensureCity().setVisible(true);
  else if (city) city.setVisible(false);
  if (worm) ensureWormhole().setVisible(true);
  else if (wormhole) wormhole.setVisible(false);
  if (minimalMode) ensureMinimal().setVisible(true);
  else if (minimal) minimal.setVisible(false);

  // Give the minimal field a sensible default view the first time it is entered (still adjustable).
  if (minimalMode && !minimalActive) {
    orbit.targetTheta = orbit.theta = 0;
    orbit.targetPhi = orbit.phi = 0.46;
    orbit.radiusTarget = orbit.radius = 82;
  }

  cyberActive = cyber;
  wormholeActive = worm;
  minimalActive = minimalMode;

  stage.setFeedbackEnabled(worm); // the jump relies on the streak/afterimage pass for motion blur
  stage.setToneExposure(worm ? 1.65 : cyber ? 1.75 : 1.3);

  // The wormhole and the minimal field bring their own star content.
  stars.points.visible = !worm && !minimalMode;
  cover.setCorridor(0, CORRIDOR_SPACING);
  applyBackground();
  syncCoverVisibility();
}

function applySettings(settings) {
  if (!settings) return;

  currentQuality = settings.quality || "balanced";
  bloomEnabled = settings.bloom === true;
  glowMultiplier = typeof settings.glow === "number" ? settings.glow : 1;
  reduceMotion = !!settings.reduceMotion;
  applyRenderQuality();

  lyricsEnabled = settings.lyrics !== false;
  lyricMode = settings.lyricMode === "particle" ? "particle" : "default";
  syncLyricsUi();

  presetIndex = Math.max(0, Math.min(COVER_PRESETS.length - 1, settings.coverPreset | 0));
  cover.setPreset(presetIndex);
  chrome.setPreset(presetIndex);
  applyPresetMode();

  const particleSize = typeof settings.particleSize === "number" ? settings.particleSize : 1;
  const particleCount = typeof settings.particleCount === "number" ? settings.particleCount : 1;
  const colorMode = typeof settings.particleColorMode === "number" ? settings.particleColorMode : 1;
  cover.setSizeMultiplier(particleSize);
  cover.setGridMultiplier(particleCount);
  cover.setColorMode(colorMode);
  cover.setPreferCover(settings.coverParticles === true);
  particleColorOverride = settings.particleColor || "";
  if (particleColorOverride) cover.setMonoColor(particleColorOverride);
  if (minimal) {
    minimal.setSizeMultiplier(particleSize);
    minimal.setGridMultiplier(particleCount);
    minimal.setColorMode(colorMode);
    if (particleColorOverride) minimal.setMonoColor(particleColorOverride);
  }

  shelfOffsetX = settings.shelfOffsetX || 0;
  shelf.setLayout({
    size: settings.shelfSize || 1,
    offsetX: settings.shelfOffsetX || 0,
    offsetY: settings.shelfOffsetY || 0,
    angleY: ((settings.shelfAngleY || 0) * Math.PI) / 180,
    opacity: settings.shelfOpacity || 1,
  });

  bgColorSetting = settings.backgroundColor || "#05060a";
  bgImageSetting = settings.backgroundImage || null;
  applyBackground();

  chrome.applySettings(settings);

  if (settings.libraryKind) action("getLibrary", { kind: settings.libraryKind });
}

chrome.setPreset(presetIndex);
chrome.setQuality(currentQuality);
applyPresetMode();
syncLyricsUi();

const accent = new THREE.Color("#22c55e");

function applyViewport() {
  const width = Math.max(1, canvas.clientWidth);
  const height = Math.max(1, canvas.clientHeight);
  const pixelsPerWorld = stage.pixelsPerWorldAtUnit();
  cover.setPixelsPerWorld(pixelsPerWorld);
  stars.setHeight(pixelsPerWorld);
  if (minimal) minimal.setHeight(pixelsPerWorld);
  shelf.setViewport(width, height);
}

function setAccent(hex) {
  if (!hex || !/^#?[0-9a-f]{6}$/i.test(hex)) return;
  accent.set(hex);
  stars.setAccent(accent);
  shelf.setAccent(hex);
  cover.setAccent(hex);
  lyricsStage.setAccent(hex);
  chrome.setAccent(hex);
  accentHexCurrent = hex || accentHexCurrent;
  if (city) city.setAccent(accentHexCurrent);
  if (wormhole) wormhole.setAccent(accentHexCurrent);
  if (minimal) minimal.setAccent(accentHexCurrent);
  if (!particleColorOverride) {
    cover.setMonoColor(hex);
    chrome.setParticleColorValue(hex);
  }
}

onMessage((message) => {
  switch (message.type) {
    case "audio":
      feedAudio(message);
      break;
    case "state":
      chrome.applyState(message);
      cover.setTitle(message.title);
      setAccent(message.accent);
      break;
    case "settings":
      applySettings(message);
      break;
    case "active":
      setActive(message.active !== false);
      break;
    case "artwork":
      if (message.key) shelf.setArtwork(message.key, message.data);
      else if (message.data) cover.setImage(message.data);
      else cover.clearImage();
      break;
    case "library":
      shelf.setLibrary(message.kind, message.cards || []);
      chrome.setSource(message.kind);
      break;
    case "detail":
      shelf.setDetail(message);
      break;
    case "lyrics":
      lyricLines = (message.lines || []).map((line) => line.text || "");
      lyricIndex = 0;
      lyricsStage.setLines(message.lines || []);
      syncLyricsUi();
      break;
    case "lyricIndex":
      lyricIndex = message.index || 0;
      lyricsStage.setActiveIndex(lyricIndex);
      syncLyricsUi();
      break;
    case "orbit":
      // Camera deltas forwarded from the host when the user drags/scrolls on empty desktop space.
      if (typeof message.dx === "number") orbit.targetTheta += message.dx * 0.005;
      if (typeof message.dy === "number")
        orbit.targetPhi = Math.max(-1.15, Math.min(1.15, orbit.targetPhi + message.dy * 0.005));
      if (typeof message.wheel === "number" && message.wheel !== 0)
        orbit.radiusTarget = Math.max(4.5, Math.min(12, orbit.radiusTarget + message.wheel * 0.0025));
      break;
    default:
      break;
  }
});

window.addEventListener("resize", applyViewport);
applyViewport();

let travel = 0;
let last = performance.now();
let lastRender = 0;
let frames = 0;
let fpsClock = last;

function frame(now) {
  if (!active) return;
  if (reduceMotion && now - lastRender < 33) {
    requestAnimationFrame(frame);
    return;
  }
  lastRender = now;

  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;
  const time = now / 1000;

  updateAudio(dt);
  stage.updateFeedback(audio, dt);

  const cam = stage.camera;

  if (cyberActive) {
    // Night-city flythrough: the street streams past while the camera drifts and banks, so it
    // reads as a cinematic chase rather than a static shot.
    const speed = 10 + audio.pulse * 11 + audio.beat * 18;
    travel += speed * dt;
    ensureCity().advance(dt, speed);
    city.update(dt, audio);

    const sway = Math.sin(travel * 0.06) * 1.0 + Math.sin(travel * 0.021) * 0.5;
    cam.position.set(
      sway,
      city.groundY + 1.75 + Math.sin(travel * 0.03) * 0.15,
      10 + Math.sin(travel * 0.013) * 0.8
    );
    cam.lookAt(sway * 0.35, city.groundY + 2.6, -26);
    cam.rotateZ(Math.sin(travel * 0.025) * 0.05);
    city.alignEq(cam.position);
    cam.fov = 54 - audio.beat * 3;
    cam.updateProjectionMatrix();
  } else if (wormholeActive) {
    // Hyperspace jump: first-person, very fast, wide lens and heavy banking.
    const speed = 45 + audio.pulse * 60 + audio.beat * 95;
    travel += speed * dt;
    ensureWormhole().update(dt, audio, speed);

    const swayX = Math.sin(travel * 0.03) * 1.3;
    const swayY = Math.cos(travel * 0.023) * 1.0;
    cam.position.set(swayX, swayY, 0);
    cam.lookAt(swayX * 0.2, swayY * 0.2, -90);
    cam.rotateZ(Math.sin(travel * 0.02) * 0.25 + Math.sin(travel * 0.008) * 0.15);
    cam.fov = 72 + audio.beat * 8;
    cam.updateProjectionMatrix();
  } else if (minimalActive) {
    // Calm drift, but the view is fully user-controllable (drag to orbit, wheel to zoom).
    ensureMinimal().update(dt, audio);
    orbit.theta += (orbit.targetTheta - orbit.theta) * Math.min(1, dt * 10);
    orbit.phi += (orbit.targetPhi - orbit.phi) * Math.min(1, dt * 8);
    orbit.radius += (orbit.radiusTarget - orbit.radius) * Math.min(1, dt * 6);

    const theta = orbit.theta + Math.sin(time * 0.06) * 0.05;
    const phi = orbit.phi + Math.cos(time * 0.045) * 0.03;
    const radius = orbit.radius - audio.pulse * 1.5;
    const cosPhi = Math.cos(phi);
    cam.position.set(
      radius * cosPhi * Math.sin(theta),
      radius * Math.sin(phi),
      radius * cosPhi * Math.cos(theta)
    );
    cam.lookAt(0, Math.sin(time * 0.04) * 1.0, 0);
    cam.rotateZ(Math.sin(time * 0.03) * 0.02);
    cam.fov = 32;
    cam.updateProjectionMatrix();
  } else {
    if (cam.fov !== 45) {
      cam.fov = 45;
      cam.updateProjectionMatrix();
    }
    orbit.theta += (orbit.targetTheta - orbit.theta) * Math.min(1, dt * 10);
    orbit.phi += (orbit.targetPhi - orbit.phi) * Math.min(1, dt * 10);
    orbit.radius += (orbit.radiusTarget - orbit.radius) * Math.min(1, dt * 6);

    const theta = orbit.theta + Math.sin(time * 0.15) * 0.05;
    const phi = orbit.phi + Math.cos(time * 0.12) * 0.03;
    const radius = orbit.radius - audio.pulse * 1.1 - audio.beat * 0.25;
    const cosPhi = Math.cos(phi);
    cam.position.set(
      radius * cosPhi * Math.sin(theta),
      radius * Math.sin(phi),
      radius * cosPhi * Math.cos(theta)
    );
    cam.lookAt(0, 0, 0);
  }

  cover.update(dt, audio, time);
  stars.update(dt, audio);

  shelf.update(dt, time);
  // Keep the shelf lane clear of the cover art: slide the cover left (side) or lift it (stage).
  const shelfVis = shelf.visibility || 0;
  if (shelfMode === "stage") {
    cover.group.position.x = 0;
    cover.group.position.y = COVER_SHELF_LIFT * shelfVis;
  } else {
    cover.group.position.x = -Math.max(0, COVER_SHELF_SHIFT - shelfOffsetX) * shelfVis;
    cover.group.position.y = 0;
  }
  lyricsStage.update(dt, audio, time);

  stage.bloom.strength = baseBloom * glowMultiplier + audio.beat * 0.6 + Math.max(0, audio.pulse - 0.6) * 0.3;
  stage.render();

  frames++;
  if (now - fpsClock >= 500) {
    chrome.setFps(Math.round((frames * 1000) / (now - fpsClock)));
    frames = 0;
    fpsClock = now;
  }

  requestAnimationFrame(frame);
}

/* The host pauses the loop when the 3D view is hidden (otherwise a collapsed WebView2 keeps
   rendering on the GPU). Resume resets the clock so the first frame after the pause stays smooth. */
function setActive(next) {
  const on = next !== false;
  if (on === active) return;
  active = on;
  if (on) {
    last = performance.now();
    requestAnimationFrame(frame);
  }
}

requestAnimationFrame(frame);

chrome.setBridge("connected");
action("ready");
