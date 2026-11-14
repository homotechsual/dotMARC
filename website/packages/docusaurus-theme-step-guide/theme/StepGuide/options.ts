import {useThemeConfig} from '@docusaurus/theme-common';

/** The theme's settings, from `themeConfig.stepGuide` with defaults filled in. */
export interface StepGuideOptions {
  /** Docs with this front matter key set to true get the switch. */
  frontMatterKey: string;
  /** Heading levels that start a new step. */
  headingLevels: number[];
  /** Prefix for what the theme saves in the reader's localStorage. */
  storageNamespace: string;
  /** Show the dismissible "new" callout to readers who haven't used the switch yet. */
  showIntroHint: boolean;
  /** CSS selector for the element(s) whose children are the guide's content. Several selectors can be given as a
   * comma-separated list or an array. */
  articleSelector: string;
  /** CSS selectors for wrapper elements to look inside: headings and content in a matching element take part in
   * steps as if they were direct children of the article. */
  wrapperSelectors: string[];
}

export const DEFAULT_ARTICLE_SELECTOR = '.theme-doc-markdown';

// Module-level so the defaults are the same array every render: effects depend on them.
const DEFAULT_HEADING_LEVELS = [2, 3];
const NO_WRAPPERS: string[] = [];

export function useStepGuideOptions(): StepGuideOptions {
  const configured = (useThemeConfig() as {stepGuide?: Partial<Omit<StepGuideOptions, 'articleSelector'>> & {
    articleSelector?: string | string[];
  }}).stepGuide ?? {};
  const article = configured.articleSelector;
  return {
    frontMatterKey: configured.frontMatterKey ?? 'stepGuide',
    headingLevels: configured.headingLevels ?? DEFAULT_HEADING_LEVELS,
    storageNamespace: configured.storageNamespace ?? 'step-guide',
    showIntroHint: configured.showIntroHint ?? true,
    articleSelector: Array.isArray(article) ? article.join(', ') : article || DEFAULT_ARTICLE_SELECTOR,
    wrapperSelectors: configured.wrapperSelectors ?? NO_WRAPPERS,
  };
}
