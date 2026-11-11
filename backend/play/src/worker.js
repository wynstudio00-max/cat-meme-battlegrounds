// Serves the WebGL build (static assets in ./public) for play.catbattlegrounds.com, 2026-10-28.
// Unity's Brotli files (Build/*.unityweb) are sent as they are with "Content-Encoding: br", so the browser unpacks them natively
// (fast; the build's JS fallback would also work, just slower). Everything else comes straight from the asset store.

const TYPES = {
  'wasm.unityweb': 'application/wasm',                 // lets the browser compile the wasm while it downloads
  'framework.js.unityweb': 'application/javascript',
  'data.unityweb': 'application/octet-stream',
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const res = await env.ASSETS.fetch(request);
    if (!url.pathname.startsWith('/Build/') || !url.pathname.endsWith('.unityweb') || !res.ok) {
      if (url.pathname === '/' || url.pathname.endsWith('.html')) {
        const h = new Headers(res.headers);
        h.set('Cache-Control', 'no-cache');            // a new build shows up at once
        return new Response(res.body, { status: res.status, headers: h });
      }
      return res;
    }
    const kind = Object.keys(TYPES).find((k) => url.pathname.endsWith(k));
    const h = new Headers(res.headers);
    h.set('Content-Type', kind ? TYPES[kind] : 'application/octet-stream');
    h.set('Content-Encoding', 'br');
    h.set('Cache-Control', 'public, max-age=3600');
    h.delete('Content-Length');
    return new Response(res.body, { status: res.status, headers: h, encodeBody: 'manual' });   // already Brotli: do not compress again
  },
};
