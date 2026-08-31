// Validate each page fragment: extract <script page-setup> body and try new Function().
const fs = require('fs');
const path = require('path');

// Stub Vue globals so pageSetup() can execute in Node
global.Vue = {
  ref: (v) => ({ value: v }),
  reactive: (o) => o,
  computed: (fn) => ({ value: undefined }),
  watch: () => () => {},
  onMounted: () => {},
  onUnmounted: () => {},
  nextTick: (fn) => { try { if (fn) fn(); } catch (e) { console.log('  nextTick err:', e.message); } return Promise.resolve(); },
  provide: () => {},
  inject: () => ({}),
};
global.echarts = { init: () => ({ setOption: () => {} }) };

const dir = path.join(__dirname, 'src', 'Beneflow.Api', 'wwwroot', 'pages');
const files = fs.readdirSync(dir).filter(f => f.endsWith('.html')).sort();
let bad = 0;
for (const f of files) {
  const full = path.join(dir, f);
  const html = fs.readFileSync(full, 'utf8');
  const m = html.match(/<script\s+page-setup[^>]*>([\s\S]*?)<\/script>/i);
  if (!m) {
    console.log(`[NO SCRIPT] ${f}`);
    continue;
  }
  const body = m[1] + '\n;return (typeof pageSetup !== "undefined") ? pageSetup : function(){return {};};';
  try {
    const fn = new Function(body);
    const setup = fn();
    // try calling pageSetup with a fake deps to catch runtime setup errors (refs etc.)
    const fakeDeps = {
      currentUser: { name: 'x', role: 'x', permissions: ['*'] },
      api: { get: async () => ({ list: [], total: 0 }), post: async () => ({}), put: async () => ({}), delete: async () => ({}) },
      common: { money: () => '0', num: () => '0', formatDate: () => '', formatDateTime: () => '', recentDays: () => [], orderPrefix: () => '', downloadBlob: () => {}, hasPerm: () => true },
      goPage: () => {}, toast: () => {}, confirm: async () => {},
    };
    let result;
    try { result = typeof setup === 'function' ? setup(fakeDeps) : {}; }
    catch (e) { console.log(`[SETUP THROW] ${f}: ${e.message}`); bad++; continue; }
    // count keys
    const keys = result && typeof result === 'object' ? Object.keys(result).length : 0;
    console.log(`[OK] ${f} (setup returns ${keys} keys)`);
  } catch (e) {
    console.log(`[SYNTAX ERROR] ${f}: ${e.message}`);
    bad++;
  }
}
console.log(bad === 0 ? '\nALL PAGES OK' : `\n${bad} PAGE(S) WITH ERRORS`);
