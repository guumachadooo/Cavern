import { spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  loadWorkspaceSnapshot,
  readWhitelistedDoc,
  renderDashboardHtml,
} from './dashboard.mjs';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const ROOT_DIR = path.resolve(__dirname, '../..');
const PORT = Number(process.env.PORT || 3000);
const HOST = '0.0.0.0';

// Bridge whitelisted AI Studio Secrets from the dev-server process to local CLI tools
// via a strictly private 0600 file in /tmp (outside the repo, NEVER exposed over HTTP).
const RUNTIME_ENV_BRIDGE_PATH = '/tmp/.cavern_runtime_env.json';
const LIVE_SYNC_STATUS_PATH = '/tmp/cavern-flight-recorder/watcher_status.json';
const LIVE_SYNC_SCRIPT_PATH = path.join(ROOT_DIR, '.agents', 'agent', 'live_sync_watcher.py');
const FEDERATION_STATUS_PATH = '/tmp/cavern-federation/federation_status.json';
const FEDERATION_SCRIPT_PATH = path.join(ROOT_DIR, '.agents', 'agent', 'repo_federation.py');
const WHITELISTED_RUNTIME_KEYS = [
  'GITHUB_TOKEN',
  'GH_TOKEN',
  'CLOUDFLARE_API_TOKEN',
  'CLOUDFLARE_ACCOUNT_ID',
  'CLOUDFLARE_WORKER_NAME',
  'CAVERN_BRIDGE_PAIR_SECRET',
  'CAVERN_RELAY_URL',
  'CAVERN_ADMIN_KEY',
  'CAVERN_KNOCK_ID',
];

function syncPrivateRuntimeEnvBridge() {
  try {
    const payload = {};
    for (const key of WHITELISTED_RUNTIME_KEYS) {
      const val = String(process.env[key] || '').trim();
      if (val) payload[key] = val;
    }
    if (Object.keys(payload).length > 0) {
      fs.writeFileSync(RUNTIME_ENV_BRIDGE_PATH, JSON.stringify(payload), { mode: 0o600 });
      try {
        fs.chmodSync(RUNTIME_ENV_BRIDGE_PATH, 0o600);
      } catch {
        // Best effort on non-POSIX filesystems
      }
    }
  } catch {
    // Never block preview server boot if /tmp is read-only
  }
}

syncPrivateRuntimeEnvBridge();

// Zero-quota background live Antigravity mirror + Flight Recorder watcher
let liveWatcherProc = null;
let shuttingDown = false;
let watcherRestartAttempts = 0;

function startLiveSyncWatcher() {
  if (shuttingDown || process.env.CAVERN_DISABLE_LIVE_SYNC === '1') return;
  if (!fs.existsSync(LIVE_SYNC_SCRIPT_PATH)) return;
  try {
    const child = spawn(
      'python3',
      ['-B', LIVE_SYNC_SCRIPT_PATH, '--daemon', '--workspace', ROOT_DIR],
      {
        cwd: ROOT_DIR,
        stdio: 'ignore',
        env: { ...process.env },
      }
    );
    liveWatcherProc = child;
    child.on('exit', () => {
      if (liveWatcherProc === child) liveWatcherProc = null;
      if (!shuttingDown && watcherRestartAttempts < 5) {
        watcherRestartAttempts += 1;
        setTimeout(startLiveSyncWatcher, Math.min(30000, 3000 * watcherRestartAttempts));
      }
    });
    child.on('error', () => {
      if (liveWatcherProc === child) liveWatcherProc = null;
    });
  } catch {
    // Never block preview server if python3 is unavailable
  }
}

function readLiveSyncStatus() {
  try {
    if (fs.existsSync(LIVE_SYNC_STATUS_PATH)) {
      const parsed = JSON.parse(fs.readFileSync(LIVE_SYNC_STATUS_PATH, 'utf8'));
      return { ...parsed, spawnedByServer: Boolean(liveWatcherProc && !liveWatcherProc.killed) };
    }
  } catch {
    // Ignore transient reads
  }
  return {
    ok: true,
    running: Boolean(liveWatcherProc && !liveWatcherProc.killed),
    spawnedByServer: Boolean(liveWatcherProc && !liveWatcherProc.killed),
  };
}

function warmFederatedRepos() {
  if (shuttingDown || process.env.CAVERN_DISABLE_FEDERATION_WARM === '1') return;
  if (!fs.existsSync(FEDERATION_SCRIPT_PATH)) return;
  try {
    const child = spawn(
      'python3',
      ['-B', FEDERATION_SCRIPT_PATH, '--warm', '--workspace', ROOT_DIR],
      {
        cwd: ROOT_DIR,
        stdio: 'ignore',
        env: { ...process.env },
      }
    );
    child.unref();
  } catch {
    // Best-effort zero-quota warm hydrate; never block preview boot
  }
}

function readFederationStatus() {
  try {
    if (fs.existsSync(FEDERATION_STATUS_PATH)) {
      return JSON.parse(fs.readFileSync(FEDERATION_STATUS_PATH, 'utf8'));
    }
  } catch {
    // Ignore transient reads
  }
  return { ok: true, status: 'warming', mountRoot: '/tmp/cavern-federation' };
}

startLiveSyncWatcher();
warmFederatedRepos();

function sendJson(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
  });
  res.end(body);
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);

  if (url.pathname === '/health') {
    return sendJson(res, 200, {
      ok: true,
      service: 'cavernize-workspace-preview',
      port: PORT,
      liveSyncWatcher: Boolean(liveWatcherProc && !liveWatcherProc.killed),
    });
  }

  if (url.pathname === '/api/live-sync') {
    return sendJson(res, 200, readLiveSyncStatus());
  }

  if (url.pathname === '/api/federation') {
    return sendJson(res, 200, readFederationStatus());
  }

  if (url.pathname === '/favicon.ico') {
    res.writeHead(204);
    res.end();
    return;
  }

  if (url.pathname === '/api/context') {
    return sendJson(res, 200, loadWorkspaceSnapshot(ROOT_DIR));
  }

  if (url.pathname === '/api/doc') {
    const id = url.searchParams.get('id') || 'handoff';
    const doc = readWhitelistedDoc(ROOT_DIR, id);
    if (!doc) {
      return sendJson(res, 404, { error: 'Unknown document id' });
    }
    return sendJson(res, 200, doc);
  }

  if (url.pathname === '/' || url.pathname === '/index.html') {
    const snapshot = loadWorkspaceSnapshot(ROOT_DIR);
    const initialDoc = readWhitelistedDoc(ROOT_DIR, 'handoff') || {
      id: 'handoff',
      label: 'Handoff',
      path: '.agents/docs/HANDOFF.md',
      lines: 0,
      content: '',
    };
    const html = renderDashboardHtml(snapshot, initialDoc);
    res.writeHead(200, {
      'Content-Type': 'text/html; charset=utf-8',
      'Cache-Control': 'no-store',
    });
    res.end(html);
    return;
  }

  res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
  res.end('Not found');
});

server.listen(PORT, HOST, () => {
  console.log(`Cavernize Android workspace preview listening at http://${HOST}:${PORT}`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    shuttingDown = true;
    if (liveWatcherProc && !liveWatcherProc.killed) {
      try {
        liveWatcherProc.kill('SIGTERM');
      } catch {
        // Best effort
      }
    }
    server.close(() => process.exit(0));
  });
}
