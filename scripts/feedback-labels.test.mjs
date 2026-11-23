import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {test} from 'node:test';
import {CHANGE_TYPES, PRODUCT_AREAS} from '../website/src/plugins/featureRequests/taxonomy.ts';
import {formLabels, labelsToSync} from './feedback-labels.mjs';

const body = '### Change type\n\nBug fix\n\n### Product areas\n\nDNS & provider push, SPF & DKIM\n\n### Request description\n\nSomething needs fixing.';

test('GitHub form selections map to allowlisted labels', () => {
  assert.deepEqual(formLabels(body), ['type:bug', 'area:dns', 'area:spf-dkim']);
  assert.equal(formLabels('An ordinary unstructured issue'), null);
  assert.deepEqual(formLabels(body.replaceAll('\n', '\r\n')), ['type:bug', 'area:dns', 'area:spf-dkim']);
  for (const changed of [body.replace('Bug fix', 'arbitrary-label'), body.replace('SPF & DKIM', 'unknown'),
    body.replace('SPF & DKIM', 'DNS & provider push')]) {
    assert.throws(() => formLabels(changed), /Invalid/);
  }
});

test('description-only edits preserve maintainer label corrections', () => {
  assert.equal(labelsToSync({action: 'edited', changes: {body: {from: body}}}, `${body}\nMore prose.`), null);
  assert.equal(labelsToSync({action: 'edited', changes: {title: {from: 'Old title'}}}, body), null);
  assert.deepEqual(labelsToSync({action: 'opened'}, body), ['type:bug', 'area:dns', 'area:spf-dkim']);
  assert.deepEqual(labelsToSync({action: 'edited', changes: {body: {from: body}}}, body.replace('Bug fix', 'Enhancement')),
    ['type:enhancement', 'area:dns', 'area:spf-dkim']);
});

test('GitHub form dropdowns stay aligned with the shared taxonomy', () => {
  const form = readFileSync(new URL('../.github/ISSUE_TEMPLATE/feature-request.yml', import.meta.url), 'utf8');
  const typeOptions = form.split('      label: Change type')[1].split('    validations:')[0];
  const areaOptions = form.split('      label: Product areas')[1].split('    validations:')[0];
  const options = text => [...text.matchAll(/^        - (.+)\r?$/gm)].map(match => match[1].trim());
  assert.deepEqual(options(typeOptions), CHANGE_TYPES.map(type => type.name));
  assert.deepEqual(options(areaOptions), PRODUCT_AREAS.map(area => area.name));
  assert.match(areaOptions, /multiple: true/);
});

test('reviewed backfill manifest uses valid classifications', () => {
  const manifest = JSON.parse(readFileSync(new URL('./feedback-classifications.json', import.meta.url), 'utf8'));
  assert.equal(Object.keys(manifest).length, 58);
  for (const [number, item] of Object.entries(manifest)) {
    assert.match(number, /^[1-9]\d*$/);
    assert.ok(CHANGE_TYPES.some(type => type.id === item.type), number);
    assert.ok(item.areas.length > 0, number);
    assert.equal(new Set(item.areas).size, item.areas.length, number);
    assert.ok(item.areas.every(id => PRODUCT_AREAS.some(area => area.id === id)), number);
  }
});
