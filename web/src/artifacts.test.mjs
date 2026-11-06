import test from 'node:test';
import assert from 'node:assert/strict';
import {formatBytes, jobActive, jobPercent, matches} from './artifacts.ts';

const job = (patch = {}) => ({id: '1', state: 'downloading', link: '', applicationId: 'pricing', buildId: '1842', buildNumber: '2.18.5', bytesReceived: 0, totalBytes: null, error: null, artifact: null, alreadyImported: false, startedAt: '', finishedAt: null, ...patch});

test('import progress reflects real bytes and only reaches 100% once imported', () => {
  assert.equal(jobPercent(job()), 0);
  assert.equal(jobPercent(job({bytesReceived: 512, totalBytes: 1024})), 50);
  assert.equal(jobPercent(job({bytesReceived: 1024, totalBytes: 1024, state: 'verifying'})), 99);
  assert.equal(jobPercent(job({state: 'completed', finishedAt: 'x'})), 100);
  assert.ok(jobActive(job()));
  assert.equal(jobActive(job({state: 'failed', finishedAt: 'x'})), false);
});

test('artifact sizes are human readable', () => {
  assert.equal(formatBytes(0), '0 B');
  assert.equal(formatBytes(1536), '1.5 KB');
  assert.equal(formatBytes(300 * 1024 * 1024), '300 MB');
  assert.equal(formatBytes(-1), '—');
});

test('library search covers application, version, build and commit', () => {
  const entry = {status: 'available', artifact: {applicationId: 'pricing', version: '2.18.5', buildId: '1842', commit: 'a8e27bd', branch: 'main'}};
  for (const query of ['pricing service', '2.18', '1842', 'A8E27', ' main ']) assert.ok(matches(entry, 'Pricing Service', query), query);
  assert.equal(matches(entry, 'Pricing Service', 'risk'), false);
});
