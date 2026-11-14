import {createContext, useContext} from 'react';

/** One task (a tickable item) that belongs to a step of a guide. */
export interface WaypointTask {
  /** The id of the step's heading, which is where the task is ticked off. */
  stepId: string;
  label: string;
  checked: boolean;
  toggle: () => void;
}

export interface WaypointTaskRegistry {
  /** Hands the guide a group's tasks (or `null` when the group goes away), keyed by a group id of your choosing. */
  register: (groupId: string, tasks: WaypointTask[] | null) => void;
}

/** Provided by Waypoint around a doc's content. A checklist component inside the doc can register its items
 * here so each is shown at the end of its own step. Outside a Waypoint guide there is no provider and the hook returns
 * `null`, so the checklist just behaves as it normally does. */
export const WaypointTasksContext = createContext<WaypointTaskRegistry | null>(null);

export function useWaypointTasks(): WaypointTaskRegistry | null {
  return useContext(WaypointTasksContext);
}

/** Put this attribute on any element of a guide that should only show in the whole-guide view, such as a checklist
 * that has been split up into per-step tasks. */
export const HIDE_IN_STEPS_ATTRIBUTE = 'data-waypoint-hidden';
