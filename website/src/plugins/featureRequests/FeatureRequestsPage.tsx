import React, {useEffect, useRef, useState, type ReactNode} from 'react';
import Layout from '@theme/Layout';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import {readRoadmap, type Roadmap, type RoadmapIssue, type StatusDescriptions} from './roadmap';
import styles from './styles.module.css';
import {CHANGE_TYPES, PRODUCT_AREAS, isChangeType, type ChangeType, type ProductArea} from './taxonomy';

const REPO = 'https://github.com/homotechsual/dotMARC';
const REQUEST_URL = `${REPO}/issues/new?template=feature-request.yml`;
const STATUS_LABELS = {
  triage: 'Needs triage', considering: 'Considering', planned: 'Planned',
  'in-progress': 'In progress', shipped: 'Shipped',
} as const;

type TurnstileApi = {
  render: (element: HTMLElement, options: {
    sitekey: string; action: string; callback: (token: string) => void;
    'expired-callback': () => void; 'error-callback': () => void;
  }) => string;
  reset: (id: string) => void;
  remove: (id: string) => void;
};
declare global { interface Window { turnstile?: TurnstileApi; } }

let turnstileLoading: Promise<void> | undefined;
function loadTurnstile(): Promise<void> {
  if (window.turnstile) return Promise.resolve();
  if (!turnstileLoading) {
    turnstileLoading = new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
      script.async = true;
      script.onload = () => resolve();
      script.onerror = () => {
        script.remove();
        turnstileLoading = undefined;
        reject(new Error('The anti-spam check could not load. Please submit directly on GitHub.'));
      };
      document.head.appendChild(script);
    });
  }
  return turnstileLoading;
}

function IssueCard({issue, release, descriptions}: {issue: RoadmapIssue; release?: string; descriptions: StatusDescriptions}): ReactNode {
  // Finished work that is waiting for its release shows as "Ready for release", not "In progress", so it stands out. The
  // issue's status stays in progress on GitHub until the release workflow closes it.
  const ready = issue.awaitingRelease;
  const statusName = ready ? 'Ready for release' : Object.entries(STATUS_LABELS).find(([status]) => status === issue.status)?.[1];
  // The hover text is the GitHub label's description, so it is maintained in one place. The card is one link named by
  // its title, so the status and its meaning are also given to screen readers as the link's description.
  const statusDescription = ready ? descriptions.ready : descriptions[issue.status as keyof StatusDescriptions];
  const describedBy = `roadmap-issue-${issue.number}-status`;
  return <article className={styles.issue}>
    <a className={styles.issueLink} href={issue.url} aria-label={issue.title} aria-describedby={describedBy}>
    <span id={describedBy} className={styles.visuallyHidden}>
      {`${[statusName, statusDescription].filter((part): part is string => Boolean(part)).map(part => part.replace(/\.$/, '')).join('. ')}.`}
    </span>
    <div className={styles.issueMeta}>
      <span className={styles.issueNumber}>#{issue.number}</span>
      <span className={styles.changeType}>{CHANGE_TYPES.find(type => type.id === issue.changeType)?.name ?? 'Type not classified'}</span>
      <span className={styles.issueStatus} data-status={ready ? 'ready' : issue.status} title={statusDescription}>{statusName}</span>
    </div>
    <span className={styles.issueTitle}>{issue.title}</span>
    {issue.areas.length > 0 && <ul className={styles.areas} aria-label="Product areas">
      {issue.areas.map(id => <li key={id}>{PRODUCT_AREAS.find(area => area.id === id)?.name}</li>)}
    </ul>}
    {issue.classificationWarning && <small className={styles.warning}>Classification needs maintainer review</small>}
    {release && <small>Target: {release}</small>}
    {issue.statusWarning && <small className={styles.warning}>Status needs maintainer review</small>}
    </a>
  </article>;
}

function RequestForm({api, siteKey, issues}: {api: string; siteKey: string; issues: RoadmapIssue[]}): ReactNode {
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [useCase, setUseCase] = useState('');
  const [changeType, setChangeType] = useState<ChangeType>('feature');
  const [areas, setAreas] = useState<ProductArea[]>([]);
  const [consent, setConsent] = useState(false);
  const [githubUsername, setGithubUsername] = useState('');
  const [mentionConsent, setMentionConsent] = useState(false);
  const [token, setToken] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [receipt, setReceipt] = useState<{url: string; number: number} | null>(null);
  const container = useRef<HTMLDivElement>(null);
  const widget = useRef<string | null>(null);
  const requestId = useRef<string | null>(null);

  useEffect(() => {
    let disposed = false;
    loadTurnstile().then(() => {
      if (disposed || !container.current) return;
      if (!window.turnstile) throw new Error('The anti-spam check is unavailable.');
      widget.current = window.turnstile.render(container.current, {
        sitekey: siteKey, action: 'feature-request', callback: setToken,
        'expired-callback': () => setToken(''),
        'error-callback': () => { setToken(''); setError('The anti-spam check failed. Please reload or submit on GitHub.'); },
      });
    }).catch((failure: unknown) => { if (!disposed) setError(failure instanceof Error ? failure.message : 'Anti-spam check failed.'); });
    return () => {
      disposed = true;
      if (widget.current && window.turnstile) window.turnstile.remove(widget.current);
      widget.current = null;
    };
  }, [siteKey]);

  const terms = title.toLowerCase().split(/\s+/).filter(term => term.length > 3);
  const suggestions = terms.length ? issues.filter(issue => terms.some(term => issue.title.toLowerCase().includes(term))).slice(0, 4) : [];

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true); setError('');
    requestId.current ??= crypto.randomUUID();
    try {
      const response = await fetch(`${api}/requests`, {
        method: 'POST', headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({title, description, useCase, changeType, areas, githubUsername, mentionConsent, publicConsent: consent, turnstileToken: token, requestId: requestId.current}),
        signal: AbortSignal.timeout(30000),
      });
      const data: unknown = await response.json();
      if (!response.ok) {
        const message = data && typeof data === 'object' && 'error' in data && typeof data.error === 'string'
          ? data.error : 'The request could not be submitted. Check GitHub before retrying.';
        throw new Error(message);
      }
      if (!data || typeof data !== 'object' || !('issue' in data) || !data.issue || typeof data.issue !== 'object'
        || !('number' in data.issue) || typeof data.issue.number !== 'number'
        || !('url' in data.issue) || typeof data.issue.url !== 'string'
        || !data.issue.url.startsWith(`${REPO}/issues/`)) throw new Error('Invalid confirmation. Check GitHub before retrying.');
      setReceipt({number: data.issue.number, url: data.issue.url});
    } catch (failure: unknown) {
      setError(failure instanceof Error ? failure.message : 'Submission failed. Check GitHub before retrying.');
    } finally {
      setBusy(false); setToken('');
      if (widget.current && window.turnstile) window.turnstile.reset(widget.current);
    }
  }

  if (receipt) return <div className={styles.receipt} role="status">
    <h3>Your request is on GitHub</h3>
    <p><a className={styles.primaryAction} href={receipt.url}>#{receipt.number} on GitHub</a></p>
    <p>A maintainer will triage it before adding it to the public roadmap.</p>
    <p>For comments and progress updates, open the issue while signed in to GitHub and select <strong>Subscribe</strong>.
      GitHub controls delivery through your notification settings. A mention alone does not guarantee ongoing updates.</p>
  </div>;
  return <form onSubmit={submit} className={styles.form}>
    <p>Submissions become public GitHub issues, posted by the dotMARC app. Do not include credentials, client details or other sensitive information. No email address is collected.</p>
    <fieldset disabled={busy}>
      <label htmlFor="request-type">Change type</label>
      <select id="request-type" value={changeType} onChange={event => { if (isChangeType(event.target.value)) setChangeType(event.target.value); }}>
        {CHANGE_TYPES.map(type => <option key={type.id} value={type.id}>{type.name}</option>)}
      </select>
      <fieldset className={styles.areaChoices}>
        <legend>Product areas (choose at least one)</legend>
        {PRODUCT_AREAS.map(area => <label key={area.id} className={styles.consent}>
          <input type="checkbox" checked={areas.includes(area.id)} onChange={event => setAreas(previous => event.target.checked
            ? [...previous, area.id] : previous.filter(id => id !== area.id))} />{area.name}
        </label>)}
      </fieldset>
      <label htmlFor="request-title">Short title</label>
      <input id="request-title" required minLength={8} maxLength={160} value={title} onChange={event => setTitle(event.target.value)} />
      {suggestions.length > 0 && <aside aria-label="Possibly related requests">
        <strong>Already requested?</strong>
        <ul>{suggestions.map(issue => <li key={issue.number}><a href={issue.url}>{issue.title}</a></li>)}</ul>
      </aside>}
      <label htmlFor="request-description">What would you like to change?</label>
      <textarea id="request-description" required minLength={20} maxLength={4000} rows={5} value={description} onChange={event => setDescription(event.target.value)}
        placeholder={changeType === 'bug' ? 'Include steps to reproduce, expected and actual behaviour, and your dotMARC version. Do not include secrets.' : 'Describe the capability or improvement you would like.'} />
      <label htmlFor="request-use-case">What would this help you do?</label>
      <textarea id="request-use-case" required minLength={10} maxLength={2000} rows={3} value={useCase} onChange={event => setUseCase(event.target.value)} />
      <label htmlFor="request-github">GitHub username (optional)</label>
      <input id="request-github" aria-describedby="request-github-help" maxLength={40} placeholder="Your GitHub username"
        value={githubUsername} onChange={event => { setGithubUsername(event.target.value); setMentionConsent(false); }} />
      <p id="request-github-help" className={styles.updated}>Want to follow this request? We can mention your account on the issue.
        This username is public and not verified. Open the issue after submitting and select Subscribe for ongoing updates.</p>
      {githubUsername.trim() && <label className={styles.consent}>
        <input type="checkbox" required checked={mentionConsent} onChange={event => setMentionConsent(event.target.checked)} />
        I consent to my GitHub username being published and mentioned on this issue.
      </label>}
      <label className={styles.consent}>
        <input type="checkbox" required checked={consent} onChange={event => setConsent(event.target.checked)} />
        I understand this request will be public.
      </label>
      <div ref={container} />
      <button type="submit" disabled={busy || !token || !consent || !areas.length || Boolean(githubUsername.trim() && !mentionConsent)}>{busy ? 'Submitting…' : 'Submit request'}</button>
    </fieldset>
    {error && <p role="alert" className={styles.error}>{error}</p>}
  </form>;
}

export default function FeatureRequests(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  const apiValue = siteConfig.customFields?.feedbackApiUrl;
  const keyValue = siteConfig.customFields?.turnstileSiteKey;
  const api = typeof apiValue === 'string' ? apiValue.replace(/\/$/, '') : '';
  const siteKey = typeof keyValue === 'string' ? keyValue : '';
  const [data, setData] = useState<Roadmap | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [attempt, setAttempt] = useState(0);
  const [view, setView] = useState<'board' | 'releases'>('releases');
  const [search, setSearch] = useState('');
  const [areaFilter, setAreaFilter] = useState('');
  const [typeFilter, setTypeFilter] = useState('');
  const [showShipped, setShowShipped] = useState(false);
  const [requestOpen, setRequestOpen] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError('');
    fetch(`${api}/roadmap`, {signal: AbortSignal.any([controller.signal, AbortSignal.timeout(20000)])})
      .then(async response => {
        const body: unknown = await response.json();
        if (!response.ok) throw new Error('The roadmap is temporarily unavailable. You can still browse or submit on GitHub.');
        return readRoadmap(body);
      }).then(setData).catch((failure: unknown) => {
        if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Unable to load roadmap.');
      }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [api, attempt]);

  const filtered = data?.issues.filter(issue => issue.title.toLowerCase().includes(search.toLowerCase())
    && (!typeFilter || (typeFilter === 'unclassified' ? issue.changeType === null : issue.changeType === typeFilter))
    && (!areaFilter || (areaFilter === 'unclassified' ? !issue.areas.length : issue.areas.some(area => area === areaFilter)))) ?? [];
  const upcoming = data?.milestones.filter(plan => !plan.released) ?? [];
  const shipped = data?.milestones.filter(plan => plan.released) ?? [];
  const matches = (plan: {number: number}) => (!search && !areaFilter && !typeFilter) || filtered.some(issue => issue.milestone === plan.number);
  function ReleaseCard({plan}: {plan: NonNullable<typeof data>['milestones'][number]}): ReactNode {
    const items = filtered.filter(issue => issue.milestone === plan.number);
    const all = data?.issues.filter(issue => issue.milestone === plan.number) ?? [];
    const complete = all.filter(issue => issue.completed).length;
    return <section className={styles.release}>
      <div className={styles.releaseHeading}><div><p className={styles.eyebrow}>{plan.released ? 'Release archive' : 'Coming next'}</p><h2><a href={plan.url}>{plan.title}</a></h2></div>
        <span className={styles.badge}>{plan.released ? 'Released' : 'Planned'}</span></div>
      {plan.released && plan.releaseUrl && <p><a href={plan.releaseUrl}>View published release</a></p>}
      {!plan.released && <p className={styles.updated}>{plan.dueOn ? `Target date: ${new Date(plan.dueOn).toLocaleDateString()}` : 'No release date committed.'}</p>}
      {plan.description && <p className={styles.description}>{plan.description}</p>}
      <div className={styles.releaseProgress}>
        <div><p>{complete} of {all.length} public items completed.</p><span>{all.length ? Math.round(100 * complete / all.length) : 0}%</span></div>
        <progress value={complete} max={Math.max(1, all.length)} aria-label={`${plan.title}: ${complete} of ${all.length} public items completed`} />
        <small>Item count, not an estimate of remaining effort.</small>
      </div>
      <div className={styles.issueList}>
        {items.length ? items.map(issue => <IssueCard key={issue.number} issue={issue} descriptions={data?.statusDescriptions ?? {}} />) : <p>No matching public items listed yet.</p>}
      </div>
    </section>;
  }
  return <Layout title="Features and Roadmap" description="Suggest features and enhancements, report bugs and see planned dotMARC releases, maintained on GitHub.">
    <main className={`container margin-vert--lg ${styles.main}`}>
      <header className={styles.header}>
        <div><p className={styles.eyebrow}>Help shape dotMARC</p><h1>Features &amp; roadmap</h1><p>See what is coming next, explore ideas and tell us what would help you.</p></div>
        <a className={styles.primaryAction} href="#request-feature" onClick={() => setRequestOpen(true)}>Submit feedback</a>
      </header>
      <p className={styles.updated}>Planned scope can change. Unscheduled ideas are not release commitments.</p>
      {loading && <p role="status">Loading roadmap…</p>}
      {error && <div role="alert" className={styles.error}>{error} <button onClick={() => setAttempt(value => value + 1)}>Retry</button></div>}
      {data && <>
        <p className={styles.updated}>Synced from GitHub: {new Date(data.updatedAt).toLocaleString()}
          {(data.stale || error) && <strong className={styles.warning}> · Showing cached data; GitHub refresh is unavailable.</strong>}
        </p>
        <nav className={styles.toolbar} aria-label="Roadmap views">
          <div className={styles.viewSwitch}>
            <button aria-pressed={view === 'releases'} onClick={() => setView('releases')}>Planned releases</button>
            <button aria-pressed={view === 'board'} onClick={() => setView('board')}>Board</button>
          </div>
          <div className={styles.featureSearch}><label htmlFor="roadmap-search">Find an item</label>
            <input id="roadmap-search" type="search" placeholder="Filter roadmap by title..." value={search} onChange={event => setSearch(event.target.value)} /></div>
        </nav>
        <div className={styles.filters} role="group" aria-label="Roadmap filters">
          <div className={styles.filterField}><label htmlFor="roadmap-area">Product area</label>
          <select id="roadmap-area" value={areaFilter} onChange={event => setAreaFilter(event.target.value)}>
            <option value="">All areas</option>{PRODUCT_AREAS.map(area => <option key={area.id} value={area.id}>{area.name}</option>)}
            <option value="unclassified">Not classified</option>
          </select></div>
          <div className={styles.filterField}><label htmlFor="roadmap-type">Change type</label>
          <select id="roadmap-type" value={typeFilter} onChange={event => setTypeFilter(event.target.value)}>
            <option value="">All types</option>{CHANGE_TYPES.map(type => <option key={type.id} value={type.id}>{type.name}</option>)}
            <option value="unclassified">Not classified</option>
          </select></div>
          {(search || areaFilter || typeFilter) && <button onClick={() => {setSearch(''); setAreaFilter(''); setTypeFilter('');}}>Clear filters</button>}
        </div>
        {view === 'board' ? <>
          <label className={styles.consent}><input type="checkbox" checked={showShipped} onChange={event => setShowShipped(event.target.checked)} />Include shipped items</label>
          <div className={styles.board}>
          {Object.entries(STATUS_LABELS).filter(([status]) => showShipped || status !== 'shipped').map(([status, label]) => {
            const items = filtered.filter(issue => issue.status === status);
            return <section key={status} className={styles.column}>
              <h2>{label} <small>({items.length})</small></h2>
              {items.length ? items.map(issue => <IssueCard key={issue.number} issue={issue} release={data.milestones.find(plan => plan.number === issue.milestone)?.title} descriptions={data.statusDescriptions} />) : <p className={styles.updated}>No matching items.</p>}
            </section>;
          })}
        </div></> : <>
          <div className={styles.releases}>
          {upcoming.filter(matches).map(plan => <ReleaseCard key={plan.number} plan={plan} />)}
          {!upcoming.filter(matches).length && <p>No matching planned releases.</p>}
          </div>
          <section className={styles.unscheduled}><div className={styles.releaseHeading}><h2>Unscheduled ideas</h2><span className={styles.updated}>Not yet assigned to a release</span></div>
            <div className={styles.ideaGrid}>
            {filtered.filter(issue => !data.milestones.some(plan => plan.number === issue.milestone)).map(issue => <IssueCard key={issue.number} issue={issue} descriptions={data.statusDescriptions} />)}
            {!filtered.some(issue => !data.milestones.some(plan => plan.number === issue.milestone)) && <p>No matching unscheduled ideas.</p>}
            </div>
          </section>
          {shipped.length > 0 && <details className={styles.history}>
            <summary>Previously shipped releases ({shipped.length})</summary>
            <div className={styles.releases}>{shipped.filter(matches).map(plan => <ReleaseCard key={plan.number} plan={plan} />)}</div>
          </details>}
        </>}
      </>}
      <section id="request-feature" className={styles.request}><h2>Have an idea or found a bug?</h2>
        <p>Search the public roadmap first, or <a href={`${REPO}/issues`}>browse all requests on GitHub</a>. New requests need maintainer triage before appearing here.</p>
        <p><a href={REQUEST_URL}>Submit directly on GitHub</a>, or use the form below.</p>
        <details open={requestOpen} onToggle={event => setRequestOpen(event.currentTarget.open)}>
          <summary>Submit a feature, enhancement or bug on this site</summary>
        {siteKey && data?.submissionsEnabled
          ? <RequestForm api={api} siteKey={siteKey} issues={data.issues} />
          : <p>Website submissions are currently unavailable. <a href={REQUEST_URL}>Submit your request on GitHub</a>.</p>}
        </details>
      </section>
    </main>
  </Layout>;
}
