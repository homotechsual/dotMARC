// Type declarations for the theme's own imports. @theme-init is how a theme wraps another theme's component (a site
// uses @theme-original in its own src/theme folder), and Docusaurus ships no types for it.
declare module '@theme-init/DocItem/Content' {
  import type {ComponentType} from 'react';
  import type {Props} from '@theme/DocItem/Content';

  const Content: ComponentType<Props>;
  export default Content;
}

// The theme's own components, for sites that import them.
declare module '@theme/StepGuide/Checklist' {
  import type {ReactElement} from 'react';

  export type ChecklistItem = string | {text: string; step: string};
  export interface ChecklistProps {
    id: string;
    items: ChecklistItem[];
    onProgress?: (completed: number, total: number) => void;
  }
  export default function Checklist(props: ChecklistProps): ReactElement;
}

declare module '@theme/StepGuide/tasks' {
  export interface StepGuideTask {
    stepId: string;
    label: string;
    checked: boolean;
    toggle: () => void;
  }
  export interface StepGuideTaskRegistry {
    register: (groupId: string, tasks: StepGuideTask[] | null) => void;
  }
  export function useStepGuideTasks(): StepGuideTaskRegistry | null;
  export const HIDE_IN_STEPS_ATTRIBUTE: string;
}
