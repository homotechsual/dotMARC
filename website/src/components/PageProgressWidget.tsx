import React from 'react';
import {useChecklistProgress} from './ChecklistProgressContext';
import styles from './PageProgressWidget.module.css';

/** Floating bottom-right summary of every <Checklist/> currently mounted on the page - rendered
 * once, site-wide, by src/theme/Root.tsx. Renders nothing on pages with no checklists. */
export default function PageProgressWidget(): React.ReactElement | null {
  const progress = useChecklistProgress();

  if (!progress || !progress.hasChecklists || progress.totals.total === 0) {
    return null;
  }

  const {completed, total} = progress.totals;
  const done = completed === total;
  const percent = Math.round((completed / total) * 100);

  return (
    <div className={done ? `${styles.widget} ${styles.widgetDone}` : styles.widget} role="status">
      <div className={styles.ring} style={{'--percent': `${percent}%`} as React.CSSProperties}>
        <span className={styles.ringLabel}>{done ? '✓' : `${percent}%`}</span>
      </div>
      <span className={styles.text}>{done ? 'Page complete!' : `${completed} of ${total} tasks complete`}</span>
    </div>
  );
}
