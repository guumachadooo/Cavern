/**
 * Cavernize Android — Free Ephemeral Live Diagnostic Relay (Cloudflare Worker + KV)
 *
 * 100% FREE on Cloudflare Workers Free Tier (100,000 req/day + KV Free Tier).
 * Zero npm / zero package.json needed — paste directly into Cloudflare Dashboard:
 *   1. Cloudflare Dashboard -> Storage & Databases -> KV -> Create namespace: CAVERNIZE_RELAY
 *   2. Workers & Pages -> Create Worker -> Name: cavernize-diag-relay -> Deploy
 *   3. Worker Settings -> Bindings -> Add KV Namespace binding:
 *        Variable name: RELAY_KV   |   KV namespace: CAVERNIZE_RELAY
 *   4. Edit Code -> paste this entire file -> Save and Deploy.
 *   5. Copy your Worker URL (e.g. https://cavernize-diag-relay.<your-subdomain>.workers.dev)
 *      into Cavernize Android -> Settings -> Diagnostics -> Live Bridge Setup.
 *
 * Security model:
 * - Sessions are opt-in on the phone and expire automatically after <= 15 minutes (900s KV TTL).
 * - Session token (>= 20 chars) is sent ONLY via X-Session-Token header (never in URL) and
 *   hashed with SHA-256 before forming KV keys.
 * - Only a strict whitelist of read-only diagnostic commands is accepted.
 * - Max payload size capped at 256 KB.
 */

const MAX_TTL_SECONDS = 900; // 15 minutes hard ceiling
const MAX_BODY_BYTES = 256 * 1024;
const ALLOWED_COMMANDS = new Set([
  "ping",
  "quick_check",
  "full_check",
  "log_tail",
  "summary",
  "audio_routes",
  "memory",
]);

function jsonResp(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

async function sha256Hex(text) {
  const data = new TextEncoder().encode(text);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)]
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

function getValidToken(request) {
  const token = (request.headers.get("X-Session-Token") || "").trim();
  if (token.length < 20 || token.length > 96 || !/^[A-Za-z0-9_-]+$/.test(token)) {
    return null;
  }
  return token;
}

async function readBoundedJson(request) {
  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) {
    return { error: "Payload exceeds 256 KB limit", status: 413 };
  }
  try {
    return { data: raw ? JSON.parse(raw) : {} };
  } catch {
    return { error: "Invalid JSON body", status: 400 };
  }
}

export default {
  async fetch(request, env) {
    if (!env || !env.RELAY_KV) {
      return jsonResp({ ok: false, error: "RELAY_KV binding missing in Worker settings." }, 500);
    }

    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "") || "/";

    // Health check endpoint (no secrets exposed)
    if (request.method === "GET" && path === "/health") {
      return jsonResp({ ok: true, service: "cavernize-live-relay", version: 1, maxTtlSeconds: MAX_TTL_SECONDS });
    }

    const token = getValidToken(request);
    if (!token) {
      return jsonResp({ ok: false, error: "Missing or invalid X-Session-Token header." }, 401);
    }

    const hash = await sha256Hex(token);
    const metaKey = `s:${hash}:meta`;
    const cmdKey = `s:${hash}:cmd`;
    const resKeyPrefix = `s:${hash}:res:`;

    // 1. Phone opens ephemeral session (TTL <= 900s)
    if (request.method === "POST" && path === "/v1/session/open") {
      const parsed = await readBoundedJson(request);
      if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
      const ttl = Math.min(MAX_TTL_SECONDS, Math.max(60, Number(parsed.data.ttlSeconds) || MAX_TTL_SECONDS));
      const expiresAtUtc = new Date(Date.now() + ttl * 1000).toISOString();
      const meta = {
        openedAtUtc: new Date().toISOString(),
        expiresAtUtc,
        device: String(parsed.data.device || "").slice(0, 80),
        appVersion: String(parsed.data.appVersion || "").slice(0, 32),
      };
      await env.RELAY_KV.put(metaKey, JSON.stringify(meta), { expirationTtl: ttl });
      return jsonResp({ ok: true, expiresAtUtc, ttlSeconds: ttl });
    }

    // All remaining endpoints require an active session
    const metaRaw = await env.RELAY_KV.get(metaKey);
    if (!metaRaw) {
      return jsonResp({ ok: false, error: "Session expired or not open." }, 404);
    }

    // 2. Phone closes session immediately
    if (request.method === "DELETE" && path === "/v1/session/close") {
      await Promise.all([env.RELAY_KV.delete(metaKey), env.RELAY_KV.delete(cmdKey)]);
      return jsonResp({ ok: true, closed: true });
    }

    // 3. AI submits a whitelisted read-only diagnostic command
    if (request.method === "POST" && path === "/v1/session/cmd") {
      const parsed = await readBoundedJson(request);
      if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
      const cmd = String(parsed.data.cmd || "").trim().toLowerCase();
      if (!ALLOWED_COMMANDS.has(cmd)) {
        return jsonResp({ ok: false, error: `Command '${cmd}' not in read-only whitelist.` }, 400);
      }
      const requestId = String(parsed.data.requestId || crypto.randomUUID()).replace(/[^A-Za-z0-9_-]/g, "").slice(0, 48);
      const payload = {
        requestId,
        cmd,
        lines: Math.min(500, Math.max(20, Number(parsed.data.lines) || 120)),
        errorsOnly: Boolean(parsed.data.errorsOnly),
        queuedAtUtc: new Date().toISOString(),
      };
      await env.RELAY_KV.put(cmdKey, JSON.stringify(payload), { expirationTtl: 180 });
      return jsonResp({ ok: true, requestId, cmd });
    }

    // 4. Phone polls for the next pending command
    if (request.method === "GET" && path === "/v1/session/poll") {
      const cmdRaw = await env.RELAY_KV.get(cmdKey);
      if (!cmdRaw) {
        return jsonResp({ ok: true, hasCommand: false });
      }
      await env.RELAY_KV.delete(cmdKey);
      return jsonResp({ ok: true, hasCommand: true, command: JSON.parse(cmdRaw) });
    }

    // 5. Phone uploads sanitized diagnostic result for a requestId
    if (request.method === "POST" && path === "/v1/session/result") {
      const parsed = await readBoundedJson(request);
      if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
      const requestId = String(parsed.data.requestId || "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 48);
      if (!requestId) {
        return jsonResp({ ok: false, error: "Missing requestId." }, 400);
      }
      await env.RELAY_KV.put(resKeyPrefix + requestId, JSON.stringify(parsed.data), { expirationTtl: 300 });
      return jsonResp({ ok: true, requestId });
    }

    // 6. AI polls for result of requestId
    if (request.method === "GET" && path === "/v1/session/result") {
      const requestId = (url.searchParams.get("requestId") || "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 48);
      if (!requestId) {
        return jsonResp({ ok: false, error: "Missing requestId query parameter." }, 400);
      }
      const resRaw = await env.RELAY_KV.get(resKeyPrefix + requestId);
      if (!resRaw) {
        return jsonResp({ ok: true, ready: false });
      }
      await env.RELAY_KV.delete(resKeyPrefix + requestId);
      return jsonResp({ ok: true, ready: true, result: JSON.parse(resRaw) });
    }

    return jsonResp({ ok: false, error: "Not found." }, 404);
  },
};
