// Run with: node --test packages/docusaurus-theme-step-guide/test/steps.test.mjs
// The page logic is plain TypeScript with no DOM imports, so Node can run it directly; the elements are small fakes.
import assert from 'node:assert/strict';
import {test} from 'node:test';
import {buildSteps, findStepForHash, showEverything, showOnly} from '../theme/StepGuide/steps.ts';

function element(tagName, {id = '', text = '', hidden = false, children = []} = {}) {
  const node = {
    tagName,
    id,
    textContent: text,
    style: {display: ''},
    hasAttribute: (name) => hidden && name === 'data-step-guide-hidden',
    contains: (other) => other === node || children.some((child) => child === other || child.contains?.(other)),
  };
  return node;
}

const article = (...nodes) => ({children: nodes});

test('splits at h2 and h3, keeping what comes before the first heading as an overview', () => {
  const steps = buildSteps(
    article(
      element('H1', {text: 'Guide'}),
      element('P'),
      element('H2', {id: 'one', text: 'One'}),
      element('P'),
      element('H3', {id: 'one-a', text: 'One A'}),
      element('H2', {id: 'two', text: 'Two'}),
    ),
    [2, 3],
    'Overview',
  );
  assert.deepEqual(
    steps.map((step) => [step.id, step.title, step.isSubStep, step.nodes.length]),
    [
      [null, 'Overview', false, 1],
      ['one', 'One', false, 2],
      ['one-a', 'One A', true, 1],
      ['two', 'Two', false, 1],
    ],
  );
});

test('the page title and header are never part of a step', () => {
  const steps = buildSteps(article(element('HEADER'), element('H1'), element('H2', {id: 'a', text: 'A'})), [2, 3], 'Overview');
  assert.equal(steps.length, 1);
  assert.equal(steps[0].id, 'a');
});

test('a page that opens on a heading has no overview step', () => {
  const steps = buildSteps(article(element('H2', {id: 'a', text: 'A'}), element('P')), [2, 3], 'Overview');
  assert.deepEqual(steps.map((step) => step.id), ['a']);
});

test('only the configured heading levels start steps', () => {
  const steps = buildSteps(
    article(element('H2', {id: 'a', text: 'A'}), element('H3', {id: 'b', text: 'B'}), element('H4', {id: 'c', text: 'C'})),
    [2],
    'Overview',
  );
  assert.deepEqual(steps.map((step) => step.id), ['a']);
  assert.equal(steps[0].nodes.length, 3);
});

test('sub-steps are the levels below the highest one that starts steps', () => {
  const steps = buildSteps(article(element('H3', {id: 'a', text: 'A'}), element('H4', {id: 'b', text: 'B'})), [3, 4], 'Overview');
  assert.deepEqual(steps.map((step) => step.isSubStep), [false, true]);
});

test('the zero width space from heading anchors is stripped from titles', () => {
  const [step] = buildSteps(article(element('H2', {id: 'a', text: '​Title '})), [2], 'Overview');
  assert.equal(step.title, 'Title');
});

test('shows one step at a time, and keeps whole-guide-only elements hidden', () => {
  const checklist = element('DIV', {hidden: true});
  const steps = buildSteps(article(element('P'), checklist, element('H2', {id: 'a', text: 'A'}), element('P')), [2], 'Overview');
  showOnly(steps, 0);
  assert.equal(steps[0].nodes[0].style.display, '');
  assert.equal(checklist.style.display, 'none');
  assert.equal(steps[1].nodes[0].style.display, 'none');
  showEverything(steps);
  assert.equal(checklist.style.display, '');
  assert.equal(steps[1].nodes[0].style.display, '');
});

test('finds the step a hash points into, including an element inside a step', () => {
  const inner = element('SPAN', {id: 'inner'});
  const wrapper = element('DIV', {children: [inner]});
  const steps = buildSteps(article(element('H2', {id: 'a', text: 'A'}), element('H2', {id: 'b', text: 'B'}), wrapper), [2], 'Overview');
  const lookup = (id) => (id === 'inner' ? inner : null);
  assert.equal(findStepForHash(steps, '#a', lookup), 0);
  assert.equal(findStepForHash(steps, '#b', lookup), 1);
  assert.equal(findStepForHash(steps, '#inner', lookup), 1);
  assert.equal(findStepForHash(steps, '#missing', lookup), -1);
  assert.equal(findStepForHash(steps, '', lookup), -1);
});
