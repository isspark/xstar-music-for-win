/* First-person hyperspace jump: the camera races down a glowing wormhole whose ribs and ribbons
 * stream past while the palette drifts purple -> cyan -> gold. Colour-cycling nebulae, morphing
 * abstract geometry and the feedback pass (motion blur / light ribbons) sell the speed. */

import * as THREE from "three";

/* Hue-keyed cycle so the palette rolls purple -> blue -> cyan -> magenta -> gold instead of
 * cutting straight through green. Matches palette() in the tunnel shader. */
function palette(t, out) {
  const p = ((t % 1) + 1) % 1;
  let h;
  if (p < 0.34) h = 0.76 + (0.5 - 0.76) * (p / 0.34);
  else if (p < 0.67) h = 0.5 + (1.12 - 0.5) * ((p - 0.34) / 0.33);
  else h = 1.12 + (1.76 - 1.12) * ((p - 0.67) / 0.33);
  return out.setHSL(((h % 1) + 1) % 1, 0.85, 0.55);
}

function glowTexture() {
  const size = 128;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  const gradient = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
  gradient.addColorStop(0, "rgba(255,255,255,1)");
  gradient.addColorStop(0.35, "rgba(255,255,255,0.45)");
  gradient.addColorStop(1, "rgba(255,255,255,0)");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, size, size);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

const TUNNEL_VERT = `
  varying vec2 vUv;
  void main() {
    vUv = uv;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

const TUNNEL_FRAG = `
  uniform float uScroll;
  uniform float uTime;
  uniform float uPulse;
  varying vec2 vUv;

  vec3 hsl2rgb(vec3 c) {
    vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
  }

  vec3 palette(float t) {
    float p = fract(t);
    float h;
    if (p < 0.34) h = mix(0.76, 0.50, p / 0.34);
    else if (p < 0.67) h = mix(0.50, 1.12, (p - 0.34) / 0.33);
    else h = mix(1.12, 1.76, (p - 0.67) / 0.33);
    return hsl2rgb(vec3(fract(h), 0.85, 0.55));
  }

  void main() {
    // Ribs sweeping toward the viewer + long light ribbons running down the tube.
    float v = vUv.y * 70.0 - uScroll;
    float rib = fract(v);
    float ribMask = smoothstep(0.0, 0.08, rib) * (1.0 - smoothstep(0.22, 0.5, rib));
    float u = vUv.x * 84.0;
    float ribbon = pow(abs(sin(u * 3.14159265)), 14.0);
    float twist = 0.5 + 0.5 * sin(vUv.y * 18.8496 + uTime * 0.8);

    vec3 col = palette(uTime * 0.09 + vUv.y * 0.2);
    float glow = ribMask * 0.26 + ribbon * (0.4 + twist * 0.6);
    glow *= 1.0 + uPulse * 1.4;
    gl_FragColor = vec4(col * glow, 1.0);
  }
`;

const SHAPE_VERT = `
  uniform float uTime;
  uniform float uAmp;
  varying float vFade;
  void main() {
    float d = sin(position.x * 1.3 + uTime) * cos(position.y * 1.3 + uTime * 0.9) * sin(position.z * 1.1 + uTime * 0.7);
    vec3 p = position + normal * d * uAmp;
    vFade = 0.6 + 0.4 * d;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
  }
`;

const SHAPE_FRAG = `
  uniform vec3 uColor;
  uniform float uPulse;
  varying float vFade;
  void main() { gl_FragColor = vec4(uColor * vFade * (0.55 + uPulse * 0.9), 1.0); }
`;

export function createWormhole(scene) {
  const group = new THREE.Group();
  group.visible = false;
  scene.add(group);

  const glow = glowTexture();

  /* ---------------- tunnel ---------------- */
  const tunnelMaterial = new THREE.ShaderMaterial({
    uniforms: {
      uScroll: { value: 0 },
      uTime: { value: 0 },
      uPulse: { value: 0 },
    },
    vertexShader: TUNNEL_VERT,
    fragmentShader: TUNNEL_FRAG,
    side: THREE.BackSide,
    transparent: true,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });
  const tunnel = new THREE.Mesh(
    new THREE.CylinderGeometry(6, 6, 560, 80, 1, true),
    tunnelMaterial
  );
  tunnel.rotation.x = -Math.PI / 2;
  tunnel.position.z = -250;
  tunnel.frustumCulled = false;
  group.add(tunnel);

  /* ---------------- light at the end ---------------- */
  const core = new THREE.Sprite(
    new THREE.SpriteMaterial({
      map: glow,
      transparent: true,
      opacity: 0.5,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    })
  );
  core.position.set(0, 0, -330);
  core.scale.setScalar(45);
  group.add(core);

  /* ---------------- colour-cycling nebulae ---------------- */
  const nebulae = [];
  for (let i = 0; i < 10; i++) {
    const sprite = new THREE.Sprite(
      new THREE.SpriteMaterial({
        map: glow,
        transparent: true,
        opacity: 0.14,
        depthWrite: false,
        blending: THREE.AdditiveBlending,
      })
    );
    const angle = Math.random() * Math.PI * 2;
    const radius = 2 + Math.random() * 3.6;
    sprite.position.set(Math.cos(angle) * radius, Math.sin(angle) * radius, -Math.random() * 320 + 20);
    sprite.scale.setScalar(26 + Math.random() * 40);
    sprite.userData = { phase: Math.random(), baseZ: sprite.position.z, spin: (Math.random() - 0.5) * 0.4 };
    group.add(sprite);
    nebulae.push(sprite);
  }

  /* ---------------- morphing abstract geometry ---------------- */
  const shapes = [];
  const shapeGeometries = [
    new THREE.IcosahedronGeometry(1, 0),
    new THREE.TorusKnotGeometry(0.7, 0.22, 48, 7),
    new THREE.OctahedronGeometry(1, 1),
  ];
  for (let i = 0; i < 5; i++) {
    const material = new THREE.ShaderMaterial({
      uniforms: { uTime: { value: 0 }, uAmp: { value: 0.35 }, uPulse: { value: 0 }, uColor: { value: new THREE.Color() } },
      vertexShader: SHAPE_VERT,
      fragmentShader: SHAPE_FRAG,
      wireframe: true,
      transparent: true,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    });
    const mesh = new THREE.Mesh(shapeGeometries[i % shapeGeometries.length], material);
    const angle = Math.random() * Math.PI * 2;
    const radius = Math.random() * 3.4;
    mesh.position.set(Math.cos(angle) * radius, Math.sin(angle) * radius, -80 - Math.random() * 260);
    mesh.scale.setScalar(2.5 + Math.random() * 4);
    mesh.rotation.set(Math.random() * 3, Math.random() * 3, Math.random() * 3);
    mesh.userData = { baseZ: mesh.position.z, spin: new THREE.Vector3((Math.random() - 0.5) * 0.6, (Math.random() - 0.5) * 0.6, (Math.random() - 0.5) * 0.6), phase: Math.random() };
    group.add(mesh);
    shapes.push(mesh);
  }

  const _color = new THREE.Color();
  let scroll = 0;
  let elapsed = 0;

  function wrap(z, base, span, ahead) {
    let value = z;
    while (value > ahead) value -= span;
    while (value < ahead - span) value += span;
    return value;
  }

  return {
    group,
    setVisible(visible) {
      group.visible = !!visible;
    },
    setAccent(hex) {
      // The tunnel palette is fixed (purple -> cyan -> gold); the accent only nudges the core.
      if (hex && /^#?[0-9a-f]{6}$/i.test(hex)) core.material.color.set(hex).lerp(new THREE.Color(0xffffff), 0.4);
    },
    update(dt, a, speed) {
      elapsed += dt;
      scroll += speed * dt * 0.05;
      tunnelMaterial.uniforms.uScroll.value = scroll;
      tunnelMaterial.uniforms.uTime.value = elapsed;
      const pulse = a.beat * 0.6 + a.pulse * 0.35;
      tunnelMaterial.uniforms.uPulse.value = pulse;

      palette(elapsed * 0.09, _color);
      core.material.color.copy(_color);
      core.scale.setScalar(42 + a.pulse * 22);

      for (const sprite of nebulae) {
        sprite.position.z = wrap(sprite.position.z + speed * dt * 0.18, sprite.userData.baseZ, 340, 30);
        palette(elapsed * 0.09 + sprite.userData.phase, _color);
        sprite.material.color.copy(_color);
        sprite.material.opacity = 0.07 + 0.11 * Math.abs(Math.sin(elapsed * 0.4 + sprite.userData.phase * 6.28));
      }

      for (const mesh of shapes) {
        mesh.position.z = wrap(mesh.position.z + speed * dt * 0.55, mesh.userData.baseZ, 320, 30);
        mesh.rotation.x += mesh.userData.spin.x * dt;
        mesh.rotation.y += mesh.userData.spin.y * dt;
        mesh.rotation.z += mesh.userData.spin.z * dt;
        mesh.material.uniforms.uTime.value = elapsed + mesh.userData.phase * 10;
        mesh.material.uniforms.uAmp.value = 0.3 + a.pulse * 0.7;
        mesh.material.uniforms.uPulse.value = pulse;
        palette(elapsed * 0.09 + mesh.userData.phase, _color);
        mesh.material.uniforms.uColor.value.copy(_color);
      }
    },
    dispose() {
      tunnel.geometry.dispose();
      tunnelMaterial.dispose();
      for (const geometry of shapeGeometries) geometry.dispose();
      for (const sprite of [...nebulae]) sprite.material.dispose();
      core.material.dispose();
      glow.dispose();
      scene.remove(group);
    },
  };
}
