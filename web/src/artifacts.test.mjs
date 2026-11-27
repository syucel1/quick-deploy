import test from 'node:test';
import assert from 'node:assert/strict';
import {deleteBlocked, formatBytes, jobActive, jobPercent, matches, parseKeep, retentionSummary} from './artifacts.ts';

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

test('builds in use, or with unknown usage, cannot be deleted', () => {
  const entry = (inUseBy) => ({status: 'available', inUseBy, artifact: {applicationId: 'pricing', buildId: '1842'}});
  assert.equal(deleteBlocked(entry([])), null);
  assert.equal(deleteBlocked(entry(['pricing-01', 'pricing-02'])), 'In use on pricing-01, pricing-02');
  assert.match(deleteBlocked(entry(null)), /unknown/);
  assert.match(deleteBlocked(entry(undefined)), /unknown/);
});

test('retention keep count accepts whole numbers from 1 to 1000 only', () => {
  for (const [value, expected] of [['5', 5], [' 1 ', 1], ['1000', 1000], ['0', null], ['1001', null], ['2.5', null], ['-3', null], ['', null], ['1e2', null]]) assert.equal(parseKeep(value), expected, value);
});

test('retention summary states what is deleted, freed and protected', () => {
  const e = (size) => ({status: 'available', inUseBy: [], artifact: {size}});
  assert.equal(retentionSummary({keepPerApplication: 3, configuredKeepPerApplication: 0, remove: [e(1024), e(1024)], reclaimBytes: 2048, protected: []}),
    'Keeping the newest 3 builds per application deletes 2 builds and frees 2.0 KB.');
  assert.equal(retentionSummary({keepPerApplication: 1, configuredKeepPerApplication: 1, remove: [], reclaimBytes: 0, protected: [e(1)]}),
    'Nothing to delete when keeping the newest 1 build per application. 1 build older than that stays because discovery reports it installed or running.');
});
