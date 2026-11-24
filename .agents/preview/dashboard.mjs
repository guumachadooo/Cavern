import fs from 'node:fs';
import path from 'node:path';

export const ALLOWED_DOCS = {
  handoff: { label: 'Handoff', relPath: '.agents/docs/HANDOFF.md' },
  master: { label: 'Master Plan W46-W51', relPath: '.agents/docs/MASTER_AUDIT_PLAN_W46_W51.md' },
  agents: { label: 'AGENTS.md (Auto)', relPath: 'AGENTS.md' },
  onboarding: { label: 'Onboarding', relPath: '.agents/docs/EXTERNAL_AI_ONBOARDING.md' },
  sync: { label: 'Sync Ledger', relPath: '.agents/docs/EXTERNAL_AI_SYNC.md' },
  lessons: { label: 'Build Lessons', relPath: '.agents/docs/BUILD_LESSONS.md' },
  extra: { label: 'Extra Requests', relPath: '.agents/docs/EXTRA_REQUESTS.md' },
  hostMatrix: { label: 'Host Matrix', relPath: '.agents/host_matrix.json' },
};

export function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

export function parseCsprojMeta(xml) {
  const pick = (tag, fallback = '—') => {
    const m = String(xml || '').match(new RegExp(`<${tag}>([^<]+)</${tag}>`));
    return m ? m[1].trim() : fallback;
  };
  return {
    title: pick('ApplicationTitle', 'Cavernize'),
    applicationId: pick('ApplicationId', 'hu.sbence.cavern.cavernize'),
    displayVersion: pick('ApplicationDisplayVersion', '0.0.0'),
    versionCode: pick('ApplicationVersion', '0'),
    targetFramework: pick('TargetFramework', 'net11.0-android'),
    minSdk: pick('SupportedOSPlatformVersion', '26.0'),
  };
}

export function parseHandoff(md) {
  const lines = String(md || '').split(/\r?\n/);
  const fields = { wave: '—', summary: '', next: '', roadmap: '', lastHost: '' };
  for (const line of lines) {
    if (line.startsWith('ts=') || line.includes('|host=')) {
      const m = line.match(/(?:^|\|)host=([^|\s]+)/);
      if (m) fields.lastHost = m[1].trim();
    } else if (line.startsWith('WAVE|')) fields.wave = line.slice(5).trim();
    else if (line.startsWith('SUMMARY|') || line.startsWith('summary|')) fields.summary = line.slice(8).trim();
    else if (line.startsWith('NEXT|') || line.startsWith('next|')) fields.next = line.slice(5).trim();
    else if (line.startsWith('ROADMAP|')) fields.roadmap = line.slice(8).trim();
  }
  return fields;
}

export function parseSyncEntries(md, maxItems = 5) {
  const lines = String(md || '').split(/\r?\n/);
  const entries = [];
  let section = 'Cavern';
  for (const raw of lines) {
    const line = raw.trim();
    if (line.startsWith('## Plugin')) {
      section = 'Plugin';
      continue;
    }
    if (line.startsWith('## Cavern')) {
      section = 'Cavern';
      continue;
    }
    if (line.startsWith('## Historical')) {
      section = 'History';
      continue;
    }
    if (line.startsWith('- ') && line.includes(' | ')) {
      const parts = line.slice(2).split(' | ').map((p) => p.trim());
      if (parts.length >= 5) {
        entries.push({
          section,
          date: parts[0],
          commit: parts[1],
          scope: parts[2],
          files: parts[3],
          summary: parts[4],
        });
      }
    }
  }
  return entries.slice(0, maxItems);
}

export function parseExtraRequests(md) {
  const text = String(md || '');
  const pendingMatch = text.match(/^## Pending Extra Requests\s*$([\s\S]*?)(?=^##\s|\Z)/m);
  const completedParts = text.split(/^## Completed Requests\s*$/m);
  const pendingBlock = pendingMatch ? pendingMatch[1] : (completedParts[0] || '');
  const completedBlock = completedParts.length > 1 ? completedParts[1] : '';
  const pendingLines = pendingBlock
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.startsWith('- REQ|'));
  const completedLines = completedBlock
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.startsWith('- REQ|'));
  return {
    pendingCount: pendingLines.length,
    completedCount: completedLines.length,
    pendingItems: pendingLines.map((l) => l.slice(2)),
  };
}

export function countBuildLessons(md) {
  const matches = String(md || '').match(/^###\s+`?BL-\d+`?/gm);
  return matches ? matches.length : 0;
}

function readTextSafe(filePath) {
  try {
    return fs.readFileSync(filePath, 'utf8');
  } catch {
    return '';
  }
}

export function readWhitelistedDoc(rootDir, docKey) {
  const spec = ALLOWED_DOCS[docKey];
  if (!spec) {
    return null;
  }
  const abs = path.resolve(rootDir, spec.relPath);
  const rootResolved = path.resolve(rootDir);
  if (!abs.startsWith(rootResolved + path.sep)) {
    return null;
  }
  const content = readTextSafe(abs);
  return {
    id: docKey,
    label: spec.label,
    path: spec.relPath,
    lines: content ? content.split(/\r?\n/).length : 0,
    content: content || '(File not found)',
  };
}

export function loadWorkspaceSnapshot(rootDir) {
  const csproj = readTextSafe(path.join(rootDir, 'CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj'));
  const handoffMd = readTextSafe(path.join(rootDir, '.agents/docs/HANDOFF.md'));
  const syncMd = readTextSafe(path.join(rootDir, '.agents/docs/EXTERNAL_AI_SYNC.md'));
  const extraMd = readTextSafe(path.join(rootDir, '.agents/docs/EXTRA_REQUESTS.md'));
  const lessonsMd = readTextSafe(path.join(rootDir, '.agents/docs/BUILD_LESSONS.md'));
  const agentsMd = readTextSafe(path.join(rootDir, 'AGENTS.md'));

  const csprojMeta = parseCsprojMeta(csproj);
  const handoff = parseHandoff(handoffMd);
  const recentSync = parseSyncEntries(syncMd, 4);
  const extra = parseExtraRequests(extraMd);
  const lessonsCount = countBuildLessons(lessonsMd);
  const autoContextActive = agentsMd.includes('Auto-Loaded');

  return {
    generatedAt: new Date().toISOString(),
    app: csprojMeta,
    handoff,
    recentSync,
    extra,
    lessonsCount,
    autoContextActive,
    docs: Object.entries(ALLOWED_DOCS).map(([id, spec]) => ({
      id,
      label: spec.label,
      path: spec.relPath,
    })),
  };
}

export function renderDashboardHtml(snapshot, initialDoc) {
  const app = snapshot.app;
  const handoff = snapshot.handoff;
  const docButtons = snapshot.docs
    .map(
      (doc, idx) =>
        `<button type="button" class="doc-tab${idx === 0 ? ' active' : ''}" data-doc-id="${escapeHtml(doc.id)}" aria-pressed="${idx === 0 ? 'true' : 'false'}">${escapeHtml(doc.label)}</button>`
    )
    .join('');

  const syncRows = snapshot.recentSync.length
    ? snapshot.recentSync
        .map(
          (entry) => `
          <article class="sync-item">
            <div class="sync-head">
              <span class="scope-tag">${escapeHtml(entry.section)}</span>
              <strong class="sync-scope">${escapeHtml(entry.scope)}</strong>
              <span class="sync-date">${escapeHtml(entry.date)}</span>
            </div>
            <p class="sync-summary">${escapeHtml(entry.summary.slice(0, 220))}${entry.summary.length > 220 ? '…' : ''}</p>
          </article>`
        )
        .join('')
    : '<p class="muted">No sync entries found.</p>';

  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>${escapeHtml(app.title)} Android — Workspace Preview</title>
  <meta name="description" content="Lightweight zero-dependency AI Studio workspace preview for Cavern / CavernizeAndroid (.NET Android C# DSP project)." />
  <style>
    :root {
      color-scheme: dark;
      --bg: #080A10;
      --card: rgba(22, 23, 31, 0.86);
      --elevated: rgba(32, 33, 43, 0.92);
      --border: rgba(255, 255, 255, 0.13);
      --border-strong: rgba(89, 143, 255, 0.45);
      --primary: #387AFF;
      --primary-hi: #598FFF;
      --green: #34D399;
      --amber: #F59E0B;
      --text: #FCFCFF;
      --text-sub: #B7B7BB;
      --text-muted: #8E909A;
      --radius-card: 22px;
      --radius-btn: 14px;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      font-family: -apple-system, BlinkMacSystemFont, "SF Pro Display", "Segoe UI", Roboto, sans-serif;
      background:
        radial-gradient(620px 420px at 18% 8%, rgba(56, 122, 255, 0.22), transparent 70%),
        radial-gradient(520px 380px at 88% 36%, rgba(110, 124, 246, 0.16), transparent 72%),
        var(--bg);
      color: var(--text);
      line-height: 1.5;
    }
    .shell {
      max-width: 1120px;
      margin: 0 auto;
      padding: 28px 20px 48px;
    }
    header.topbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      margin-bottom: 24px;
      padding: 20px 24px;
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: var(--radius-card);
      backdrop-filter: blur(18px);
    }
    .brand h1 {
      margin: 0;
      font-size: 1.45rem;
      font-weight: 700;
      letter-spacing: -0.02em;
    }
    .brand p {
      margin: 4px 0 0;
      font-size: 0.9rem;
      color: var(--text-sub);
    }
    .actions {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }
    .status-dot {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 7px 13px;
      border-radius: 999px;
      background: rgba(52, 211, 153, 0.14);
      border: 1px solid rgba(52, 211, 153, 0.38);
      color: #A7F3D0;
      font-size: 0.82rem;
      font-weight: 600;
    }
    .status-dot::before {
      content: "";
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--green);
    }
    button.primary-btn {
      appearance: none;
      border: 1px solid rgba(89, 143, 255, 0.55);
      background: var(--primary);
      color: var(--text);
      font-weight: 600;
      font-size: 0.88rem;
      padding: 9px 16px;
      border-radius: var(--radius-btn);
      cursor: pointer;
      transition: transform 0.12s ease, background 0.15s ease;
    }
    button.primary-btn:hover { background: var(--primary-hi); }
    button.primary-btn:active { transform: scale(0.97); }
    .metrics {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 14px;
      margin-bottom: 20px;
    }
    .metric-card, .panel {
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: var(--radius-card);
      padding: 18px 20px;
      backdrop-filter: blur(16px);
    }
    .metric-label {
      font-size: 0.78rem;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--text-muted);
      margin-bottom: 6px;
    }
    .metric-value {
      font-size: 1.25rem;
      font-weight: 700;
      color: var(--text);
      overflow-wrap: anywhere;
    }
    .metric-sub {
      margin-top: 4px;
      font-size: 0.82rem;
      color: var(--text-sub);
    }
    .grid {
      display: grid;
      grid-template-columns: 1fr 1.25fr;
      gap: 18px;
      align-items: start;
    }
    .panel h2 {
      margin: 0 0 12px;
      font-size: 1.05rem;
      font-weight: 700;
      letter-spacing: -0.01em;
    }
    .kv-block {
      padding: 12px 14px;
      border-radius: 14px;
      background: var(--elevated);
      border: 1px solid rgba(255, 255, 255, 0.08);
      margin-bottom: 12px;
    }
    .kv-title {
      font-size: 0.76rem;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--primary-hi);
      margin-bottom: 4px;
    }
    .kv-body {
      margin: 0;
      font-size: 0.88rem;
      color: var(--text-sub);
      overflow-wrap: anywhere;
    }
    .sync-list {
      display: flex;
      flex-direction: column;
      gap: 10px;
      margin-top: 10px;
    }
    .sync-item {
      padding: 12px 14px;
      border-radius: 14px;
      background: var(--elevated);
      border: 1px solid rgba(255, 255, 255, 0.08);
    }
    .sync-head {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: nowrap;
      margin-bottom: 6px;
    }
    .scope-tag {
      flex-shrink: 0;
      font-size: 0.72rem;
      font-weight: 700;
      padding: 2px 8px;
      border-radius: 999px;
      background: rgba(56, 122, 255, 0.2);
      border: 1px solid rgba(89, 143, 255, 0.45);
      color: #C7DBFF;
    }
    .sync-scope {
      flex: 1 1 auto;
      min-width: 0;
      font-size: 0.85rem;
      color: var(--text);
      overflow-wrap: anywhere;
    }
    .sync-date {
      flex-shrink: 0;
      font-size: 0.76rem;
      color: var(--text-muted);
      white-space: nowrap;
    }
    .sync-summary {
      margin: 0;
      font-size: 0.82rem;
      color: var(--text-sub);
      overflow-wrap: anywhere;
    }
    .doc-tabs {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-bottom: 12px;
    }
    button.doc-tab {
      appearance: none;
      border: 1px solid var(--border);
      background: rgba(255, 255, 255, 0.05);
      color: var(--text-sub);
      font-size: 0.82rem;
      font-weight: 600;
      padding: 7px 12px;
      border-radius: 11px;
      cursor: pointer;
    }
    button.doc-tab:hover {
      background: rgba(255, 255, 255, 0.1);
      color: var(--text);
    }
    button.doc-tab.active {
      background: rgba(56, 122, 255, 0.22);
      border-color: var(--border-strong);
      color: var(--text);
    }
    .doc-meta {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
      font-size: 0.8rem;
      color: var(--text-muted);
    }
    pre.doc-viewer {
      margin: 0;
      padding: 14px;
      border-radius: 14px;
      background: #0D0F17;
      border: 1px solid rgba(255, 255, 255, 0.09);
      color: #E4E6F0;
      font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
      font-size: 0.78rem;
      line-height: 1.5;
      max-height: 460px;
      overflow: auto;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .muted { color: var(--text-muted); font-size: 0.85rem; }
    footer.foot {
      margin-top: 20px;
      text-align: center;
      font-size: 0.78rem;
      color: var(--text-muted);
    }
    @media (max-width: 900px) {
      .metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .grid { grid-template-columns: 1fr; }
    }
    @media (max-width: 520px) {
      .shell { padding: 16px 12px 32px; }
      header.topbar { padding: 16px; }
      .metrics { grid-template-columns: 1fr; }
    }
  </style>
</head>
<body>
  <div class="shell">
    <header class="topbar">
      <div class="brand">
        <h1>${escapeHtml(app.title)} Android — Workspace Preview</h1>
        <p>${escapeHtml(app.applicationId)} · ${escapeHtml(app.targetFramework)} · C# / .NET / Android NDK</p>
      </div>
      <div class="actions">
        <span class="status-dot" data-testid="server-status">Dev Server Ready</span>
        <button type="button" class="primary-btn" id="refreshBtn">Refresh Live Context</button>
      </div>
    </header>

    <section class="metrics" aria-label="Project Summary">
      <div class="metric-card">
        <div class="metric-label">Android App</div>
        <div class="metric-value" id="metricVersion">v${escapeHtml(app.displayVersion)}</div>
        <div class="metric-sub">Code ${escapeHtml(app.versionCode)} · minSdk ${escapeHtml(app.minSdk)}</div>
      </div>
      <div class="metric-card">
        <div class="metric-label">Active Wave</div>
        <div class="metric-value" id="metricWave">${escapeHtml(handoff.wave)}</div>
        <div class="metric-sub">Synced via HANDOFF.md</div>
      </div>
      <div class="metric-card">
        <div class="metric-label">Auto-Loaded Context</div>
        <div class="metric-value" id="metricContext">${snapshot.autoContextActive ? 'Active (AGENTS.md)' : 'Check AGENTS.md'}</div>
        <div class="metric-sub">${snapshot.lessonsCount} timeless build lessons</div>
      </div>
      <div class="metric-card">
        <div class="metric-label">Extra Requests</div>
        <div class="metric-value" id="metricExtra">${snapshot.extra.pendingCount} pending</div>
        <div class="metric-sub">${snapshot.extra.completedCount} completed</div>
      </div>
    </section>

    <main class="grid">
      <section class="panel" aria-label="Session Handoff and Sync Ledger">
        <h2>Live Session Handoff</h2>
        <div class="kv-block">
          <div class="kv-title">Summary</div>
          <p class="kv-body" id="handoffSummary">${escapeHtml(handoff.summary || 'No summary yet.')}</p>
        </div>
        <div class="kv-block">
          <div class="kv-title">Next Step</div>
          <p class="kv-body" id="handoffNext">${escapeHtml(handoff.next || 'No pending next step.')}</p>
        </div>

        <h2>Recent Sync Ledger</h2>
        <div class="sync-list" id="syncList">${syncRows}</div>
      </section>

      <section class="panel" aria-label="Living Context Documents">
        <h2>Living Project Context</h2>
        <nav class="doc-tabs" aria-label="Context files" id="docTabs">${docButtons}</nav>
        <div class="doc-meta">
          <span id="docPath">${escapeHtml(initialDoc.path)}</span>
          <span id="docLines">${initialDoc.lines} lines</span>
        </div>
        <pre class="doc-viewer" id="docViewer" data-testid="doc-viewer">${escapeHtml(initialDoc.content)}</pre>
      </section>
    </main>

    <footer class="foot">
      Zero-dependency Node.js preview shim for AI Studio Build · Real application builds via GitHub Actions (.github/workflows/BuildAndroid.yml)
    </footer>
  </div>

  <script>
    const docTabs = document.getElementById('docTabs');
    const docViewer = document.getElementById('docViewer');
    const docPath = document.getElementById('docPath');
    const docLines = document.getElementById('docLines');
    const refreshBtn = document.getElementById('refreshBtn');
    let activeDocId = 'handoff';

    async function loadDoc(id) {
      activeDocId = id;
      docTabs.querySelectorAll('button.doc-tab').forEach((btn) => {
        const isCurrent = btn.dataset.docId === id;
        btn.classList.toggle('active', isCurrent);
        btn.setAttribute('aria-pressed', isCurrent ? 'true' : 'false');
      });
      try {
        const res = await fetch('/api/doc?id=' + encodeURIComponent(id), { cache: 'no-store' });
        if (!res.ok) throw new Error('HTTP ' + res.status);
        const data = await res.json();
        docPath.textContent = data.path;
        docLines.textContent = data.lines + ' lines';
        docViewer.textContent = data.content;
      } catch (err) {
        docViewer.textContent = 'Failed to load document: ' + err.message;
      }
    }

    async function refreshContext() {
      refreshBtn.disabled = true;
      refreshBtn.textContent = 'Refreshing...';
      try {
        const res = await fetch('/api/context', { cache: 'no-store' });
        if (res.ok) {
          const snap = await res.json();
          document.getElementById('metricVersion').textContent = 'v' + snap.app.displayVersion;
          document.getElementById('metricWave').textContent = snap.handoff.wave;
          document.getElementById('metricContext').textContent = snap.autoContextActive ? 'Active (AGENTS.md)' : 'Check AGENTS.md';
          document.getElementById('metricExtra').textContent = snap.extra.pendingCount + ' pending';
          document.getElementById('handoffSummary').textContent = snap.handoff.summary || 'No summary yet.';
          document.getElementById('handoffNext').textContent = snap.handoff.next || 'No pending next step.';
        }
        await loadDoc(activeDocId);
      } finally {
        refreshBtn.disabled = false;
        refreshBtn.textContent = 'Refresh Live Context';
      }
    }

    docTabs.addEventListener('click', (ev) => {
      const btn = ev.target.closest('button.doc-tab');
      if (btn && btn.dataset.docId) {
        loadDoc(btn.dataset.docId);
      }
    });

    refreshBtn.addEventListener('click', refreshContext);
  </script>
</body>
</html>`;
}
