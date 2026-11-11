const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const secret = 'A'.repeat(64);
const results = [];
let focused = true;
let nextId = 'request-one';
const sandbox = {
  importScripts() { sandbox.SCREENKEY_EDGE_TOKEN = secret; },
  setInterval() {},
  TextEncoder, AbortSignal,
  chrome: {
    runtime: {getPlatformInfo(callback) { callback({}); }},
    windows: {async getLastFocused() { return {focused, tabs: [{active: true, id: 7, url: 'https://example.com/'}]}; }},
    scripting: {async executeScript() { return [{result: {title: 'Example', html: '<p>Saved</p>'}}]; }}
  },
  async fetch(url, options) {
    assert.equal(options.headers['X-ScreenKey-Extension'], secret);
    if (url.endsWith('/poll')) return {ok: true, async json() { return {id: nextId}; }};
    if (url.endsWith('/result')) { results.push(JSON.parse(options.body)); return {ok: true}; }
    throw Error('Unexpected route');
  }
};
vm.runInNewContext(fs.readFileSync('browser/edge/worker.js', 'utf8'), sandbox);
async function run() {
  while (results.length < 1) await new Promise(resolve => setTimeout(resolve, 1));
  assert.equal(results[0].id, 'request-one');
  assert.equal(results[0].html, '<p>Saved</p>');
  focused = false; nextId = 'request-two';
  await sandbox.poll();
  assert.equal(results[1].id, 'request-two');
  assert.match(results[1].error, /foreground/);
  assert.equal(results[1].html, undefined);
  console.log('Edge worker authenticated polling and active-window checks passed.');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
