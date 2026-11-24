import {pathToFileURL} from 'node:url';
import {classifyTag} from './release-tags.mjs';

// Issues stay open, labelled status:in-progress, until their release is cut. Finished work also carries release:ready.
// When a release is published this closes the milestone's release:ready issues as shipped, moves every other open issue
// in it to the next milestone, and closes the milestone.
export const READY_LABEL = 'release:ready';
export const SHIPPED_LABEL = 'status:shipped';

function versionParts(title) {
  const match = /^v?(\d+)\.(\d+)\.(\d+)$/.exec(title?.trim() ?? '');
  return match ? match.slice(1).map(Number) : null;
}

/** A milestone title's version. Titles may carry a theme after the version ("v0.14.0 — Action centre"), as the roadmap
 * allows, so only the leading version counts. */
function milestoneVersion(title) {
  const match = /^v?(\d+)\.(\d+)\.(\d+)(?:\s|$)/.exec(title?.trim() ?? '');
  return match ? match.slice(1).map(Number) : null;
}

function compareVersions(left, right) {
  for (let index = 0; index < 3; index++) {
    if (left[index] !== right[index]) return left[index] - right[index];
  }
  return 0;
}

/** Works out what closing out a release does, without calling GitHub. `milestones` and `issues` are GitHub API objects;
 * `issues` are the milestone's open issues. Returns the milestone to close, the issues to ship, the issues to move and
 * where they go (an existing milestone, or a title to create when there is none). */
export function planCloseOut(version, milestones, issues) {
  const releaseParts = versionParts(version);
  if (!releaseParts) throw new Error(`Not a release version: ${version}.`);
  const title = `v${releaseParts.join('.')}`;
  const milestone = milestones.find(candidate => milestoneVersion(candidate.title) && compareVersions(milestoneVersion(candidate.title), releaseParts) === 0);
  if (!milestone) throw new Error(`No milestone named ${title}.`);

  const open = issues.filter(issue => !issue.pull_request && issue.state === 'open' && issue.milestone?.number === milestone.number);
  const ship = open.filter(issue => issue.labels.some(label => label.name === READY_LABEL));
  const move = open.filter(issue => !ship.includes(issue));

  const later = milestones
    .filter(candidate => candidate.state === 'open' && milestoneVersion(candidate.title) && compareVersions(milestoneVersion(candidate.title), releaseParts) > 0)
    .sort((left, right) => compareVersions(milestoneVersion(left.title), milestoneVersion(right.title)));
  const next = later[0] ?? null;
  const createTitle = next || move.length === 0 ? null : `v${releaseParts[0]}.${releaseParts[1] + 1}.0`;

  return {milestone, ship, move, next, createTitle};
}

/** The labels a shipped issue ends with: every status:* label replaced by status:shipped, and release:ready removed. */
export function shippedLabels(labels) {
  const names = labels.map(label => label.name).filter(name => !name.startsWith('status:') && name !== READY_LABEL);
  return [...names, SHIPPED_LABEL];
}

async function main() {
  const [, , version, option] = process.argv;
  const dryRun = option === '--dry-run';
  if (!version) throw new Error('Usage: node scripts/release-issues.mjs <version> [--dry-run]');
  if (classifyTag(`v${version.replace(/^v/, '')}`).kind !== 'release') {
    console.log(`${version} is a pre-release: issues are only closed out for releases.`);
    return;
  }

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
  const all = async path => {
    const items = [];
    for (let page = 1; ; page++) {
      const batch = await api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`);
      items.push(...batch);
      if (batch.length < 100) return items;
    }
  };

  const milestones = await all('milestones?state=all');
  const releaseParts = versionParts(version);
  const target = milestones.find(candidate => milestoneVersion(candidate.title)?.join('.') === releaseParts?.join('.'));
  const issues = target ? await all(`issues?milestone=${target.number}&state=open`) : [];
  const plan = planCloseOut(version, milestones, issues);
  const nextTitle = plan.next?.title ?? plan.createTitle;

  console.log(`Closing out ${plan.milestone.title}: ${plan.ship.length} shipped, ${plan.move.length} moved${nextTitle ? ` to ${nextTitle}` : ''}.`);
  for (const issue of plan.ship) console.log(`  ship #${issue.number} ${issue.title}`);
  for (const issue of plan.move) console.log(`  move #${issue.number} ${issue.title}`);
  if (dryRun) return;

  for (const issue of plan.ship) {
    await api(`issues/${issue.number}`, 'PATCH', {labels: shippedLabels(issue.labels)});
    await api(`issues/${issue.number}/comments`, 'POST', {body: `Shipped in ${plan.milestone.title}.`});
    await api(`issues/${issue.number}`, 'PATCH', {state: 'closed', state_reason: 'completed'});
  }

  if (plan.move.length > 0) {
    const next = plan.next ?? await api('milestones', 'POST', {title: plan.createTitle});
    for (const issue of plan.move) {
      await api(`issues/${issue.number}`, 'PATCH', {milestone: next.number});
      await api(`issues/${issue.number}/comments`, 'POST', {body: `Not finished for ${plan.milestone.title}, so moved to ${next.title}.`});
    }
  }

  await api(`milestones/${plan.milestone.number}`, 'PATCH', {state: 'closed'});
  console.log(`Closed milestone ${plan.milestone.title}.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
