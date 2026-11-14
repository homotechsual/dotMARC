import {createContext, useContext} from 'react';

/** One checklist item that belongs to a step of a guide, as step mode shows it. */
export interface StepChecklistEntry {
  /** The id of the step's heading, which is where the item is ticked off. */
  stepId: string;
  label: string;
  checked: boolean;
  toggle: () => void;
}

export interface StepChecklistRegistry {
  /** Gives step mode a checklist's items (or `null` when the checklist goes away). */
  register: (checklistId: string, entries: StepChecklistEntry[] | null) => void;
}

/** Provided by StepMode, so a Checklist whose items name the step they belong to can hand them over to be shown
 * one per step. Without a provider (every page that isn't a step-mode guide) it does nothing. */
export const StepChecklistContext = createContext<StepChecklistRegistry | null>(null);

export function useStepChecklistRegistry(): StepChecklistRegistry | null {
  return useContext(StepChecklistContext);
}
