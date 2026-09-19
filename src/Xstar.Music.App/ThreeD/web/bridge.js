/* C# <-> web bridge. C# pushes {type:'state'|'audio'|'library'}; the page posts
 * {type:'action', action, ...} back. */

const webview = (window.chrome && window.chrome.webview) || null;
const listeners = new Set();

if (webview) {
  webview.addEventListener("message", (event) => {
    const data = event.data;
    if (!data || typeof data !== "object") return;
    for (const fn of listeners) {
      try { fn(data); } catch (err) { console.error(err); }
    }
  });
}

export const connected = !!webview;

export function onMessage(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

export function post(message) {
  if (webview) webview.postMessage(message);
}

export function action(name, extra) {
  post(Object.assign({ type: "action", action: name }, extra || {}));
}
