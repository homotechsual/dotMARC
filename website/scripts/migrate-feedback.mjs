import {readFile} from 'node:fs/promises';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

const repository = 'homotechsual/dotMARC';
const apply = process.argv.includes('--apply');
const source = JSON.parse(await readFile(new URL('../data/legacy-canny-roadmap.json', import.meta.url), 'utf8'));
function api(path, method = 'GET', body) {
  const args = ['api', `repos/${repository}/${path}`, '--method', method];
  if (body) args.push('--input', '-');
  return JSON.parse(execFileSync('gh', args, {input: body ? JSON.stringify(body) : undefined, encoding: 'utf8'}));
}
function list(path) {
  const items = [];
  for (let page = 1; page <= 100; page++) {
    const batch = api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`);
    items.push(...batch);
    if (batch.length < 100) return items;
  }
  throw new Error('Pagination limit exceeded.');
}
const labels = [
  ['type:feature', 'Feature request'], ['roadmap:public', 'Approved public roadmap item'],
  ['status:triage', 'Needs maintainer triage'], ['status:considering', 'Under consideration'],
  ['status:planned', 'Planned work'], ['status:in-progress', 'Work in progress or awaiting release'],
  ['status:shipped', 'Completed work in a published release'],
];
const existingLabels = new Set(list('labels').map(label => label.name));
for (const [name, description] of labels) {
  if (!existingLabels.has(name)) {
    console.log(`${apply ? 'Create' : 'Would create'} label ${name}`);
    if (apply) api('labels', 'POST', {name, description, color: 'b11f4b'});
  }
}
const milestones = list('milestones?state=all');
const releases = new Set(list('releases').filter(release => !release.draft && !release.prerelease).map(release => release.tag_name));
const issues = list('issues?state=all').filter(issue => !issue.pull_request);
let created = 0;
for (const [index, item] of source.entries()) {
  const marker = `<!-- legacy-canny-roadmap:${index} -->`;
  if (issues.some(issue => issue.body?.includes(marker))) continue;
  const matching = issues.filter(issue => issue.title.trim().toLowerCase() === item.title.trim().toLowerCase());
  if (matching.length) throw new Error(`Existing issue matches "${item.title}" without migration marker. Reconcile it before applying.`);
  let milestone;
  if (item.version) {
    milestone = milestones.find(plan => plan.title === item.version);
    if (!milestone) {
      console.log(`${apply ? 'Create' : 'Would create'} milestone ${item.version}`);
      milestone = apply ? api('milestones', 'POST', {title: item.version}) : {title: item.version, number: null};
      milestones.push(milestone);
    }
  }
  const shipped = item.status === 'complete' && releases.has(item.version);
  const status = shipped ? 'shipped' : item.status === 'complete' || item.status === 'in progress'
    ? 'in-progress' : item.status === 'planned' ? 'planned' : 'considering';
  const body = [
    ...item.details,
    '---',
    'Migrated from the curated dotMARC Canny roadmap snapshot. Original wording is retained; votes, comments and subscriber identities were not imported.',
    `Source: \`website/data/legacy-canny-roadmap.json\`, entry ${index}. Original status: ${item.status}.`,
    ...(item.url ? [`Original request: ${item.url}`] : []),
    marker,
  ].join('\n\n');
  console.log(`${apply ? 'Create' : 'Would create'} ${item.title} (${status}, ${item.version ?? 'unscheduled'})`);
  if (apply) {
    const createdIssue = api('issues', 'POST', {
      title: item.title, body, labels: ['type:feature', 'roadmap:public', `status:${status}`],
      ...(milestone ? {milestone: milestone.number} : {}),
    });
    if (shipped) api(`issues/${createdIssue.number}`, 'PATCH', {state: 'closed', state_reason: 'completed'});
    console.log(createdIssue.html_url);
    issues.push({...createdIssue, body});
  }
  created++;
}
console.log(`${apply ? 'Migrated' : 'Would migrate'} ${created} requests from ${fileURLToPath(new URL('../data/legacy-canny-roadmap.json', import.meta.url))}.`);
