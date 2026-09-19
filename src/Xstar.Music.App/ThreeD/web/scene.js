/* Stage: WebGL renderer, perspective camera, background (colour / image) and a post chain:
 * main pass -> bloom -> lyric overlay pass (unbloomed) -> OutputPass (colour management). */

import * as THREE from "three";
import { EffectComposer } from "three/addons/postprocessing/EffectComposer.js";
import { RenderPass } from "three/addons/postprocessing/RenderPass.js";
import { UnrealBloomPass } from "three/addons/postprocessing/UnrealBloomPass.js";
import { OutputPass } from "three/addons/postprocessing/OutputPass.js";

export const QUALITY = {
  eco: { grid: 112, dpr: 0.85, bloom: false, stars: 700, bloomStrength: 0.0 },
  balanced: { grid: 160, dpr: 1.15, bloom: true, stars: 1400, bloomStrength: 0.8 },
  high: { grid: 200, dpr: 1.5, bloom: true, stars: 2400, bloomStrength: 1.0 },
};

const DEFAULT_BG = "#05060a";

export function createStage(canvas, tierName) {
  const tier = QUALITY[tierName] || QUALITY.balanced;

  const renderer = new THREE.WebGLRenderer({
    canvas,
    antialias: false,
    alpha: false,
    powerPreference: "high-performance",
  });
  renderer.setClearColor(DEFAULT_BG, 1);
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, tier.dpr));

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 300);
  camera.position.set(0, 0, 7);

  const composer = new EffectComposer(renderer);
  const mainPass = new RenderPass(scene, camera);
  composer.addPass(mainPass);

  // Smaller radius keeps the particle field crisp instead of smearing it into a blur.
  const bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), tier.bloomStrength, 0.32, 0.6);
  bloom.enabled = tier.bloom;
  composer.addPass(bloom);

  // OutputPass applies tone mapping + sRGB conversion (without it the image looks washed out).
  const outputPass = new OutputPass();
  composer.addPass(outputPass);

  const state = { bloom: tier.bloom };

  /* ---------------- background ---------------- */

  const bgColor = new THREE.Color(DEFAULT_BG);
  const bgCanvas = document.createElement("canvas");
  let bgImage = null;
  let bgTexture = null;

  function redrawBackground() {
    if (!bgImage) {
      scene.background = bgColor;
      return;
    }
    const width = Math.max(320, Math.round(canvas.clientWidth) || 1280);
    const height = Math.max(200, Math.round(canvas.clientHeight) || 720);
    const scale = Math.min(1, 1280 / width);
    bgCanvas.width = Math.round(width * scale);
    bgCanvas.height = Math.round(height * scale);
    const ctx = bgCanvas.getContext("2d");
    const iw = bgImage.naturalWidth || bgCanvas.width;
    const ih = bgImage.naturalHeight || bgCanvas.height;
    const cover = Math.max(bgCanvas.width / iw, bgCanvas.height / ih);
    const dw = iw * cover;
    const dh = ih * cover;
    ctx.drawImage(bgImage, (bgCanvas.width - dw) / 2, (bgCanvas.height - dh) / 2, dw, dh);
    // Scrim + vignette keep the image dim so it neither overpowers the cover nor blooms.
    ctx.fillStyle = "rgba(0,0,0,0.5)";
    ctx.fillRect(0, 0, bgCanvas.width, bgCanvas.height);
    const vignette = ctx.createRadialGradient(
      bgCanvas.width / 2, bgCanvas.height * 0.45, 0,
      bgCanvas.width / 2, bgCanvas.height * 0.45, Math.max(bgCanvas.width, bgCanvas.height) * 0.72
    );
    vignette.addColorStop(0, "rgba(0,0,0,0)");
    vignette.addColorStop(1, "rgba(0,0,0,0.8)");
    ctx.fillStyle = vignette;
    ctx.fillRect(0, 0, bgCanvas.width, bgCanvas.height);

    if (!bgTexture) {
      bgTexture = new THREE.CanvasTexture(bgCanvas);
      bgTexture.colorSpace = THREE.SRGBColorSpace;
    }
    bgTexture.needsUpdate = true;
    scene.background = bgTexture;
  }

  function resize() {
    const width = Math.max(1, canvas.clientWidth);
    const height = Math.max(1, canvas.clientHeight);
    renderer.setSize(width, height, false);
    composer.setSize(width, height);
    bloom.setSize(width, height);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
    redrawBackground();
  }

  window.addEventListener("resize", resize);
  resize();

  function pixelsPerWorldAtUnit() {
    const size = new THREE.Vector2();
    renderer.getDrawingBufferSize(size);
    const fovRad = (camera.fov * Math.PI) / 180;
    return size.y / (2 * Math.tan(fovRad / 2));
  }

  return {
    renderer,
    scene,
    camera,
    composer,
    bloom,
    tier,
    resize,
    pixelsPerWorldAtUnit,

    /** Renders an extra scene (lyrics) after bloom, so bright text is not bloomed. */
    addOverlayScene(overlayScene) {
      const pass = new RenderPass(overlayScene, camera);
      pass.clear = false;
      const outputIndex = composer.passes.indexOf(outputPass);
      composer.passes.splice(outputIndex, 0, pass);
      return pass;
    },

    setBackgroundColor(hex) {
      if (!/^#?[0-9a-f]{6}$/i.test(hex || "")) return;
      bgColor.set(hex);
      renderer.setClearColor(bgColor, 1);
      if (!bgImage) scene.background = bgColor;
      document.body.style.backgroundColor = hex;
    },
    setBackgroundImage(dataUri) {
      const image = new Image();
      image.onload = () => { bgImage = image; redrawBackground(); };
      image.src = dataUri;
    },
    clearBackgroundImage() {
      bgImage = null;
      redrawBackground();
    },

    setBloomEnabled(enabled) {
      state.bloom = !!enabled;
      bloom.enabled = !!enabled;
    },
    setPixelRatioMax(value) {
      renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, value || 1));
      resize();
    },
    render() {
      // Always go through the composer so OutputPass keeps colour management correct.
      composer.render();
    },
    dispose() {
      window.removeEventListener("resize", resize);
      composer.dispose?.();
      renderer.dispose();
    },
  };
}
