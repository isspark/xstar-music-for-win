/* Background star river: additive point sprites on a spherical shell, slowly drifting. */

import * as THREE from "three";

function circleTexture() {
  const size = 64;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  const gradient = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
  gradient.addColorStop(0, "rgba(255,255,255,1)");
  gradient.addColorStop(0.35, "rgba(255,255,255,0.55)");
  gradient.addColorStop(1, "rgba(255,255,255,0)");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, size, size);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

const VERT = `
  attribute float aSize;
  attribute vec3 aColor;
  uniform float uTime;
  uniform float uLevel;
  uniform float uHeight;
  varying vec3 vColor;
  varying float vTwinkle;
  void main() {
    vColor = aColor;
    float tw = 0.6 + 0.4 * sin(uTime * 2.0 + position.x * 3.0 + position.y * 2.0);
    vTwinkle = tw * (1.0 + uLevel * 1.2);
    vec4 mv = modelViewMatrix * vec4(position, 1.0);
    gl_Position = projectionMatrix * mv;
    gl_PointSize = aSize * (1.0 + uLevel * 0.7) * (uHeight / -mv.z) * 0.06;
  }
`;

const FRAG = `
  uniform sampler2D uMap;
  uniform vec3 uAccent;
  varying vec3 vColor;
  varying float vTwinkle;
  void main() {
    vec4 tex = texture2D(uMap, gl_PointCoord);
    vec3 col = mix(vColor, uAccent, 0.3);
    gl_FragColor = vec4(col * vTwinkle, tex.a * clamp(vTwinkle, 0.0, 1.0));
  }
`;

export function createStarfield(scene, count) {
  const positions = new Float32Array(count * 3);
  const sizes = new Float32Array(count);
  const colors = new Float32Array(count * 3);
  const color = new THREE.Color();

  for (let i = 0; i < count; i++) {
    const radius = 24 + Math.random() * 60;
    const theta = Math.random() * Math.PI * 2;
    const phi = Math.acos(2 * Math.random() - 1);
    positions[i * 3] = radius * Math.sin(phi) * Math.cos(theta);
    positions[i * 3 + 1] = radius * Math.sin(phi) * Math.sin(theta);
    positions[i * 3 + 2] = radius * Math.cos(phi);
    sizes[i] = 0.6 + Math.random() * 2.2;
    color.setHSL(0.55 + Math.random() * 0.14, 0.45, 0.6 + Math.random() * 0.3);
    colors[i * 3] = color.r;
    colors[i * 3 + 1] = color.g;
    colors[i * 3 + 2] = color.b;
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
  geometry.setAttribute("aSize", new THREE.BufferAttribute(sizes, 1));
  geometry.setAttribute("aColor", new THREE.BufferAttribute(colors, 3));

  const material = new THREE.ShaderMaterial({
    uniforms: {
      uMap: { value: circleTexture() },
      uAccent: { value: new THREE.Color(0x8ab4ff) },
      uTime: { value: 0 },
      uLevel: { value: 0 },
      uHeight: { value: 900 },
    },
    vertexShader: VERT,
    fragmentShader: FRAG,
    transparent: true,
    depthWrite: false,
    blending: THREE.AdditiveBlending,
  });

  const points = new THREE.Points(geometry, material);
  points.frustumCulled = false;
  scene.add(points);

  return {
    points,
    setHeight(pixelsPerWorld) {
      material.uniforms.uHeight.value = pixelsPerWorld;
    },
    setAccent(colorValue) {
      material.uniforms.uAccent.value.copy(colorValue);
    },
    update(dt, a) {
      material.uniforms.uTime.value += dt;
      material.uniforms.uLevel.value = a.level;
      points.rotation.y += dt * 0.012;
      points.rotation.x += dt * 0.004;
    },
    dispose() {
      geometry.dispose();
      material.uniforms.uMap.value.dispose();
      material.dispose();
      scene.remove(points);
    },
  };
}
