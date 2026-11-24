import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {describe, test} from 'node:test';
import {planCloseOut, shippedLabels} from './release-issues.mjs';

const milestone = (number, title, state = 'open') => ({number, title, state});
const issue = (number, milestoneNumber, labels, extra = {}) => ({
  number, title: `Issue ${number}`, state: 'open', milestone: {number: milestoneNumber}, labels: labels.map(name => ({name})), ...extra,
});

const milestones = [milestone(9, 'v0.14.0'), milestone(10, 'v0.15.0'), milestone(11, 'v0.16.0'), milestone(8, 'v0.13.0', 'closed')];

describe('planCloseOut', () => {
  test('release:ready issues ship and every other open issue moves to the next milestone', () => {
    const issues = [
      issue(75, 9, ['status:in-progress', 'release:ready']),
      issue(43, 9, ['status:in-progress']),
      issue(54, 9, ['status:planned']),
    ];
    const plan = planCloseOut('0.14.0', milestones, issues);
    assert.equal(plan.milestone.number, 9);
    assert.deepEqual(plan.ship.map(item => item.number), [75]);
    assert.deepEqual(plan.move.map(item => item.number), [43, 54]);
    assert.equal(plan.next.title, 'v0.15.0');
    assert.equal(plan.createTitle, null);
  });

  test('accepts the version with or without its v', () => {
    assert.equal(planCloseOut('v0.14.0', milestones, []).milestone.number, 9);
  });

  test('the next milestone is the nearest later open one, not a closed or earlier one', () => {
    const plan = planCloseOut('0.14.0', [milestone(12, 'v0.16.0'), milestone(9, 'v0.14.0'), milestone(13, 'v0.15.0', 'closed'), milestone(8, 'v0.13.0')], [issue(1, 9, [])]);
    assert.equal(plan.next.title, 'v0.16.0');
  });

  test('with no later open milestone, it names the next minor version to create', () => {
    const plan = planCloseOut('0.14.0', [milestone(9, 'v0.14.0')], [issue(1, 9, ['status:planned'])]);
    assert.equal(plan.next, null);
    assert.equal(plan.createTitle, 'v0.15.0');
  });

  test('creates nothing when every issue ships', () => {
    const plan = planCloseOut('0.14.0', [milestone(9, 'v0.14.0')], [issue(1, 9, ['release:ready'])]);
    assert.equal(plan.createTitle, null);
    assert.equal(plan.move.length, 0);
  });

  test('ignores pull requests, closed issues and issues in other milestones', () => {
    const plan = planCloseOut('0.14.0', milestones, [
      issue(1, 9, ['release:ready'], {pull_request: {}}),
      issue(2, 9, ['release:ready'], {state: 'closed'}),
      issue(3, 10, ['release:ready']),
    ]);
    assert.equal(plan.ship.length, 0);
    assert.equal(plan.move.length, 0);
  });

  test('refuses a version with no milestone, or a pre-release version', () => {
    assert.throws(() => planCloseOut('0.99.0', milestones, []), /No milestone named v0.99.0/);
    assert.throws(() => planCloseOut('0.14.0-beta.1', milestones, []), /Not a release version/);
  });
});

test('shipped issues swap every status label for status:shipped and drop release:ready', () => {
  assert.deepEqual(shippedLabels([{name: 'status:in-progress'}, {name: 'release:ready'}, {name: 'area:dns'}, {name: 'roadmap:public'}]),
    ['area:dns', 'roadmap:public', 'status:shipped']);
});

test('the release workflow closes out issues after the GitHub release is published', () => {
  const workflow = readFileSync(new URL('../.github/workflows/release.yml', import.meta.url), 'utf8');
  const job = workflow.split('close-out-issues:')[1]?.split(/\n  [a-z-]+:\n/)[0] ?? '';
  assert.match(job, /needs: release/);
  assert.match(job, /issues: write/);
  assert.match(job, /node scripts\/release-issues\.mjs/);
});
