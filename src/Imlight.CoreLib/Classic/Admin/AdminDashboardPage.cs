/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * ADMIN DASHBOARD PAGE
 * ========================================================================
 *
 * PURPOSE:
 * The dashboard's one HTML page (inline CSS and script, no external
 * files). It polls /api/state every 5 seconds; every value from the server
 * is written with textContent, never as HTML.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/01/2026
 */

namespace Imlight.CoreLib.Classic.Admin;

internal static class AdminDashboardPage {

    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>W101 Classic Admin</title>
<style>
:root { --bg:#f4f1ea; --card:#fffdf8; --ink:#2b2418; --muted:#7a6f5c; --line:#e2dbcc; --accent:#6b3fa0; --ok:#2e7d32; --warn:#b26a00; --bad:#b3261e; }
@media (prefers-color-scheme: dark) { :root { --bg:#16131c; --card:#211d29; --ink:#ece6f5; --muted:#a59cb5; --line:#3a3346; --accent:#b58cf0; --ok:#7fd88a; --warn:#f0b35a; --bad:#ff8a80; } }
* { box-sizing:border-box; }
body { margin:0; background:var(--bg); color:var(--ink); font:14px/1.45 system-ui,-apple-system,Segoe UI,sans-serif; }
header { display:flex; align-items:center; justify-content:space-between; padding:12px 16px; border-bottom:1px solid var(--line); background:var(--card); position:sticky; top:0; }
header h1 { font-size:17px; margin:0; letter-spacing:.3px; }
header .meta { color:var(--muted); font-size:13px; }
main { max-width:1180px; margin:0 auto; padding:16px; display:grid; gap:14px; grid-template-columns:repeat(auto-fit,minmax(340px,1fr)); }
section { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px 14px; min-width:0; }
section.wide { grid-column:1/-1; }
h2 { font-size:14px; margin:0 0 8px; text-transform:uppercase; letter-spacing:.6px; color:var(--muted); }
table { width:100%; border-collapse:collapse; font-size:13px; }
th,td { text-align:left; padding:4px 6px; border-bottom:1px solid var(--line); vertical-align:top; overflow-wrap:anywhere; }
th { color:var(--muted); font-weight:600; }
.stats { display:flex; gap:18px; flex-wrap:wrap; }
.stat b { display:block; font-size:22px; }
.stat span { color:var(--muted); font-size:12px; }
input,select,textarea,button { font:inherit; color:inherit; }
input[type=text],input[type=number],input[type=password],textarea,select { background:var(--bg); border:1px solid var(--line); border-radius:6px; padding:5px 7px; width:100%; }
button { background:var(--accent); color:#fff; border:0; border-radius:6px; padding:6px 12px; cursor:pointer; }
button.ghost { background:transparent; color:var(--accent); border:1px solid var(--accent); }
button.danger { background:var(--bad); }
.row { display:flex; gap:8px; align-items:center; margin:6px 0; flex-wrap:wrap; }
.row > * { flex:1 1 auto; }
.row > button { flex:0 0 auto; }
.muted { color:var(--muted); }
.ERROR,.Error,.Fatal { color:var(--bad); }
.Warning { color:var(--warn); }
.pill { display:inline-block; padding:1px 7px; border-radius:10px; border:1px solid var(--line); font-size:12px; }
.log { max-height:340px; overflow:auto; font:12px/1.4 ui-monospace,Menlo,monospace; }
.log div { padding:2px 0; border-bottom:1px dashed var(--line); }
#login { max-width:340px; margin:12vh auto; background:var(--card); border:1px solid var(--line); border-radius:10px; padding:18px; }
#msg { position:fixed; right:16px; bottom:16px; background:var(--ink); color:var(--bg); padding:8px 12px; border-radius:8px; display:none; max-width:80vw; }
.setting { display:grid; grid-template-columns:1fr 120px auto; gap:8px; align-items:center; padding:5px 0; border-bottom:1px solid var(--line); }
.setting p { margin:2px 0 0; color:var(--muted); font-size:12px; }
@media (max-width:520px) { .setting { grid-template-columns:1fr; } main { padding:10px; } }
</style>
</head>
<body>
<div id="login" hidden>
  <h2>W101 Classic admin</h2>
  <div class="row"><input id="u" type="text" placeholder="Administrator account" autocomplete="username"></div>
  <div class="row"><input id="p" type="password" placeholder="Password" autocomplete="current-password"></div>
  <div class="row"><button id="loginBtn">Log in</button></div>
  <p class="muted" id="loginErr"></p>
</div>
<div id="app" hidden>
<header><h1>W101 Classic admin</h1><div class="meta"><span id="who"></span> · <a href="#" id="logout">log out</a></div></header>
<main>
  <section>
    <h2>Server</h2>
    <div class="stats">
      <div class="stat"><b id="uptime">-</b><span>uptime</span></div>
      <div class="stat"><b id="onlineCount">-</b><span>online</span></div>
      <div class="stat"><b id="duelCount">-</b><span>fights</span></div>
      <div class="stat"><b id="errCount">-</b><span>errors / warnings</span></div>
    </div>
    <p class="muted" id="profile"></p>
  </section>
  <section>
    <h2>Safe restart and backup</h2>
    <p id="restartState" class="muted">No restart scheduled.</p>
    <div class="row"><input id="rMinutes" type="number" min="0" max="240" value="5" title="minutes of warning"><input id="rReason" type="text" placeholder="reason (optional)"></div>
    <div class="row"><button id="restartBtn">Restart</button><button id="backupBtn" class="ghost">Back up</button><button id="cancelBtn" class="danger">Cancel</button></div>
    <p class="muted">Warns in game, waits for fights to end (capped by RestartMaxWaitMinutes), then restarts. A backup stops the server for a minute.</p>
  </section>
  <section>
    <h2>Broadcast</h2>
    <div class="row"><textarea id="bText" rows="2" maxlength="500" placeholder="Message to every wizard"></textarea></div>
    <div class="row"><button id="bBtn">Send</button></div>
  </section>
  <section class="wide">
    <h2>Online</h2>
    <table><thead><tr><th>Wizard</th><th>Account</th><th>Zone</th><th>In a fight</th></tr></thead><tbody id="online"></tbody></table>
  </section>
  <section class="wide">
    <h2>Fights in progress</h2>
    <table><thead><tr><th>Zone</th><th>Kind</th><th>Wizards</th><th>Creatures</th><th>Dropped (seat held)</th><th>Started</th></tr></thead><tbody id="duels"></tbody></table>
  </section>
  <section class="wide" id="extras"></section>
  <section class="wide">
    <h2>Switches</h2>
    <div id="settings"></div>
  </section>
  <section>
    <h2>Backups</h2>
    <p class="muted" id="backupDir"></p>
    <table><thead><tr><th>Archive</th><th>Size</th><th>Written</th></tr></thead><tbody id="backups"></tbody></table>
  </section>
  <section>
    <h2>Recent warnings and errors</h2>
    <div class="log" id="log"></div>
  </section>
</main>
</div>
<div id="msg"></div>
<script>
const $ = id => document.getElementById(id);
const el = (tag, text, cls) => { const e = document.createElement(tag); if (text !== undefined && text !== null) e.textContent = String(text); if (cls) e.className = cls; return e; };
function toast(text) { const m = $('msg'); m.textContent = text; m.style.display = 'block'; clearTimeout(toast.t); toast.t = setTimeout(() => m.style.display = 'none', 4000); }
async function api(path, body) {
  const opts = body === undefined ? {} : { method:'POST', headers:{ 'Content-Type':'application/json', 'X-W101C':'1' }, body:JSON.stringify(body) };
  const r = await fetch(path, Object.assign({ credentials:'same-origin' }, opts));
  let data = {}; try { data = await r.json(); } catch (e) {}
  if (r.status === 401) { showLogin(); throw new Error(data.error || 'log in first'); }
  if (!r.ok) throw new Error(data.error || ('HTTP ' + r.status));
  return data;
}
function showLogin() { $('app').hidden = true; $('login').hidden = false; }
function fmtUptime(s) { const d = Math.floor(s/86400), h = Math.floor(s%86400/3600), m = Math.floor(s%3600/60); return (d ? d+'d ' : '') + h + 'h ' + m + 'm'; }
function fmtTime(t) { try { return new Date(t).toLocaleString(); } catch (e) { return t; } }
function fmtBytes(b) { return b > 1048576 ? (b/1048576).toFixed(1)+' MB' : (b/1024).toFixed(0)+' KB'; }
function rows(tbody, items, cells, empty) {
  const tb = $(tbody); tb.replaceChildren();
  if (!items || !items.length) { const tr = el('tr'); const td = el('td', empty, 'muted'); td.colSpan = 9; tr.append(td); tb.append(tr); return; }
  for (const it of items) { const tr = el('tr'); for (const c of cells(it)) tr.append(el('td', c)); tb.append(tr); }
}
let editing = null;
function renderSettings(list) {
  if (editing) return;
  const box = $('settings'); box.replaceChildren();
  let group = null;
  for (const s of list) {
    if (s.group !== group) { group = s.group; box.append(el('h2', group)); }
    const row = el('div', null, 'setting');
    const left = el('div'); left.append(el('b', s.key + ' ')); left.append(el('span', s.source === 'default' ? 'default' : s.source, 'pill')); left.append(el('p', s.description + ' (default ' + s.defaultValue + ')'));
    let input;
    if (s.kind === 'bool') { input = el('select'); for (const v of ['true','false']) { const o = el('option', v); o.value = v; input.append(o); } input.value = s.value; }
    else { input = el('input'); input.type = 'number'; input.step = s.kind === 'int' ? '1' : 'any'; if (s.min !== null) input.min = s.min; if (s.max !== null) input.max = s.max; input.value = s.value; }
    input.addEventListener('focus', () => editing = s.key); input.addEventListener('blur', () => setTimeout(() => { if (editing === s.key) editing = null; }, 300));
    const btns = el('div', null, 'row');
    const save = el('button', 'Set'); save.onclick = async () => { try { const r = await api('/api/settings', { key:s.key, value:String(input.value) }); toast(s.key + ' = ' + input.value + (r.note ? ' (' + r.note + ')' : '')); editing = null; refresh(); } catch (e) { toast(e.message); } };
    const reset = el('button', 'Reset', 'ghost'); reset.title = 'back to Imlight.ini or the default'; reset.onclick = async () => { try { await api('/api/settings', { key:s.key, value:'' }); toast(s.key + ' reset'); editing = null; refresh(); } catch (e) { toast(e.message); } };
    if (!s.live) { save.disabled = true; reset.disabled = true; }
    btns.append(save, reset);
    row.append(left, input, btns); box.append(row);
  }
}
function renderExtras(sections) {
  const box = $('extras'); box.replaceChildren();
  const names = Object.keys(sections || {});
  box.hidden = names.length === 0;
  for (const name of names) {
    box.append(el('h2', name));
    const v = sections[name];
    if (Array.isArray(v)) {
      if (!v.length) { box.append(el('p', 'none', 'muted')); continue; }
      const keys = Object.keys(v[0]); const t = el('table'); const hr = el('tr'); for (const k of keys) hr.append(el('th', k)); const th = el('thead'); th.append(hr); t.append(th);
      const tb = el('tbody'); for (const it of v) { const tr = el('tr'); for (const k of keys) tr.append(el('td', typeof it[k] === 'object' && it[k] !== null ? JSON.stringify(it[k]) : it[k])); tb.append(tr); } t.append(tb); box.append(t);
    } else if (v && typeof v === 'object') {
      const t = el('table'); const tb = el('tbody'); for (const [k, x] of Object.entries(v)) { const tr = el('tr'); tr.append(el('th', k), el('td', typeof x === 'object' && x !== null ? JSON.stringify(x) : x)); tb.append(tr); } t.append(tb); box.append(t);
    } else box.append(el('p', v));
  }
}
async function refresh() {
  let s; try { s = await api('/api/state'); } catch (e) { return; }
  $('login').hidden = true; $('app').hidden = false;
  $('who').textContent = s.user;
  $('uptime').textContent = fmtUptime(s.uptimeSeconds);
  $('onlineCount').textContent = s.online.length;
  $('duelCount').textContent = s.duels.length;
  $('errCount').textContent = s.errors + ' / ' + s.warnings;
  $('profile').textContent = 'Profile ' + s.profile + ', started ' + fmtTime(s.startedUtc);
  $('restartState').textContent = s.restart ? (s.restart.kind + ' by ' + s.restart.requestedBy + ': ' + s.restart.state + '; due ' + fmtTime(s.restart.deadlineUtc) + ', latest ' + fmtTime(s.restart.hardCapUtc) + (s.restart.reason ? ' (' + s.restart.reason + ')' : '')) : 'No restart scheduled.';
  rows('online', s.online, w => [w.wizard, w.account, w.zoneName ? w.zoneName + ' (' + w.zone + ')' : w.zone, w.inDuel ? 'yes' : ''], 'Nobody is online.');
  rows('duels', s.duels, d => [d.zone, d.pvp ? 'PvP' : 'PvE', d.wizards, d.creatures, d.heldSeats, fmtTime(d.startedUtc)], 'No fights.');
  $('backupDir').textContent = s.backupDirectory;
  rows('backups', s.backups, b => [b.name, fmtBytes(b.bytes), fmtTime(b.writtenUtc)], 'No backups found.');
  const log = $('log'); log.replaceChildren();
  if (!s.recentLog.length) log.append(el('div', 'Nothing since the server started.', 'muted'));
  for (const e of s.recentLog) { const d = el('div', null, e.level); d.append(el('span', fmtTime(e.time) + ' ', 'muted')); d.append(document.createTextNode(e.message)); log.append(d); }
  renderSettings(s.settings);
  renderExtras(s.sections);
}
$('loginBtn').onclick = async () => { try { await api('/api/login', { username:$('u').value, password:$('p').value }); $('p').value = ''; $('loginErr').textContent = ''; refresh(); } catch (e) { $('loginErr').textContent = e.message; } };
$('p').addEventListener('keydown', e => { if (e.key === 'Enter') $('loginBtn').click(); });
$('logout').onclick = async e => { e.preventDefault(); try { await api('/api/logout', {}); } catch (x) {} showLogin(); };
$('bBtn').onclick = async () => { const t = $('bText').value.trim(); if (!t) return; try { const r = await api('/api/broadcast', { text:t }); toast('Sent to ' + r.sent + ' wizard(s).'); $('bText').value = ''; } catch (e) { toast(e.message); } };
async function schedule(kind) { const m = Number($('rMinutes').value); if (!confirm((kind === 'backup' ? 'Back up' : 'Restart') + ' the server in ' + m + ' minute(s)?')) return; try { await api('/api/restart', { kind, minutes:m, reason:$('rReason').value }); toast(kind + ' scheduled'); refresh(); } catch (e) { toast(e.message); } }
$('restartBtn').onclick = () => schedule('restart');
$('backupBtn').onclick = () => schedule('backup');
$('cancelBtn').onclick = async () => { try { const r = await api('/api/cancel', {}); toast(r.ok ? 'Cancelled.' : 'Nothing to cancel.'); refresh(); } catch (e) { toast(e.message); } };
refresh().then(() => { if ($('app').hidden) showLogin(); });
setInterval(() => { if (!$('app').hidden) refresh(); }, 5000);
</script>
</body>
</html>
""";

}
