/* Minimal particle field: a clean grid of white / blue points floating in a void. The grid ripples
 * into audio-driven wave patterns (low bands lift broad swells, highs add fine ripples) while a
 * sparse star layer drifts around it. Soft additive discs + bloom keep it airy and elegant.
 * Honours the shared particle settings (size / count / colour mode) like the shape presets do. */

import * as THREE from "three";

const SPAN = 40;

const WAVE_VERT = `
  attribute float aRand;
  uniform float uTime;
  uniform float uBass;
  uniform float uMid;
  uniform float uTreble;
  uniform float uHeight;
  uniform float uSize;
  uniform float uSizeMul;
  uniform float uPulse;
  uniform float uColorMode;
  uniform vec3 uMonoColor;
  uniform vec3 uAccent;
  varying vec3 vColor;
  varying float vAlpha;

  vec3 hsv2rgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
  }

  void main() {
    vec3 p = position;
    float x = p.x;
    float z = p.z;

    float wave = 0.0;
    wave += sin(x * 0.28 + uTime * 0.8) * cos(z * 0.24 + uTime * 0.6) * (0.55 + uBass * 2.6);
    wave += sin((x + z) * 0.16 - uTime * 0.45) * (0.35 + uMid * 1.8);
    wave += sin(x * 0.85 + uTime * 1.5) * 0.16 * (0.35 + uTreble * 2.4);
    wave += sin(length(vec2(x, z)) * 0.42 - uTime * 1.1) * (0.35 + uBass * 1.4);
    p.y += wave;

    float t = clamp(wave * 0.22 + 0.5, 0.0, 1.0);
    vec3 cool = vec3(0.16, 0.34, 0.85);
    vec3 ice = vec3(0.82, 0.9, 1.0);
    if (uColorMode < 0.5) {
      // 单色: user tint, brightness following the wave height
      vColor = uMonoColor * (0.5 + t * 0.7);
    } else if (uColorMode > 1.5) {
      // 彩色: hue follows the wave and drifts over time
      vColor = hsv2rgb(vec3(fract(0.55 + t * 0.3 + uTime * 0.03), 0.72, 0.55 + t * 0.35));
    } else {
      vColor = mix(cool, ice, t) + aRand * 0.05;
    }
    vAlpha = 0.35 + t * 0.55 + uPulse * 0.2;

    vec4 mv = modelViewMatrix * vec4(p, 1.0);
    gl_Position = projectionMatrix * mv;
    gl_PointSize = uSize * uSizeMul * (uHeight / -mv.z) * (0.6 + aRand * 0.7);
  }
`;

const SOFT_FRAG = `
  varying vec3 vColor;
  varying float vAlpha;
  uniform float uGlow;
  void main() {
    float d = length(gl_PointCoord - 0.5);
    if (d > 0.5) discard;
    // Soft feathered disc: a small solid core inside a wide halo.
    float halo = smoothstep(0.5, 0.0, d);
    float core = smoothstep(0.18, 0.0, d);
    float a = (halo * 0.55 + core * 0.75) * vAlpha * uGlow;
    gl_FragColor = vec4(vColor * a, a);
  }
`;

const STAR_VERT = `
  attribute float aRand;
  uniform float uTime;
  uniform float uHeight;
  uniform float uSizeMul;
  varying vec3 vColor;
  varying float vAlpha;
  void main() {
    vec3 p = position;
    p.y += sin(uTime * 0.3 + aRand * 6.28) * 1.4;
    p.x += cos(uTime * 0.2 + aRand * 6.28) * 1.1;
    float t = step(0.5, aRand);
    vColor = mix(vec3(0.6, 0.75, 1.0), vec3(1.0), t);
    vAlpha = 0.3 + 0.5 * fract(aRand * 7.3);
    vec4 mv = modelViewMatrix * vec4(p, 1.0);
    gl_Position = projectionMatrix * mv;
    gl_PointSize = (1.2 + aRand * 2.2) * uSizeMul * (uHeight / -mv.z) * 0.12;
  }
`;

export function createMinimalField(scene, tier, accentHex) {
  const group = new THREE.Group();
  group.visible = false;
  scene.add(group);

  const baseGrid = Math.max(64, Math.min(180, Math.round((tier && tier.grid ? tier.grid : 160) * 0.6)));
  let columns = baseGrid;

  function buildGrid(n) {
    const total = n * n;
    const positions = new Float32Array(total * 3);
    const rands = new Float32Array(total);
    let p = 0;
    for (let j = 0; j < n; j++) {
      for (let i = 0; i < n; i++) {
        const u = i / (n - 1) - 0.5;
        const v = j / (n - 1) - 0.5;
        positions[p * 3] = u * SPAN;
        positions[p * 3 + 1] = 0;
        positions[p * 3 + 2] = v * SPAN;
        rands[p] = Math.random();
        p++;
      }
    }
    return { positions, rands };
  }

  const initial = buildGrid(columns);
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(initial.positions, 3));
  geometry.setAttribute("aRand", new THREE.BufferAttribute(initial.rands, 1));

  const material = new THREE.ShaderMaterial({
    uniforms: {
      uTime: { value: 0 },
      uBass: { value: 0 },
      uMid: { value: 0 },
      uTreble: { value: 0 },
      uHeight: { value: 900 },
      uSize: { value: (SPAN / (columns - 1)) * 1.2 },
      uSizeMul: { value: 1 },
      uPulse: { value: 0 },
      uGlow: { value: 1 },
      uColorMode: { value: 1 },
      uMonoColor: { value: new THREE.Color(accentHex || "#22c55e") },
      uAccent: { value: new THREE.Color(accentHex || "#22c55e") },
    },
    vertexShader: WAVE_VERT,
    fragmentShader: SOFT_FRAG,
    transparent: true,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const field = new THREE.Points(geometry, material);
  field.frustumCulled = false;
  group.add(field);

  /* ---------------- floating star layer ---------------- */
  const STAR_COUNT = 1100;
  const starPos = new Float32Array(STAR_COUNT * 3);
  const starRand = new Float32Array(STAR_COUNT);
  for (let i = 0; i < STAR_COUNT; i++) {
    starPos[i * 3] = (Math.random() - 0.5) * 220;
    starPos[i * 3 + 1] = -20 + Math.random() * 90;
    starPos[i * 3 + 2] = (Math.random() - 0.5) * 220;
    starRand[i] = Math.random();
  }
  const starGeometry = new THREE.BufferGeometry();
  starGeometry.setAttribute("position", new THREE.BufferAttribute(starPos, 3));
  starGeometry.setAttribute("aRand", new THREE.BufferAttribute(starRand, 1));
  const starMaterial = new THREE.ShaderMaterial({
    uniforms: {
      uTime: { value: 0 },
      uHeight: { value: 900 },
      uGlow: { value: 1 },
      uSizeMul: { value: 1 },
    },
    vertexShader: STAR_VERT,
    fragmentShader: SOFT_FRAG,
    transparent: true,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const stars = new THREE.Points(starGeometry, starMaterial);
  stars.frustumCulled = false;
  group.add(stars);

  return {
    group,
    setVisible(visible) {
      group.visible = !!visible;
    },
    setHeight(pixelsPerWorld) {
      material.uniforms.uHeight.value = pixelsPerWorld;
      starMaterial.uniforms.uHeight.value = pixelsPerWorld;
    },
    setAccent(hex) {
      if (!hex || !/^#?[0-9a-f]{6}$/i.test(hex)) return;
      material.uniforms.uAccent.value.set(hex);
    },
    setSizeMultiplier(mul) {
      material.uniforms.uSizeMul.value = Math.max(0.1, mul || 1);
      starMaterial.uniforms.uSizeMul.value = Math.max(0.1, mul || 1);
    },
    setGridMultiplier(mul) {
      const n = Math.max(48, Math.min(240, Math.round(baseGrid * (mul || 1))));
      if (n === columns) return;
      columns = n;
      const built = buildGrid(n);
      geometry.setAttribute("position", new THREE.BufferAttribute(built.positions, 3));
      geometry.setAttribute("aRand", new THREE.BufferAttribute(built.rands, 1));
      material.uniforms.uSize.value = (SPAN / (n - 1)) * 1.2;
    },
    setColorMode(mode) {
      material.uniforms.uColorMode.value = mode | 0;
    },
    setMonoColor(hex) {
      if (!hex || !/^#?[0-9a-f]{6}$/i.test(hex)) return;
      material.uniforms.uMonoColor.value.set(hex);
    },
    update(dt, a) {
      material.uniforms.uTime.value += dt;
      material.uniforms.uBass.value = a.pulse !== undefined ? a.pulse : a.bass;
      material.uniforms.uMid.value = a.mid;
      material.uniforms.uTreble.value = a.treble;
      material.uniforms.uPulse.value = a.beat;
      starMaterial.uniforms.uTime.value += dt;
      group.rotation.y += dt * 0.02; // slow, meditative drift
    },
    dispose() {
      geometry.dispose();
      starGeometry.dispose();
      material.dispose();
      starMaterial.dispose();
      scene.remove(group);
    },
  };
}
