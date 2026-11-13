import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildGameDataCatalog } from './catalog.ts';
import { createDefaultProfile, resolveProfilesResult } from './attributeResolver.ts';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const catalogJson = JSON.parse(
  fs.readFileSync(path.resolve(__dirname, '../__golden__/simulatorCatalog.json'), 'utf8'),
);
const parityCases = JSON.parse(
  fs.readFileSync(path.resolve(__dirname, '../__golden__/parityCases.json'), 'utf8'),
);

function assertDeepAlmostEqual(actual, expected, pathPrefix = '$', tol = 1e-12) {
  if (expected === null || expected === undefined) {
    assert.equal(actual ?? null, null, `${pathPrefix}: expected null`);
    return;
  }
  if (typeof expected === 'number') {
    assert.equal(typeof actual, 'number', `${pathPrefix}: expected number, got ${typeof actual}`);
    const diff = Math.abs(actual - expected);
    const scale = Math.max(1, Math.abs(expected));
    assert.ok(
      diff <= tol * scale,
      `${pathPrefix}: expected ${expected}, got ${actual} (diff=${diff})`,
    );
    return;
  }
  if (typeof expected === 'string' || typeof expected === 'boolean') {
    assert.equal(actual, expected, `${pathPrefix}`);
    return;
  }
  if (Array.isArray(expected)) {
    assert.ok(Array.isArray(actual), `${pathPrefix}: expected array`);
    assert.equal(actual.length, expected.length, `${pathPrefix}.length`);
    for (let i = 0; i < expected.length; i++) {
      assertDeepAlmostEqual(actual[i], expected[i], `${pathPrefix}[${i}]`, tol);
    }
    return;
  }
  assert.equal(typeof actual, 'object', `${pathPrefix}: expected object`);
  assert.ok(actual !== null, `${pathPrefix}: expected non-null object`);
  for (const [key, expVal] of Object.entries(expected)) {
    assertDeepAlmostEqual(actual[key], expVal, `${pathPrefix}.${key}`, tol);
  }
}

test('Phase 2 parity: fingerprint, defaultProfile, resolveCase', () => {
  const catalog = buildGameDataCatalog(catalogJson);
  assert.equal(catalog.fingerprint, parityCases.fingerprint);

  const def = createDefaultProfile(catalog);
  assertDeepAlmostEqual(def, parityCases.defaultProfile, 'defaultProfile');

  const res = resolveProfilesResult(parityCases.resolveCase.request, catalog);
  assertDeepAlmostEqual(res, parityCases.resolveCase.expected, 'resolveCase', 1e-12);
});
