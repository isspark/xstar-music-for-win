/* XSTAR 3D mode entry point: wires the bridge, stage, starfield, cover cloud, shelf, lyrics and chrome. */

import * as THREE from "three";
import { onMessage, action } from "./bridge.js";
import { audio, feedAudio, updateAudio } from "./audio.js";
import { createStage, QUALITY } from "./scene.js";
import { createStarfield } from "./starfield.js";
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
  shelf.setPresence("always"); // 默认常驻，便于发现；控制栏可切换为自动隐藏
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
    orbit.radiusTarget = Math.max(4.5, Math.min(12, orbit.radiusTarget + event.deltaY * 0.0025));
  },
  { passive: true }
);

const lyricsScene = new THREE.Scene();
const lyricsStage = createLyrics(lyricsScene);
stage.addOverlayScene(lyricsScene);

let active = true; // host-gated render loop; a hidden 3D view must not keep drawing
let lyricsEnabled = true;
let lyricMode = "default"; // "default" = 3D text stage; "particle" = current line shapes the cloud
let lyricLines = [];
let lyricIndex = 0;
let bloomEnabled = true;
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

/* Reconciles the two lyric renderers: the 3D text stage (default) and the particle mask (粒子). */
function syncLyricsUi() {
  const hasLyrics = lyricLines.length > 0;
  lyricsStage.setVisible(lyricsEnabled && lyricMode === "default");
  cover.setLyricText(lyricsEnabled && lyricMode === "particle" ? currentLyricLine() : null);
  const on = lyricsEnabled && hasLyrics;
  chrome.setLyricsActive(on);
  chrome.setNowPlayingVisible(!on);
}

const chrome = initChrome({
  onPreset(index) {
    presetIndex = Math.max(0, Math.min(COVER_PRESETS.length - 1, index | 0));
    cover.setPreset(presetIndex);
    chrome.setPreset(presetIndex);
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
  },
  onGlow(value) {
    glowMultiplier = value;
  },
  onParticleCount(value) {
    cover.setGridMultiplier(value);
  },
  onColorMode(value) {
    cover.setColorMode(value);
  },
  onParticleColor(value) {
    particleColorOverride = value || "";
    cover.setMonoColor(particleColorOverride);
  },
});

function applyRenderQuality() {
  const tier = QUALITY[currentQuality] || QUALITY.balanced;
  stage.setBloomEnabled(bloomEnabled && tier.bloom);
  stage.setPixelRatioMax(tier.dpr);
  baseBloom = tier.bloomStrength;
}

function applySettings(settings) {
  if (!settings) return;

  currentQuality = settings.quality || "balanced";
  bloomEnabled = settings.bloom !== false;
  glowMultiplier = typeof settings.glow === "number" ? settings.glow : 1;
  reduceMotion = !!settings.reduceMotion;
  applyRenderQuality();

  lyricsEnabled = settings.lyrics !== false;
  lyricMode = settings.lyricMode === "particle" ? "particle" : "default";
  syncLyricsUi();

  presetIndex = Math.max(0, Math.min(COVER_PRESETS.length - 1, settings.coverPreset | 0));
  cover.setPreset(presetIndex);
  chrome.setPreset(presetIndex);

  cover.setSizeMultiplier(typeof settings.particleSize === "number" ? settings.particleSize : 1);
  cover.setGridMultiplier(typeof settings.particleCount === "number" ? settings.particleCount : 1);
  cover.setColorMode(typeof settings.particleColorMode === "number" ? settings.particleColorMode : 1);
  particleColorOverride = settings.particleColor || "";
  if (particleColorOverride) cover.setMonoColor(particleColorOverride);

  shelf.setLayout({
    size: settings.shelfSize || 1,
    offsetX: settings.shelfOffsetX || 0,
    offsetY: settings.shelfOffsetY || 0,
    angleY: ((settings.shelfAngleY || 0) * Math.PI) / 180,
    opacity: settings.shelfOpacity || 1,
  });

  stage.setBackgroundColor(settings.backgroundColor || "#05060a");
  if (settings.backgroundImage) stage.setBackgroundImage(settings.backgroundImage);
  else stage.clearBackgroundImage();

  chrome.applySettings(settings);

  if (settings.libraryKind) action("getLibrary", { kind: settings.libraryKind });
}

chrome.setPreset(presetIndex);
chrome.setQuality(currentQuality);
syncLyricsUi();

const accent = new THREE.Color("#22c55e");

function applyViewport() {
  const width = Math.max(1, canvas.clientWidth);
  const height = Math.max(1, canvas.clientHeight);
  const pixelsPerWorld = stage.pixelsPerWorldAtUnit();
  cover.setPixelsPerWorld(pixelsPerWorld);
  stars.setHeight(pixelsPerWorld);
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
  cover.update(dt, audio, time);
  stars.update(dt, audio);

  const cam = stage.camera;

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

  shelf.update(dt, time);
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
