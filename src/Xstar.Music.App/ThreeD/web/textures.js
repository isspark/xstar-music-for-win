/* Canvas2D helpers that rasterise shelf cards and the detail panel into textures. */

const FONT = '"Segoe UI", "Microsoft YaHei UI", system-ui, sans-serif';

const KIND_LABEL = { album: "专辑", playlist: "歌单", favorites: "收藏", track: "单曲", recent: "最近" };

function roundRect(ctx, x, y, w, h, r) {
  const radius = Math.min(r, w / 2, h / 2);
  ctx.beginPath();
  ctx.moveTo(x + radius, y);
  ctx.arcTo(x + w, y, x + w, y + h, radius);
  ctx.arcTo(x + w, y + h, x, y + h, radius);
  ctx.arcTo(x, y + h, x, y, radius);
  ctx.arcTo(x, y, x + w, y, radius);
  ctx.closePath();
}

function clipText(ctx, text, maxWidth) {
  if (!text) return "";
  if (ctx.measureText(text).width <= maxWidth) return text;
  let value = text;
  while (value.length > 1 && ctx.measureText(value + "…").width > maxWidth) {
    value = value.slice(0, -1);
  }
  return value + "…";
}

function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || "");
  if (!m) return { r: 34, g: 197, b: 94 };
  const n = parseInt(m[1], 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

function drawPlaceholderCover(ctx, x, y, size, title, accent) {
  const rgb = hexToRgb(accent);
  const gradient = ctx.createLinearGradient(x, y, x + size, y + size);
  gradient.addColorStop(0, `rgb(${Math.round(rgb.r * 0.9)},${Math.round(rgb.g * 0.9)},${Math.round(rgb.b * 0.9)})`);
  gradient.addColorStop(1, "#101321");
  ctx.fillStyle = gradient;
  roundRect(ctx, x, y, size, size, 16);
  ctx.fill();

  const initial = (title || "?").trim().charAt(0).toUpperCase();
  ctx.fillStyle = "rgba(255,255,255,0.92)";
  ctx.font = `700 ${Math.round(size * 0.44)}px ${FONT}`;
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText(initial, x + size / 2, y + size / 2 + 2);
  ctx.textAlign = "left";
  ctx.textBaseline = "alphabetic";
}

export function drawCard(canvas, card, accent, coverImage) {
  const w = canvas.width;
  const h = canvas.height;
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, w, h);

  // card body
  const bg = ctx.createLinearGradient(0, 0, w, h);
  bg.addColorStop(0, "rgba(30, 33, 48, 0.96)");
  bg.addColorStop(1, "rgba(15, 16, 26, 0.96)");
  ctx.fillStyle = bg;
  roundRect(ctx, 2, 2, w - 4, h - 4, 22);
  ctx.fill();
  ctx.lineWidth = 2;
  ctx.strokeStyle = "rgba(255,255,255,0.10)";
  ctx.stroke();

  // cover
  const pad = 20;
  const size = h - pad * 2;
  const cx = pad;
  const cy = pad;
  if (coverImage && coverImage.complete && coverImage.naturalWidth > 0) {
    ctx.save();
    roundRect(ctx, cx, cy, size, size, 16);
    ctx.clip();
    ctx.drawImage(coverImage, cx, cy, size, size);
    ctx.restore();
  } else {
    drawPlaceholderCover(ctx, cx, cy, size, card.title, accent);
  }

  // text
  const tx = cx + size + 22;
  const maxWidth = w - tx - 92;
  ctx.textBaseline = "alphabetic";

  ctx.fillStyle = "rgba(255,255,255,0.5)";
  ctx.font = `600 22px ${FONT}`;
  ctx.fillText(KIND_LABEL[card.kindKey] || "合集", tx, cy + 44);

  ctx.fillStyle = "#f8fafc";
  ctx.font = `700 40px ${FONT}`;
  ctx.fillText(clipText(ctx, card.title, maxWidth), tx, cy + 106);

  ctx.fillStyle = "rgba(255,255,255,0.6)";
  ctx.font = `400 24px ${FONT}`;
  ctx.fillText(clipText(ctx, card.subtitle || "", maxWidth), tx, cy + 152);

  if (card.count > 0) {
    ctx.fillStyle = "rgba(255,255,255,0.38)";
    ctx.font = `400 22px ${FONT}`;
    ctx.fillText(card.count + " 首", tx, cy + 200);
  }

  // play button (bottom-right hit region)
  const rgb = hexToRgb(accent);
  ctx.fillStyle = `rgba(${rgb.r},${rgb.g},${rgb.b},0.95)`;
  ctx.beginPath();
  ctx.arc(w - 58, h - 58, 34, 0, Math.PI * 2);
  ctx.fill();
  ctx.fillStyle = "#0f172a";
  ctx.beginPath();
  ctx.moveTo(w - 66, h - 74);
  ctx.lineTo(w - 66, h - 42);
  ctx.lineTo(w - 40, h - 58);
  ctx.closePath();
  ctx.fill();

  return canvas;
}

const ROW_H = 60;
const HEADER_H = 132;

export function detailMetrics(canvas) {
  const rows = Math.max(1, Math.floor((canvas.height - HEADER_H) / ROW_H));
  return { rowHeight: ROW_H, headerHeight: HEADER_H, visibleRows: rows };
}

export function drawDetail(canvas, detail, accent, scrollRow) {
  const w = canvas.width;
  const h = canvas.height;
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, w, h);

  // panel
  ctx.fillStyle = "rgba(14, 15, 24, 0.95)";
  roundRect(ctx, 2, 2, w - 4, h - 4, 26);
  ctx.fill();
  ctx.lineWidth = 2;
  ctx.strokeStyle = "rgba(255,255,255,0.12)";
  ctx.stroke();

  // header
  ctx.save();
  roundRect(ctx, 2, 2, w - 4, h - 4, 26);
  ctx.clip();
  const rgb = hexToRgb(accent);
  const header = ctx.createLinearGradient(0, 0, w, HEADER_H);
  header.addColorStop(0, `rgba(${rgb.r},${rgb.g},${rgb.b},0.30)`);
  header.addColorStop(1, "rgba(0,0,0,0)");
  ctx.fillStyle = header;
  ctx.fillRect(0, 0, w, HEADER_H);
  ctx.restore();

  ctx.fillStyle = "#f8fafc";
  ctx.font = `700 34px ${FONT}`;
  ctx.fillText(clipText(ctx, detail.title, w - 60), 30, 58);
  ctx.fillStyle = "rgba(255,255,255,0.55)";
  ctx.font = `400 24px ${FONT}`;
  ctx.fillText(detail.subtitle || detail.tracks.length + " 首", 30, 98);

  const meta = detailMetrics(canvas);
  const start = Math.max(0, Math.min(scrollRow, Math.max(0, detail.tracks.length - meta.visibleRows)));

  for (let i = 0; i < meta.visibleRows; i++) {
    const trackIndex = start + i;
    if (trackIndex >= detail.tracks.length) break;
    const track = detail.tracks[trackIndex];
    const y = HEADER_H + i * ROW_H;

    if (i % 2 === 0) {
      ctx.fillStyle = "rgba(255,255,255,0.03)";
      ctx.fillRect(16, y + 3, w - 32, ROW_H - 6);
    }

    ctx.fillStyle = "rgba(255,255,255,0.35)";
    ctx.font = `400 22px ${FONT}`;
    ctx.fillText(String(trackIndex + 1).padStart(2, "0"), 30, y + 38);

    ctx.fillStyle = "#f1f5f9";
    ctx.font = `500 26px ${FONT}`;
    ctx.fillText(clipText(ctx, track.title, w - 260), 80, y + 38);

    ctx.fillStyle = "rgba(255,255,255,0.45)";
    ctx.font = `400 21px ${FONT}`;
    const seconds = Math.max(0, Math.round((track.durationMs || 0) / 1000));
    const duration = `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
    ctx.fillText(duration, w - 88, y + 38);
  }

  return { start, visibleRows: meta.visibleRows, rowHeight: ROW_H, headerHeight: HEADER_H };
}
