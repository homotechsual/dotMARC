import React, {useEffect, useState} from 'react';
import {useChecklistProgress} from './ChecklistProgressContext';
import {HIDE_IN_STEPS_ATTRIBUTE, useWaypointTasks} from '@theme/Waypoint/tasks';
import styles from './Checklist.module.css';

/** An item is plain text, or text plus the id of the heading of the step it belongs to. When every item names a step,
 * a guide's step-by-step mode hides this list and shows each item at the end of its own step instead. */
export type ChecklistItem = string | {text: string; step: string};

const textOf = (item: ChecklistItem): string => (typeof item === 'string' ? item : item.text);

export interface ChecklistProps {
  /** Stable id for this checklist - used as its localStorage key, so keep it unique per page and
   * unchanged once published (renaming it resets every reader's progress on that checklist). */
  id: string;
  items: ChecklistItem[];
}

/** A per-reader progress tracker for a setup procedure - "X of Y complete", persisted in the
 * browser's localStorage (per-visitor, never sent anywhere). Renders as an ordinary unchecked
 * list on the server/first paint to avoid a hydration mismatch, then syncs from localStorage once
 * mounted - see the `hydrated` gate below. */
export default function Checklist({id, items}: ChecklistProps): React.ReactElement {
  const storageKey = `dotmarc-docs-checklist:${id}`;
  const [checked, setChecked] = useState<boolean[]>(() => items.map(() => false));
  const [hydrated, setHydrated] = useState(false);
  const pageProgress = useChecklistProgress();

  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(storageKey);
      if (raw) {
        const saved = JSON.parse(raw);
        if (Array.isArray(saved) && saved.length === items.length) {
          setChecked(saved);
        }
      }
    } catch {
      // localStorage unavailable (private browsing, storage blocked, etc.) - stay unchecked.
    }
    setHydrated(true);
    // Only run once on mount - `items`/`storageKey` are expected to stay stable for a given
    // rendered checklist (see the `id` doc comment above).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const toggle = (index: number) => {
    const next = [...checked];
    next[index] = !next[index];
    setChecked(next);
    try {
      window.localStorage.setItem(storageKey, JSON.stringify(next));
    } catch {
      // Nothing to fall back to here - the toggle still works for this page view, it just won't
      // persist to the next one.
    }
  };

  const completedCount = checked.filter(Boolean).length;
  const percent = items.length === 0 ? 0 : Math.round((completedCount / items.length) * 100);

  useEffect(() => {
    if (!hydrated) {
      // Skip the pre-hydration render (always all-unchecked) so the page-wide widget doesn't
      // briefly report 0 complete before localStorage has been read.
      return undefined;
    }
    pageProgress?.registerChecklist(id, {completed: completedCount, total: items.length});
    return () => pageProgress?.unregisterChecklist(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hydrated, completedCount, items.length, id]);

  const stepRegistry = useWaypointTasks();
  const allInSteps = items.length > 0 && items.every((item) => typeof item !== 'string');

  useEffect(() => {
    if (!stepRegistry || !allInSteps || !hydrated) {
      return undefined;
    }
    stepRegistry.register(
      id,
      items.map((item, index) => ({
        stepId: (item as {step: string}).step,
        label: textOf(item),
        checked: checked[index],
        toggle: () => toggle(index),
      })),
    );
    return () => stepRegistry.register(id, null);
    // `toggle` closes over `checked`, so it is refreshed whenever `checked` is.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stepRegistry, allInSteps, hydrated, checked, id]);

  return (
    <div className={styles.checklist} {...(allInSteps && stepRegistry ? {[HIDE_IN_STEPS_ATTRIBUTE]: ''} : {})}>
      <div className={styles.header}>
        <span className={styles.progressText}>
          {completedCount} of {items.length} complete
        </span>
        <div className={styles.progressBar}>
          <div className={styles.progressBarFill} style={{width: `${percent}%`}} />
        </div>
      </div>
      <ul className={styles.list}>
        {items.map((item, index) => (
          // eslint-disable-next-line react/no-array-index-key
          <li key={index} className={styles.item}>
            <label className={styles.label}>
              <input
                type="checkbox"
                className={styles.checkbox}
                checked={hydrated ? checked[index] : false}
                onChange={() => toggle(index)}
              />
              <span className={checked[index] && hydrated ? styles.itemTextDone : styles.itemText}>
                {textOf(item)}
              </span>
            </label>
          </li>
        ))}
      </ul>
    </div>
  );
}
