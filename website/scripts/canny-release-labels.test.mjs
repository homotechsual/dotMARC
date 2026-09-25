// Run with: node --test website/scripts/canny-release-labels.test.mjs
import assert from 'node:assert/strict';
import {test} from 'node:test';
import {correctReleaseLine, releaseLabel, shouldMarkComplete, statusForNewPost} from './canny-release-labels.mjs';

const finished = {title: 'Add server logs', version: 'v0.7.0', status: 'complete'};
const planned = {title: 'Add a public API', version: 'v0.8.0', status: 'planned'};

test('a complete idea says "Released in" only once its version is released', () => {
  assert.equal(releaseLabel(finished, true), 'Released in');
  assert.equal(releaseLabel(finished, false), 'Target release');
  assert.equal(releaseLabel(finished, undefined), 'Target release');
  assert.equal(releaseLabel(finished, null), 'Target release');
});

test('an idea that is not complete always says "Target release"', () => {
  assert.equal(releaseLabel(planned, true), 'Target release');
});

test('a new post for an unreleased complete idea starts as in progress, not complete', () => {
  assert.equal(statusForNewPost(finished, false), 'in progress');
  assert.equal(statusForNewPost(finished, undefined), 'in progress');
  assert.equal(statusForNewPost(finished, true), 'complete');
  assert.equal(statusForNewPost(planned, false), 'planned');
});

test('an existing post is only marked complete once released, and never twice', () => {
  assert.equal(shouldMarkComplete(finished, 'planned', true), true);
  assert.equal(shouldMarkComplete(finished, 'planned', false), false);
  assert.equal(shouldMarkComplete(finished, 'planned', undefined), false);
  assert.equal(shouldMarkComplete(finished, 'complete', true), false);
  assert.equal(shouldMarkComplete(planned, 'planned', true), false);
});

test('"Target release" becomes "Released in" once the version is released', () => {
  const details = 'Target release: v0.7.0\n\nServer logs in the app.';
  assert.equal(correctReleaseLine(details, finished, true), 'Released in: v0.7.0\n\nServer logs in the app.');
});

test('a premature "Released in" goes back to "Target release" while the version is unreleased', () => {
  const details = 'Released in: v0.7.0\n\nServer logs in the app.';
  assert.equal(correctReleaseLine(details, finished, false), 'Target release: v0.7.0\n\nServer logs in the app.');
});

test('the line is left alone when release status is unknown', () => {
  assert.equal(correctReleaseLine('Released in: v0.7.0\n\nx', finished, undefined), null);
  assert.equal(correctReleaseLine('Released in: v0.7.0\n\nx', finished, null), null);
  assert.equal(correctReleaseLine('Target release: v0.7.0\n\nx', finished, null), null);
});

test('a genuine release line for an older version is never touched', () => {
  // The idea was released in v0.6.0 and the roadmap now (wrongly or not) says v0.7.0: not this function's call.
  assert.equal(correctReleaseLine('Released in: v0.6.0\n\nx', finished, false), null);
});

test('only the first line is edited, and only when it opens the description', () => {
  const details = 'Some other text\n\nReleased in: v0.7.0';
  assert.equal(correctReleaseLine(details, finished, false), null);
});

test('ideas that are not complete are never corrected', () => {
  assert.equal(correctReleaseLine('Target release: v0.8.0\n\nx', planned, true), null);
  assert.equal(correctReleaseLine('Released in: v0.8.0\n\nx', planned, false), null);
});

test('an already correct line needs no change', () => {
  assert.equal(correctReleaseLine('Released in: v0.7.0\n\nx', finished, true), null);
  assert.equal(correctReleaseLine('Target release: v0.7.0\n\nx', finished, false), null);
});
