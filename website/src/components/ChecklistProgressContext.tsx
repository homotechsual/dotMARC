import React, {createContext, useCallback, useContext, useMemo, useState} from 'react';

interface ChecklistState {
  completed: number;
  total: number;
}

interface ChecklistProgressContextValue {
  registerChecklist: (id: string, state: ChecklistState) => void;
  unregisterChecklist: (id: string) => void;
  totals: ChecklistState;
  hasChecklists: boolean;
}

const ChecklistProgressContext = createContext<ChecklistProgressContextValue | null>(null);

/** Wraps the whole site (see src/theme/Root.tsx) so every <Checklist/> on a page can report its
 * own completed/total into one shared registry, and PageProgressWidget can sum them into a
 * page-wide total. Registrations live and die with the Checklist components that made them, so
 * navigating to a different page - which unmounts the old page's checklists and mounts the new
 * page's - naturally resets the totals with no page-identity tracking needed here. */
export function ChecklistProgressProvider({children}: {children: React.ReactNode}): React.ReactElement {
  const [registry, setRegistry] = useState<Record<string, ChecklistState>>({});

  const registerChecklist = useCallback((id: string, state: ChecklistState) => {
    setRegistry((prev) => {
      const existing = prev[id];
      if (existing && existing.completed === state.completed && existing.total === state.total) {
        return prev;
      }
      return {...prev, [id]: state};
    });
  }, []);

  const unregisterChecklist = useCallback((id: string) => {
    setRegistry((prev) => {
      if (!(id in prev)) {
        return prev;
      }
      const next = {...prev};
      delete next[id];
      return next;
    });
  }, []);

  const totals = useMemo<ChecklistState>(() => {
    const values = Object.values(registry);
    return {
      completed: values.reduce((sum, v) => sum + v.completed, 0),
      total: values.reduce((sum, v) => sum + v.total, 0),
    };
  }, [registry]);

  const value = useMemo<ChecklistProgressContextValue>(
    () => ({registerChecklist, unregisterChecklist, totals, hasChecklists: Object.keys(registry).length > 0}),
    [registerChecklist, unregisterChecklist, totals, registry],
  );

  return <ChecklistProgressContext.Provider value={value}>{children}</ChecklistProgressContext.Provider>;
}

/** Returns null outside the provider (e.g. if a Checklist somehow renders before Root mounts) -
 * callers must handle that rather than assume it's always present. */
export function useChecklistProgress(): ChecklistProgressContextValue | null {
  return useContext(ChecklistProgressContext);
}
