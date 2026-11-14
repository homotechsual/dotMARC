import React, {useEffect, useState} from 'react';
import {translate} from '@docusaurus/Translate';
import {useWaypointOptions} from '../options';
import {readStorage, writeStorage} from '../steps';
import {HIDE_IN_STEPS_ATTRIBUTE, useWaypointTasks} from '../tasks';
import styles from './styles.module.css';

/** An item is plain text, or text plus the id of the heading of the step it belongs to. */
export type ChecklistItem = string | {text: string; step: string};

export interface ChecklistProps {
  /** Stable id for this checklist. It is part of the localStorage key, so keep it unique within the site and
   * unchanged once published: renaming it resets every reader's ticks. */
  id: string;
  items: ChecklistItem[];
  /** Called with the number ticked and the total each time they change, once the saved ticks have been read. Use it
   * to feed a page-wide progress display. */
  onProgress?: (completed: number, total: number) => void;
}

const textOf = (item: ChecklistItem): string => (typeof item === 'string' ? item : item.text);

/** A tick list whose ticks are saved in the reader's browser. In a Waypoint guide, when every item names the step it
 * belongs to, the list is hidden in one-step-at-a-time mode and each item is ticked at the end of its own step
 * instead. The same ticks back both views. */
export default function Checklist({id, items, onProgress}: ChecklistProps): React.ReactElement {
  const {storageNamespace} = useWaypointOptions();
  const storageKey = `${storageNamespace}-checklist:${id}`;
  const [checked, setChecked] = useState<boolean[]>(() => items.map(() => false));
  const [hydrated, setHydrated] = useState(false);
  const stepTasks = useWaypointTasks();
  const everyItemHasAStep = items.length > 0 && items.every((item) => typeof item !== 'string');

  // Read the saved ticks after the first render, so the server's markup (all unticked) matches it.
  useEffect(() => {
    const raw = readStorage(storageKey);
    if (raw) {
      try {
        const saved: unknown = JSON.parse(raw);
        if (Array.isArray(saved) && saved.length === items.length) {
          setChecked(saved.map(Boolean));
        }
      } catch {
        // A damaged value: start unticked.
      }
    }
    setHydrated(true);
    // Only on mount: `items` and the key are expected to stay the same for a rendered checklist.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const toggle = (index: number) => {
    const next = [...checked];
    next[index] = !next[index];
    setChecked(next);
    writeStorage(storageKey, JSON.stringify(next));
  };

  const completed = checked.filter(Boolean).length;

  useEffect(() => {
    if (hydrated) {
      onProgress?.(completed, items.length);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hydrated, completed, items.length]);

  useEffect(() => {
    if (!stepTasks || !everyItemHasAStep || !hydrated) {
      return undefined;
    }
    stepTasks.register(
      id,
      items.map((item, index) => ({
        stepId: (item as {step: string}).step,
        label: textOf(item),
        checked: checked[index],
        toggle: () => toggle(index),
      })),
    );
    return () => stepTasks.register(id, null);
    // `toggle` closes over `checked`, so it is refreshed whenever `checked` is.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stepTasks, everyItemHasAStep, hydrated, checked, id]);

  const percent = items.length === 0 ? 0 : Math.round((completed / items.length) * 100);

  return (
    <div className={styles.checklist} {...(everyItemHasAStep && stepTasks ? {[HIDE_IN_STEPS_ATTRIBUTE]: ''} : {})}>
      <div className={styles.header}>
        <span className={styles.progressText}>
          {translate(
            {id: 'theme.waypoint.checklist.progress', message: '{completed} of {total} complete'},
            {completed, total: items.length},
          )}
        </span>
        <div className={styles.progressBar} aria-hidden="true">
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
              <span className={hydrated && checked[index] ? styles.itemTextDone : undefined}>{textOf(item)}</span>
            </label>
          </li>
        ))}
      </ul>
    </div>
  );
}
