// The page logic of Waypoint, kept free of React so it can be tested on its own.

/** What the controls need to know about one step. */
export interface StepInfo {
  /** The step heading's id, which is also its URL hash. `null` for the overview before the first heading. */
  id: string | null;
  title: string;
  /** True for a sub-step (a heading level below the highest one that starts steps). */
  isSubStep: boolean;
}

/** A step plus the page elements that belong to it. */
export interface StepDom extends StepInfo {
  heading: HTMLElement | null;
  nodes: HTMLElement[];
}

const HIDE_IN_STEPS_ATTRIBUTE = 'data-waypoint-hidden';

/** The elements of a guide in reading order, looking inside any wrapper elements as if their children sat directly
 * in the article. A wrapper that holds headings can then still split the page into steps. */
function* contentNodes(
  children: ArrayLike<Element>,
  isWrapper: ((node: Element) => boolean) | undefined,
): Generator<HTMLElement> {
  for (const node of Array.from(children)) {
    if (isWrapper?.(node)) {
      yield* contentNodes(node.children, isWrapper);
    } else {
      yield node as HTMLElement;
    }
  }
}

export interface BuildStepsOptions {
  /** Heading levels that start a step, such as [2, 3]. */
  headingLevels: number[];
  /** The name of the first step, which holds whatever comes before the first heading. */
  overviewTitle: string;
  /** Says which elements are wrappers to look inside rather than content. */
  isWrapper?: (node: Element) => boolean;
}

/** Splits rendered articles into steps: one per heading at a chosen level, plus an overview for whatever comes
 * before the first heading. The page title (h1) and any header stay on screen in every step. Give several roots when
 * the guide's content is spread over more than one element. */
export function buildSteps(roots: Array<{children: ArrayLike<Element>}>, options: BuildStepsOptions): StepDom[] {
  const {headingLevels, overviewTitle, isWrapper} = options;
  const topLevel = Math.min(...headingLevels);
  const tags = new Set(headingLevels.map((level) => `H${level}`));
  const steps: StepDom[] = [{id: null, title: overviewTitle, isSubStep: false, heading: null, nodes: []}];
  for (const root of roots) {
    for (const node of contentNodes(root.children, isWrapper)) {
      const tag = node.tagName;
      if (tag === 'H1' || tag === 'HEADER') {
        continue;
      }
      if (tags.has(tag)) {
        steps.push({
          id: node.id || null,
          title: (node.textContent ?? '').replace(/​/g, '').trim(),
          isSubStep: Number(tag.slice(1)) > topLevel,
          heading: node,
          nodes: [node],
        });
        continue;
      }
      steps[steps.length - 1].nodes.push(node);
    }
  }
  // A page that opens straight on a heading has no overview to show.
  return steps[0].nodes.length === 0 ? steps.slice(1) : steps;
}

export function showOnly(steps: StepDom[], current: number): void {
  steps.forEach((step, index) => {
    for (const node of step.nodes) {
      // Anything marked as whole-guide-only stays hidden here, for example a checklist shown a step at a time.
      const hide = index !== current || node.hasAttribute(HIDE_IN_STEPS_ATTRIBUTE);
      node.style.display = hide ? 'none' : '';
    }
  });
}

export function showEverything(steps: StepDom[]): void {
  for (const step of steps) {
    for (const node of step.nodes) {
      node.style.display = '';
    }
  }
}

/** The index of the step a URL hash points into, or -1. */
export function findStepForHash(steps: StepDom[], hash: string, getElementById: (id: string) => Element | null): number {
  if (!hash) {
    return -1;
  }
  let id = hash.replace(/^#/, '');
  try {
    id = decodeURIComponent(id);
  } catch {
    // Keep the raw value.
  }
  const target = getElementById(id);
  return steps.findIndex(
    (step) =>
      step.id === id || (target !== null && step.nodes.some((node) => node === target || node.contains(target))),
  );
}

export function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    // Storage blocked (private browsing and so on): the page still works, it just forgets.
    return null;
  }
}

export function writeStorage(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Nothing to fall back to.
  }
}
