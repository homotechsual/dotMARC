import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readRoadmap} from '../../src/plugins/featureRequests/roadmap.ts';
import {buildRoadmap} from '../src/roadmap.mjs';

const payload = () => ({
  ...buildRoadmap([{
    number: 1, title: 'A useful feature', html_url: 'https://github.com/homotechsual/dotMARC/issues/1',
    labels: [{name: 'roadmap:public'}, {name: 'status:planned'}], state: 'open', milestone: null,
  }], [], []),
  stale: false, submissionsEnabled: true,
});
test('website accepts Worker response shape', () => {
  assert.equal(readRoadmap(payload()).issues[0].title, 'A useful feature');
});
test('website rejects unexpected links, invalid states and dates', () => {
  for (const patch of [{url: 'https://evil.example/'}, {status: 'unknown'}, {number: -1}, {completed: 'yes'},
    {changeType: 'unknown'}, {areas: ['invalid']}, {areas: ['dns', 'dns']}]) {
    const value = payload();
    value.issues[0] = {...value.issues[0], ...patch};
    assert.throws(() => readRoadmap(value));
  }
  assert.throws(() => readRoadmap({...payload(), updatedAt: 'invalid'}));
  assert.throws(() => readRoadmap({...payload(), issues: null}));
});

test('old cached roadmap items remain visibly unclassified', () => {
  const value = payload();
  delete value.issues[0].changeType;
  delete value.issues[0].areas;
  delete value.issues[0].classificationWarning;
  const issue = readRoadmap(value).issues[0];
  assert.equal(issue.changeType, null);
  assert.deepEqual(issue.areas, []);
  assert.equal(issue.classificationWarning, true);
});
