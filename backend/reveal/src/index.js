// Worker cmbg-reveal: reveals waiting pass-card opens (see reveal.js).
//   cron (every minute)  -> runReveal
//   POST /poke           -> runReveal now (the card page calls it right after an open() is mined)
//   GET  /health         -> status (pending, operator address + balance)
import { runReveal, status } from "./reveal.js";

function cors(env, req) {
  const allowed = (env.ALLOW_ORIGIN || "*").split(",").map((s) => s.trim());
  const origin = req.headers.get("Origin") || "";
  const allow = allowed.includes("*") ? "*" : allowed.includes(origin) ? origin : allowed[0];
  return { "Access-Control-Allow-Origin": allow, "Access-Control-Allow-Methods": "GET, POST, OPTIONS", "Access-Control-Allow-Headers": "Content-Type", Vary: "Origin" };
}

function json(data, code, headers) {
  return new Response(JSON.stringify(data), { status: code || 200, headers: { "Content-Type": "application/json", "Cache-Control": "no-store", ...headers } });
}

export default {
  async fetch(req, env, ctx) {
    const h = cors(env, req);
    if (req.method === "OPTIONS") return new Response(null, { headers: h });
    const path = new URL(req.url).pathname.replace(/\/+$/, "");
    // mainnet Worker before the contracts exist (deploy.mjs --mainnet step a): stay idle instead of failing every call
    if (!env.SOURCE || !env.CHAIN_CHECKPOINTS) return json({ ok: true, idle: true, reason: "no source / checkpoints yet" }, 200, h);
    try {
      if (path === "/health" && req.method === "GET") return json({ ok: true, ...(await status(env)) }, 200, h);
      if (path === "/poke" && req.method === "POST") return json({ ok: true, ...(await runReveal(env)) }, 200, h);
      return json({ ok: false, error: "not found" }, 404, h);
    } catch (e) {
      console.error("reveal error", e && (e.shortMessage || e.message));
      return json({ ok: false, error: String((e && (e.shortMessage || e.message)) || e).slice(0, 200) }, 500, h);
    }
  },

  async scheduled(event, env, ctx) {
    if (!env.SOURCE || !env.CHAIN_CHECKPOINTS) return;   // not set up yet (mainnet before the deploy)
    ctx.waitUntil(
      runReveal(env).then(
        (r) => { if (r.revealed || r.skipped) console.log("cron", JSON.stringify(r)); },
        (e) => console.error("cron error", e && (e.shortMessage || e.message))
      )
    );
  },
};
