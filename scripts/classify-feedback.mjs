import {execFileSync} from 'node:child_process';
import {readFileSync} from 'node:fs';
import {CHANGE_TYPES, PRODUCT_AREAS, isChangeType, isProductArea} from '../website/src/plugins/featureRequests/taxonomy.ts';

const apply = process.argv.includes('--apply');
if (process.argv.slice(2).some(argument => argument !== '--apply')) throw new Error('Usage: node scripts/classify-feedback.mjs [--apply]');
const repository = 'homotechsual/dotMARC';
const gh = (...args) => execFileSync('gh', [...args, '--repo', repository], {encoding: 'utf8'});
const manifest = JSON.parse(readFileSync(new URL('./feedback-classifications.json', import.meta.url), 'utf8'));
const managed = [...CHANGE_TYPES.map(type => `type:${type.id}`), ...PRODUCT_AREAS.map(area => `area:${area.id}`)];
const issues = JSON.parse(gh('issue', 'list', '--state', 'all', '--label', 'roadmap:public', '--limit', '1000', '--json', 'number,title,labels'));
for (const [number, entry] of Object.entries(manifest)) {
  if (!isChangeType(entry.type) || !Array.isArray(entry.areas) || !entry.areas.length || !entry.areas.every(isProductArea)
    || new Set(entry.areas).size !== entry.areas.length) throw new Error(`Invalid classification for #${number}.`);
  if (!issues.some(issue => String(issue.number) === number)) throw new Error(`Manifest issue #${number} is no longer public; review before applying.`);
}
for (const issue of issues) {
  if (!manifest[issue.number]) throw new Error(`Public issue #${issue.number} needs a reviewed classification before applying.`);
}
if (apply) {
  for (const type of CHANGE_TYPES) gh('label', 'create', `type:${type.id}`, '--description', type.name, '--color', 'b11f4b', '--force');
  for (const area of PRODUCT_AREAS) gh('label', 'create', `area:${area.id}`, '--description', area.name, '--color', 'b11f4b', '--force');
}
let changed = 0;
for (const issue of issues) {
  const entry = manifest[issue.number];
  const desired = [`type:${entry.type}`, ...entry.areas.map(area => `area:${area}`)];
  const current = issue.labels.map(label => label.name);
  const add = desired.filter(label => !current.includes(label));
  const remove = current.filter(label => managed.includes(label) && !desired.includes(label));
  if (!add.length && !remove.length) continue;
  changed++;
  console.log(`#${issue.number} ${issue.title}: ${desired.join(', ')}`);
  if (apply) gh('issue', 'edit', String(issue.number),
    ...add.flatMap(label => ['--add-label', label]), ...remove.flatMap(label => ['--remove-label', label]));
}
console.log(`${apply ? 'Applied' : 'Previewed'} classifications: ${changed} changes across ${issues.length} public items.`);
