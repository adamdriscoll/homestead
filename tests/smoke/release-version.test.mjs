import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import test from 'node:test';

const script = resolve('.github/scripts/release-version.mjs');
function version(tag) {
  return spawnSync(process.execPath, [script, tag], { encoding: 'utf8' });
}

for (const [tag, expected] of [['v0.0.1', '0.0.1'], ['v1.2.3-rc.1', '1.2.3-rc.1']]) {
  test(`release ${tag} supplies the application version independently of source defaults`, () => {
    const result = version(tag);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), expected);
  });
}

for (const tag of ['', '1.2.3', 'v01.2.3', 'v1.2', 'v1.2.3-01', 'v1.2.3+metadata', 'v1.2.3/path', `v1.2.3-${'a'.repeat(128)}`]) {
  test(`rejects unsupported release tag ${JSON.stringify(tag)}`, () => {
    const result = version(tag);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Release tag must/);
  });
}
