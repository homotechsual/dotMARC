import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {useDoc} from '@docusaurus/plugin-content-docs/client';
import styles from './StepMode.module.css';

type ViewMode = 'all' | 'steps';

/** What the controls need to know about one step. */
interface StepInfo {
  /** The step heading's id, which is also its URL hash. `null` for the overview before the first heading. */
  id: string | null;
  title: string;
  /** True for a sub-step (an h3), false for a main step (an h2) or the overview. */
  isSubStep: boolean;
}

/** A step plus the page elements that belong to it. */
interface StepDom extends StepInfo {
  heading: HTMLElement | null;
  nodes: HTMLElement[];
}

const MODE_KEY = 'dotmarc-docs-view-mode';
const HINT_KEY = 'dotmarc-docs-step-mode-hint';
const stepKey = (permalink: string) => `dotmarc-docs-step:${permalink}`;

function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    // Storage blocked (private browsing and so on): the page still works, it just forgets.
    return null;
  }
}

function writeStorage(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Nothing to fall back to.
  }
}

/** Splits the rendered article into steps: one per h2 or h3, plus an overview for whatever comes before the first
 * heading. The page title (h1) and any header stay on screen in every step. */
function buildSteps(article: HTMLElement): StepDom[] {
  const steps: StepDom[] = [{id: null, title: 'Overview', isSubStep: false, heading: null, nodes: []}];
  for (const node of Array.from(article.children) as HTMLElement[]) {
    const tag = node.tagName;
    if (tag === 'H1' || tag === 'HEADER') {
      continue;
    }
    if (tag === 'H2' || tag === 'H3') {
      steps.push({
        id: node.id || null,
        title: (node.textContent ?? '').replace(/​/g, '').trim(),
        isSubStep: tag === 'H3',
        heading: node,
        nodes: [node],
      });
      continue;
    }
    steps[steps.length - 1].nodes.push(node);
  }
  // A page that opens straight on a heading has no overview to show.
  return steps[0].nodes.length === 0 ? steps.slice(1) : steps;
}

function showOnly(steps: StepDom[], current: number): void {
  steps.forEach((step, index) => {
    for (const node of step.nodes) {
      node.style.display = index === current ? '' : 'none';
    }
  });
}

function showEverything(steps: StepDom[]): void {
  for (const step of steps) {
    for (const node of step.nodes) {
      node.style.display = '';
    }
  }
}

function findStepForHash(steps: StepDom[], hash: string): number {
  if (!hash) {
    return -1;
  }
  let id = hash.replace(/^#/, '');
  try {
    id = decodeURIComponent(id);
  } catch {
    // Keep the raw value.
  }
  const target = document.getElementById(id);
  return steps.findIndex(
    (step) => step.id === id || (target !== null && step.nodes.some((node) => node === target || node.contains(target))),
  );
}

/** Lets a guide be read all at once (the default) or one step at a time. Wraps a doc's content and only does
 * anything for docs with `stepMode: true` in their front matter. In step mode every h2 and h3 starts a new step, and
 * the rest of the page is hidden until you get there, which suits readers who find a long page overwhelming. */
export default function StepMode({children}: {children: React.ReactNode}): React.ReactElement {
  const {frontMatter, metadata} = useDoc();
  const enabled = (frontMatter as {stepMode?: unknown}).stepMode === true;
  const permalink = metadata.permalink;

  const rootRef = useRef<HTMLDivElement>(null);
  const stepsRef = useRef<StepDom[]>([]);
  const [mode, setMode] = useState<ViewMode>('all');
  const [steps, setSteps] = useState<StepInfo[]>([]);
  const [current, setCurrent] = useState(0);
  const [announcement, setAnnouncement] = useState('');
  const [hintVisible, setHintVisible] = useState(false);

  // Remember the reader's choice between visits.
  useEffect(() => {
    if (enabled && readStorage(MODE_KEY) === 'steps') {
      setMode('steps');
    }
  }, [enabled]);

  // Tell people who haven't met step mode yet. Shown after the first paint, so the server's markup is unchanged, and
  // never again once it's dismissed or they've used the switch themselves.
  useEffect(() => {
    if (enabled && readStorage(HINT_KEY) === null && readStorage(MODE_KEY) === null) {
      setHintVisible(true);
    }
  }, [enabled]);

  const dismissHint = useCallback(() => {
    writeStorage(HINT_KEY, 'dismissed');
    setHintVisible(false);
  }, []);

  const reveal = useCallback((index: number, options: {updateHash: boolean; scroll: boolean; focus: boolean}) => {
    const dom = stepsRef.current;
    if (index < 0 || index >= dom.length) {
      return;
    }
    const step = dom[index];
    showOnly(dom, index);
    setCurrent(index);
    writeStorage(stepKey(permalink), step.id ?? '');
    setAnnouncement(`Step ${index + 1} of ${dom.length}: ${step.title}`);
    if (options.updateHash) {
      const url = `${window.location.pathname}${window.location.search}${step.id ? `#${step.id}` : ''}`;
      window.history.replaceState(window.history.state, '', url);
    }
    if (options.scroll) {
      // Wait a frame so the newly shown step has a layout to scroll to.
      requestAnimationFrame(() => {
        // Scroll to the top of the guide, not the step's heading: the controls and the title stay in view that way.
        rootRef.current?.scrollIntoView({block: 'start'});
        if (options.focus && step.heading) {
          step.heading.setAttribute('tabindex', '-1');
          step.heading.focus({preventScroll: true});
        }
      });
    }
  }, [permalink]);

  // Build or tear down the steps whenever the mode changes.
  useEffect(() => {
    const article = rootRef.current?.querySelector<HTMLElement>('.theme-doc-markdown');
    if (!enabled || !article) {
      return undefined;
    }
    if (mode === 'all') {
      showEverything(stepsRef.current);
      stepsRef.current = [];
      setSteps([]);
      return undefined;
    }
    const dom = buildSteps(article);
    stepsRef.current = dom;
    setSteps(dom.map(({id, title, isSubStep}) => ({id, title, isSubStep})));

    const fromHash = findStepForHash(dom, window.location.hash);
    const savedId = readStorage(stepKey(permalink));
    const fromSaved = savedId ? dom.findIndex((step) => step.id === savedId) : -1;
    const start = fromHash >= 0 ? fromHash : Math.max(fromSaved, 0);
    reveal(start, {updateHash: false, scroll: fromHash >= 0, focus: false});

    return () => {
      showEverything(stepsRef.current);
    };
  }, [enabled, mode, permalink, reveal]);

  // A link, or the table of contents, that points at a step which is hidden should take you there.
  useEffect(() => {
    if (!enabled || mode !== 'steps') {
      return undefined;
    }
    const onHashChange = () => {
      const index = findStepForHash(stepsRef.current, window.location.hash);
      if (index >= 0) {
        reveal(index, {updateHash: false, scroll: false, focus: false});
        requestAnimationFrame(() => rootRef.current?.scrollIntoView({block: 'start'}));
      }
    };
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, [enabled, mode, reveal]);

  const chooseMode = (next: ViewMode) => {
    writeStorage(MODE_KEY, next);
    setMode(next);
    dismissHint();
  };

  const labels = useMemo(() => steps.map((step) => step.title), [steps]);

  if (!enabled) {
    return <>{children}</>;
  }

  const inSteps = mode === 'steps' && steps.length > 0;
  const last = steps.length - 1;
  const percent = inSteps && last > 0 ? Math.round((current / last) * 100) : 0;

  return (
    <div ref={rootRef} className={styles.root}>
      <div className={`${styles.bar} ${inSteps ? styles.barSticky : ''}`}>
        <div className={styles.barRow}>
          <div className={styles.switchWrap}>
            <div className={styles.modeSwitch} role="group" aria-label="How to read this guide">
              <button
                type="button"
                className={`${styles.modeButton} ${mode === 'all' ? styles.modeButtonActive : ''}`}
                aria-pressed={mode === 'all'}
                onClick={() => chooseMode('all')}>
                Whole guide
              </button>
              <button
                type="button"
                className={`${styles.modeButton} ${mode === 'steps' ? styles.modeButtonActive : ''}`}
                aria-pressed={mode === 'steps'}
                onClick={() => chooseMode('steps')}>
                One step at a time
              </button>
            </div>
            {hintVisible && mode === 'all' && (
              <div className={styles.hint} role="note">
                <p className={styles.hintTitle}>New: read this one step at a time</p>
                <p className={styles.hintText}>
                  Long guide feeling like a lot? Switch to showing one step at a time, with a progress bar and Back and
                  Next buttons.
                </p>
                <div className={styles.hintActions}>
                  <button type="button" className="button button--primary button--sm" onClick={() => chooseMode('steps')}>
                    Try it
                  </button>
                  <button type="button" className="button button--link button--sm" onClick={dismissHint}>
                    No thanks
                  </button>
                </div>
              </div>
            )}
          </div>
          {inSteps && (
            <>
              <span className={styles.count}>
                Step {current + 1} of {steps.length}
              </span>
              <select
                className={styles.jump}
                aria-label="Jump to a step"
                value={current}
                onChange={(event) => reveal(Number(event.target.value), {updateHash: true, scroll: true, focus: true})}>
                {steps.map((step, index) => (
                  <option key={step.id ?? 'overview'} value={index}>
                    {step.isSubStep ? `   ${labels[index]}` : labels[index]}
                  </option>
                ))}
              </select>
            </>
          )}
        </div>
        {inSteps && (
          <div className={styles.progress} aria-hidden="true">
            <div className={styles.progressFill} style={{width: `${percent}%`}} />
          </div>
        )}
      </div>

      {children}

      {inSteps && (
        <nav className={styles.nav} aria-label="Step navigation">
          <button
            type="button"
            className="button button--secondary"
            disabled={current === 0}
            onClick={() => reveal(current - 1, {updateHash: true, scroll: true, focus: true})}>
            ← Back
          </button>
          <button
            type="button"
            className="button button--primary"
            disabled={current === last}
            onClick={() => reveal(current + 1, {updateHash: true, scroll: true, focus: true})}>
            {current === last ? 'End of the guide' : `Next: ${labels[current + 1]} →`}
          </button>
        </nav>
      )}

      <div className={styles.srOnly} role="status" aria-live="polite">
        {announcement}
      </div>
    </div>
  );
}
