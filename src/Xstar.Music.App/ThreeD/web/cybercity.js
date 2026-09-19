/* Cyberpunk night city: neon towers with lit windows, a wet reflective street, a holographic
 * spectrum wall rising from the pavement, bass shockwaves, flying cars with light trails and rain.
 * Everything is procedural (canvas textures + shaders) — no external assets. */

import * as THREE from "three";

export const GROUND_Y = -2.4;
const STREET_HALF = 6;
const CITY_SPAN = 240;

function glowTexture() {
  const size = 128;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  const gradient = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
  gradient.addColorStop(0, "rgba(255,255,255,1)");
  gradient.addColorStop(0.35, "rgba(255,255,255,0.5)");
  gradient.addColorStop(1, "rgba(255,255,255,0)");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, size, size);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

function rainTexture() {
  const w = 16;
  const h = 64;
  const canvas = document.createElement("canvas");
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext("2d");
  const gradient = ctx.createLinearGradient(0, 0, 0, h);
  gradient.addColorStop(0, "rgba(200,225,255,0)");
  gradient.addColorStop(0.45, "rgba(200,225,255,0.85)");
  gradient.addColorStop(1, "rgba(200,225,255,0)");
  ctx.fillStyle = gradient;
  ctx.fillRect(w / 2 - 1.5, 0, 3, h);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

/* Vertical soft bar: used for neon light smears on the wet street and car trails. */
function streakTexture() {
  const size = 256;
  const canvas = document.createElement("canvas");
  canvas.width = 32;
  canvas.height = size;
  const ctx = canvas.getContext("2d");
  const gradient = ctx.createLinearGradient(0, 0, 0, size);
  gradient.addColorStop(0, "rgba(255,255,255,0.9)");
  gradient.addColorStop(0.5, "rgba(255,255,255,0.28)");
  gradient.addColorStop(1, "rgba(255,255,255,0)");
  ctx.fillStyle = gradient;
  for (let i = 0; i < 32; i++) {
    const a = (1 - Math.abs(i / 32 - 0.5) * 2) * 0.9;
    ctx.globalAlpha = Math.max(0, a);
    ctx.fillRect(i, 0, 1, size);
  }
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

function signTexture() {
  const w = 128;
  const h = 64;
  const canvas = document.createElement("canvas");
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "rgba(6,8,20,0.9)";
  ctx.fillRect(0, 0, w, h);
  ctx.strokeStyle = "rgba(255,255,255,0.9)";
  ctx.lineWidth = 3;
  ctx.strokeRect(4, 4, w - 8, h - 8);
  for (let i = 0; i < 5; i++) {
    ctx.globalAlpha = 0.5 + Math.random() * 0.5;
    ctx.fillStyle = "rgba(255,255,255,0.9)";
    const bw = 6 + Math.random() * 22;
    const bh = 4 + Math.random() * 10;
    ctx.fillRect(12 + Math.random() * (w - 40), 14 + Math.random() * (h - 34), bw, bh);
  }
  ctx.globalAlpha = 1;
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

/* Vertical gradient for the glowing skyline haze on the horizon. */
function horizonTexture() {
  const w = 8;
  const h = 256;
  const canvas = document.createElement("canvas");
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext("2d");
  const gradient = ctx.createLinearGradient(0, 0, 0, h);
  gradient.addColorStop(0, "rgba(120,60,255,0)");
  gradient.addColorStop(0.65, "rgba(150,70,255,0.35)");
  gradient.addColorStop(0.88, "rgba(220,90,200,0.55)");
  gradient.addColorStop(1, "rgba(255,120,190,0.15)");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, w, h);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

/* Dark wet asphalt: faint grid + scattered glints. The neon colour comes from the smears on top. */
function asphaltTexture() {
  const size = 512;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "#04050b";
  ctx.fillRect(0, 0, size, size);
  ctx.strokeStyle = "rgba(90,140,255,0.10)";
  ctx.lineWidth = 1;
  for (let i = 0; i <= size; i += 32) {
    ctx.beginPath();
    ctx.moveTo(i, 0);
    ctx.lineTo(i, size);
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(0, i);
    ctx.lineTo(size, i);
    ctx.stroke();
  }
  for (let i = 0; i < 900; i++) {
    ctx.fillStyle = `rgba(150,190,255,${Math.random() * 0.10})`;
    ctx.fillRect(Math.random() * size, Math.random() * size, 1, 1);
  }
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.repeat.set(10, 10);
  return texture;
}

const BUILDING_VERT = `
  varying vec3 vWorld;
  varying vec3 vNormalW;
  void main() {
    vWorld = (modelMatrix * vec4(position, 1.0)).xyz;
    vNormalW = normalize(mat3(modelMatrix) * normal);
    gl_Position = projectionMatrix * viewMatrix * vec4(vWorld, 1.0);
  }
`;

const BUILDING_FRAG = `
  uniform vec3 uNeon;
  uniform vec3 uFog;
  uniform float uPulse;
  uniform float uFogDensity;
  varying vec3 vWorld;
  varying vec3 vNormalW;

  float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

  void main() {
    // Window grid laid out in world space so it stays uniform no matter how a tower is scaled.
    vec2 axis = abs(vNormalW.x) > 0.5 ? vec2(vWorld.z, vWorld.y) : vec2(vWorld.x, vWorld.y);
    vec2 cell = floor(axis * 1.4);
    vec2 g = fract(axis * 1.4);
    float wy = smoothstep(0.18, 0.28, g.y) * smoothstep(0.92, 0.82, g.y);
    float wx = smoothstep(0.22, 0.32, g.x) * smoothstep(0.88, 0.78, g.x);
    float lit = step(0.42, hash(cell));
    float flicker = 0.85 + 0.15 * hash(cell + 7.3);

    // A faint per-tower ambient tint keeps the silhouettes readable against the black sky.
    vec3 base = vec3(0.02, 0.025, 0.05) + uNeon * 0.05 + vec3(0.02, 0.012, 0.05) * hash(floor(vWorld.xz * 0.1));
    vec3 warm = mix(vec3(0.6, 0.85, 1.05), uNeon, 0.6);
    float window = wx * wy * lit * flicker;
    vec3 col = base + warm * window * (2.4 + uPulse * 2.6);

    // Fog keeps the far towers from cluttering the frame and sells the depth of the street.
    float dist = length(vWorld - cameraPosition);
    col = mix(col, uFog, 1.0 - exp(-dist * uFogDensity));
    gl_FragColor = vec4(col, 1.0);
  }
`;

export function createCyberCity(scene) {
  const group = new THREE.Group();
  group.visible = false;
  scene.add(group);

  const glow = glowTexture();
  const streak = streakTexture();
  const neonPalette = [0x22d3ee, 0xff4fd8, 0x8b5cf6, 0x22c55e, 0xf59e0b];
  const _color = new THREE.Color();

  /* ---------------- ground ---------------- */
  const ground = new THREE.Mesh(
    new THREE.PlaneGeometry(520, 520),
    new THREE.MeshBasicMaterial({ map: asphaltTexture() })
  );
  ground.rotation.x = -Math.PI / 2;
  ground.position.y = GROUND_Y;
  group.add(ground);

  /* ---------------- towers ---------------- */
  const buildingMaterial = new THREE.ShaderMaterial({
    uniforms: {
      uNeon: { value: new THREE.Color(0x22d3ee) },
      uFog: { value: new THREE.Color(0x140a2e) },
      uPulse: { value: 0 },
      uFogDensity: { value: 0.013 },
    },
    vertexShader: BUILDING_VERT,
    fragmentShader: BUILDING_FRAG,
  });
  const boxGeometry = new THREE.BoxGeometry(1, 1, 1);
  boxGeometry.translate(0, 0.5, 0);

  const towers = [];
  const signs = [];
  for (let i = 0; i < 120; i++) {
    const side = i % 2 === 0 ? -1 : 1;
    const width = 4 + Math.random() * 9;
    const depth = 4 + Math.random() * 9;
    const height = 10 + Math.random() * 60;
    const mesh = new THREE.Mesh(boxGeometry, buildingMaterial);
    mesh.scale.set(width, height, depth);
    mesh.position.set(
      side * (STREET_HALF + 2 + Math.pow(Math.random(), 0.7) * 40),
      GROUND_Y,
      -Math.random() * CITY_SPAN + 30
    );
    mesh.userData.baseZ = mesh.position.z;
    group.add(mesh);
    towers.push(mesh);

    // Aircraft-warning beacons on the tall towers double as rooftop neon.
    if (height > 34) {
      const tint = new THREE.Color(neonPalette[Math.floor(Math.random() * neonPalette.length)]);
      const beacon = new THREE.Sprite(
        new THREE.SpriteMaterial({
          map: glow,
          color: tint,
          transparent: true,
          opacity: 0.8,
          depthWrite: false,
          blending: THREE.AdditiveBlending,
        })
      );
      beacon.position.set(mesh.position.x, GROUND_Y + height + 0.6, mesh.position.z);
      beacon.scale.setScalar(1.7);
      beacon.userData = { baseZ: mesh.position.z, tint, phase: Math.random() * Math.PI * 2, gain: 1.3 };
      group.add(beacon);
      signs.push(beacon);
    }
  }

  /* ---------------- horizon haze ---------------- */
  const horizon = new THREE.Mesh(
    new THREE.PlaneGeometry(900, 150),
    new THREE.MeshBasicMaterial({
      map: horizonTexture(),
      transparent: true,
      opacity: 0.75,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
      depthTest: false,
    })
  );
  horizon.position.set(0, GROUND_Y + 60, -CITY_SPAN + 20);
  horizon.renderOrder = -1;
  group.add(horizon);

  /* ---------------- neon signs + their wet-street smears ---------------- */
  for (let i = 0; i < 34; i++) {
    const side = Math.random() < 0.5 ? -1 : 1;
    const z = -Math.random() * CITY_SPAN + 20;
    const y = GROUND_Y + 2 + Math.random() * 18;
    const tint = new THREE.Color(neonPalette[i % neonPalette.length]);

    const sign = new THREE.Mesh(
      new THREE.PlaneGeometry(3.4 + Math.random() * 3, 1.7 + Math.random() * 1.6),
      new THREE.MeshBasicMaterial({
        map: signTexture(),
        color: tint,
        transparent: true,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        side: THREE.DoubleSide,
      })
    );
    sign.position.set(side * (STREET_HALF + 0.4), y, z);
    sign.rotation.y = side > 0 ? -Math.PI / 2 : Math.PI / 2;
    sign.userData = { baseZ: z, tint, phase: Math.random() * Math.PI * 2, gain: 0.7 + Math.random() * 0.6 };
    group.add(sign);
    signs.push(sign);

    // Reflected smear lying on the wet ground toward the viewer.
    const smear = new THREE.Mesh(
      new THREE.PlaneGeometry(3 + Math.random() * 3, 22 + Math.random() * 16),
      new THREE.MeshBasicMaterial({
        map: streak,
        color: tint,
        transparent: true,
        opacity: 0.35,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
      })
    );
    smear.rotation.x = -Math.PI / 2;
    smear.rotation.z = Math.random() * Math.PI;
    smear.position.set(side * (STREET_HALF - 0.6), GROUND_Y + 0.02, z + 6);
    smear.userData = { baseZ: z + 6, tint, phase: sign.userData.phase, gain: sign.userData.gain };
    group.add(smear);
    signs.push(smear);
  }

  /* ---------------- scattered neon facade lights ---------------- */
  for (let i = 0; i < 80; i++) {
    const side = Math.random() < 0.5 ? -1 : 1;
    const tint = new THREE.Color(neonPalette[i % neonPalette.length]);
    const light = new THREE.Sprite(
      new THREE.SpriteMaterial({
        map: glow,
        color: tint,
        transparent: true,
        opacity: 0.5,
        depthWrite: false,
        blending: THREE.AdditiveBlending,
      })
    );
    const z = -Math.random() * CITY_SPAN + 20;
    light.position.set(side * (STREET_HALF + Math.random() * 32), GROUND_Y + 2 + Math.random() * 44, z);
    light.scale.setScalar(1.2 + Math.random() * 3.6);
    light.userData = { baseZ: z, tint, phase: Math.random() * Math.PI * 2, gain: 0.9 + Math.random() * 0.9 };
    group.add(light);
    signs.push(light);
  }

  /* ---------------- wet-street light spill ---------------- */
  const spill = new THREE.Mesh(
    new THREE.PlaneGeometry(34, 70),
    new THREE.MeshBasicMaterial({
      map: glow,
      color: 0x3a2a6a,
      transparent: true,
      opacity: 0.22,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
    })
  );
  spill.rotation.x = -Math.PI / 2;
  spill.position.set(0, GROUND_Y + 0.01, -12);
  group.add(spill);

  /* ---------------- holographic spectrum wall ---------------- */
  const EQ_COUNT = 40;
  const EQ_MAX = 8;
  const eqGroup = new THREE.Group();
  eqGroup.position.set(0, GROUND_Y, -6); // closer to the camera
  eqGroup.scale.setScalar(0.5); // and smaller overall
  group.add(eqGroup);
  const eqBars = [];
  for (let i = 0; i < EQ_COUNT; i++) {
    const t = i / (EQ_COUNT - 1);
    const bar = new THREE.Mesh(
      new THREE.PlaneGeometry(0.42, 1).translate(0, 0.5, 0),
      new THREE.MeshBasicMaterial({
        color: new THREE.Color().setHSL(0.55 - t * 0.35, 0.85, 0.52),
        transparent: true,
        opacity: 0.6,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        side: THREE.DoubleSide,
      })
    );
    bar.position.set((t - 0.5) * 24, 0, 0);
    eqGroup.add(bar);
    eqBars.push(bar);
  }
  // Glowing base line the bars rise from.
  const eqBase = new THREE.Mesh(
    new THREE.PlaneGeometry(25, 1.6),
    new THREE.MeshBasicMaterial({ map: glow, color: 0x22d3ee, transparent: true, opacity: 0.5, blending: THREE.AdditiveBlending, depthWrite: false })
  );
  eqBase.rotation.x = -Math.PI / 2;
  eqBase.position.set(0, 0.02, 0);
  eqGroup.add(eqBase);

  /* ---------------- bass shockwaves ---------------- */
  const waves = [];
  for (let i = 0; i < 5; i++) {
    const ring = new THREE.Mesh(
      new THREE.RingGeometry(0.82, 1, 72).rotateX(-Math.PI / 2),
      new THREE.MeshBasicMaterial({ color: 0x22d3ee, transparent: true, opacity: 0, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide })
    );
    ring.position.set(0, GROUND_Y + 0.03, 2);
    ring.visible = false;
    group.add(ring);
    waves.push({ mesh: ring, life: 0 });
  }

  /* ---------------- flying cars with light trails ---------------- */
  const cars = [];
  for (let i = 0; i < 6; i++) {
    const car = new THREE.Group();
    const tint = new THREE.Color(neonPalette[(i + 1) % neonPalette.length]);
    const body = new THREE.Mesh(
      new THREE.BoxGeometry(0.7, 0.24, 2.2),
      new THREE.MeshBasicMaterial({ color: 0xdfefff })
    );
    const trail = new THREE.Mesh(
      new THREE.PlaneGeometry(0.5, 10),
      new THREE.MeshBasicMaterial({ map: streak, color: tint, transparent: true, opacity: 0.8, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide })
    );
    trail.rotation.x = Math.PI / 2;
    trail.position.z = 5.6;
    car.add(body, trail);
    car.userData = {
      tint,
      speed: (14 + Math.random() * 26) * (Math.random() < 0.5 ? -1 : 1),
      baseY: GROUND_Y + 6 + Math.random() * 16,
      baseZ: -Math.random() * CITY_SPAN + 20,
    };
    car.position.set((Math.random() - 0.5) * 70, car.userData.baseY, car.userData.baseZ);
    group.add(car);
    cars.push(car);
  }

  /* ---------------- rain ---------------- */
  const RAIN_COUNT = 1400;
  const rainPositions = new Float32Array(RAIN_COUNT * 3);
  for (let i = 0; i < RAIN_COUNT; i++) {
    rainPositions[i * 3] = (Math.random() - 0.5) * 120;
    rainPositions[i * 3 + 1] = GROUND_Y + Math.random() * 60;
    rainPositions[i * 3 + 2] = (Math.random() - 0.5) * 140;
  }
  const rainGeometry = new THREE.BufferGeometry();
  rainGeometry.setAttribute("position", new THREE.BufferAttribute(rainPositions, 3));
  const rain = new THREE.Points(
    rainGeometry,
    new THREE.PointsMaterial({
      map: rainTexture(),
      color: 0xa8c8ff,
      size: 0.7,
      transparent: true,
      opacity: 0.8,
      blending: THREE.AdditiveBlending,
      depthWrite: false,
      sizeAttenuation: true,
    })
  );
  rain.position.z = 6;
  group.add(rain);

  const baseZ = new Map();
  for (const mesh of [...towers, ...signs, ...cars]) baseZ.set(mesh, mesh.userData.baseZ);

  let waveCursor = 0;

  /** Streams the city toward the camera and wraps each piece across the span. */
  function advance(dt, speed) {
    const dz = speed * dt;
    for (const mesh of towers) {
      mesh.position.z = wrapZ(mesh.position.z + dz, mesh.userData.baseZ);
    }
    for (const mesh of signs) {
      mesh.position.z = wrapZ(mesh.position.z + dz, mesh.userData.baseZ);
    }
    for (const car of cars) {
      const data = car.userData;
      car.position.z = wrapZ(car.position.z + dz, data.baseZ);
      car.position.x += data.speed * dt;
      if (car.position.x > 70) car.position.x = -70;
      else if (car.position.x < -70) car.position.x = 70;
    }
  }

  function wrapZ(z, base) {
    const min = base - CITY_SPAN;
    let value = z;
    while (value > base + 40) value -= CITY_SPAN;
    while (value < min) value += CITY_SPAN;
    return value;
  }

  return {
    group,
    groundY: GROUND_Y,
    setVisible(visible) {
      group.visible = !!visible;
      rain.visible = !!visible;
    },
    setAccent(hex) {
      if (!hex || !/^#?[0-9a-f]{6}$/i.test(hex)) return;
      buildingMaterial.uniforms.uNeon.value.set(hex);
      eqBase.material.color.set(hex);
    },
    setFogColor(color) {
      buildingMaterial.uniforms.uFog.value.copy(color);
    },
    setFogDensity(value) {
      buildingMaterial.uniforms.uFogDensity.value = value;
    },
    advance,
    update(dt, a) {
      const beat = a.beat || 0;
      const pulse = a.pulse || 0;
      buildingMaterial.uniforms.uPulse.value = pulse;

      // Neon breathing: each sign (and its street smear) pulses on its own phase, kicked by the beat.
      for (const sign of signs) {
        const data = sign.userData;
        const v = 0.45 + data.gain * (0.35 + pulse * 0.7 + beat * 0.9) * (0.75 + 0.25 * Math.sin(data.phase + performance.now() * 0.004));
        sign.material.opacity = Math.min(1, v);
        if (sign.material.color && data.tint) sign.material.color.copy(data.tint).multiplyScalar(0.6 + v * 0.8);
      }

      // Spectrum wall: each bar rises from the pavement with its frequency band.
      const spectrum = a.spectrum;
      const bins = spectrum ? spectrum.length : 0;
      for (let i = 0; i < eqBars.length; i++) {
        const t = i / (eqBars.length - 1);
        let value = 0;
        if (bins) {
          const index = Math.min(bins - 1, Math.floor(Math.pow(t, 1.35) * bins));
          value = spectrum[index] || 0;
        }
        const target = Math.max(0.06, Math.min(1.35, value * 1.7)) * EQ_MAX;
        const bar = eqBars[i];
        bar.scale.y += (target - bar.scale.y) * Math.min(1, dt * 14);
        bar.material.opacity = 0.35 + Math.min(0.35, value);
      }

      // Bass shockwave.
      if (beat > 0.55) spawnWave(pulse);
      for (const wave of waves) {
        if (wave.life <= 0) continue;
        wave.life -= dt * 0.6;
        const grow = 1 + (1 - wave.life) * 16;
        wave.mesh.scale.set(grow, 1, grow);
        wave.mesh.material.opacity = Math.max(0, wave.life) * 0.6;
        if (wave.life <= 0) wave.mesh.visible = false;
      }

      // Rain: fall and wrap, then keep the volume centred on the camera.
      const attr = rainGeometry.attributes.position;
      const arr = attr.array;
      const fall = 42 * dt;
      for (let i = 1; i < arr.length; i += 3) {
        arr[i] -= fall;
        if (arr[i] < GROUND_Y) arr[i] = GROUND_Y + 60;
      }
      attr.needsUpdate = true;
      rain.position.z = 6;
    },
    setCameraZ(z) {
      rain.position.z = z + 6;
    },
    /** Keeps the spectrum wall square-on to the camera so near bars do not fan out of proportion. */
    alignEq(cameraPosition) {
      eqGroup.rotation.y = Math.atan2(cameraPosition.x, cameraPosition.z - eqGroup.position.z);
    },
    dispose() {
      scene.remove(group);
    },
  };

  function spawnWave(strength) {
    const wave = waves[waveCursor % waves.length];
    waveCursor++;
    wave.life = 1;
    wave.mesh.visible = true;
    wave.mesh.material.opacity = 0.6;
    wave.mesh.material.color.setHex(strength > 1.1 ? 0xff4fd8 : 0x22d3ee);
  }
}
