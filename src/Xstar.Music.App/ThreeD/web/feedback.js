/* Infinite tunnel / spiral: a temporal feedback pass.
 *
 * Each frame the previous result is sampled slightly zoomed-in and rotated, decayed, then added
 * under the current frame. Because the echo is re-fed every frame it never ends — the whole scene
 * (cover particles, starfield, background) keeps receding into a vanishing point and slowly spins.
 * Audio drives the rates, so the tunnel breathes with the music. */

import * as THREE from "three";
import { Pass, FullScreenQuad } from "three/addons/postprocessing/Pass.js";

const VERT = `
  varying vec2 vUv;
  void main() {
    vUv = uv;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

const FRAG = `
  uniform sampler2D tDiffuse;
  uniform sampler2D tPrev;
  uniform float uZoom;
  uniform float uRot;
  uniform float uDamp;
  uniform float uChroma;
  varying vec2 vUv;

  void main() {
    vec2 uv = vUv - 0.5;
    float c = cos(uRot);
    float s = sin(uRot);
    vec2 rot = vec2(uv.x * c - uv.y * s, uv.x * s + uv.y * c);

    // Zooming the sampling coords pulls the old frame toward the centre (flying forward).
    // A per-channel zoom offset adds a subtle colour smear that deepens with the beat.
    vec2 cr = rot * (uZoom * (1.0 - uChroma)) + 0.5;
    vec2 cg = rot * uZoom + 0.5;
    vec2 cb = rot * (uZoom * (1.0 + uChroma)) + 0.5;
    vec3 prev = vec3(
      texture2D(tPrev, cr).r,
      texture2D(tPrev, cg).g,
      texture2D(tPrev, cb).b
    );

    // Ring mask: echoes live between the rim (fresh content) and the very centre, so the trail
    // reads as a tunnel instead of piling up into a white core at the vanishing point.
    float r = length(uv);
    float ring = smoothstep(0.0, 0.06, r) * (1.0 - smoothstep(0.44, 0.50, r));
    // max() (not add) keeps the echo from stacking into a white-out: it can linger but never
    // exceeds the brightness it came from, so with a fast enough zoom the echoes read as clean
    // concentric copies receding into the distance.
    vec3 echo = prev * uDamp * ring;
    gl_FragColor = vec4(max(texture2D(tDiffuse, vUv).rgb, echo), 1.0);
  }
`;

const COPY_FRAG = `
  uniform sampler2D tDiffuse;
  varying vec2 vUv;
  void main() { gl_FragColor = texture2D(tDiffuse, vUv); }
`;

export class FeedbackPass extends Pass {
  constructor(width = 1, height = 1) {
    super();
    this.needsSwap = true;

    this.uniforms = {
      tDiffuse: { value: null },
      tPrev: { value: null },
      uZoom: { value: 1.006 },
      uRot: { value: 0.004 },
      uDamp: { value: 0.92 },
      uChroma: { value: 0.004 },
    };

    this.material = new THREE.ShaderMaterial({
      uniforms: this.uniforms,
      vertexShader: VERT,
      fragmentShader: FRAG,
      depthTest: false,
      depthWrite: false,
      blending: THREE.NoBlending,
    });
    this.copyMaterial = new THREE.ShaderMaterial({
      uniforms: { tDiffuse: { value: null } },
      vertexShader: VERT,
      fragmentShader: COPY_FRAG,
      depthTest: false,
      depthWrite: false,
      blending: THREE.NoBlending,
    });

    this.quad = new FullScreenQuad(this.material);
    this.copyQuad = new FullScreenQuad(this.copyMaterial);

    const options = { type: THREE.HalfFloatType, depthBuffer: false, stencilBuffer: false };
    this.rtPrev = new THREE.WebGLRenderTarget(width, height, options);
    this.rtOut = new THREE.WebGLRenderTarget(width, height, options);
    this._started = false;
  }

  setSize(width, height) {
    this.rtPrev.setSize(width, height);
    this.rtOut.setSize(width, height);
    this._started = false;
  }

  /** Clears the echo history (call when enabling so old frames do not bleed in). */
  reset() {
    this._started = false;
  }

  render(renderer, writeBuffer, readBuffer) {
    if (!this._started) {
      renderer.setRenderTarget(this.rtPrev);
      renderer.clear(true, false, false);
      this._started = true;
    }

    this.uniforms.tDiffuse.value = readBuffer.texture;
    this.uniforms.tPrev.value = this.rtPrev.texture;
    renderer.setRenderTarget(this.rtOut);
    this.quad.render(renderer);

    // rtOut now holds this frame's result — it becomes the previous frame for the next one.
    const swap = this.rtPrev;
    this.rtPrev = this.rtOut;
    this.rtOut = swap;

    this.copyMaterial.uniforms.tDiffuse.value = this.rtPrev.texture;
    renderer.setRenderTarget(writeBuffer);
    this.copyQuad.render(renderer);
  }

  dispose() {
    this.rtPrev.dispose();
    this.rtOut.dispose();
    this.material.dispose();
    this.copyMaterial.dispose();
  }
}
