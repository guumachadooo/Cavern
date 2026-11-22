import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  countBuildLessons,
  escapeHtml,
  loadWorkspaceSnapshot,
  parseCsprojMeta,
  parseExtraRequests,
  parseHandoff,
  parseSyncEntries,
  readWhitelistedDoc,
  renderDashboardHtml,
} from './dashboard.mjs';

const ROOT_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

test('escapeHtml neutralizes HTML special characters', () => {
  assert.equal(escapeHtml('<script>"x"&\'y\'</script>'), '&lt;script&gt;&quot;x&quot;&amp;&#39;y&#39;&lt;/script&gt;');
});

test('parseCsprojMeta extracts Android project metadata', () => {
  const xml = `
    <Project>
      <PropertyGroup>
        <TargetFramework>net11.0-android</TargetFramework>
        <SupportedOSPlatformVersion>26.0</SupportedOSPlatformVersion>
        <ApplicationId>hu.sbence.cavern.cavernize</ApplicationId>
        <ApplicationVersion>29</ApplicationVersion>
        <ApplicationDisplayVersion>2.9.0</ApplicationDisplayVersion>
        <ApplicationTitle>Cavernize</ApplicationTitle>
      </PropertyGroup>
    </Project>`;
  const meta = parseCsprojMeta(xml);
  assert.equal(meta.title, 'Cavernize');
  assert.equal(meta.displayVersion, '2.9.0');
  assert.equal(meta.versionCode, '29');
  assert.equal(meta.targetFramework, 'net11.0-android');
  assert.equal(meta.minSdk, '26.0');
});

test('parseHandoff extracts wave, summary, and next step', () => {
  const md = [
    '# Session Handoff',
    'WAVE|W38',
    'SUMMARY|Added zero-dep preview',
    'NEXT|Push commit',
  ].join('\n');
  const parsed = parseHandoff(md);
  assert.equal(parsed.wave, 'W38');
  assert.equal(parsed.summary, 'Added zero-dep preview');
  assert.equal(parsed.next, 'Push commit');
});

test('parseSyncEntries extracts newest entries with section labels', () => {
  const md = [
    '## Cavern Project Sync Entries (Newest First)',
    '- 2026-11-22 | pending-push | W38-preview | package.json | Added preview shim | checks:ok | ci_apk:skip | next:push',
    '## Plugin (`dynamic-fractal-planner`) Sync Entries (Newest First)',
    '- 2026-11-22 | pending-push | plugin-v2.6.0 | plugin.json | Promoted scripts | checks:ok | ci_apk:skip | next:none',
  ].join('\n');
  const rows = parseSyncEntries(md, 5);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].section, 'Cavern');
  assert.equal(rows[0].scope, 'W38-preview');
  assert.equal(rows[1].section, 'Plugin');
});

test('parseExtraRequests distinguishes pending vs completed items', () => {
  const md = [
    '> GATE|move to `## Completed Requests` when done.',
    '## Pending Extra Requests',
    '- REQ|foo -> bar',
    '## Completed Requests',
    '- REQ|done1 -> DONE',
    '- REQ|done2 -> DONE',
  ].join('\n');
  const parsed = parseExtraRequests(md);
  assert.equal(parsed.pendingCount, 1);
  assert.equal(parsed.completedCount, 2);
});

test('countBuildLessons counts BL headings accurately', () => {
  assert.equal(countBuildLessons('### `BL-001` | Foo\ntext\n### BL-002 | Bar\n'), 2);
});

test('readWhitelistedDoc rejects unknown keys and path traversal', () => {
  assert.equal(readWhitelistedDoc(ROOT_DIR, '../etc/passwd'), null);
  assert.equal(readWhitelistedDoc(ROOT_DIR, 'nonexistent'), null);
  const handoff = readWhitelistedDoc(ROOT_DIR, 'handoff');
  assert.ok(handoff);
  assert.equal(handoff.path, '.agents/docs/HANDOFF.md');
  assert.ok(handoff.lines > 0);
});

test('loadWorkspaceSnapshot and renderDashboardHtml work on live repository', () => {
  const snap = loadWorkspaceSnapshot(ROOT_DIR);
  assert.equal(snap.app.applicationId, 'hu.sbence.cavern.cavernize');
  assert.ok(snap.lessonsCount >= 1);
  assert.ok(snap.extra.completedCount >= 1);
  const doc = readWhitelistedDoc(ROOT_DIR, 'handoff');
  const html = renderDashboardHtml(snap, doc);
  assert.ok(html.includes('Cavernize Android — Workspace Preview'));
  assert.ok(html.includes('Dev Server Ready'));
});
