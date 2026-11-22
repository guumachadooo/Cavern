/**
 * Cavernize Android — Unified Diagnostics & Live Bridge Hub v3 (Cloudflare Worker + KV)
 *
 * 100% FREE on Cloudflare Workers Free Tier (100,000 req/day + KV Free Tier).
 * Replaces GitHub commit spam for runtime device logs & self-checkups AND powers Live Bridge v3
 * (biometric background wake + 30+ shortcuts + unrestricted Tier-2 dynamic reflection/UI/shell).
 *
 * === PASSO A PASSO NO PAINEL CLOUDFLARE EM PORTUGUES (PT-BR) ===
 *   1. Acesse https://dash.cloudflare.com e faca login.
 *   2. Menu esquerdo -> "Armazenamento e bancos de dados" -> "Workers KV" (ou "KV"):
 *        - Clique em "Criar" / "Criar um namespace"
 *        - Nome: CAVERNIZE_RELAY  -> clique em "Criar" / "Adicionar".
 *   3. Menu esquerdo -> "Computacao (Workers)" -> "Workers e Pages" (ou direto "Workers e Pages"):
 *        - Clique em "Criar" -> aba "Workers" -> "Criar Worker" (ou "Comecar com Hello World")
 *        - Nome: cavernize-diag-relay -> clique em "Implantar" (Deploy).
 *   4. Abra o Worker criado -> aba "Configuracoes" (Settings) -> secao "Associacoes" (Bindings)
 *      (em contas antigas: "Variaveis" -> "Associacoes de namespace KV"):
 *        - Clique em "Adicionar" / "Adicionar associacao" -> escolha "Namespace KV" (KV namespace)
 *        - Nome da variavel (Variable name): RELAY_KV  (exatamente assim, em maiusculas!)
 *        - Namespace KV: selecione CAVERNIZE_RELAY
 *        - Clique em "Salvar" / "Implantar".
 *   5. No canto superior direito do Worker, clique em "Editar codigo" (Edit code):
 *        - Apague tudo no arquivo worker.js, cole TODO este codigo e clique em "Implantar" (Deploy).
 *   6. Copie a URL do Worker (ex.: https://cavernize-diag-relay.<seu-subdominio>.workers.dev)
 *      e cole no app: Configuracoes -> Sistema -> Diagnostico -> Configurar Ponte / Nuvem.
 *
 * === SECURITY MODEL (v3) ===
 * - Zero plaintext pairing secret on Cloudflare:
 *     * Wake channel uses X-Device-Hash = sha256("wake-channel-v2:" + pairSecretHex).
 *     * Wake requests carry HMAC-SHA256 signatures verified strictly on-device against Android Keystore.
 *     * Biometric approval returns the ephemeral session token E2E encrypted (SHA256-CTR + HMAC-SHA256).
 *     * Telemetry storage (/v1/telemetry/*) requires BOTH X-Device-Hash AND X-Telemetry-Key
 *       (= sha256("telemetry-access-v3:" + pairSecretHex)), hashed again on the edge into an isolated
 *       KV bucket key `t:${sha256(devHash + ":" + telemetryKey)}:*` with 14-day auto-expiring TTL.
 *     * Optional extra hardening: set Worker Secret `CAVERNIZE_ADMIN_KEY` in Settings -> Variables & Secrets
 *       if you want an additional server-side X-Admin-Key gate.
 * - Zero wildcard CORS on authenticated endpoints.
 */

const MAX_TTL_SECONDS = 3600; // 60 min hard ceiling for live sessions
const MAX_WAKE_TTL_SECONDS = 180; // 3 min for biometric approval
const TELEMETRY_TTL_SECONDS = 14 * 24 * 3600; // 14 days automatic KV hygiene
const MAX_BODY_BYTES = 1024 * 1024; // 1 MB payload ceiling (supports full logs + screenshots)
const MAX_HISTORY_ENTRIES = 10;
const CMD_NAME_RE = /^[a-z0-9_.:-]{1,64}$/;
const HEX64_RE = /^[0-9a-f]{64}$/;
const ALLOWED_TELEMETRY_KINDS = new Set([
  "log",
  "summary",
  "selfcheck_md",
  "selfcheck_json",
  "ui_audit",
]);

function jsonResp(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      "X-Content-Type-Options": "nosniff",
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

function timingSafeEqualStr(a, b) {
  if (typeof a !== "string" || typeof b !== "string") return false;
  const enc = new TextEncoder();
  const ab = enc.encode(a);
  const bb = enc.encode(b);
  if (ab.length !== bb.length) return false;
  let diff = 0;
  for (let i = 0; i < ab.length; i++) {
    diff |= ab[i] ^ bb[i];
  }
  return diff === 0;
}

function verifyOptionalAdminGate(request, env) {
  const expected = env && typeof env.CAVERNIZE_ADMIN_KEY === "string" ? env.CAVERNIZE_ADMIN_KEY.trim() : "";
  if (!expected) return true;
  const provided = (request.headers.get("X-Admin-Key") || "").trim();
  return provided.length > 0 && timingSafeEqualStr(provided, expected);
}

function getValidToken(request) {
  const token = (request.headers.get("X-Session-Token") || "").trim();
  if (token.length < 20 || token.length > 96 || !/^[A-Za-z0-9_-]+$/.test(token)) {
    return null;
  }
  return token;
}

function getValidHex64Header(request, headerName) {
  const h = (request.headers.get(headerName) || "").trim().toLowerCase();
  return HEX64_RE.test(h) ? h : null;
}

function sanitizeId(raw) {
  return String(raw || "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 48);
}

async function readBoundedJson(request, maxBytes = MAX_BODY_BYTES) {
  const raw = await request.text();
  if (raw.length > maxBytes) {
    return { error: `Payload exceeds ${Math.round(maxBytes / 1024)} KB limit`, status: 413 };
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
      return jsonResp({ ok: false, error: "RELAY_KV binding missing in Worker Settings -> Bindings (Associacoes)." }, 500);
    }

    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "") || "/";

    // Health check endpoint (no secrets exposed)
    if (request.method === "GET" && (path === "/health" || path === "/")) {
      return jsonResp({
        ok: true,
        service: "cavernize-unified-hub",
        version: 3,
        maxTtlSeconds: MAX_TTL_SECONDS,
        telemetryTtlDays: Math.round(TELEMETRY_TTL_SECONDS / 86400),
        supportsWake: true,
        supportsFlexibleCommands: true,
        supportsDynamicExecution: true,
        supportsTelemetry: true,
      });
    }

    if (!verifyOptionalAdminGate(request, env)) {
      return jsonResp({ ok: false, error: "Invalid or missing X-Admin-Key." }, 403);
    }

    // ==================== UNIFIED TELEMETRY & LOG STORAGE (/v1/telemetry/*) ====================
    if (path.startsWith("/v1/telemetry/")) {
      const devHash = getValidHex64Header(request, "X-Device-Hash");
      const telKey = getValidHex64Header(request, "X-Telemetry-Key");
      if (!devHash || !telKey) {
        return jsonResp({
          ok: false,
          error: "Missing or invalid X-Device-Hash / X-Telemetry-Key headers (64 hex chars each).",
        }, 401);
      }

      const bucket = await sha256Hex(`${devHash}:${telKey}`);
      const prefix = `t:${bucket}:`;

      // T1. Upload one or more diagnostic artifacts atomically
      if (request.method === "POST" && path === "/v1/telemetry/upload") {
        const parsed = await readBoundedJson(request, MAX_BODY_BYTES);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const nowUtc = new Date().toISOString();
        const meta = d.meta && typeof d.meta === "object" ? d.meta : {};
        const artifacts = {};

        if (d.artifacts && typeof d.artifacts === "object") {
          for (const [k, v] of Object.entries(d.artifacts)) {
            if (ALLOWED_TELEMETRY_KINDS.has(k) && typeof v === "string") {
              artifacts[k] = v;
            }
          }
        } else if (typeof d.kind === "string" && ALLOWED_TELEMETRY_KINDS.has(d.kind) && typeof d.content === "string") {
          artifacts[d.kind] = d.content;
        }

        const kinds = Object.keys(artifacts);
        if (kinds.length === 0) {
          return jsonResp({
            ok: false,
            error: "No valid telemetry artifacts provided (expected log, summary, selfcheck_md, selfcheck_json, or ui_audit).",
          }, 400);
        }

        let totalBytes = 0;
        const writes = [];
        for (const k of kinds) {
          const val = artifacts[k];
          totalBytes += val.length;
          writes.push(env.RELAY_KV.put(`${prefix}latest:${k}`, val, { expirationTtl: TELEMETRY_TTL_SECONDS }));
        }

        const snapshotMeta = {
          uploadedAtUtc: nowUtc,
          trigger: String(meta.trigger || d.trigger || "manual").slice(0, 48),
          appVersion: String(meta.appVersion || "").slice(0, 48),
          device: String(meta.device || "").slice(0, 80),
          sessionId: String(meta.sessionId || "").slice(0, 48),
          verdict: String(meta.verdict || "").slice(0, 32),
          summary: String(meta.summary || "").slice(0, 240),
          kinds,
          totalBytes,
        };
        writes.push(env.RELAY_KV.put(`${prefix}latest:meta`, JSON.stringify(snapshotMeta), { expirationTtl: TELEMETRY_TTL_SECONDS }));

        // Maintain rolling history index
        let history = [];
        try {
          const rawHist = await env.RELAY_KV.get(`${prefix}history`);
          if (rawHist) {
            const parsedHist = JSON.parse(rawHist);
            if (Array.isArray(parsedHist)) history = parsedHist;
          }
        } catch {
          history = [];
        }
        history.unshift(snapshotMeta);
        if (history.length > MAX_HISTORY_ENTRIES) {
          history = history.slice(0, MAX_HISTORY_ENTRIES);
        }
        writes.push(env.RELAY_KV.put(`${prefix}history`, JSON.stringify(history), { expirationTtl: TELEMETRY_TTL_SECONDS }));

        await Promise.all(writes);
        return jsonResp({
          ok: true,
          uploadedAtUtc: nowUtc,
          kinds,
          totalBytes,
          ttlDays: Math.round(TELEMETRY_TTL_SECONDS / 86400),
        });
      }

      // T2. Fetch latest telemetry artifact(s)
      if (request.method === "GET" && path === "/v1/telemetry/latest") {
        const kind = (url.searchParams.get("kind") || "all").trim().toLowerCase();
        if (kind === "meta") {
          const rawMeta = await env.RELAY_KV.get(`${prefix}latest:meta`);
          return jsonResp({ ok: true, meta: rawMeta ? JSON.parse(rawMeta) : null });
        }
        if (kind === "all") {
          const [metaRaw, logRaw, sumRaw, scMdRaw, scJsonRaw, uiRaw] = await Promise.all([
            env.RELAY_KV.get(`${prefix}latest:meta`),
            env.RELAY_KV.get(`${prefix}latest:log`),
            env.RELAY_KV.get(`${prefix}latest:summary`),
            env.RELAY_KV.get(`${prefix}latest:selfcheck_md`),
            env.RELAY_KV.get(`${prefix}latest:selfcheck_json`),
            env.RELAY_KV.get(`${prefix}latest:ui_audit`),
          ]);
          return jsonResp({
            ok: true,
            meta: metaRaw ? JSON.parse(metaRaw) : null,
            artifacts: {
              log: logRaw || null,
              summary: sumRaw || null,
              selfcheck_md: scMdRaw || null,
              selfcheck_json: scJsonRaw || null,
              ui_audit: uiRaw || null,
            },
          });
        }
        if (!ALLOWED_TELEMETRY_KINDS.has(kind)) {
          return jsonResp({ ok: false, error: `Unknown telemetry kind '${kind}'.` }, 400);
        }
        const [val, metaRaw] = await Promise.all([
          env.RELAY_KV.get(`${prefix}latest:${kind}`),
          env.RELAY_KV.get(`${prefix}latest:meta`),
        ]);
        if (val === null) {
          return jsonResp({ ok: false, error: `No telemetry uploaded yet for kind '${kind}'.` }, 404);
        }
        return jsonResp({
          ok: true,
          kind,
          meta: metaRaw ? JSON.parse(metaRaw) : null,
          content: val,
        });
      }

      // T3. Fetch rolling upload history
      if (request.method === "GET" && path === "/v1/telemetry/history") {
        const rawHist = await env.RELAY_KV.get(`${prefix}history`);
        return jsonResp({ ok: true, history: rawHist ? JSON.parse(rawHist) : [] });
      }

      // T4. Verify telemetry auth/connectivity cleanly
      if (request.method === "GET" && path === "/v1/telemetry/ping") {
        const rawMeta = await env.RELAY_KV.get(`${prefix}latest:meta`);
        return jsonResp({
          ok: true,
          service: "cavernize-unified-hub",
          version: 3,
          hasTelemetry: Boolean(rawMeta),
          lastUpload: rawMeta ? JSON.parse(rawMeta).uploadedAtUtc : null,
        });
      }

      return jsonResp({ ok: false, error: "Unknown telemetry endpoint." }, 404);
    }

    // ==================== PAIRED BIOMETRIC WAKE ENDPOINTS ====================
    if (path.startsWith("/v1/wake/")) {
      const devHash = getValidHex64Header(request, "X-Device-Hash");
      if (!devHash) {
        return jsonResp({ ok: false, error: "Missing or invalid X-Device-Hash header (64 hex chars)." }, 401);
      }
      const pendingKey = `w:${devHash}:pending`;
      const statusPrefix = `w:${devHash}:st:`;

      if (request.method === "POST" && path === "/v1/wake/request") {
        const parsed = await readBoundedJson(request, 64 * 1024);
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

      if (request.method === "GET" && path === "/v1/wake/poll") {
        const raw = await env.RELAY_KV.get(pendingKey);
        if (!raw) {
          return jsonResp({ ok: true, hasWake: false });
        }
        await env.RELAY_KV.delete(pendingKey);
        return jsonResp({ ok: true, hasWake: true, wake: JSON.parse(raw) });
      }

      if (request.method === "POST" && path === "/v1/wake/approve") {
        const parsed = await readBoundedJson(request, 64 * 1024);
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

      if (request.method === "POST" && path === "/v1/wake/deny") {
        const parsed = await readBoundedJson(request, 32 * 1024);
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

    if (request.method === "POST" && path === "/v1/session/open") {
      const parsed = await readBoundedJson(request, 64 * 1024);
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

    const metaRaw = await env.RELAY_KV.get(metaKey);
    if (!metaRaw) {
      return jsonResp({ ok: false, error: "Session expired or not open." }, 404);
    }

    if (request.method === "DELETE" && path === "/v1/session/close") {
      await Promise.all([env.RELAY_KV.delete(metaKey), env.RELAY_KV.delete(cmdKey)]);
      return jsonResp({ ok: true, closed: true });
    }

    if (request.method === "POST" && path === "/v1/session/cmd") {
      const parsed = await readBoundedJson(request, MAX_BODY_BYTES);
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

    if (request.method === "GET" && path === "/v1/session/poll") {
      const cmdRaw = await env.RELAY_KV.get(cmdKey);
      if (!cmdRaw) {
        return jsonResp({ ok: true, hasCommand: false });
      }
      await env.RELAY_KV.delete(cmdKey);
      return jsonResp({ ok: true, hasCommand: true, command: JSON.parse(cmdRaw) });
    }

    if (request.method === "POST" && path === "/v1/session/result") {
      const parsed = await readBoundedJson(request, MAX_BODY_BYTES);
      if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
      const requestId = sanitizeId(parsed.data?.requestId);
      if (!requestId) {
        return jsonResp({ ok: false, error: "Missing requestId." }, 400);
      }
      await env.RELAY_KV.put(resKeyPrefix + requestId, JSON.stringify(parsed.data), { expirationTtl: 300 });
      return jsonResp({ ok: true, requestId });
    }

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
