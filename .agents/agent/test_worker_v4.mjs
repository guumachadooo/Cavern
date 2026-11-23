import test from "node:test";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import worker from "../docs/cloudflare_live_relay_worker.js";

function makeMockKv() {
  const store = new Map();
  return {
    async get(key) {
      return store.has(key) ? store.get(key) : null;
    },
    async put(key, val) {
      store.set(key, String(val));
    },
    async delete(key) {
      store.delete(key);
    },
  };
}

function sha256(text) {
  return crypto.createHash("sha256").update(text).digest("hex");
}

const PAIR_SECRET = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
const DEV_HASH = sha256(`wake-channel-v2:${PAIR_SECRET}`);
const KNOCK_ID = sha256(`knock-channel-v5:${DEV_HASH}`);
const AGENT_KEY = sha256(`agent-access-v4:${PAIR_SECRET}`);
const TEL_KEY = sha256(`telemetry-access-v3:${PAIR_SECRET}`);
const SAMPLE_PUB_B64 = Buffer.concat([Buffer.from([0x04]), Buffer.alloc(64, 0x42)]).toString("base64");

async function req(env, method, path, headers = {}, body = null) {
  const init = { method, headers: { ...headers } };
  if (body !== null) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  const r = await worker.fetch(new Request(`https://relay.example.workers.dev${path}`, init), env);
  return { status: r.status, json: await r.json() };
}

test("Worker v5 health reports all v3, v4 & v5 Smart Knock capabilities", async () => {
  const env = { RELAY_KV: makeMockKv() };
  const r = await req(env, "GET", "/health");
  assert.equal(r.status, 200);
  assert.equal(r.json.version, 5);
  assert.equal(r.json.supportsSmartKnock, true);
  assert.equal(r.json.supportsOmniAgent, true);
  assert.equal(r.json.supportsChunkedBlobs, true);
  assert.equal(r.json.supportsSwarmBus, true);
  assert.equal(r.json.supportsHotExec, true);
  assert.equal(r.json.supportsTelemetry, true);
});

test("Worker v4 /v1/agent/* multi-channel heartbeat, FIFO queue, and result flow", async () => {
  const env = { RELAY_KV: makeMockKv() };
  const hTermux = { "X-Device-Hash": DEV_HASH, "X-Agent-Key": AGENT_KEY, "X-Agent-Channel": "termux" };
  const hAndroid = { "X-Device-Hash": DEV_HASH, "X-Agent-Key": AGENT_KEY, "X-Agent-Channel": "android" };

  assert.equal((await req(env, "POST", "/v1/agent/heartbeat", hTermux, { hostEnv: "debian-proot", securityMode: "smart" })).status, 200);
  assert.equal((await req(env, "POST", "/v1/agent/heartbeat", hAndroid, { hostEnv: "android-companion", securityMode: "smart" })).status, 200);

  const stAll = await req(env, "GET", "/v1/agent/status?all=1", hTermux);
  assert.equal(stAll.json.channels.termux.online, true);
  assert.equal(stAll.json.channels.android.online, true);

  // Queue two commands on termux and verify strict FIFO isolation from android
  assert.equal((await req(env, "POST", "/v1/agent/cmd", hTermux, { requestId: "r1", envelope: { v: 4, ct: "a" } })).status, 200);
  assert.equal((await req(env, "POST", "/v1/agent/cmd", hTermux, { requestId: "r2", envelope: { v: 4, ct: "b" } })).status, 200);

  const pAndroid = await req(env, "GET", "/v1/agent/poll", hAndroid);
  assert.equal(pAndroid.json.hasCommand, false);

  const p1 = await req(env, "GET", "/v1/agent/poll", hTermux);
  assert.equal(p1.json.hasCommand, true);
  assert.equal(p1.json.command.requestId, "r1");
  assert.equal(p1.json.remaining, 1);

  const p2 = await req(env, "GET", "/v1/agent/poll", hTermux);
  assert.equal(p2.json.command.requestId, "r2");

  assert.equal((await req(env, "POST", "/v1/agent/result", hTermux, { requestId: "r1", envelope: { v: 4, ct: "ok" } })).status, 200);
  const res1 = await req(env, "GET", "/v1/agent/result?requestId=r1", hTermux);
  assert.equal(res1.json.ready, true);
  assert.equal(res1.json.result.envelope.ct, "ok");
});

test("Worker v4 /v1/blob/* chunked upload, commit, download, and delete", async () => {
  const env = { RELAY_KV: makeMockKv() };
  const h = { "X-Device-Hash": DEV_HASH, "X-Agent-Key": AGENT_KEY };

  assert.equal((await req(env, "POST", "/v1/blob/chunk", h, { blobId: "b1", index: 0, data: "PART0_" })).status, 200);
  assert.equal((await req(env, "POST", "/v1/blob/chunk", h, { blobId: "b1", index: 1, data: "PART1" })).status, 200);
  assert.equal((await req(env, "POST", "/v1/blob/commit", h, { blobId: "b1", totalChunks: 2, sha256: "abc", bytes: 11 })).status, 200);

  const meta = await req(env, "GET", "/v1/blob/meta?blobId=b1", h);
  assert.equal(meta.json.meta.totalChunks, 2);
  const c0 = await req(env, "GET", "/v1/blob/chunk?blobId=b1&index=0", h);
  const c1 = await req(env, "GET", "/v1/blob/chunk?blobId=b1&index=1", h);
  assert.equal(c0.json.data + c1.json.data, "PART0_PART1");

  const del = await req(env, "DELETE", "/v1/blob/delete?blobId=b1", h);
  assert.equal(del.json.deletedChunks, 2);
  assert.equal((await req(env, "GET", "/v1/blob/meta?blobId=b1", h)).status, 404);
});

test("Worker v4 /v1/swarm/* enqueue, claim, complete, and clear lifecycle", async () => {
  const env = { RELAY_KV: makeMockKv() };
  const h = { "X-Device-Hash": DEV_HASH, "X-Agent-Key": AGENT_KEY };

  const enq = await req(env, "POST", "/v1/swarm/enqueue", h, {
    taskId: "t_audit",
    role: "debian-worker",
    title: "Run acoustic check",
    instructions: "Run check_android_cs.py",
  });
  assert.equal(enq.status, 200);
  assert.equal(enq.json.task.status, "pending");

  const clm = await req(env, "POST", "/v1/swarm/claim", h, { workerId: "antigravity-debian", role: "debian-worker" });
  assert.equal(clm.json.claimed, true);
  assert.equal(clm.json.task.taskId, "t_audit");

  const cmp = await req(env, "POST", "/v1/swarm/complete", h, {
    taskId: "t_audit",
    workerId: "antigravity-debian",
    ok: true,
    summary: "All 7 layers PASS (0 errors, 0 warnings)",
  });
  assert.equal(cmp.json.status, "done");

  const full = await req(env, "GET", "/v1/swarm/task?taskId=t_audit", h);
  assert.equal(full.json.task.result.summary, "All 7 layers PASS (0 errors, 0 warnings)");

  const clr = await req(env, "DELETE", "/v1/swarm/clear?doneOnly=1", h);
  assert.equal(clr.json.cleared, 1);
});

test("Worker v4 preserves 100% backwards-compatibility with v3 telemetry & session endpoints", async () => {
  const env = { RELAY_KV: makeMockKv() };
  const hTel = { "X-Device-Hash": DEV_HASH, "X-Telemetry-Key": TEL_KEY };
  assert.equal((await req(env, "POST", "/v1/telemetry/upload", hTel, { kind: "summary", content: "{\"ok\":true}" })).status, 200);
  const tel = await req(env, "GET", "/v1/telemetry/latest?kind=summary", hTel);
  assert.equal(tel.json.content, "{\"ok\":true}");

  const hSess = { "X-Session-Token": "CAV-AAAA-BBBB-CCCC-DDDD-EEEE-FFFF" };
  assert.equal((await req(env, "POST", "/v1/session/open", hSess, { ttlSeconds: 600, maxTier: 2 })).status, 200);
  assert.equal((await req(env, "POST", "/v1/session/cmd", hSess, { cmd: "hot_probe_status", args: {} })).status, 200);
  const poll = await req(env, "GET", "/v1/session/poll", hSess);
  assert.equal(poll.json.command.cmd, "hot_probe_status");
});

test("Worker v5 /v1/knock/* zero-secret ECDH lifecycle, owner X-Device-Hash auth, 409 lock & deny cooldown", async () => {
  const env = { RELAY_KV: makeMockKv() };
  const hPublic = { "X-Knock-Id": KNOCK_ID };
  const hOwner = { "X-Device-Hash": DEV_HASH };

  // 1. Public AI knocks with double-hashed knockId & ephemeral P-256 public key
  const k1 = await req(env, "POST", "/v1/knock/request", hPublic, {
    knockReqId: "k_req_1",
    clientPubKey: SAMPLE_PUB_B64,
    requestedScope: "workspace",
    requestedTier: 2,
    reason: "Connect to S25U",
  });
  assert.equal(k1.status, 200);
  assert.equal(k1.json.knockReqId, "k_req_1");

  // 2. Single-slot lock rejects concurrent knock with 409
  const dup = await req(env, "POST", "/v1/knock/request", hPublic, {
    knockReqId: "k_req_dup",
    clientPubKey: SAMPLE_PUB_B64,
  });
  assert.equal(dup.status, 409);

  // 3. Public caller CANNOT poll or approve (missing X-Device-Hash -> 401)
  assert.equal((await req(env, "GET", "/v1/knock/poll", hPublic)).status, 401);
  assert.equal((await req(env, "POST", "/v1/knock/approve", hPublic, { knockReqId: "k_req_1", serverPubKey: SAMPLE_PUB_B64, encryptedGrant: { v: 4 } })).status, 401);

  // 4. Owner phone polls with X-Device-Hash (Worker derives knockId server-side)
  const p = await req(env, "GET", "/v1/knock/poll", hOwner);
  assert.equal(p.status, 200);
  assert.equal(p.json.hasKnock, true);
  assert.equal(p.json.knock.knockReqId, "k_req_1");

  // 5. Owner approves with E2E encrypted grant
  const app = await req(env, "POST", "/v1/knock/approve", hOwner, {
    knockReqId: "k_req_1",
    serverPubKey: SAMPLE_PUB_B64,
    encryptedGrant: { v: 4, ct: "enc_payload" },
    scope: "workspace",
  });
  assert.equal(app.status, 200);

  // 6. AI receives approved grant once (then auto-deleted from KV -> 404)
  const st1 = await req(env, "GET", "/v1/knock/status?knockReqId=k_req_1", hPublic);
  assert.equal(st1.status, 200);
  assert.equal(st1.json.status, "approved");
  assert.equal(st1.json.encryptedGrant.ct, "enc_payload");
  assert.equal((await req(env, "GET", "/v1/knock/status?knockReqId=k_req_1", hPublic)).status, 404);

  // 7. Deny activates automatic lockout cooldown (429 on subsequent knock)
  const k2 = await req(env, "POST", "/v1/knock/request", hPublic, {
    knockReqId: "k_req_2",
    clientPubKey: SAMPLE_PUB_B64,
  });
  assert.equal(k2.status, 200);
  await req(env, "GET", "/v1/knock/poll", hOwner);
  const den = await req(env, "POST", "/v1/knock/deny", hOwner, { knockReqId: "k_req_2", reason: "Not me", cooldownSeconds: 900 });
  assert.equal(den.status, 200);
  const blocked = await req(env, "POST", "/v1/knock/request", hPublic, {
    knockReqId: "k_req_3",
    clientPubKey: SAMPLE_PUB_B64,
  });
  assert.equal(blocked.status, 429);
});

test("Worker v5 /v1/knock/request enforces 3-per-15-min rate limit", async () => {
  const env = { RELAY_KV: makeMockKv() };
  const hPublic = { "X-Knock-Id": KNOCK_ID };
  const hOwner = { "X-Device-Hash": DEV_HASH };

  for (let i = 1; i <= 3; i++) {
    const r = await req(env, "POST", "/v1/knock/request", hPublic, {
      knockReqId: `rate_${i}`,
      clientPubKey: SAMPLE_PUB_B64,
    });
    assert.equal(r.status, 200);
    await req(env, "GET", "/v1/knock/poll", hOwner);
  }
  const r4 = await req(env, "POST", "/v1/knock/request", hPublic, {
    knockReqId: "rate_4",
    clientPubKey: SAMPLE_PUB_B64,
  });
  assert.equal(r4.status, 429);
});

