// Run with: node --test packages/docusaurus-theme-waypoint/test/steps.test.mjs
// The page logic is plain TypeScript with no DOM imports, so Node can run it directly; the elements are small fakes.
import assert from 'node:assert/strict';
import {test} from 'node:test';
import {buildSteps, findStepForHash, showEverything, showOnly} from '../theme/Waypoint/steps.ts';

function element(tagName, {id = '', text = '', hidden = false, className = '', children = []} = {}) {
  const node = {
    tagName,
    id,
    className,
    textContent: text,
    style: {display: ''},
    children,
    hasAttribute: (name) => hidden && name === 'data-waypoint-hidden',
    matches: (selector) => selector.split(',').some((part) => part.trim() === `.${className}`),
    contains: (other) => other === node || children.some((child) => child === other || child.contains?.(other)),
  };
  return node;
}

const article = (...nodes) => ({children: nodes});
const build = (roots, overrides = {}) =>
  buildSteps(Array.isArray(roots) ? roots : [roots], {headingLevels: [2, 3], overviewTitle: 'Overview', ...overrides});

test('splits at h2 and h3, keeping what comes before the first heading as an overview', () => {
  const steps = build(
    article(
      element('H1', {text: 'Guide'}),
      element('P'),
      element('H2', {id: 'one', text: 'One'}),
      element('P'),
      element('H3', {id: 'one-a', text: 'One A'}),
      element('H2', {id: 'two', text: 'Two'}),
    ),
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
  const steps = build(article(element('HEADER'), element('H1'), element('H2', {id: 'a', text: 'A'})));
  assert.equal(steps.length, 1);
  assert.equal(steps[0].id, 'a');
});

test('a page that opens on a heading has no overview step', () => {
  const steps = build(article(element('H2', {id: 'a', text: 'A'}), element('P')));
  assert.deepEqual(steps.map((step) => step.id), ['a']);
});

test('only the configured heading levels start steps', () => {
  const steps = build(
    article(element('H2', {id: 'a', text: 'A'}), element('H3', {id: 'b', text: 'B'}), element('H4', {id: 'c', text: 'C'})),
    {headingLevels: [2]},
  );
  assert.deepEqual(steps.map((step) => step.id), ['a']);
  assert.equal(steps[0].nodes.length, 3);
});

test('sub-steps are the levels below the highest one that starts steps', () => {
  const steps = build(article(element('H3', {id: 'a', text: 'A'}), element('H4', {id: 'b', text: 'B'})), {
    headingLevels: [3, 4],
  });
  assert.deepEqual(steps.map((step) => step.isSubStep), [false, true]);
});

test('the zero width space from heading anchors is stripped from titles', () => {
  const [step] = build(article(element('H2', {id: 'a', text: '​Title '})), {headingLevels: [2]});
  assert.equal(step.title, 'Title');
});

test('headings inside a wrapper do not start steps unless the wrapper is named', () => {
  const wrapper = element('DIV', {className: 'guide', children: [element('H2', {id: 'a', text: 'A'}), element('P')]});
  const steps = build(article(element('P'), wrapper));
  assert.deepEqual(steps.map((step) => step.id), [null]);
  assert.equal(steps[0].nodes.length, 2);
});

test('a named wrapper is looked inside, so its headings start steps in reading order', () => {
  const wrapper = element('DIV', {
    className: 'guide',
    children: [element('H2', {id: 'a', text: 'A'}), element('P'), element('H2', {id: 'b', text: 'B'})],
  });
  const steps = build(article(element('P'), wrapper, element('P')), {isWrapper: (node) => node.matches('.guide')});
  assert.deepEqual(
    steps.map((step) => [step.id, step.nodes.length]),
    [
      [null, 1],
      ['a', 2],
      ['b', 2],
    ],
  );
});

test('wrappers can be nested', () => {
  const inner = element('DIV', {className: 'inner', children: [element('H2', {id: 'a', text: 'A'})]});
  const outer = element('DIV', {className: 'outer', children: [inner]});
  const steps = build(article(outer), {isWrapper: (node) => node.matches('.outer, .inner')});
  assert.deepEqual(steps.map((step) => step.id), ['a']);
});

test('content spread over several article elements is one guide, in order', () => {
  const steps = build([
    article(element('P'), element('H2', {id: 'a', text: 'A'})),
    article(element('P'), element('H2', {id: 'b', text: 'B'}), element('P')),
  ]);
  assert.deepEqual(
    steps.map((step) => [step.id, step.nodes.length]),
    [
      [null, 1],
      ['a', 2],
      ['b', 2],
    ],
  );
});

test('shows one step at a time, and keeps whole-guide-only elements hidden', () => {
  const checklist = element('DIV', {hidden: true});
  const steps = build(article(element('P'), checklist, element('H2', {id: 'a', text: 'A'}), element('P')), {
    headingLevels: [2],
  });
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
  const steps = build(article(element('H2', {id: 'a', text: 'A'}), element('H2', {id: 'b', text: 'B'}), wrapper), {
    headingLevels: [2],
  });
  const lookup = (id) => (id === 'inner' ? inner : null);
  assert.equal(findStepForHash(steps, '#a', lookup), 0);
  assert.equal(findStepForHash(steps, '#b', lookup), 1);
  assert.equal(findStepForHash(steps, '#inner', lookup), 1);
  assert.equal(findStepForHash(steps, '#missing', lookup), -1);
  assert.equal(findStepForHash(steps, '', lookup), -1);
});
