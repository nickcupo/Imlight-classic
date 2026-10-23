// CLASSIC: exercise the actual inline dashboard script without browser/network dependencies.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(process.argv[2] || path.join(__dirname, '../../src/Imlight.CoreLib/Classic/Admin/AdminDashboardPage.cs'), 'utf8');
const script = source.match(/<script>([\s\S]*?)<\/script>/)[1];
new vm.Script(script); // Includes the polling/login code even though automatic timers are disabled below.
class Element {
  constructor(tag) { this.tagName = tag; this.children = []; this.events = {}; this.style = {}; this.value = ''; this._text = ''; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; this._text = ''; }
  addEventListener(event, callback) { this.events[event] = callback; }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(node => node.textContent).join(''); }
}
const nodes = Object.fromEntries([...source.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], new Element('div')]));
nodes.onlineFilter.value = 'all';
let state = {
  user: 'admin', uptimeSeconds: 60, profile: 'test', startedUtc: '2026-10-23T12:00:00Z',
  online: [
    { wizard: '<img src=x onerror=alert(1)>', account: 'human', zone: 'WC_Hub', zoneName: 'Commons', inDuel: true, isSimulated: false },
    { wizard: 'Simulated wizard', account: '—', zone: 'WC_Hub', zoneName: 'Commons', inDuel: false, isSimulated: true },
  ], duels: [], backups: [], recentLog: [], settings: [], sections: {}, warnings: 0, errors: 0,
};
const context = vm.createContext({
  Node: Element,
  document: {
    getElementById(id) { assert.ok(nodes[id], `Unknown element ${id}`); return nodes[id]; },
    createElement: tag => new Element(tag),
    createTextNode(text) { const node = new Element('#text'); node.textContent = text; return node; },
  },
  fetch: async url => ({ ok: true, status: 200, json: async () => url === '/api/state' ? state : { enabled: false } }),
});
vm.runInContext(script.slice(0, script.indexOf('refresh().then(')), context);
(async () => {
  await vm.runInContext('refresh()', context);
  assert.equal(nodes.onlineCount.textContent, '2');
  assert.equal(nodes.realCount.textContent, '1');
  assert.equal(nodes.simulatedCount.textContent, '1');
  assert.equal(nodes.online.children.length, 2);
  assert.equal(nodes.online.children[0].children[0].textContent, state.online[0].wizard);
  assert.equal(nodes.online.children[0].children[0].children.length, 0, 'Wizard names stay literal text');
  assert.equal(nodes.online.children[0].children[1].children[0].textContent, 'Real player');
  assert.equal(nodes.online.children[1].children[1].children[0].textContent, 'Simulated');

  nodes.onlineFilter.value = 'real';
  nodes.onlineFilter.events.change();
  assert.equal(nodes.online.children.length, 1);
  assert.equal(nodes.online.children[0].children[2].textContent, 'human');
  await vm.runInContext('refresh()', context);
  assert.equal(nodes.onlineFilter.value, 'real', 'Polling preserves the chosen filter');
  assert.equal(nodes.online.children.length, 1);
  assert.equal(nodes.onlineCount.textContent, '2', 'Filtering does not change the overall total');

  nodes.onlineFilter.value = 'simulated';
  nodes.onlineFilter.events.change();
  assert.equal(nodes.online.children.length, 1);
  assert.equal(nodes.online.children[0].children[1].textContent, 'Simulated');
  state.online = state.online.filter(wizard => !wizard.isSimulated);
  await vm.runInContext('refresh()', context);
  assert.equal(nodes.online.textContent, 'No simulated players are online.');
  assert.equal(nodes.simulatedCount.textContent, '0');

  state.online = [];
  nodes.onlineFilter.value = 'real';
  await vm.runInContext('refresh()', context);
  assert.equal(nodes.online.textContent, 'No real players are online.');
  nodes.onlineFilter.value = 'all';
  nodes.onlineFilter.events.change();
  assert.equal(nodes.online.textContent, 'Nobody is online.');
  console.log('PASS: totals, labels, human/simulated filters, polling, empty states and literal-text names');
})().catch(error => { console.error(error); process.exitCode = 1; });
