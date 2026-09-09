import React from 'react';
import {ChecklistProgressProvider} from '@site/src/components/ChecklistProgressContext';
import PageProgressWidget from '@site/src/components/PageProgressWidget';

/** Docusaurus renders this once around the whole app (every page, every route) - the standard
 * "theme/Root" swizzle point for a provider that needs to wrap everything. Used here so any
 * number of <Checklist/> components on a doc page can report into one shared progress total,
 * summarized by the floating PageProgressWidget. */
export default function Root({children}: {children: React.ReactNode}): React.ReactElement {
  return (
    <ChecklistProgressProvider>
      {children}
      <PageProgressWidget />
    </ChecklistProgressProvider>
  );
}
