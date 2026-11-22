/**
 * Cavernize Android — Free Live Bridge v2 Relay (Cloudflare Worker + KV)
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
 * Security model (v2):
 * - Zero plaintext pairing secret on Cloudflare: clients only send X-Device-Hash (SHA-256
 *   derived public wake channel ID). Wake requests carry HMAC-SHA256 signatures verified
 *   strictly on-device against the Android Keystore-encrypted pairing key.
 * - On biometric fingerprint approval, the phone encrypts the ephemeral session code
 *   end-to-end (Encrypt-then-MAC: SHA256-CTR + HMAC-SHA256) before uploading to /v1/wake/approve,
 *   so even Cloudflare KV never sees the plaintext session token.
 * - Sessions expire automatically in KV (TTL <= 3600s, 60 min hard ceiling).
 * - Flexible command relay: enforces safe command syntax + bounded payload (<= 512 KB), while
 *   the Android device enforces exact Tier 0 / Tier 1 / Tier 2 authorization, secret scrubbing,
 *   canonical path confinement, and W^X native binary restrictions.
 */

const MAX_TTL_SECONDS = 3600; // 60 minutes hard ceiling
const MAX_WAKE_TTL_SECONDS = 180; // 3 minutes for biometric approval
const MAX_BODY_BYTES = 512 * 1024;
const CMD_NAME_RE = /^[a-z0-9_.:-]{1,64}$/;
const HEX64_RE = /^[0-9a-f]{64}$/;

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

function getValidDeviceHash(request) {
  const h = (request.headers.get("X-Device-Hash") || "").trim().toLowerCase();
  return HEX64_RE.test(h) ? h : null;
}

function sanitizeId(raw) {
  return String(raw || "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 48);
}

async function readBoundedJson(request) {
  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) {
    return { error: "Payload exceeds 512 KB limit", status: 413 };
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
      return jsonResp({
        ok: true,
        service: "cavernize-live-relay",
        version: 2,
        maxTtlSeconds: MAX_TTL_SECONDS,
        supportsWake: true,
        supportsFlexibleCommands: true,
      });
    }

    // ==================== PAIRED BIOMETRIC WAKE ENDPOINTS ====================
    if (path.startsWith("/v1/wake/")) {
      const devHash = getValidDeviceHash(request);
      if (!devHash) {
        return jsonResp({ ok: false, error: "Missing or invalid X-Device-Hash header (64 hex chars)." }, 401);
      }
      const pendingKey = `w:${devHash}:pending`;
      const statusPrefix = `w:${devHash}:st:`;

      // W1. AI submits HMAC-signed wake request
      if (request.method === "POST" && path === "/v1/wake/request") {
        const parsed = await readBoundedJson(request);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const wakeId = sanitizeId(d.wakeId || crypto.randomUUID());
        const sig = String(d.signature || "").trim().toLowerCase();
        if (!wakeId || !HEX64_RE.test(sig)) {
          return jsonResp({ ok: false, error: "Invalid wakeId or HMAC signature format." }, 400);
        }
        const wakePayload = {
          wakeId,
          timestampUnix: Number(d.timestampUnix) || Math.floor(Date.now() / 1000),
          requestedTier: Math.max(0, Math.min(2, Number(d.requestedTier ?? 1))),
          durationSeconds: Math.max(300, Math.min(MAX_TTL_SECONDS, Number(d.durationSeconds) || 1800)),
          reason: String(d.reason || "AI session request").slice(0, 160),
          requester: String(d.requester || "AI Assistant").slice(0, 64),
          signature: sig,
          queuedAtUtc: new Date().toISOString(),
        };
        await Promise.all([
          env.RELAY_KV.put(pendingKey, JSON.stringify(wakePayload), { expirationTtl: MAX_WAKE_TTL_SECONDS }),
          env.RELAY_KV.put(
            statusPrefix + wakeId,
            JSON.stringify({ status: "pending", wakeId, queuedAtUtc: wakePayload.queuedAtUtc }),
            { expirationTtl: MAX_WAKE_TTL_SECONDS + 60 }
          ),
        ]);
        return jsonResp({ ok: true, wakeId, expiresInSeconds: MAX_WAKE_TTL_SECONDS });
      }

      // W2. Background Wake Service on phone polls for pending wake request
      if (request.method === "GET" && path === "/v1/wake/poll") {
        const raw = await env.RELAY_KV.get(pendingKey);
        if (!raw) {
          return jsonResp({ ok: true, hasWake: false });
        }
        await env.RELAY_KV.delete(pendingKey);
        return jsonResp({ ok: true, hasWake: true, wake: JSON.parse(raw) });
      }

      // W3. Phone approves wake after native BiometricPrompt (uploads encrypted session envelope)
      if (request.method === "POST" && path === "/v1/wake/approve") {
        const parsed = await readBoundedJson(request);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const wakeId = sanitizeId(d.wakeId);
        if (!wakeId || !d.encryptedSession) {
          return jsonResp({ ok: false, error: "Missing wakeId or encryptedSession." }, 400);
        }
        await env.RELAY_KV.put(
          statusPrefix + wakeId,
          JSON.stringify({
            status: "approved",
            wakeId,
            encryptedSession: d.encryptedSession,
            approvedAtUtc: new Date().toISOString(),
          }),
          { expirationTtl: 180 }
        );
        return jsonResp({ ok: true, wakeId, status: "approved" });
      }

      // W4. Phone denies wake request
      if (request.method === "POST" && path === "/v1/wake/deny") {
        const parsed = await readBoundedJson(request);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const wakeId = sanitizeId(d.wakeId);
        if (!wakeId) {
          return jsonResp({ ok: false, error: "Missing wakeId." }, 400);
        }
        await env.RELAY_KV.put(
          statusPrefix + wakeId,
          JSON.stringify({
            status: "denied",
            wakeId,
            reason: String(d.reason || "Denied by device user").slice(0, 120),
            deniedAtUtc: new Date().toISOString(),
          }),
          { expirationTtl: 120 }
        );
        return jsonResp({ ok: true, wakeId, status: "denied" });
      }

      // W5. AI polls wake approval status
      if (request.method === "GET" && path === "/v1/wake/status") {
        const wakeId = sanitizeId(url.searchParams.get("wakeId"));
        if (!wakeId) {
          return jsonResp({ ok: false, error: "Missing wakeId query parameter." }, 400);
        }
        const raw = await env.RELAY_KV.get(statusPrefix + wakeId);
        if (!raw) {
          return jsonResp({ ok: false, error: "Wake request expired or unknown." }, 404);
        }
        const parsed = JSON.parse(raw);
        if (parsed.status === "approved" || parsed.status === "denied") {
          await env.RELAY_KV.delete(statusPrefix + wakeId);
        }
        return jsonResp({ ok: true, ...parsed });
      }

      return jsonResp({ ok: false, error: "Unknown wake endpoint." }, 404);
    }

    // ==================== EPHEMERAL SESSION ENDPOINTS ====================
    const token = getValidToken(request);
    if (!token) {
      return jsonResp({ ok: false, error: "Missing or invalid X-Session-Token header." }, 401);
    }

    const hash = await sha256Hex(token);
    const metaKey = `s:${hash}:meta`;
    const cmdKey = `s:${hash}:cmd`;
    const resKeyPrefix = `s:${hash}:res:`;

    // 1. Phone opens ephemeral session (TTL <= 3600s)
    if (request.method === "POST" && path === "/v1/session/open") {
      const parsed = await readBoundedJson(request);
      if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
      const d = parsed.data || {};
      const ttl = Math.min(MAX_TTL_SECONDS, Math.max(60, Number(d.ttlSeconds) || 1800));
      const expiresAtUtc = new Date(Date.now() + ttl * 1000).toISOString();
      const meta = {
        openedAtUtc: new Date().toISOString(),
        expiresAtUtc,
        maxTier: Math.max(0, Math.min(2, Number(d.maxTier ?? 1))),
        device: String(d.device || "").slice(0, 80),
        appVersion: String(d.appVersion || "").slice(0, 32),
      };
      await env.RELAY_KV.put(metaKey, JSON.stringify(meta), { expirationTtl: ttl });
      return jsonResp({ ok: true, expiresAtUtc, ttlSeconds: ttl, maxTier: meta.maxTier });
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

    // 3. AI submits any structured command (device enforces Tier 0/1/2 policy)
    if (request.method === "POST" && path === "/v1/session/cmd") {
      const parsed = await readBoundedJson(request);
      if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
      const d = parsed.data || {};
      const cmd = String(d.cmd || "").trim().toLowerCase();
      if (!CMD_NAME_RE.test(cmd)) {
        return jsonResp({ ok: false, error: `Invalid command name '${cmd}'.` }, 400);
      }
      const requestId = sanitizeId(d.requestId || crypto.randomUUID());
      const argsObj = d.args && typeof d.args === "object" && !Array.isArray(d.args) ? d.args : {};
      const payload = {
        requestId,
        cmd,
        args: argsObj,
        lines: Math.min(1000, Math.max(20, Number(d.lines) || 150)),
        errorsOnly: Boolean(d.errorsOnly),
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

    // 5. Phone uploads sanitized response for a requestId
    if (request.method === "POST" && path === "/v1/session/result") {
      const parsed = await readBoundedJson(request);
      if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
      const requestId = sanitizeId(parsed.data?.requestId);
      if (!requestId) {
        return jsonResp({ ok: false, error: "Missing requestId." }, 400);
      }
      await env.RELAY_KV.put(resKeyPrefix + requestId, JSON.stringify(parsed.data), { expirationTtl: 300 });
      return jsonResp({ ok: true, requestId });
    }

    // 6. AI polls for result of requestId
    if (request.method === "GET" && path === "/v1/session/result") {
      const requestId = sanitizeId(url.searchParams.get("requestId"));
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
