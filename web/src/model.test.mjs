import test from 'node:test';
import assert from 'node:assert/strict';
import {applications, servers, instances, createArtifact, canRun, complete, stages} from './model.ts';

test('inventory maps every unique instance to a known application and server', () => {
  assert.equal(instances.length, 48);
  assert.equal(new Set(instances.map(i => i.id)).size, instances.length);
  for (const instance of instances) {
    assert.ok(applications.some(a => a.id === instance.applicationId));
    assert.ok(servers.includes(instance.server));
  }
});

test('deployment stages unlock only after every target completes the prior stage', () => {
  const artifact = createArtifact(applications[0].id, '1.0', '1');
  assert.ok(canRun(artifact, 'install'));
  assert.equal(canRun(artifact, 'replicate'), false);
  assert.equal(canRun(artifact, 'restart'), false);
  artifact.progress.install = [100, 100, 99];
  assert.equal(canRun(artifact, 'replicate'), false);
  artifact.progress.install[2] = 100;
  assert.ok(canRun(artifact, 'replicate'));
  assert.equal(canRun(artifact, 'install'), false);
  artifact.progress.replicate = [100, 100, 100];
  assert.ok(canRun(artifact, 'restart'));
  artifact.progress.restart = [100, 100, 100];
  assert.ok(stages.every(stage => complete(artifact, stage)));
  assert.ok(stages.every(stage => !canRun(artifact, stage)));
});

test('artifacts have isolated progress and immutable build metadata', () => {
  const first = createArtifact(applications[0].id, '1', '123');
  const second = createArtifact(applications[0].id, '2', '124');
  first.progress.install[0] = 100;
  assert.equal(second.progress.install[0], 0);
  assert.notEqual(first.id, second.id);
  assert.equal(first.build, '123');
});
