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
    return sendJson(res, 200, { ok: true, service: 'cavernize-workspace-preview', port: PORT });
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
    server.close(() => process.exit(0));
  });
}
