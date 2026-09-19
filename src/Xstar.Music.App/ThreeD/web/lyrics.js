/* 3D lyric stage: a floating textured plane in front of the cover. The active line is
 * centred, brightened and glowing; neighbouring lines fall off with distance. */

import * as THREE from "three";

const W = 1024;
const H = 640;
const VISIBLE = 7;
const FONT = '"Segoe UI", "Microsoft YaHei UI", system-ui, sans-serif';

function rgba(hex, alpha) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || "");
  const n = m ? parseInt(m[1], 16) : 0x22c55e;
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
}

function clip(ctx, text, maxWidth) {
  if (!text) return "";
  if (ctx.measureText(text).width <= maxWidth) return text;
  let value = text;
  while (value.length > 1 && ctx.measureText(value + "…").width > maxWidth) value = value.slice(0, -1);
  return value + "…";
}

export function createLyrics(scene) {
  const canvas = document.createElement("canvas");
  canvas.width = W;
  canvas.height = H;
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.minFilter = THREE.LinearFilter;
  texture.generateMipmaps = false;

  const material = new THREE.MeshBasicMaterial({
    map: texture,
    transparent: true,
    depthWrite: false,
    depthTest: false,
    side: THREE.DoubleSide,
    opacity: 0,
  });
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(5.6, 3.5), material);
  mesh.position.set(0, 0.15, 1.9);
  mesh.renderOrder = 310;
  mesh.visible = false;
  scene.add(mesh);

  let lines = [];
  let active = 0;
  let accent = "#22c55e";
  let enabled = true;

  function redraw() {
    const ctx = canvas.getContext("2d");
    ctx.clearRect(0, 0, W, H);
    if (lines.length === 0) {
      texture.needsUpdate = true;
      return;
    }

    const rowHeight = H / VISIBLE;
    // Keep the active line vertically centred, even near the start/end of the song.
    let first = active - Math.floor(VISIBLE / 2);
    if (first + VISIBLE > lines.length) first = lines.length - VISIBLE;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";

    for (let i = 0; i < VISIBLE; i++) {
      const index = first + i;
      if (index < 0 || index >= lines.length) continue;
      const text = lines[index] || "♪";
      const y = rowHeight * (i + 0.5);
      const distance = Math.abs(index - active);
      const isActive = index === active;

      if (isActive) {
        const glow = ctx.createRadialGradient(W / 2, y, 0, W / 2, y, W * 0.26);
        glow.addColorStop(0, rgba(accent, 0.12));
        glow.addColorStop(1, "rgba(0,0,0,0)");
        ctx.save();
        ctx.globalCompositeOperation = "lighter";
        ctx.fillStyle = glow;
        ctx.fillRect(0, y - rowHeight * 0.7, W, rowHeight * 1.4);
        ctx.restore();
      }

      const size = isActive ? 56 : Math.max(22, 40 - distance * 5);
      const alpha = isActive ? 1 : Math.max(0.08, 0.44 - distance * 0.13);
      ctx.font = `${isActive ? 700 : 400} ${size}px ${FONT}`;
      ctx.fillStyle = isActive ? "#ffffff" : `rgba(226,232,240,${alpha})`;
      ctx.fillText(clip(ctx, text, W - 140), W / 2, y);
    }

    texture.needsUpdate = true;
  }

  function syncVisibility() {
    mesh.visible = enabled && lines.length > 0;
  }

  return {
    setLines(next) {
      lines = Array.isArray(next) ? next.map((line) => line.text || "") : [];
      if (active >= lines.length) active = 0;
      redraw();
      syncVisibility();
    },
    setActiveIndex(index) {
      const next = Math.max(0, index | 0);
      if (next === active) return;
      active = next;
      redraw();
    },
    setVisible(value) {
      enabled = !!value;
      syncVisibility();
    },
    isVisible() {
      return mesh.visible;
    },
    setAccent(hex) {
      accent = hex || accent;
      redraw();
    },
    update(dt, a, time) {
      mesh.position.y = 0.15 + Math.sin(time * 0.5) * 0.03;
      material.opacity = mesh.visible ? 0.9 + a.bass * 0.1 : 0;
      const scale = 1 + a.beat * 0.015;
      mesh.scale.setScalar(scale);
    },
    dispose() {
      mesh.geometry.dispose();
      material.dispose();
      texture.dispose();
      scene.remove(mesh);
    },
  };
}
