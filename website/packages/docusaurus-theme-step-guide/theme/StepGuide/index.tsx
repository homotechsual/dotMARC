import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {useDoc} from '@docusaurus/plugin-content-docs/client';
import {translate} from '@docusaurus/Translate';
import styles from './styles.module.css';
import {useStepGuideOptions} from './options';
import {
  buildSteps,
  findStepForHash,
  readStorage,
  showEverything,
  showOnly,
  writeStorage,
  type StepDom,
  type StepInfo,
} from './steps';
import {StepGuideTasksContext, type StepGuideTask, type StepGuideTaskRegistry} from './tasks';

type ViewMode = 'all' | 'steps';

/** Lets a guide be read all at once (the default) or one step at a time. Wraps a doc's content and only does
 * anything for docs with the configured front matter key set to true. In step mode every heading at the configured
 * levels starts a new step, and the rest of the page is hidden until you get there, which suits readers who find a
 * long page overwhelming. */
export default function StepGuide({children}: {children: React.ReactNode}): React.ReactElement {
  const {frontMatter, metadata} = useDoc();
  const options = useStepGuideOptions();
  const enabled = (frontMatter as Record<string, unknown>)[options.frontMatterKey] === true;
  const permalink = metadata.permalink;

  const modeKey = `${options.storageNamespace}-view-mode`;
  const hintKey = `${options.storageNamespace}-step-mode-hint`;
  const stepKey = `${options.storageNamespace}-step:${permalink}`;

  const rootRef = useRef<HTMLDivElement>(null);
  const stepsRef = useRef<StepDom[]>([]);
  const [mode, setMode] = useState<ViewMode>('all');
  const [steps, setSteps] = useState<StepInfo[]>([]);
  const [current, setCurrent] = useState(0);
  const [announcement, setAnnouncement] = useState('');
  const [hintVisible, setHintVisible] = useState(false);
  const [taskGroups, setTaskGroups] = useState<Record<string, StepGuideTask[]>>({});

  const registry = useMemo<StepGuideTaskRegistry>(
    () => ({
      register: (groupId, tasks) =>
        setTaskGroups((previous) => {
          const next = {...previous};
          if (tasks) {
            next[groupId] = tasks;
          } else {
            delete next[groupId];
          }
          return next;
        }),
    }),
    [],
  );

  // Remember the reader's choice between visits.
  useEffect(() => {
    if (enabled && readStorage(modeKey) === 'steps') {
      setMode('steps');
    }
  }, [enabled, modeKey]);

  // Tell people who haven't met step mode yet. Shown after the first paint, so the server's markup is unchanged, and
  // never again once it's dismissed or they've used the switch themselves.
  useEffect(() => {
    if (enabled && options.showIntroHint && readStorage(hintKey) === null && readStorage(modeKey) === null) {
      setHintVisible(true);
    }
  }, [enabled, options.showIntroHint, hintKey, modeKey]);

  const dismissHint = useCallback(() => {
    writeStorage(hintKey, 'dismissed');
    setHintVisible(false);
  }, [hintKey]);

  const reveal = useCallback(
    (index: number, revealOptions: {updateHash: boolean; scroll: boolean; focus: boolean}) => {
      const dom = stepsRef.current;
      if (index < 0 || index >= dom.length) {
        return;
      }
      const step = dom[index];
      showOnly(dom, index);
      setCurrent(index);
      writeStorage(stepKey, step.id ?? '');
      setAnnouncement(
        translate(
          {
            id: 'theme.stepGuide.announcement',
            message: 'Step {current} of {total}: {title}',
            description: 'Read out to screen reader users when the step changes',
          },
          {current: index + 1, total: dom.length, title: step.title},
        ),
      );
      if (revealOptions.updateHash) {
        const url = `${window.location.pathname}${window.location.search}${step.id ? `#${step.id}` : ''}`;
        window.history.replaceState(window.history.state, '', url);
      }
      if (revealOptions.scroll) {
        // Wait a frame so the newly shown step has a layout to scroll to.
        requestAnimationFrame(() => {
          // Scroll to the top of the guide, not the step's heading: the controls and the title stay in view that way.
          rootRef.current?.scrollIntoView({block: 'start'});
          if (revealOptions.focus && step.heading) {
            step.heading.setAttribute('tabindex', '-1');
            step.heading.focus({preventScroll: true});
          }
        });
      }
    },
    [stepKey],
  );

  // Build or tear down the steps whenever the mode changes.
  useEffect(() => {
    const articles = rootRef.current ? Array.from(rootRef.current.querySelectorAll<HTMLElement>(options.articleSelector)) : [];
    if (!enabled || articles.length === 0) {
      return undefined;
    }
    if (mode === 'all') {
      showEverything(stepsRef.current);
      stepsRef.current = [];
      setSteps([]);
      return undefined;
    }
    const overviewTitle = translate({
      id: 'theme.stepGuide.overview',
      message: 'Overview',
      description: 'The name of the first step, which holds the introduction before the first heading',
    });
    const wrapperSelector = options.wrapperSelectors.join(', ');
    const dom = buildSteps(articles, {
      headingLevels: options.headingLevels,
      overviewTitle,
      isWrapper: wrapperSelector ? (node) => node.matches(wrapperSelector) : undefined,
    });
    stepsRef.current = dom;
    setSteps(dom.map(({id, title, isSubStep}) => ({id, title, isSubStep})));

    const fromHash = findStepForHash(dom, window.location.hash, (id) => document.getElementById(id));
    const savedId = readStorage(stepKey);
    const fromSaved = savedId ? dom.findIndex((step) => step.id === savedId) : -1;
    const start = fromHash >= 0 ? fromHash : Math.max(fromSaved, 0);
    reveal(start, {updateHash: false, scroll: fromHash >= 0, focus: false});

    return () => {
      showEverything(stepsRef.current);
    };
  }, [enabled, mode, stepKey, options.headingLevels, options.articleSelector, options.wrapperSelectors, reveal]);

  // A link, or the table of contents, that points at a step which is hidden should take you there.
  useEffect(() => {
    if (!enabled || mode !== 'steps') {
      return undefined;
    }
    const onHashChange = () => {
      const index = findStepForHash(stepsRef.current, window.location.hash, (id) => document.getElementById(id));
      if (index >= 0) {
        reveal(index, {updateHash: false, scroll: false, focus: false});
        requestAnimationFrame(() => rootRef.current?.scrollIntoView({block: 'start'}));
      }
    };
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, [enabled, mode, reveal]);

  const chooseMode = (next: ViewMode) => {
    writeStorage(modeKey, next);
    setMode(next);
    dismissHint();
  };

  const labels = useMemo(() => steps.map((step) => step.title), [steps]);

  if (!enabled) {
    return <>{children}</>;
  }

  const tasks = Object.values(taskGroups).flat();
  const tasksFor = (stepId: string | null) => (stepId ? tasks.filter((task) => task.stepId === stepId) : []);
  const isDone = (stepId: string | null) => {
    const forStep = tasksFor(stepId);
    return forStep.length > 0 && forStep.every((task) => task.checked);
  };
  const currentTasks = tasksFor(steps[current]?.id ?? null);

  const inSteps = mode === 'steps' && steps.length > 0;
  const last = steps.length - 1;
  const percent = inSteps && last > 0 ? Math.round((current / last) * 100) : 0;

  const t = {
    groupLabel: translate({id: 'theme.stepGuide.switchLabel', message: 'How to read this guide'}),
    wholeGuide: translate({id: 'theme.stepGuide.wholeGuide', message: 'Whole guide'}),
    oneStep: translate({id: 'theme.stepGuide.oneStepAtATime', message: 'One step at a time'}),
    hintTitle: translate({id: 'theme.stepGuide.hint.title', message: 'New: read this one step at a time'}),
    hintText: translate({
      id: 'theme.stepGuide.hint.text',
      message:
        'Long guide feeling like a lot? Switch to showing one step at a time, with a progress bar and Back and Next buttons.',
    }),
    hintTry: translate({id: 'theme.stepGuide.hint.try', message: 'Try it'}),
    hintDismiss: translate({id: 'theme.stepGuide.hint.dismiss', message: 'No thanks'}),
    count: translate(
      {id: 'theme.stepGuide.count', message: 'Step {current} of {total}'},
      {current: current + 1, total: steps.length},
    ),
    jump: translate({id: 'theme.stepGuide.jump', message: 'Jump to a step'}),
    navLabel: translate({id: 'theme.stepGuide.navLabel', message: 'Step navigation'}),
    back: translate({id: 'theme.stepGuide.back', message: '← Back'}),
    end: translate({id: 'theme.stepGuide.end', message: 'End of the guide'}),
  };

  return (
    <StepGuideTasksContext.Provider value={registry}>
      <div ref={rootRef} className={styles.root}>
        <div className={`${styles.bar} ${inSteps ? styles.barSticky : ''}`}>
          <div className={styles.barRow}>
            <div className={styles.switchWrap}>
              <div className={styles.modeSwitch} role="group" aria-label={t.groupLabel}>
                <button
                  type="button"
                  className={`${styles.modeButton} ${mode === 'all' ? styles.modeButtonActive : ''}`}
                  aria-pressed={mode === 'all'}
                  onClick={() => chooseMode('all')}>
                  {t.wholeGuide}
                </button>
                <button
                  type="button"
                  className={`${styles.modeButton} ${mode === 'steps' ? styles.modeButtonActive : ''}`}
                  aria-pressed={mode === 'steps'}
                  onClick={() => chooseMode('steps')}>
                  {t.oneStep}
                </button>
              </div>
              {hintVisible && mode === 'all' && (
                <div className={styles.hint} role="note">
                  <p className={styles.hintTitle}>{t.hintTitle}</p>
                  <p className={styles.hintText}>{t.hintText}</p>
                  <div className={styles.hintActions}>
                    <button
                      type="button"
                      className="button button--primary button--sm"
                      onClick={() => chooseMode('steps')}>
                      {t.hintTry}
                    </button>
                    <button type="button" className="button button--link button--sm" onClick={dismissHint}>
                      {t.hintDismiss}
                    </button>
                  </div>
                </div>
              )}
            </div>
            {inSteps && (
              <>
                <span className={styles.count}>{t.count}</span>
                <select
                  className={styles.jump}
                  aria-label={t.jump}
                  value={current}
                  onChange={(event) => reveal(Number(event.target.value), {updateHash: true, scroll: true, focus: true})}>
                  {steps.map((step, index) => (
                    <option key={step.id ?? 'overview'} value={index}>
                      {`${isDone(step.id) ? '✓ ' : ''}${step.isSubStep ? '   ' : ''}${labels[index]}`}
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
          <>
            {currentTasks.length > 0 && (
              <div className={styles.done}>
                {currentTasks.map((task) => (
                  <label key={task.label} className={styles.doneLabel}>
                    <input type="checkbox" checked={task.checked} onChange={task.toggle} />
                    <span>
                      {translate({id: 'theme.stepGuide.done', message: 'Done: {task}'}, {task: task.label})}
                    </span>
                  </label>
                ))}
              </div>
            )}
            <nav className={styles.nav} aria-label={t.navLabel}>
              <button
                type="button"
                className="button button--secondary"
                disabled={current === 0}
                onClick={() => reveal(current - 1, {updateHash: true, scroll: true, focus: true})}>
                {t.back}
              </button>
              <button
                type="button"
                className="button button--primary"
                disabled={current === last}
                onClick={() => reveal(current + 1, {updateHash: true, scroll: true, focus: true})}>
                {current === last
                  ? t.end
                  : translate({id: 'theme.stepGuide.next', message: 'Next: {title} →'}, {title: labels[current + 1]})}
              </button>
            </nav>
          </>
        )}

        <div className={styles.srOnly} role="status" aria-live="polite">
          {announcement}
        </div>
      </div>
    </StepGuideTasksContext.Provider>
  );
}
