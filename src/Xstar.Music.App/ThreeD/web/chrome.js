/* Self-drawn window chrome + transport + settings panel: bridges DOM controls to the C# host. */

import { action } from "./bridge.js";

function formatTime(ms) {
  const total = Math.max(0, Math.floor((ms || 0) / 1000));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return String(m).padStart(2, "0") + ":" + String(s).padStart(2, "0");
}

export function initChrome(handlers) {
  const titleEl = document.getElementById("np-title");
  const artistEl = document.getElementById("np-artist");
  const playBtn = document.querySelector('[data-action="playpause"]');
  const seek = document.getElementById("seek");
  const currentEl = document.getElementById("time-current");
  const totalEl = document.getElementById("time-total");
  const appearanceBtn = document.getElementById("btn-appearance");
  const lyricsBtn = document.getElementById("btn-lyrics");
  const nowPlayingEl = document.getElementById("now-playing");
  const fpsEl = document.getElementById("fps");
  const bridgeEl = document.getElementById("bridge");
  const qualityEl = document.getElementById("quality");

  const sourceButtons = Array.from(document.querySelectorAll("#source-seg button"));
  const presenceButtons = Array.from(document.querySelectorAll("#set-presence button"));
  const shelfModeButtons = Array.from(document.querySelectorAll("#set-shelf-mode button"));
  const cameraButtons = Array.from(document.querySelectorAll("#set-camera button"));

  // settings panel
  const settingsPanel = document.getElementById("settings-panel");
  const presetButtons = Array.from(document.querySelectorAll("#set-preset button"));
  const qualityButtons = Array.from(document.querySelectorAll("#set-quality button"));
  const bloomBtn = document.getElementById("set-bloom");
  const coverParticlesBtn = document.getElementById("set-coverparticles");
  const lyricsToggle = document.getElementById("set-lyrics");
  const lyricModeButtons = Array.from(document.querySelectorAll("#set-lyricmode button"));
  const sizeInput = document.getElementById("set-size");
  const offxInput = document.getElementById("set-offx");
  const offyInput = document.getElementById("set-offy");
  const angleInput = document.getElementById("set-angle");
  const opacityInput = document.getElementById("set-opacity");
  const bgColorInput = document.getElementById("set-bgcolor");
  const bgImageBtn = document.getElementById("set-bgimg");
  const bgClearBtn = document.getElementById("set-bgclear");
  const resetBtn = document.getElementById("btn-reset3d");
  const psizeInput = document.getElementById("set-psize");
  const glowInput = document.getElementById("set-glow");
  const pcountInput = document.getElementById("set-pcount");
  const pcolorButtons = Array.from(document.querySelectorAll("#set-pcolor button"));
  const pcolorValueInput = document.getElementById("set-pcolorvalue");

  let playing = false;
  let durationMs = 0;
  let dragging = false;

  const persist = (key, value) => { if (handlers.onSetting) handlers.onSetting(key, value); };

  function layout() {
    return {
      size: parseFloat(sizeInput.value),
      offsetX: parseFloat(offxInput.value),
      offsetY: parseFloat(offyInput.value),
      angleY: (parseFloat(angleInput.value) * Math.PI) / 180,
      opacity: parseFloat(opacityInput.value),
    };
  }
  const emitLayout = () => handlers.onShelfLayout && handlers.onShelfLayout(layout());

  /* ---------------- transport ---------------- */

  playBtn.addEventListener("click", () => action(playing ? "pause" : "play"));
  document.querySelector('[data-action="prev"]').addEventListener("click", () => action("prev"));
  document.querySelector('[data-action="next"]').addEventListener("click", () => action("next"));

  presetButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const index = parseInt(button.dataset.p, 10);
      selectPreset(index);
      if (handlers.onPreset) handlers.onPreset(index);
    });
  });

  const toggleLyrics = () => { if (handlers.onLyricsToggle) handlers.onLyricsToggle(); };
  lyricsBtn.addEventListener("click", toggleLyrics);
  lyricsToggle.addEventListener("click", toggleLyrics);

  lyricModeButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const value = button.dataset.v;
      setLyricModeValue(value);
      if (handlers.onLyricMode) handlers.onLyricMode(value);
      persist("lyricMode", value);
    });
  });

  /* ---------------- shelf controls ---------------- */

  sourceButtons.forEach((button) => {
    button.addEventListener("click", () => {
      setSource(button.dataset.source);
      if (handlers.onSource) handlers.onSource(button.dataset.source);
    });
  });

  presenceButtons.forEach((button) =>
    button.addEventListener("click", () => setPresenceValue(button.dataset.v))
  );
  shelfModeButtons.forEach((button) =>
    button.addEventListener("click", () => setModeValue(button.dataset.v))
  );
  cameraButtons.forEach((button) =>
    button.addEventListener("click", () => setCameraValue(button.dataset.v))
  );

  appearanceBtn.addEventListener("click", () => {
    settingsPanel.hidden = !settingsPanel.hidden;
  });

  function selectSeg(buttons, value) {
    buttons.forEach((button) => button.classList.toggle("active", button.dataset.v === value));
  }
  function setPresenceValue(value) {
    selectSeg(presenceButtons, value);
    if (handlers.onPresence) handlers.onPresence(value);
    persist("shelfPresence", value);
  }
  function setModeValue(value) {
    selectSeg(shelfModeButtons, value);
    if (handlers.onShelfMode) handlers.onShelfMode(value);
    persist("shelfMode", value);
  }
  function setCameraValue(value) {
    selectSeg(cameraButtons, value);
    if (handlers.onCamera) handlers.onCamera(value);
    persist("cameraMode", value);
  }
  function setSource(kind) {
    sourceButtons.forEach((button) => button.classList.toggle("active", button.dataset.source === kind));
  }
  function setQualityValue(name) {
    qualityButtons.forEach((button) => button.classList.toggle("active", button.dataset.q === name));
    qualityEl.textContent = name;
  }

  /* ---------------- settings panel ---------------- */

  qualityButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const value = button.dataset.q;
      setQualityValue(value);
      if (handlers.onQuality) handlers.onQuality(value);
      persist("quality", value);
    });
  });

  bloomBtn.addEventListener("click", () => {
    const next = !bloomBtn.classList.contains("active");
    setBloom(next);
    if (handlers.onBloom) handlers.onBloom(next);
    persist("bloom", next);
  });

  coverParticlesBtn.addEventListener("click", () => {
    const next = !coverParticlesBtn.classList.contains("active");
    setCoverParticles(next);
    if (handlers.onCoverParticles) handlers.onCoverParticles(next);
    persist("coverParticles", next);
  });

  [sizeInput, offxInput, offyInput, angleInput, opacityInput].forEach((input) => {
    input.addEventListener("input", emitLayout);
  });
  sizeInput.addEventListener("change", () => persist("shelfSize", parseFloat(sizeInput.value)));
  offxInput.addEventListener("change", () => persist("shelfOffsetX", parseFloat(offxInput.value)));
  offyInput.addEventListener("change", () => persist("shelfOffsetY", parseFloat(offyInput.value)));
  angleInput.addEventListener("change", () => persist("shelfAngleY", parseFloat(angleInput.value)));
  opacityInput.addEventListener("change", () => persist("shelfOpacity", parseFloat(opacityInput.value)));

  bgColorInput.addEventListener("input", () => {
    if (handlers.onBackgroundColor) handlers.onBackgroundColor(bgColorInput.value);
  });
  bgColorInput.addEventListener("change", () => persist("backgroundColor", bgColorInput.value));
  bgImageBtn.addEventListener("click", () => {
    if (handlers.onPickBackgroundImage) handlers.onPickBackgroundImage();
  });
  bgClearBtn.addEventListener("click", () => {
    if (handlers.onClearBackgroundImage) handlers.onClearBackgroundImage();
  });
  resetBtn.addEventListener("click", () => action("resetSettings"));

  [psizeInput, glowInput, pcountInput].forEach((input) => {
    input.addEventListener("input", () => {
      if (input === psizeInput && handlers.onParticleSize) handlers.onParticleSize(parseFloat(input.value));
      if (input === glowInput && handlers.onGlow) handlers.onGlow(parseFloat(input.value));
      if (input === pcountInput && handlers.onParticleCount) handlers.onParticleCount(parseFloat(input.value));
    });
  });
  psizeInput.addEventListener("change", () => persist("particleSize", parseFloat(psizeInput.value)));
  glowInput.addEventListener("change", () => persist("glow", parseFloat(glowInput.value)));
  pcountInput.addEventListener("change", () => persist("particleCount", parseFloat(pcountInput.value)));
  pcolorButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const value = parseInt(button.dataset.c, 10);
      setColorMode(value);
      if (handlers.onColorMode) handlers.onColorMode(value);
      persist("particleColorMode", value);
    });
  });
  pcolorValueInput.addEventListener("input", () => {
    if (handlers.onParticleColor) handlers.onParticleColor(pcolorValueInput.value);
  });
  pcolorValueInput.addEventListener("change", () => persist("particleColor", pcolorValueInput.value));

  function setBloom(on) {
    bloomBtn.classList.toggle("active", on);
    bloomBtn.textContent = on ? "开" : "关";
  }
  function setCoverParticles(on) {
    coverParticlesBtn.classList.toggle("active", on);
    coverParticlesBtn.textContent = on ? "开" : "关";
  }
  function setColorMode(value) {
    pcolorButtons.forEach((button) => button.classList.toggle("active", parseInt(button.dataset.c, 10) === value));
  }
  function selectPreset(index) {
    presetButtons.forEach((button) => button.classList.toggle("active", parseInt(button.dataset.p, 10) === index));
  }
  function setLyricsToggle(on) {
    lyricsToggle.classList.toggle("active", on);
    lyricsToggle.textContent = on ? "开" : "关";
  }
  function setLyricModeValue(value) {
    selectSeg(lyricModeButtons, value === "particle" ? "particle" : "default");
  }

  /* ---------------- window chrome ---------------- */

  document.querySelectorAll("[data-win]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const name = btn.dataset.win;
      action("window" + name.charAt(0).toUpperCase() + name.slice(1));
    });
  });
  document.getElementById("btn-exit").addEventListener("click", () => action("setDisplayMode", { mode: "normal" }));

  document.querySelectorAll("#resize-edges .rz").forEach((zone) => {
    zone.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) return;
      event.preventDefault();
      action("windowResize", { edge: zone.dataset.edge });
    });
  });

  const dragRegion = document.getElementById("drag-region");
  dragRegion.addEventListener("pointerdown", (event) => {
    if (event.button === 0) action("windowDrag");
  });
  dragRegion.addEventListener("dblclick", () => action("windowMaximize"));

  seek.addEventListener("input", () => {
    dragging = true;
    const pct = seek.value / 1000;
    seek.style.setProperty("--seek", pct * 100 + "%");
    currentEl.textContent = formatTime(durationMs * pct);
  });
  seek.addEventListener("change", () => {
    action("seek", { positionMs: Math.round(durationMs * (seek.value / 1000)) });
    dragging = false;
  });

  return {
    applyState(state) {
      playing = !!state.playing;
      titleEl.textContent = state.title || "还没有歌曲";
      artistEl.textContent = state.artist || "";
      playBtn.innerHTML = playing ? "\uE769" : "\uE768";

      durationMs = state.durationMs || 0;
      totalEl.textContent = formatTime(durationMs);
      if (!dragging && durationMs > 0) {
        const pct = Math.min(1, Math.max(0, (state.positionMs || 0) / durationMs));
        seek.value = Math.round(pct * 1000);
        seek.style.setProperty("--seek", pct * 100 + "%");
        currentEl.textContent = formatTime(state.positionMs || 0);
      }
    },
    applySettings(s) {
      if (!s) return;
      setPresenceValueSilently(s.shelfPresence);
      setModeValueSilently(s.shelfMode);
      setCameraValueSilently(s.cameraMode);
      setSource(s.libraryKind);
      setQualityValue(s.quality);
      setBloom(s.bloom === true);
      setCoverParticles(s.coverParticles === true);
      setLyricsToggle(s.lyrics !== false);
      setLyricModeValue(s.lyricMode);
      if (typeof s.shelfSize === "number") sizeInput.value = s.shelfSize;
      if (typeof s.shelfOffsetX === "number") offxInput.value = s.shelfOffsetX;
      if (typeof s.shelfOffsetY === "number") offyInput.value = s.shelfOffsetY;
      if (typeof s.shelfAngleY === "number") angleInput.value = s.shelfAngleY;
      if (typeof s.shelfOpacity === "number") opacityInput.value = s.shelfOpacity;
      if (s.backgroundColor) bgColorInput.value = s.backgroundColor;
      if (typeof s.particleSize === "number") psizeInput.value = s.particleSize;
      if (typeof s.glow === "number") glowInput.value = s.glow;
      if (typeof s.particleCount === "number") pcountInput.value = s.particleCount;
      setColorMode(typeof s.particleColorMode === "number" ? s.particleColorMode : 1);
      if (s.particleColor) pcolorValueInput.value = s.particleColor;
      selectPreset(typeof s.coverPreset === "number" ? s.coverPreset : 0);
    },
    setPreset(index) { selectPreset(index); },
    setLyricsActive(active) {
      const on = !!active;
      lyricsBtn.classList.toggle("active", on);
      setLyricsToggle(on);
    },
    setNowPlayingVisible(visible) { nowPlayingEl.style.display = visible ? "" : "none"; },
    setSource,
    setFps(value) { fpsEl.textContent = value + " fps"; },
    setBridge(text) { bridgeEl.textContent = "bridge: " + text; },
    setQuality(name) { setQualityValue(name); },
    setAccent(hex) { document.documentElement.style.setProperty("--accent", hex || "#22c55e"); },
    setParticleColorValue(hex) { if (hex) pcolorValueInput.value = hex; },
  };

  // applySettings helpers without echo (no callbacks/persist)
  function setPresenceValueSilently(value) {
    selectSeg(presenceButtons, value);
    if (handlers.onPresence) handlers.onPresence(value);
  }
  function setModeValueSilently(value) {
    selectSeg(shelfModeButtons, value);
    if (handlers.onShelfMode) handlers.onShelfMode(value);
  }
  function setCameraValueSilently(value) {
    selectSeg(cameraButtons, value);
    if (handlers.onCamera) handlers.onCamera(value);
  }
}
