/**
 * Cavernize & OmniAgent - Unified Diagnostics, Live Bridge & Full-Agent Hub v4 (Cloudflare Worker + KV)
 *
 * 100% FREE on Cloudflare Workers Free Tier (100,000 req/day + KV Free Tier).
 * 100% backwards-compatible with Cavernize Android Live Bridge v3 (/v1/telemetry/*, /v1/wake/*, /v1/session/*)
 * AND powers Full-Agent v4 ("Antigravity 2"):
 *   - /v1/agent/* : Multi-channel E2E-encrypted full-agent command queue (termux, android, debian, etc.)
 *                   with live presence heartbeats and FIFO command queuing.
 *   - /v1/blob/*  : E2E-encrypted chunked transfer store (up to 64 chunks for git delta bundles,
 *                   hot-compiled C# .dll probes, or large logs) with automatic 30-min KV expiration.
 *   - /v1/swarm/* : Zero-API-cost Multi-Agent Swarm Task Bus connecting AI Studio <-> Antigravity (Debian)
 *                   <-> secondary chats/local CLI workers with compact token-diet task/result exchange.
 *
 * === COMO ATUALIZAR SEM TRAVAR O NAVEGADOR (PT-BR) ===
 *   Opcao 1 (Upload de arquivo no Editor Cloudflare - 5 segundos, sem Ctrl+V):
 *     1. Baixe este arquivo do GitHub e renomeie para `worker.js` (ou o nome que aparece na barra esquerda do Worker).
 *     2. No painel Cloudflare -> Workers e Pages -> seu Worker -> "Editar codigo" (Quick Edit).
 *     3. Na barra lateral esquerda (Explorer de arquivos), clique com o botao direito -> "Upload..." (ou arraste o arquivo)
 *        para substituir `worker.js` instantaneamente, e clique em "Implantar" (Deploy).
 *   Opcao 2 (Deploy em 1 segundo via API preservando RELAY_KV):
 *     python3 -B .agents/agent/deploy_worker.py
 *
 * === SECURITY MODEL (v4 Zero-Trust E2E) ===
 * - Zero plaintext pairing secret on Cloudflare:
 *     * X-Device-Hash   = sha256("wake-channel-v2:" + pairSecretHex)
 *     * X-Telemetry-Key = sha256("telemetry-access-v3:" + pairSecretHex)
 *     * X-Agent-Key     = sha256("agent-access-v4:" + pairSecretHex)
 * - Every /v1/agent/* command, result, and /v1/blob/* payload is E2E encrypted client-side
 *   (Encrypt-then-MAC: SHA256-CTR + HMAC-SHA256) so Cloudflare NEVER sees plaintext code, commands, or outputs.
 * - Optional Worker Secret `CAVERNIZE_ADMIN_KEY` adds an extra X-Admin-Key gate on all non-health routes.
 */

const MAX_TTL_SECONDS = 3600; // 60 min hard ceiling for live sessions
const MAX_WAKE_TTL_SECONDS = 180; // 3 min for biometric approval
const TELEMETRY_TTL_SECONDS = 14 * 24 * 3600; // 14 days automatic KV hygiene
const AGENT_PRESENCE_TTL_SECONDS = 120; // 2 min presence heartbeat
const AGENT_CMD_TTL_SECONDS = 300; // 5 min command queue TTL
const AGENT_RESULT_TTL_SECONDS = 600; // 10 min result TTL
const BLOB_TTL_SECONDS = 1800; // 30 min chunked transfer TTL
const SWARM_TTL_SECONDS = 24 * 3600; // 24h swarm task TTL
const MAX_BODY_BYTES = 1024 * 1024; // 1 MB single-request payload ceiling
const MAX_HISTORY_ENTRIES = 10;
const MAX_AGENT_QUEUE = 16;
const MAX_BLOB_CHUNKS = 64;
const MAX_SWARM_TASKS = 32;
const KNOWN_AGENT_CHANNELS = ["termux", "android", "debian", "cavernize"];
const CMD_NAME_RE = /^[a-z0-9_.:-]{1,64}$/;
const CHANNEL_RE = /^[a-z0-9_-]{1,32}$/;
const HEX64_RE = /^[0-9a-f]{64}$/;
const ALLOWED_TELEMETRY_KINDS = new Set([
  "log",
  "summary",
  "selfcheck_md",
  "selfcheck_json",
  "ui_audit",
]);

/**
 * @param {any} obj
 * @param {number} [status=200]
 * @returns {Response}
 */
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

/**
 * @param {string} text
 * @returns {Promise<string>}
 */
async function sha256Hex(text) {
  const data = new TextEncoder().encode(text);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)]
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

/**
 * @param {string} a
 * @param {string} b
 * @returns {boolean}
 */
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

/**
 * @param {Request} request
 * @param {any} env
 * @returns {boolean}
 */
function verifyOptionalAdminGate(request, env) {
  const expected = env && typeof env.CAVERNIZE_ADMIN_KEY === "string" ? env.CAVERNIZE_ADMIN_KEY.trim() : "";
  if (!expected) return true;
  const provided = (request.headers.get("X-Admin-Key") || "").trim();
  return provided.length > 0 && timingSafeEqualStr(provided, expected);
}

/**
 * @param {Request} request
 * @returns {string | null}
 */
function getValidToken(request) {
  const token = (request.headers.get("X-Session-Token") || "").trim();
  if (token.length < 20 || token.length > 96 || !/^[A-Za-z0-9_-]+$/.test(token)) {
    return null;
  }
  return token;
}

/**
 * @param {Request} request
 * @param {string} headerName
 * @returns {string | null}
 */
function getValidHex64Header(request, headerName) {
  const h = (request.headers.get(headerName) || "").trim().toLowerCase();
  return HEX64_RE.test(h) ? h : null;
}

/**
 * @param {Request} request
 * @param {string} [fallback="termux"]
 * @returns {string | null}
 */
function getValidChannel(request, fallback = "termux") {
  const raw = (request.headers.get("X-Agent-Channel") || fallback).trim().toLowerCase();
  return CHANNEL_RE.test(raw) ? raw : null;
}

/**
 * @param {any} raw
 * @returns {string}
 */
function sanitizeId(raw) {
  return String(raw || "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 64);
}

/**
 * @param {Request} request
 * @param {number} [maxBytes=MAX_BODY_BYTES]
 * @returns {Promise<{ data?: any, error?: string, status?: number }>}
 */
async function readBoundedJson(request, maxBytes = MAX_BODY_BYTES) {
  const raw = await request.text();
  if (raw.length > maxBytes) {
    return { error: `Payload exceeds ${Math.round(maxBytes / 1024)} KB limit`, status: 413 };
  }
  try {
    return { data: raw ? JSON.parse(raw) : {} };
  } catch (_err) {
    return { error: "Invalid JSON body", status: 400 };
  }
}

/**
 * @param {Request} request
 * @returns {Promise<string | null>}
 */
async function resolveAgentBucket(request) {
  const devHash = getValidHex64Header(request, "X-Device-Hash");
  const agentKey = getValidHex64Header(request, "X-Agent-Key");
  if (!devHash || !agentKey) {
    return null;
  }
  return await sha256Hex(`${devHash}:${agentKey}`);
}

/**
 * @param {any} env
 * @param {string} idxKey
 * @returns {Promise<any[]>}
 */
async function loadSwarmIndex(env, idxKey) {
  try {
    const raw = await env.RELAY_KV.get(idxKey);
    const arr = raw ? JSON.parse(raw) : [];
    return Array.isArray(arr) ? arr : [];
  } catch (_err) {
    return [];
  }
}

export default {
  /**
   * @param {Request} request
   * @param {any} env
   * @returns {Promise<Response>}
   */
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
        version: 4,
        maxTtlSeconds: MAX_TTL_SECONDS,
        telemetryTtlDays: Math.round(TELEMETRY_TTL_SECONDS / 86400),
        supportsWake: true,
        supportsFlexibleCommands: true,
        supportsDynamicExecution: true,
        supportsTelemetry: true,
        supportsOmniAgent: true,
        supportsChunkedBlobs: true,
        supportsSwarmBus: true,
        supportsHotExec: true,
      });
    }

    if (!verifyOptionalAdminGate(request, env)) {
      return jsonResp({ ok: false, error: "Invalid or missing X-Admin-Key." }, 403);
    }

    // ==================== V4 FULL-AGENT MULTI-CHANNEL RELAY (/v1/agent/*) ====================
    if (path.startsWith("/v1/agent/")) {
      const bucket = await resolveAgentBucket(request);
      if (!bucket) {
        return jsonResp({
          ok: false,
          error: "Missing or invalid X-Device-Hash / X-Agent-Key headers (64 hex chars each).",
        }, 401);
      }

      const channel = getValidChannel(request, url.searchParams.get("channel") || "termux");
      if (!channel) {
        return jsonResp({ ok: false, error: "Invalid X-Agent-Channel (expected [a-z0-9_-]{1,32})." }, 400);
      }

      const basePrefix = `ag:${bucket}:`;
      const chanPrefix = `${basePrefix}${channel}:`;
      const presenceKey = `${chanPrefix}presence`;
      const queueKey = `${chanPrefix}queue`;
      const resPrefix = `${chanPrefix}res:`;

      // A1. Agent presence heartbeat
      if (request.method === "POST" && path === "/v1/agent/heartbeat") {
        const parsed = await readBoundedJson(request, 64 * 1024);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const presence = {
          channel,
          online: true,
          lastSeenUtc: new Date().toISOString(),
          agentVersion: String(d.agentVersion || "1.0").slice(0, 32),
          hostEnv: String(d.hostEnv || channel).slice(0, 64),
          device: String(d.device || "").slice(0, 80),
          securityMode: String(d.securityMode || "smart").slice(0, 24),
          repoPath: String(d.repoPath || "").slice(0, 180),
          tools: d.tools && typeof d.tools === "object" ? d.tools : {},
          capabilities: Array.isArray(d.capabilities) ? d.capabilities.slice(0, 64) : [],
        };
        await env.RELAY_KV.put(presenceKey, JSON.stringify(presence), { expirationTtl: AGENT_PRESENCE_TTL_SECONDS });
        return jsonResp({ ok: true, channel, ttlSeconds: AGENT_PRESENCE_TTL_SECONDS });
      }

      // A2. Query status for one channel or all known channels
      if (request.method === "GET" && path === "/v1/agent/status") {
        if (url.searchParams.get("all") === "1") {
          const extra = (url.searchParams.get("channels") || "")
            .split(",")
            .map((c) => c.trim().toLowerCase())
            .filter((c) => CHANNEL_RE.test(c));
          const list = [...new Set([...KNOWN_AGENT_CHANNELS, channel, ...extra])];
          const entries = await Promise.all(list.map((c) => env.RELAY_KV.get(`${basePrefix}${c}:presence`)));
          /** @type {Record<string, any>} */
          const channels = {};
          for (let i = 0; i < list.length; i++) {
            channels[list[i]] = entries[i] ? JSON.parse(entries[i]) : { channel: list[i], online: false };
          }
          return jsonResp({ ok: true, channels });
        }
        const raw = await env.RELAY_KV.get(presenceKey);
        return jsonResp({
          ok: true,
          channel,
          online: Boolean(raw),
          presence: raw ? JSON.parse(raw) : null,
        });
      }

      // A3. AI enqueues an E2E-encrypted command into channel FIFO queue
      if (request.method === "POST" && path === "/v1/agent/cmd") {
        const parsed = await readBoundedJson(request, MAX_BODY_BYTES);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const requestId = sanitizeId(d.requestId || crypto.randomUUID());
        if (!requestId || !d.envelope || typeof d.envelope !== "object") {
          return jsonResp({ ok: false, error: "Missing requestId or encrypted envelope." }, 400);
        }
        let queue = [];
        try {
          const rawQ = await env.RELAY_KV.get(queueKey);
          if (rawQ) {
            const parsedQ = JSON.parse(rawQ);
            if (Array.isArray(parsedQ)) queue = parsedQ;
          }
        } catch (_err) {
          queue = [];
        }
        if (queue.length >= MAX_AGENT_QUEUE) {
          queue = queue.slice(-(MAX_AGENT_QUEUE - 1));
        }
        queue.push({
          requestId,
          channel,
          queuedAtUtc: new Date().toISOString(),
          envelope: d.envelope,
        });
        await env.RELAY_KV.put(queueKey, JSON.stringify(queue), { expirationTtl: AGENT_CMD_TTL_SECONDS });
        return jsonResp({ ok: true, requestId, channel, queueDepth: queue.length });
      }

      // A4. Agent dequeues next pending command from channel FIFO queue
      if (request.method === "GET" && path === "/v1/agent/poll") {
        const rawQ = await env.RELAY_KV.get(queueKey);
        if (!rawQ) {
          return jsonResp({ ok: true, hasCommand: false, channel });
        }
        let queue = [];
        try {
          const parsedQ = JSON.parse(rawQ);
          if (Array.isArray(parsedQ)) queue = parsedQ;
        } catch (_err) {
          queue = [];
        }
        if (queue.length === 0) {
          await env.RELAY_KV.delete(queueKey);
          return jsonResp({ ok: true, hasCommand: false, channel });
        }
        const nextCmd = queue.shift();
        if (queue.length > 0) {
          await env.RELAY_KV.put(queueKey, JSON.stringify(queue), { expirationTtl: AGENT_CMD_TTL_SECONDS });
        } else {
          await env.RELAY_KV.delete(queueKey);
        }
        return jsonResp({ ok: true, hasCommand: true, channel, remaining: queue.length, command: nextCmd });
      }

      // A5. Agent posts E2E-encrypted result for requestId
      if (request.method === "POST" && path === "/v1/agent/result") {
        const parsed = await readBoundedJson(request, MAX_BODY_BYTES);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const requestId = sanitizeId(d.requestId);
        if (!requestId || !d.envelope || typeof d.envelope !== "object") {
          return jsonResp({ ok: false, error: "Missing requestId or encrypted result envelope." }, 400);
        }
        await env.RELAY_KV.put(
          resPrefix + requestId,
          JSON.stringify({
            requestId,
            channel,
            completedAtUtc: new Date().toISOString(),
            envelope: d.envelope,
          }),
          { expirationTtl: AGENT_RESULT_TTL_SECONDS }
        );
        return jsonResp({ ok: true, requestId, channel });
      }

      // A6. AI fetches & deletes E2E-encrypted result for requestId
      if (request.method === "GET" && path === "/v1/agent/result") {
        const requestId = sanitizeId(url.searchParams.get("requestId"));
        if (!requestId) {
          return jsonResp({ ok: false, error: "Missing requestId query parameter." }, 400);
        }
        const raw = await env.RELAY_KV.get(resPrefix + requestId);
        if (!raw) {
          return jsonResp({ ok: true, ready: false, requestId, channel });
        }
        await env.RELAY_KV.delete(resPrefix + requestId);
        return jsonResp({ ok: true, ready: true, result: JSON.parse(raw) });
      }

      return jsonResp({ ok: false, error: "Unknown agent endpoint." }, 404);
    }

    // ==================== V4 CHUNKED LARGE-PAYLOAD STORE (/v1/blob/*) ====================
    if (path.startsWith("/v1/blob/")) {
      const bucket = await resolveAgentBucket(request);
      if (!bucket) {
        return jsonResp({
          ok: false,
          error: "Missing or invalid X-Device-Hash / X-Agent-Key headers (64 hex chars each).",
        }, 401);
      }
      const blobPrefix = `bl:${bucket}:`;

      if (request.method === "POST" && path === "/v1/blob/chunk") {
        const parsed = await readBoundedJson(request, MAX_BODY_BYTES);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const blobId = sanitizeId(d.blobId);
        const idx = Number(d.index);
        if (!blobId || !Number.isInteger(idx) || idx < 0 || idx >= MAX_BLOB_CHUNKS || typeof d.data !== "string") {
          return jsonResp({ ok: false, error: "Invalid blobId, chunk index (0..63), or data string." }, 400);
        }
        await env.RELAY_KV.put(`${blobPrefix}${blobId}:c:${idx}`, d.data, { expirationTtl: BLOB_TTL_SECONDS });
        return jsonResp({ ok: true, blobId, index: idx, bytes: d.data.length });
      }

      if (request.method === "GET" && path === "/v1/blob/chunk") {
        const blobId = sanitizeId(url.searchParams.get("blobId"));
        const idx = Number(url.searchParams.get("index"));
        if (!blobId || !Number.isInteger(idx) || idx < 0 || idx >= MAX_BLOB_CHUNKS) {
          return jsonResp({ ok: false, error: "Invalid blobId or index." }, 400);
        }
        const data = await env.RELAY_KV.get(`${blobPrefix}${blobId}:c:${idx}`);
        if (data === null) {
          return jsonResp({ ok: false, error: "Blob chunk not found or expired." }, 404);
        }
        return jsonResp({ ok: true, blobId, index: idx, data });
      }

      if (request.method === "POST" && path === "/v1/blob/commit") {
        const parsed = await readBoundedJson(request, 64 * 1024);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const blobId = sanitizeId(d.blobId);
        const totalChunks = Number(d.totalChunks);
        if (!blobId || !Number.isInteger(totalChunks) || totalChunks < 1 || totalChunks > MAX_BLOB_CHUNKS) {
          return jsonResp({ ok: false, error: "Invalid blobId or totalChunks (1..64)." }, 400);
        }
        const meta = {
          blobId,
          totalChunks,
          sha256: String(d.sha256 || "").slice(0, 64),
          bytes: Number(d.bytes) || 0,
          meta: d.meta && typeof d.meta === "object" ? d.meta : {},
          committedAtUtc: new Date().toISOString(),
        };
        await env.RELAY_KV.put(`${blobPrefix}${blobId}:meta`, JSON.stringify(meta), { expirationTtl: BLOB_TTL_SECONDS });
        return jsonResp({ ok: true, ...meta });
      }

      if (request.method === "GET" && path === "/v1/blob/meta") {
        const blobId = sanitizeId(url.searchParams.get("blobId"));
        if (!blobId) return jsonResp({ ok: false, error: "Missing blobId." }, 400);
        const raw = await env.RELAY_KV.get(`${blobPrefix}${blobId}:meta`);
        if (!raw) return jsonResp({ ok: false, error: "Blob metadata not found or expired." }, 404);
        return jsonResp({ ok: true, meta: JSON.parse(raw) });
      }

      if (request.method === "DELETE" && path === "/v1/blob/delete") {
        const blobId = sanitizeId(url.searchParams.get("blobId"));
        if (!blobId) return jsonResp({ ok: false, error: "Missing blobId." }, 400);
        const rawMeta = await env.RELAY_KV.get(`${blobPrefix}${blobId}:meta`);
        let count = 0;
        if (rawMeta) {
          try {
            count = Math.min(MAX_BLOB_CHUNKS, Math.max(0, Number(JSON.parse(rawMeta).totalChunks) || 0));
          } catch (_err) {
            count = 0;
          }
        }
        const deletes = [env.RELAY_KV.delete(`${blobPrefix}${blobId}:meta`)];
        for (let i = 0; i < count; i++) {
          deletes.push(env.RELAY_KV.delete(`${blobPrefix}${blobId}:c:${i}`));
        }
        await Promise.all(deletes);
        return jsonResp({ ok: true, blobId, deletedChunks: count });
      }

      return jsonResp({ ok: false, error: "Unknown blob endpoint." }, 404);
    }

    // ==================== V4 MULTI-AGENT SWARM TASK BUS (/v1/swarm/*) ====================
    if (path.startsWith("/v1/swarm/")) {
      const bucket = await resolveAgentBucket(request);
      if (!bucket) {
        return jsonResp({
          ok: false,
          error: "Missing or invalid X-Device-Hash / X-Agent-Key headers (64 hex chars each).",
        }, 401);
      }
      const swarmPrefix = `sw:${bucket}:`;
      const idxKey = `${swarmPrefix}index`;

      if (request.method === "POST" && path === "/v1/swarm/enqueue") {
        const parsed = await readBoundedJson(request, 256 * 1024);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const taskId = sanitizeId(d.taskId || `t_${Date.now().toString(36)}_${crypto.randomUUID().slice(0, 6)}`);
        const nowUtc = new Date().toISOString();
        const header = {
          taskId,
          role: String(d.role || "worker").slice(0, 32),
          title: String(d.title || "Swarm task").slice(0, 140),
          createdBy: String(d.createdBy || "architect").slice(0, 48),
          status: "pending",
          claimedBy: "",
          createdAtUtc: nowUtc,
          updatedAtUtc: nowUtc,
        };
        const record = {
          ...header,
          envelope: d.envelope && typeof d.envelope === "object" ? d.envelope : null,
          instructions: typeof d.instructions === "string" ? d.instructions.slice(0, 32000) : "",
          result: null,
        };
        let index = await loadSwarmIndex(env, idxKey);
        index = index.filter((item) => item.taskId !== taskId);
        index.unshift(header);
        if (index.length > MAX_SWARM_TASKS) {
          index = index.slice(0, MAX_SWARM_TASKS);
        }
        await Promise.all([
          env.RELAY_KV.put(`${swarmPrefix}task:${taskId}`, JSON.stringify(record), { expirationTtl: SWARM_TTL_SECONDS }),
          env.RELAY_KV.put(idxKey, JSON.stringify(index), { expirationTtl: SWARM_TTL_SECONDS }),
        ]);
        return jsonResp({ ok: true, task: header });
      }

      if (request.method === "GET" && path === "/v1/swarm/list") {
        const statusFilter = (url.searchParams.get("status") || "").trim().toLowerCase();
        let index = await loadSwarmIndex(env, idxKey);
        if (statusFilter) {
          index = index.filter((t) => String(t.status || "").toLowerCase() === statusFilter);
        }
        return jsonResp({ ok: true, tasks: index });
      }

      if (request.method === "POST" && path === "/v1/swarm/claim") {
        const parsed = await readBoundedJson(request, 32 * 1024);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const workerId = String(d.workerId || "worker").slice(0, 48);
        const roleFilter = String(d.role || "").trim().toLowerCase();
        const specificId = sanitizeId(d.taskId || "");
        const index = await loadSwarmIndex(env, idxKey);
        let targetIdx = -1;
        for (let i = index.length - 1; i >= 0; i--) {
          const item = index[i];
          if (specificId && item.taskId !== specificId) continue;
          if (!specificId && item.status !== "pending") continue;
          if (roleFilter && String(item.role || "").toLowerCase() !== roleFilter) continue;
          targetIdx = i;
          break;
        }
        if (targetIdx === -1) {
          return jsonResp({ ok: true, claimed: false, task: null });
        }
        const taskId = index[targetIdx].taskId;
        const rawRec = await env.RELAY_KV.get(`${swarmPrefix}task:${taskId}`);
        if (!rawRec) {
          return jsonResp({ ok: true, claimed: false, task: null });
        }
        const rec = JSON.parse(rawRec);
        const nowUtc = new Date().toISOString();
        rec.status = "claimed";
        rec.claimedBy = workerId;
        rec.updatedAtUtc = nowUtc;
        index[targetIdx].status = "claimed";
        index[targetIdx].claimedBy = workerId;
        index[targetIdx].updatedAtUtc = nowUtc;
        await Promise.all([
          env.RELAY_KV.put(`${swarmPrefix}task:${taskId}`, JSON.stringify(rec), { expirationTtl: SWARM_TTL_SECONDS }),
          env.RELAY_KV.put(idxKey, JSON.stringify(index), { expirationTtl: SWARM_TTL_SECONDS }),
        ]);
        return jsonResp({ ok: true, claimed: true, task: rec });
      }

      if (request.method === "POST" && path === "/v1/swarm/complete") {
        const parsed = await readBoundedJson(request, 512 * 1024);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const taskId = sanitizeId(d.taskId);
        if (!taskId) return jsonResp({ ok: false, error: "Missing taskId." }, 400);
        const rawRec = await env.RELAY_KV.get(`${swarmPrefix}task:${taskId}`);
        if (!rawRec) return jsonResp({ ok: false, error: "Task not found or expired." }, 404);
        const rec = JSON.parse(rawRec);
        const finalStatus = d.ok === false ? "failed" : "done";
        const nowUtc = new Date().toISOString();
        rec.status = finalStatus;
        rec.updatedAtUtc = nowUtc;
        rec.result = {
          ok: d.ok !== false,
          workerId: String(d.workerId || rec.claimedBy || "worker").slice(0, 48),
          summary: String(d.summary || "").slice(0, 4000),
          envelope: d.envelope && typeof d.envelope === "object" ? d.envelope : null,
          completedAtUtc: nowUtc,
        };
        const index = await loadSwarmIndex(env, idxKey);
        for (const item of index) {
          if (item.taskId === taskId) {
            item.status = finalStatus;
            item.updatedAtUtc = nowUtc;
            break;
          }
        }
        await Promise.all([
          env.RELAY_KV.put(`${swarmPrefix}task:${taskId}`, JSON.stringify(rec), { expirationTtl: SWARM_TTL_SECONDS }),
          env.RELAY_KV.put(idxKey, JSON.stringify(index), { expirationTtl: SWARM_TTL_SECONDS }),
        ]);
        return jsonResp({ ok: true, taskId, status: finalStatus });
      }

      if (request.method === "GET" && path === "/v1/swarm/task") {
        const taskId = sanitizeId(url.searchParams.get("taskId"));
        if (!taskId) return jsonResp({ ok: false, error: "Missing taskId." }, 400);
        const rawRec = await env.RELAY_KV.get(`${swarmPrefix}task:${taskId}`);
        if (!rawRec) return jsonResp({ ok: false, error: "Task not found." }, 404);
        return jsonResp({ ok: true, task: JSON.parse(rawRec) });
      }

      if (request.method === "DELETE" && path === "/v1/swarm/clear") {
        const onlyDone = url.searchParams.get("doneOnly") === "1";
        const index = await loadSwarmIndex(env, idxKey);
        const kept = [];
        const deletes = [];
        for (const item of index) {
          if (onlyDone && item.status !== "done" && item.status !== "failed") {
            kept.push(item);
          } else {
            deletes.push(env.RELAY_KV.delete(`${swarmPrefix}task:${item.taskId}`));
          }
        }
        deletes.push(env.RELAY_KV.put(idxKey, JSON.stringify(kept), { expirationTtl: SWARM_TTL_SECONDS }));
        await Promise.all(deletes);
        return jsonResp({ ok: true, cleared: index.length - kept.length, remaining: kept.length });
      }

      return jsonResp({ ok: false, error: "Unknown swarm endpoint." }, 404);
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

      if (request.method === "POST" && path === "/v1/telemetry/upload") {
        const parsed = await readBoundedJson(request, MAX_BODY_BYTES);
        if (parsed.error) return jsonResp({ ok: false, error: parsed.error }, parsed.status);
        const d = parsed.data || {};
        const nowUtc = new Date().toISOString();
        const meta = d.meta && typeof d.meta === "object" ? d.meta : {};
        /** @type {Record<string, string>} */
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

        let history = [];
        try {
          const rawHist = await env.RELAY_KV.get(`${prefix}history`);
          if (rawHist) {
            const parsedHist = JSON.parse(rawHist);
            if (Array.isArray(parsedHist)) history = parsedHist;
          }
        } catch (_err) {
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

      if (request.method === "GET" && path === "/v1/telemetry/history") {
        const rawHist = await env.RELAY_KV.get(`${prefix}history`);
        return jsonResp({ ok: true, history: rawHist ? JSON.parse(rawHist) : [] });
      }

      if (request.method === "GET" && path === "/v1/telemetry/ping") {
        const rawMeta = await env.RELAY_KV.get(`${prefix}latest:meta`);
        return jsonResp({
          ok: true,
          service: "cavernize-unified-hub",
          version: 4,
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
          requestedTier: Math.max(0, Math.min(2, Number(d.requestedTier != null ? d.requestedTier : 1))),
          durationSeconds: Math.max(300, Math.min(MAX_TTL_SECONDS, Number(d.durationSeconds) || 1800)),
          reason: String(d.reason || "AI session request").slice(0, 160),
          requester: String(d.requester || "AI Assistant").slice(0, 64),
          channel: String(d.channel || "cavernize").slice(0, 32),
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
        maxTier: Math.max(0, Math.min(2, Number(d.maxTier != null ? d.maxTier : 1))),
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
      const requestId = sanitizeId(parsed.data ? parsed.data.requestId : "");
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
