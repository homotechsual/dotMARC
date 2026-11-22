export const STATUSES = ['triage', 'considering', 'planned', 'in-progress', 'shipped'];

function version(title) {
  return /^v?(\d+\.\d+\.\d+)(?:\s|$)/.exec(title)?.[1] ?? null;
}

export function buildRoadmap(issues, milestones, releases, now = new Date().toISOString()) {
  const published = new Map(releases.filter(release => !release.draft && !release.prerelease)
    .map(release => [version(release.tag_name), release]).filter(([key]) => key));
  const items = issues.filter(issue => !issue.pull_request && issue.labels.some(label => label.name === 'roadmap:public'))
    .filter(issue => !(issue.state === 'closed' && issue.state_reason !== 'completed'))
    .filter(issue => !issue.labels.some(label => ['duplicate', 'invalid', 'wontfix'].includes(label.name)))
    .map(issue => {
      const labels = issue.labels.map(label => label.name);
      const statuses = STATUSES.filter(status => labels.includes(`status:${status}`));
      const release = issue.milestone ? published.get(version(issue.milestone.title)) : null;
      const completed = issue.state === 'closed' && issue.state_reason === 'completed';
      let status = statuses.length === 1 ? statuses[0] : 'triage';
      if (completed) status = release ? 'shipped' : 'in-progress';
      else if (status === 'shipped') status = 'in-progress';
      return {
        number: issue.number, title: issue.title, url: issue.html_url,
        status, milestone: issue.milestone?.number ?? null,
        completed, awaitingRelease: completed && !release,
        statusWarning: !completed && statuses.length !== 1,
      };
    }).sort((a, b) => b.number - a.number);
  const plans = milestones.filter(milestone => version(milestone.title))
    .filter(milestone => milestone.state === 'open' || published.has(version(milestone.title)))
    .map(milestone => {
      const release = published.get(version(milestone.title));
      return {
        number: milestone.number, title: milestone.title,
        description: milestone.description ?? '', url: milestone.html_url,
        dueOn: release ? null : milestone.due_on,
        released: Boolean(release), releaseUrl: release?.html_url ?? null,
        publishedAt: release?.published_at ?? null,
      };
    }).sort((a, b) => {
      if (a.released !== b.released) return a.released ? 1 : -1;
      const av = version(a.title).split('.').map(Number);
      const bv = version(b.title).split('.').map(Number);
      for (let i = 0; i < 3; i++) if (av[i] !== bv[i]) return a.released ? bv[i] - av[i] : av[i] - bv[i];
      return a.number - b.number;
    });
  return {updatedAt: now, issues: items, milestones: plans};
}
