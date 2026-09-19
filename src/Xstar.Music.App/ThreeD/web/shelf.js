/* 3D playlist shelf: windowed card carousel on the right, with an in-scene detail panel.
 * Layout/interaction is inspired by Mineradio's shelf but reimplemented from scratch. */

import * as THREE from "three";
import { drawCard, drawDetail, detailMetrics } from "./textures.js";

const CARD_W = 2.0;
const CARD_H = 1.0;
const MAX_CARDS = 11;
const VISIBLE_RADIUS = 5;
const CARD_CANVAS_W = 512;
const CARD_CANVAS_H = 256;
const DETAIL_CANVAS_W = 512;
const DETAIL_CANVAS_H = 1024;

export function createShelf(opts) {
  const { scene, camera, canvas, requestDetail, requestCardArtwork, play } = opts;

  const group = new THREE.Group();
  group.visible = false;
  scene.add(group);

  /* ---------------- card pool ---------------- */

  const slots = [];
  for (let i = 0; i < MAX_CARDS; i++) {
    const cardCanvas = document.createElement("canvas");
    cardCanvas.width = CARD_CANVAS_W;
    cardCanvas.height = CARD_CANVAS_H;
    const texture = new THREE.CanvasTexture(cardCanvas);
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
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(CARD_W, CARD_H), material);
    mesh.visible = false;
    mesh.renderOrder = 300;
    group.add(mesh);
    slots.push({ mesh, material, texture, canvas: cardCanvas, dataIndex: -1, dataId: "", floatMix: 0, image: null });
  }

  /* ---------------- detail panel ---------------- */

  const detailCanvas = document.createElement("canvas");
  detailCanvas.width = DETAIL_CANVAS_W;
  detailCanvas.height = DETAIL_CANVAS_H;
  const detailTexture = new THREE.CanvasTexture(detailCanvas);
  detailTexture.colorSpace = THREE.SRGBColorSpace;
  detailTexture.minFilter = THREE.LinearFilter;
  detailTexture.generateMipmaps = false;
  const detailMaterial = new THREE.MeshBasicMaterial({
    map: detailTexture,
    transparent: true,
    depthWrite: false,
    depthTest: false,
    side: THREE.DoubleSide,
  });
  const detailMesh = new THREE.Mesh(new THREE.PlaneGeometry(1.7, 3.4), detailMaterial);
  detailMesh.renderOrder = 330;
  detailMesh.visible = false;
  scene.add(detailMesh);

  let detailData = null;
  let detailScroll = 0;
  let detailMetricsCache = detailMetrics(detailCanvas);

  /* ---------------- state ---------------- */

  let items = [];
  let kind = "albums";
  let center = 0;
  let centerTarget = 0;
  let selectedSlot = -1;
  let presence = "auto";
  let cameraMode = "dynamic";
  let mode = "side";
  let hoverCue = 0;
  let visibility = 0;
  let windowStart = null;
  let accentHex = "#22c55e";
  let pinned = false;
  let layout = { size: 1, offsetX: 0, offsetY: 0, angleY: 0, opacity: 1 };
  const viewport = { w: 1280, h: 720 };

  const raycaster = new THREE.Raycaster();
  const ndc = new THREE.Vector2();
  const pointer = { x: 0, y: 0, inside: false };
  const artCache = new Map();
  const artRequested = new Set();
  const detailWorld = new THREE.Vector3();
  const local = new THREE.Vector3();

  /* ---------------- texture sync ---------------- */

  function redrawSlot(slot, item) {
    slot.canvas.__card = item;
    drawCard(slot.canvas, { ...item, kindKey: item.kindKey }, accentHex, slot.image);
    slot.texture.needsUpdate = true;
  }

  function syncWindow(force) {
    const start = Math.round(center) - Math.floor(MAX_CARDS / 2);
    if (!force && start === windowStart) return;
    windowStart = start;
    for (let s = 0; s < MAX_CARDS; s++) {
      const slot = slots[s];
      const index = start + s;
      const item = items[index];
      if (!item) {
        slot.mesh.visible = false;
        slot.dataIndex = -1;
        slot.dataId = "";
        continue;
      }
      if (slot.dataId !== item.id) {
        slot.dataId = item.id;
        redrawSlot(slot, item);
      }
      slot.dataIndex = index;
      slot.mesh.visible = true;
      maybeRequestArt(item);
    }
  }

  function maybeRequestArt(item) {
    if (!item || artCache.has(item.id) || artRequested.has(item.id)) return;
    artRequested.add(item.id);
    requestCardArtwork(item.id);
  }

  function setArtwork(key, dataUri) {
    artCache.set(key, dataUri || null);
    if (!dataUri) return;
    const image = new Image();
    image.onload = () => {
      for (const slot of slots) {
        if (slot.dataId === key) {
          slot.image = image;
          const item = slot.canvas.__card;
          if (item) redrawSlot(slot, item);
        }
      }
    };
    image.src = dataUri;
  }

  /* ---------------- layout ---------------- */

  function placeSlot(slot, now) {
    if (slot.dataIndex < 0) return;
    const delta = slot.dataIndex - center;
    const abs = Math.abs(delta);
    if (abs > VISIBLE_RADIUS + 1) {
      slot.mesh.visible = false;
      return;
    }
    slot.mesh.visible = true;
    const clamped = Math.min(abs, VISIBLE_RADIUS);
    const float = slot.floatMix;

    let x;
    let y;
    let z;
    let scale;
    let rotX = 0;
    let rotY;

    if (mode === "stage") {
      x = delta * 1.45;
      y = -2.0 + (1 - Math.min(abs, 2)) * 0.1;
      z = -clamped * 0.28 + float * 0.45;
      scale = 1 - clamped * 0.06;
      rotX = -0.32;
      rotY = 0;
    } else {
      x = 2.5 + clamped * 0.06 - float * 0.34;
      y = -delta * 0.6 + float * 0.06;
      z = -clamped * 0.22 + float * 0.5;
      scale = 1 - clamped * 0.055;
      rotY = -0.34 + float * 0.14;
    }

    slot.mesh.position.set(x, y, z);
    slot.mesh.rotation.set(rotX, rotY, 0);
    slot.mesh.scale.setScalar(scale * (1 + float * 0.06));

    const baseOpacity = Math.max(0.06, 1 - clamped * 0.15);
    const centerBoost = abs < 0.5 ? 1 : 0;
    slot.material.opacity = baseOpacity * visibility * layout.opacity * (0.85 + float * 0.15);
    slot.mesh.renderOrder =
      300 + Math.round(VISIBLE_RADIUS - clamped) * 2 + (abs < 0.5 ? 60 : 0) + Math.round(float * 90);
    void centerBoost;
  }

  /* ---------------- detail ---------------- */

  function redrawDetail() {
    if (!detailData) return;
    const metrics = drawDetail(detailCanvas, detailData, accentHex, detailScroll);
    detailMetricsCache = { ...detailMetricsCache, ...metrics };
    detailTexture.needsUpdate = true;
  }

  function openDetail(id) {
    requestDetail(id);
  }

  function setDetail(detail) {
    detailData = detail;
    detailScroll = 0;
    detailMesh.visible = true;
    detailMesh.position.set(0.15, 0, 1.7);
    detailMesh.rotation.set(0, -0.12, 0);
    redrawDetail();
  }

  function closeDetail() {
    detailData = null;
    detailMesh.visible = false;
  }

  function scrollDetail(rows) {
    if (!detailData) return;
    const maxScroll = Math.max(0, detailData.tracks.length - detailMetricsCache.visibleRows);
    detailScroll = Math.max(0, Math.min(maxScroll, detailScroll + rows));
    redrawDetail();
  }

  /* ---------------- hit testing ---------------- */

  function updateNdc(event) {
    const rect = canvas.getBoundingClientRect();
    ndc.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
    ndc.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
  }

  function pick(meshes) {
    raycaster.setFromCamera(ndc, camera);
    const hits = raycaster.intersectObjects(meshes, false);
    return hits.length ? hits[0] : null;
  }

  function cardLocalUV(mesh, point, width, height) {
    local.copy(point);
    mesh.worldToLocal(local);
    return { u: local.x / width + 0.5, v: local.y / height + 0.5 };
  }

  /* ---------------- input ---------------- */

  canvas.addEventListener("pointermove", (event) => {
    pointer.x = event.clientX;
    pointer.y = event.clientY;
    pointer.inside = true;
    updateNdc(event);
  });
  canvas.addEventListener("pointerleave", () => {
    pointer.inside = false;
    selectedSlot = -1;
  });

  canvas.addEventListener(
    "wheel",
    (event) => {
      const overDetail = detailData && pickDetail(event);
      if (overDetail) {
        scrollDetail(event.deltaY > 0 ? 1 : -1);
        event.preventDefault();
        return;
      }
      if (inShelfZone(event.clientX)) {
        step(event.deltaY > 0 ? 1 : -1);
        event.preventDefault();
      }
    },
    { passive: false }
  );

  canvas.addEventListener("click", (event) => {
    updateNdc(event);

    if (detailData) {
      const hit = pickDetail(event);
      if (hit) {
        const { u, v } = cardLocalUV(detailMesh, hit.point, 1.7, 3.4);
        const localY = (1 - v) * DETAIL_CANVAS_H;
        const row = Math.floor((localY - detailMetricsCache.headerHeight) / detailMetricsCache.rowHeight);
        const index = detailScroll + row;
        if (v < 0.97 && row >= 0 && index >= 0 && index < detailData.tracks.length) {
          play(detailData.tracks[index].id);
        }
        return;
      }
      closeDetail();
      return;
    }

    const meshes = slots.filter((s) => s.mesh.visible).map((s) => s.mesh);
    const hit = pick(meshes);
    if (!hit) return;
    const slot = slots.find((s) => s.mesh === hit.object);
    if (!slot || slot.dataIndex < 0) return;
    const item = items[slot.dataIndex];
    const { u, v } = cardLocalUV(slot.mesh, hit.point, CARD_W, CARD_H);

    if (u > 0.78 && v < 0.42) {
      play(item.id);
      return;
    }
    if (item.kindKey === "track") {
      play(item.id);
      return;
    }
    if (Math.abs(slot.dataIndex - center) < 0.5) {
      openDetail(item.id);
    } else {
      centerTarget = slot.dataIndex;
    }
  });

  function pickDetail(event) {
    const hit = pick([detailMesh]);
    return hit && hit.object === detailMesh ? hit : null;
  }

  function inShelfZone(clientX) {
    return clientX > viewport.w * 0.66;
  }

  /* ---------------- public API ---------------- */

  function step(direction) {
    if (items.length === 0) return;
    centerTarget = Math.max(0, Math.min(items.length - 1, Math.round(center) + direction));
  }

  function rebuild() {
    windowStart = null;
    for (const slot of slots) {
      slot.dataId = "";
      slot.dataIndex = -1;
      slot.image = null;
      slot.mesh.visible = false;
    }
    center = Math.max(0, Math.min(items.length - 1, center));
    centerTarget = center;
    syncWindow(true);
  }

  return {
    group,
    get detailOpen() { return !!detailData; },

    setViewport(w, h) {
      viewport.w = w;
      viewport.h = h;
    },
    /** True when the pointer is over a card or the detail panel (so orbit drags should not start). */
    isPointerOverContent(event) {
      updateNdc(event);
      if (detailData) return !!pick([detailMesh]);
      const meshes = slots.filter((s) => s.mesh.visible).map((s) => s.mesh);
      return !!pick(meshes);
    },
    setAccent(hex) {
      accentHex = hex || accentHex;
      for (const slot of slots) {
        const item = slot.canvas.__card;
        if (item) redrawSlot(slot, item);
      }
      redrawDetail();
    },
    setLibrary(nextKind, cards) {
      kind = nextKind;
      items = cards.map((card) => ({
        ...card,
        kindKey: card.id.startsWith("album:")
          ? "album"
          : card.id.startsWith("playlist:")
          ? "playlist"
          : card.id === "favorites"
          ? "favorites"
          : card.kind === "track"
          ? "track"
          : "recent",
      }));
      selectedSlot = -1;
      closeDetail();
      rebuild();
    },
    setArtwork,
    setPresence(value) { presence = value; },
    setCameraMode(value) { cameraMode = value; },
    setMode(value) { mode = value; rebuild(); },
    setLayout(next) {
      layout = { ...layout, ...next };
      group.scale.setScalar(layout.size);
      group.position.set(layout.offsetX, layout.offsetY, 0);
      group.rotation.y = layout.angleY;
    },
    openDetail(id) { openDetail(id); },
    setDetail(detail) { setDetail(detail); },
    closeDetail,
    getKind() { return kind; },

    update(dt, now) {
      center += (centerTarget - center) * Math.min(1, dt * 9);
      syncWindow(false);

      // hover selection
      let hoveredSlot = -1;
      if (!detailData && pointer.inside && visibility > 0.05) {
        const meshes = slots.filter((s) => s.mesh.visible).map((s) => s.mesh);
        const hit = pick(meshes);
        if (hit) {
          const slot = slots.find((s) => s.mesh === hit.object);
          if (slot && slot.dataIndex >= 0) hoveredSlot = slot.dataIndex;
        }
      }
      selectedSlot = hoveredSlot;

      for (const slot of slots) {
        const isHovered = slot.dataIndex >= 0 && slot.dataIndex === selectedSlot;
        const target = isHovered ? 1 : 0;
        slot.floatMix += (target - slot.floatMix) * Math.min(1, dt * (isHovered ? 12 : 8));
        void now;
      }

      // presence
      const shelfActive =
        presence === "always" || mode === "stage" || !!detailData || pinned || selectedSlot >= 0 ||
        (pointer.inside && pointer.x > viewport.w * 0.72);
      const target = shelfActive ? 1 : 0;
      hoverCue += (target - hoverCue) * Math.min(1, dt * 6);
      visibility = hoverCue;
      group.visible = visibility > 0.01 || !!detailData;

      for (const slot of slots) placeSlot(slot, now);

      // camera nudge (dynamic focus)
      if (cameraMode === "dynamic" && visibility > 0.01) {
        const focus = visibility * (detailData ? 1.0 : 0.7);
        camera.position.x += focus * 0.85;
        camera.position.z += focus * 0.35;
        camera.lookAt(focus * 0.9, 0, 0);
      }
      if (detailData && cameraMode === "dynamic") {
        camera.position.x += 0.35;
        camera.position.z += 0.4;
        camera.lookAt(0.4, 0, 0);
      }
    },

    dispose() {
      for (const slot of slots) {
        slot.mesh.geometry.dispose();
        slot.material.dispose();
        slot.texture.dispose();
      }
      detailMesh.geometry.dispose();
      detailMaterial.dispose();
      detailTexture.dispose();
      scene.remove(group);
      scene.remove(detailMesh);
    },
  };
}
