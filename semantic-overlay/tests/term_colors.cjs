// Compare the actual browser color function with the compiled native implementation.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(path.join(root, '../browser-extension/content.js'), 'utf8');
const fn = source.match(/function termColor\(term\) \{[\s\S]*?\n  \}/);
assert.ok(fn, 'Browser color function missing');
const color = vm.runInNewContext('(' + fn[0] + ')');
const native = spawnSync(path.join(root, 'native-host/bin/CaptionTextTest.exe'), [], { encoding: 'utf8' });
assert.equal(native.status, 0, native.stderr);
let count = 0;
for (const line of native.stdout.split(/\r?\n/)) {
  const match = line.match(/^([^:]+):(\d+,\d+,\d+)$/);
  if (!match) continue;
  assert.equal(color(match[1]).join(','), match[2], match[1]);
  count++;
}
assert.equal(count, 5);
assert.equal(color('OneAPI').join(','), color(' one api ').join(','));
assert.equal(color('ＯｎｅＡＰＩ').join(','), color('OneAPI').join(','));
console.log('Native/browser term colors match: 5 fixtures + normalized aliases');
