import React, {useEffect, useRef, useState, type ReactNode} from 'react';
import Layout from '@theme/Layout';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import {useLocation} from '@docusaurus/router';
import {readRoadmap, type Roadmap, type RoadmapIssue} from './roadmap';
import styles from './styles.module.css';

const REPO = 'https://github.com/homotechsual/dotMARC';
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

function IssueCard({issue}: {issue: RoadmapIssue}): ReactNode {
  return <article className={styles.issue}>
    <a href={issue.url}>{issue.title}</a>
    <small>#{issue.number}{issue.awaitingRelease ? ' · Completed, awaiting release' : ''}</small>
    {issue.statusWarning && <small className={styles.warning}>Status needs maintainer review</small>}
  </article>;
}

function RequestForm({api, siteKey, issues}: {api: string; siteKey: string; issues: RoadmapIssue[]}): ReactNode {
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [useCase, setUseCase] = useState('');
  const [consent, setConsent] = useState(false);
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
        body: JSON.stringify({title, description, useCase, publicConsent: consent, turnstileToken: token, requestId: requestId.current}),
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

  if (receipt) return <p role="status">Request created: <a href={receipt.url}>#{receipt.number} on GitHub</a>. A maintainer will triage it before adding it to the public roadmap.</p>;
  return <form onSubmit={submit} className={styles.form}>
    <p>Submissions become public GitHub issues, posted by the dotMARC app. Do not include credentials, client details or other sensitive information. No email address is collected.</p>
    <fieldset disabled={busy}>
      <label htmlFor="request-title">Short title</label>
      <input id="request-title" required minLength={8} maxLength={160} value={title} onChange={event => setTitle(event.target.value)} />
      {suggestions.length > 0 && <aside aria-label="Possibly related requests">
        <strong>Already requested?</strong>
        <ul>{suggestions.map(issue => <li key={issue.number}><a href={issue.url}>{issue.title}</a></li>)}</ul>
      </aside>}
      <label htmlFor="request-description">What would you like to change?</label>
      <textarea id="request-description" required minLength={20} maxLength={4000} rows={5} value={description} onChange={event => setDescription(event.target.value)} />
      <label htmlFor="request-use-case">What would this help you do?</label>
      <textarea id="request-use-case" required minLength={10} maxLength={2000} rows={3} value={useCase} onChange={event => setUseCase(event.target.value)} />
      <label className={styles.consent}>
        <input type="checkbox" required checked={consent} onChange={event => setConsent(event.target.checked)} />
        I understand this request will be public.
      </label>
      <div ref={container} />
      <button type="submit" disabled={busy || !token || !consent}>{busy ? 'Submitting…' : 'Submit feature request'}</button>
    </fieldset>
    {error && <p role="alert" className={styles.error}>{error}</p>}
  </form>;
}

export default function FeatureRequests(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  const location = useLocation();
  const apiValue = siteConfig.customFields?.feedbackApiUrl;
  const keyValue = siteConfig.customFields?.turnstileSiteKey;
  const api = typeof apiValue === 'string' ? apiValue.replace(/\/$/, '') : '';
  const siteKey = typeof keyValue === 'string' ? keyValue : '';
  const [data, setData] = useState<Roadmap | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [attempt, setAttempt] = useState(0);
  const [view, setView] = useState<'board' | 'releases'>(location.pathname.includes('/roadmap') ? 'releases' : 'board');
  const [search, setSearch] = useState('');

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

  const filtered = data?.issues.filter(issue => issue.title.toLowerCase().includes(search.toLowerCase())) ?? [];
  return <Layout title="Feedback and Roadmap" description="Request features and see planned dotMARC releases, maintained on GitHub.">
    <main className={`container margin-vert--lg ${styles.main}`}>
      <header className={styles.header}>
        <div><h1>Feedback and roadmap</h1><p>Ideas, work in progress and planned releases. Managed openly on GitHub.</p></div>
        <a href={`${REPO}/issues/new`}>Submit directly on GitHub</a>
      </header>
      <p>Planned scope can change. An unscheduled idea is not a release commitment; dates appear only when a milestone has one.</p>
      {loading && <p role="status">Loading roadmap…</p>}
      {error && <div role="alert" className={styles.error}>{error} <button onClick={() => setAttempt(value => value + 1)}>Retry</button></div>}
      {data && <>
        <p className={styles.updated}>Last successful update: {new Date(data.updatedAt).toLocaleString()}
          {(data.stale || error) && <strong className={styles.warning}> · Showing cached data; GitHub refresh is unavailable.</strong>}
        </p>
        <nav className={styles.toolbar} aria-label="Roadmap views">
          <button aria-pressed={view === 'board'} onClick={() => setView('board')}>Board</button>
          <button aria-pressed={view === 'releases'} onClick={() => setView('releases')}>Planned releases</button>
          <label htmlFor="roadmap-search">Find a feature</label>
          <input id="roadmap-search" type="search" value={search} onChange={event => setSearch(event.target.value)} />
        </nav>
        {view === 'board' ? <div className={styles.board}>
          {Object.entries(STATUS_LABELS).map(([status, label]) => {
            const items = filtered.filter(issue => issue.status === status);
            return <section key={status} className={styles.column}>
              <h2>{label} <small>({items.length})</small></h2>
              {items.length ? items.map(issue => <IssueCard key={issue.number} issue={issue} />) : <p>No matching features.</p>}
            </section>;
          })}
        </div> : <div className={styles.releases}>
          {data.milestones.map(plan => {
            const items = filtered.filter(issue => issue.milestone === plan.number);
            const complete = items.filter(issue => issue.completed).length;
            return <section key={plan.number} className={styles.release}>
              <h2><a href={plan.url}>{plan.title}</a></h2>
              <span className={styles.badge}>{plan.released ? 'Released' : 'Planned'}</span>
              {plan.released && plan.releaseUrl && <p><a href={plan.releaseUrl}>View published release</a></p>}
              {!plan.released && <p>{plan.dueOn ? `Target date: ${new Date(plan.dueOn).toLocaleDateString()}` : 'No release date committed.'}</p>}
              <p className={styles.description}>{plan.description}</p>
              <p>{complete} of {items.length} {search ? 'matching ' : ''}public features completed. This is not an estimate of remaining effort.</p>
              <progress value={complete} max={Math.max(1, items.length)} aria-label={`${plan.title}: ${complete} of ${items.length} public features completed`} />
              {items.length ? items.map(issue => <IssueCard key={issue.number} issue={issue} />) : <p>No matching public features listed yet.</p>}
            </section>;
          })}
          <section className={styles.release}><h2>Unscheduled ideas</h2>
            {filtered.filter(issue => !data.milestones.some(plan => plan.number === issue.milestone)).map(issue => <IssueCard key={issue.number} issue={issue} />)}
            {!filtered.some(issue => !data.milestones.some(plan => plan.number === issue.milestone)) && <p>No matching unscheduled ideas.</p>}
          </section>
        </div>}
      </>}
      <section className={styles.request}><h2>Request a feature</h2>
        <p>Search the public roadmap first, or <a href={`${REPO}/issues`}>browse all requests on GitHub</a>. New requests need maintainer triage before appearing here.</p>
        {siteKey && data?.submissionsEnabled
          ? <RequestForm api={api} siteKey={siteKey} issues={data.issues} />
          : <p>Website submissions are currently unavailable. <a href={`${REPO}/issues/new`}>Submit your request on GitHub</a>.</p>}
      </section>
    </main>
  </Layout>;
}
