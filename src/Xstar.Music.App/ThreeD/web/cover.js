/* Album-cover particle cloud.
 * A grid of points samples the cover texture; luminance drives depth and several morph
 * presets react strongly to the audio bands. Bloom makes it glow. */

import * as THREE from "three";

export const COVER_PRESETS = ["浮雕", "波纹", "球体", "漩涡", "律动"];

const SIZE = 4.8;
const DEPTH = 1.6;

const VERT = `
  uniform sampler2D uCover;
  uniform float uTime;
  uniform float uBass;
  uniform float uMid;
  uniform float uTreble;
  uniform float uBeat;
  uniform float uSize;
  uniform float uSizeScale;
  uniform float uSizeMul;
  uniform float uHeight;
  uniform float uDepth;
  uniform float uPreset;
  uniform float uColorMode;
  uniform vec3 uAccent;
  uniform vec3 uMonoColor;

  attribute vec2 aUv;
  attribute float aRand;

  varying vec3 vColor;
  varying float vBright;

  vec3 hsv2rgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
  }

  void main() {
    vec3 pos = position;
    vec4 tex = texture2D(uCover, aUv);
    float lum = tex.rgb.r * 0.299 + tex.rgb.g * 0.587 + tex.rgb.b * 0.114;
    if (uColorMode < 0.5) {
      // 单色: user-chosen tint, brightness follows the cover's luminance
      vColor = uMonoColor * (0.42 + lum * 0.95);
    } else if (uColorMode > 1.5) {
      // 彩色: hue sweeps across the surface and slowly drifts, saturation stays vivid
      float hue = fract(aUv.x * 0.16 + aUv.y * 0.12 + lum * 0.55 + uTime * 0.02);
      vColor = hsv2rgb(vec3(hue, 0.72, 0.5 + lum * 0.5));
    } else {
      // 自然: the original cover colours
      vColor = tex.rgb;
    }
    vBright = lum;

    float r = length(pos.xy);
    float ang = atan(pos.y, pos.x);
    float t = uTime;

    // bass opens the relief; beat adds a sharp outward kick
    float relief = uDepth * (0.55 + uBass * 1.7);

    if (uPreset < 0.5) {
      pos.z = lum * relief;
    } else if (uPreset < 1.5) {
      // concentric ripples with a finer second harmonic for more surface detail
      pos.z = lum * relief
        + sin(r * 3.0 - t * 2.0) * (0.10 + uBass * 1.0)
        + sin(r * 7.0 - t * 3.6) * (0.045 + uBass * 0.45);
    } else if (uPreset < 2.5) {
      float phi = (aUv.y - 0.5) * 3.14159265;
      float theta = (aUv.x - 0.5) * 6.2831853;
      float rad = 2.2 + lum * uDepth * 0.7 + uBass * 1.0 + uBeat * 0.35;
      pos = vec3(cos(phi) * sin(theta), sin(phi), cos(phi) * cos(theta)) * rad;
    } else if (uPreset < 3.5) {
      float rr = r * (1.0 + lum * 0.5 + uBass * 0.25);
      float a2 = ang + rr * 0.7 - t * (0.6 + uBass * 1.8);
      pos.xy = vec2(cos(a2), sin(a2)) * rr;
      pos.z = lum * relief - r * 0.4 + sin(t * 1.5 + r * 4.0) * 0.12;
    } else {
      // Rhythm: a gentle travelling swell — the surface breathes in depth with the music.
      float wave = sin(r * 1.6 - t * 1.3) * 0.5 + 0.5;
      float kick = uBass * 0.85 + uBeat * 0.45;
      pos.z += (lum - 0.5) * relief * 0.5;
      pos.z += kick * wave * (0.5 + aRand * 0.4);
    }

    // radial breathing + per-particle jitter
    pos.xy *= 1.0 + uBass * 0.12 + uBeat * 0.05;
    pos.z += (uMid * 0.8 + uTreble * 0.5) * (aRand - 0.5);

    vec4 mv = modelViewMatrix * vec4(pos, 1.0);
    gl_Position = projectionMatrix * mv;
    gl_PointSize = uSize * uSizeScale * uSizeMul * (uHeight / -mv.z) * (0.85 + aRand * 0.3) * (1.0 + uBass * 0.35);
  }
`;

const FRAG = `
  uniform float uOpacity;
  uniform float uBass;
  varying vec3 vColor;
  varying float vBright;

  void main() {
    vec2 c = gl_PointCoord - 0.5;
    float d = length(c);
    if (d > 0.5) discard;
    // Mostly-solid disc with a thin feather keeps the particle image crisp.
    float a = 1.0 - smoothstep(0.34, 0.5, d);
    vec3 col = vColor * (0.72 + vBright * 0.75) * (0.9 + uBass * 0.45);
    gl_FragColor = vec4(col, a * uOpacity);
  }
`;

const FONT = '"Segoe UI", "Microsoft YaHei UI", system-ui, sans-serif';

function hexWithAlpha(hex, alpha) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || "");
  const n = m ? parseInt(m[1], 16) : 0x22c55e;
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
}

/* Wrap a title into lines that fit `maxWidth`. CJK breaks per character, latin per word. */
function wrapTokens(ctx, text, maxWidth) {
  const tokens = text.match(/[A-Za-z0-9'’\-_.&]+|[^\s]/g) || [];
  const lines = [];
  let line = "";
  for (const token of tokens) {
    const spaced = line && /[A-Za-z0-9'’\-_.&]$/.test(line) && /[A-Za-z0-9'’\-_.&]/.test(token[0]);
    const test = line ? line + (spaced ? " " : "") + token : token;
    if (line && ctx.measureText(test).width > maxWidth) {
      lines.push(line);
      line = token;
    } else {
      line = test;
    }
  }
  if (line) lines.push(line);
  return lines;
}

/* Largest font size (down to a floor) whose wrapped block fits the canvas, then draw it centred. */
function drawTitle(ctx, text, size, accent) {
  const maxWidth = size * 0.82;
  const maxHeight = size * 0.78;
  const minSize = Math.round(size * 0.09);
  let fontSize = Math.round(size * 0.9);
  let lines = [text];
  while (fontSize > minSize) {
    ctx.font = `800 ${fontSize}px ${FONT}`;
    lines = wrapTokens(ctx, text, maxWidth);
    const blockHeight = lines.length * fontSize * 1.14;
    const widest = lines.reduce((m, l) => Math.max(m, ctx.measureText(l).width), 0);
    if (blockHeight <= maxHeight && widest <= maxWidth) break;
    fontSize -= 2;
  }
  ctx.font = `800 ${fontSize}px ${FONT}`;
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillStyle = "#ffffff";
  ctx.shadowColor = accent || "#22c55e";
  ctx.shadowBlur = fontSize * 0.4;
  const lineHeight = fontSize * 1.14;
  const startY = size / 2 - ((lines.length - 1) * lineHeight) / 2;
  lines.forEach((line, i) => ctx.fillText(line, size / 2, startY + i * lineHeight));
  ctx.shadowBlur = 0;
  ctx.textAlign = "left";
  ctx.textBaseline = "alphabetic";
}

/* No artwork: rasterise the track title so the particle cloud forms the song name. */
function makePlaceholder(accent, title) {
  const size = 512;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "#080a11";
  ctx.fillRect(0, 0, size, size);
  const gradient = ctx.createRadialGradient(size * 0.5, size * 0.44, 8, size * 0.5, size * 0.5, size * 0.75);
  gradient.addColorStop(0, hexWithAlpha(accent, 0.3));
  gradient.addColorStop(0.55, "rgba(29,35,64,0.35)");
  gradient.addColorStop(1, "#06070c");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, size, size);

  const trimmed = (title || "").trim();
  if (trimmed) {
    drawTitle(ctx, trimmed, size, accent);
  } else {
    ctx.globalAlpha = 0.5;
    for (let i = 0; i < 46; i++) {
      const y = Math.random() * size;
      ctx.strokeStyle = "rgba(255,255,255,0.12)";
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(0, y);
      ctx.lineTo(size, y);
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
  }

  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

export function createCover(scene, grid, accentHex) {
  let currentGrid = grid;

  function buildArrays(n) {
    const total = n * n;
    const positions = new Float32Array(total * 3);
    const uvs = new Float32Array(total * 2);
    const rands = new Float32Array(total);
    let p = 0;
    for (let j = 0; j < n; j++) {
      for (let i = 0; i < n; i++) {
        const u = i / (n - 1);
        const v = j / (n - 1);
        positions[p * 3] = (u - 0.5) * SIZE;
        positions[p * 3 + 1] = (v - 0.5) * SIZE;
        positions[p * 3 + 2] = 0;
        uvs[p * 2] = u;
        uvs[p * 2 + 1] = v;
        rands[p] = Math.random();
        p++;
      }
    }
    return { positions, uvs, rands };
  }

  const initial = buildArrays(grid);
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(initial.positions, 3));
  geometry.setAttribute("aUv", new THREE.BufferAttribute(initial.uvs, 2));
  geometry.setAttribute("aRand", new THREE.BufferAttribute(initial.rands, 1));

  const pointWorld = SIZE / (grid - 1);
  const material = new THREE.ShaderMaterial({
    uniforms: {
      uCover: { value: makePlaceholder(accentHex || "#22c55e", "") },
      uTime: { value: 0 },
      uBass: { value: 0 },
      uMid: { value: 0 },
      uTreble: { value: 0 },
      uBeat: { value: 0 },
      uSize: { value: pointWorld * 1.02 },
      uSizeScale: { value: 1 },
      uSizeMul: { value: 1 },
      uHeight: { value: 900 },
      uDepth: { value: DEPTH },
      uPreset: { value: 0 },
      uColorMode: { value: 1 },
      uAccent: { value: new THREE.Color(accentHex || "#22c55e") },
      uMonoColor: { value: new THREE.Color(accentHex || "#22c55e") },
      uOpacity: { value: 0.97 },
    },
    vertexShader: VERT,
    fragmentShader: FRAG,
    transparent: true,
    depthWrite: false,
    blending: THREE.NormalBlending,
  });

  const group = new THREE.Group();
  const points = new THREE.Points(geometry, material);
  points.frustumCulled = false;
  group.add(points);
  scene.add(group);

  const loader = new THREE.TextureLoader();
  let currentTexture = material.uniforms.uCover.value;
  let loadToken = 0;
  let accentHexValue = accentHex || "#22c55e";
  let titleValue = "";
  let imageTexture = null;
  let overrideText = null;

  /* Picks the particle mask: an explicit lyric line wins, then the cover, then the song title. */
  function refreshCover() {
    const next = overrideText
      ? makePlaceholder(accentHexValue, overrideText)
      : imageTexture || makePlaceholder(accentHexValue, titleValue);
    if (next === currentTexture) return;
    const previous = currentTexture;
    material.uniforms.uCover.value = next;
    currentTexture = next;
    if (previous && previous !== next && previous !== imageTexture) previous.dispose();
  }

  function setImage(dataUri) {
    const token = ++loadToken;
    loader.load(
      dataUri,
      (texture) => {
        if (token !== loadToken) {
          texture.dispose();
          return;
        }
        texture.colorSpace = THREE.SRGBColorSpace;
        texture.minFilter = THREE.LinearFilter;
        texture.magFilter = THREE.LinearFilter;
        if (imageTexture && imageTexture !== texture) imageTexture.dispose();
        imageTexture = texture;
        refreshCover();
      },
      undefined,
      () => {
        // No decodable cover: keep the title / lyric particle mask.
        if (token !== loadToken) return;
        imageTexture = null;
        refreshCover();
      }
    );
  }

  return {
    group,
    material,
    grid,
    setImage,
    /* No artwork for the current track: shape the cloud from the song title. */
    setTitle(title) {
      const next = title || "";
      if (next === titleValue) return;
      titleValue = next;
      refreshCover();
    },
    /* Lyric particle mode: the active line overrides the mask; null falls back to cover / title. */
    setLyricText(text) {
      const next = (text || "").trim() || null;
      if (next === overrideText) return;
      overrideText = next;
      refreshCover();
    },
    clearImage() {
      loadToken++;
      imageTexture = null;
      refreshCover();
    },
    setAccent(hex) {
      if (hex) {
        const changed = hex !== accentHexValue;
        accentHexValue = hex;
        material.uniforms.uAccent.value.set(hex);
        if (changed && !imageTexture) refreshCover();
      }
    },
    setSizeMultiplier(mul) {
      material.uniforms.uSizeMul.value = Math.max(0.1, mul || 1);
    },
    setColorMode(mode) {
      material.uniforms.uColorMode.value = mode | 0;
    },
    setMonoColor(hex) {
      if (!hex || !/^#?[0-9a-f]{6}$/i.test(hex)) return;
      material.uniforms.uMonoColor.value.set(hex);
    },
    setGridMultiplier(mul) {
      const n = Math.max(32, Math.min(256, Math.round(grid * (mul || 1))));
      if (n === currentGrid) return;
      currentGrid = n;
      const built = buildArrays(n);
      geometry.setAttribute("position", new THREE.BufferAttribute(built.positions, 3));
      geometry.setAttribute("aUv", new THREE.BufferAttribute(built.uvs, 2));
      geometry.setAttribute("aRand", new THREE.BufferAttribute(built.rands, 1));
      material.uniforms.uSize.value = (SIZE / (n - 1)) * 1.02;
    },
    setPreset(index) {
      material.uniforms.uPreset.value = index;
      // Finer dots for the presets whose surface detail matters most.
      const sizeScales = [1.0, 0.95, 0.85, 0.95, 1.0];
      material.uniforms.uSizeScale.value = sizeScales[index] ?? 1;
    },
    setPixelsPerWorld(pixelsPerWorld) {
      material.uniforms.uHeight.value = pixelsPerWorld;
    },
    update(dt, a, time) {
      const pump = a.pulse !== undefined ? a.pulse : a.bass;
      material.uniforms.uTime.value = time;
      material.uniforms.uBass.value = pump;
      material.uniforms.uMid.value = a.mid;
      material.uniforms.uTreble.value = a.treble;
      material.uniforms.uBeat.value = a.beat;

      group.rotation.y = Math.sin(time * 0.2) * 0.16 + pump * 0.06;
      group.rotation.x = Math.cos(time * 0.17) * 0.1;
      const scale = 1 + pump * 0.22 + a.beat * 0.1;
      group.scale.setScalar(scale);
    },
    dispose() {
      geometry.dispose();
      if (currentTexture && currentTexture !== imageTexture) currentTexture.dispose();
      imageTexture?.dispose();
      material.dispose();
      scene.remove(group);
    },
  };
}
