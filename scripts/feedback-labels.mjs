import {readFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
import {CHANGE_TYPES, PRODUCT_AREAS} from '../website/src/plugins/featureRequests/taxonomy.ts';

function section(body, heading) {
  const lines = body.split(/\r?\n/);
  const start = lines.indexOf(`### ${heading}`);
  if (start < 0) return null;
  const end = lines.findIndex((line, index) => index > start && /^#{1,3} /.test(line));
  return lines.slice(start + 1, end < 0 ? undefined : end).join('\n').trim();
}

export function formLabels(body) {
  const typeName = section(body, 'Change type');
  const areaNames = section(body, 'Product areas');
  if (typeName === null && areaNames === null) return null;
  const type = CHANGE_TYPES.find(type => type.name === typeName);
  const names = areaNames?.split(',').map(name => name.trim());
  if (!type || !names?.length || names.some(name => !PRODUCT_AREAS.some(area => area.name === name))
    || new Set(names).size !== names.length) throw new Error('Invalid change type or product areas in issue form.');
  return [`type:${type.id}`, ...PRODUCT_AREAS.filter(area => names.includes(area.name)).map(area => `area:${area.id}`)];
}

export function labelsToSync(event, currentBody) {
  const desired = formLabels(currentBody);
  if (!desired) return null;
  // Editing prose must not undo a maintainer's later label corrections.
  if (event.action === 'edited' && event.changes?.body?.from !== undefined) {
    let previous;
    try { previous = formLabels(event.changes.body.from); }
    catch { previous = null; } // Correcting invalid form fields is allowed.
    if (JSON.stringify(previous) === JSON.stringify(desired)) return null;
  } else if (event.action === 'edited') return null;
  return desired;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
  if (event.issue?.pull_request || !event.issue) throw new Error('Expected an issue event.');
  const repository = process.env.GITHUB_REPOSITORY;
  if (repository !== 'homotechsual/dotMARC') throw new Error('Unexpected repository.');
  const api = async (path, method = 'GET', body) => {
    const response = await fetch(`https://api.github.com/repos/${repository}/${path}`, {
      method, headers: {Authorization: `Bearer ${process.env.GITHUB_TOKEN}`, Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json'},
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000),
    });
    if (!response.ok) throw new Error(`GitHub ${method} ${path} failed (${response.status}).`);
    return response.status === 204 ? null : response.json();
  };
  const issuePath = `issues/${event.issue.number}`;
  const issue = await api(issuePath);
  const desired = labelsToSync(event, issue.body ?? '');
  if (desired) {
    const managed = [...CHANGE_TYPES.map(type => `type:${type.id}`), ...PRODUCT_AREAS.map(area => `area:${area.id}`)];
    for (const label of issue.labels.map(label => label.name).filter(label => managed.includes(label) && !desired.includes(label))) {
      await api(`${issuePath}/labels/${encodeURIComponent(label)}`, 'DELETE');
    }
    await api(`${issuePath}/labels`, 'POST', {labels: desired});
    console.log(`Updated classification labels for issue #${issue.number}.`);
  } else console.log('No classification changes to synchronise.');
}
