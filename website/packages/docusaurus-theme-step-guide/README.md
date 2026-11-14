# docusaurus-theme-step-guide

Lets readers switch a long Docusaurus guide to **one step at a time**: a *Whole guide / One step at a time* switch, a
progress bar, a jump menu, Back and Next buttons, and optional "done" ticks for each step. For readers who find a long
page overwhelming.

It is a single page of content shown two ways, not two versions of it. In step mode the theme sorts the rendered
article into groups at each heading and hides all but the current one. Switching back to *Whole guide* restores the
page exactly. Without JavaScript the full page is shown.

This package is currently used from this repository as a local theme and is not published yet.

## Use it

Add the theme, and opt a doc in with front matter:

```ts
// docusaurus.config.ts
themes: ['@homotechsual/docusaurus-theme-step-guide'],
themeConfig: {
  stepGuide: {
    // All optional. These are the defaults.
    frontMatterKey: 'stepGuide', // docs with `stepGuide: true` get the switch
    headingLevels: [2, 3], // heading levels that start a step
    storageNamespace: 'step-guide', // prefix for what is saved in the reader's localStorage
    showIntroHint: true, // the "New: read this one step at a time" callout for first-time visitors
    articleSelector: '.theme-doc-markdown', // where the guide's content lives (see "Different page structures")
    wrapperSelectors: [], // wrapper elements to look inside
  },
},
```

```md
---
stepGuide: true
---

# My guide

Intro text is the first step ("Overview").

## Install

## Configure
```

Every heading at the configured levels is a step. A heading at a level below the highest one is shown indented in the
jump menu.

## Colours

The stuck progress bar should match your page background so it doesn't show up as a panel. Set this to your page colour
(it defaults to `--ifm-background-surface-color`):

```css
:root {
  --step-guide-bar-background: #f7f8fa;
}
[data-theme='dark'] {
  --step-guide-bar-background: #111827;
}
```

## Different page structures

By default the theme looks at the children of Docusaurus' `.theme-doc-markdown` element. Two settings cover other
structures:

- `articleSelector` is a CSS selector (or a list, as an array or comma-separated) for the element or elements whose
  children are the guide's content. Several matches are read as one guide, in page order. Use it for a custom doc
  layout, or to add a second parent that holds part of the content.
- `wrapperSelectors` lists wrapper elements to look inside. A heading in a matching wrapper starts a step just as if
  it were a direct child of the article, so you can keep wrapping content in your own components:

```ts
stepGuide: {
  articleSelector: ['.theme-doc-markdown', '.my-extra-parent'],
  wrapperSelectors: ['.my-section', '.my-callout-group'],
},
```

The wrappers themselves stay on screen, so one with its own border or padding can leave an empty box in the steps it
has nothing in.

## Ticks: the checklist component

The theme includes a checklist whose ticks are saved in the reader's browser (under your `storageNamespace`). Give each
item the id of the heading it belongs to, and in step mode the list is replaced by a "Done" tick at the end of each
step:

```mdx
import Checklist from '@theme/StepGuide/Checklist';

<Checklist
  id="install-guide"
  items={[
    {text: 'Installed the CLI', step: 'install'},
    {text: 'Signed in', step: 'sign-in'},
  ]}
/>
```

- Items can also be plain strings. A list only moves into the steps when **every** item names a step. Otherwise it
  stays where you put it, in both views.
- `id` is part of the saved key. Keep it unique and unchanged, or readers' ticks reset.
- `onProgress(completed, total)` is called as ticks change, so you can feed your own page-wide progress display.
- The whole-guide list and the per-step ticks share the same saved state.

## Your own checklist

A checklist component of your own can show its items one per step by registering them with the tasks hook:

```tsx
import {useStepGuideTasks, HIDE_IN_STEPS_ATTRIBUTE} from '@theme/StepGuide/tasks';

const registry = useStepGuideTasks(); // null outside a step guide

useEffect(() => {
  registry?.register('my-checklist', [
    {stepId: 'install', label: 'Installed it', checked, toggle},
  ]);
  return () => registry?.register('my-checklist', null);
}, [registry, checked]);

// Put this attribute on the checklist's root element so the full list hides in step mode:
<div {...{[HIDE_IN_STEPS_ATTRIBUTE]: ''}}>…</div>
```

`stepId` is the id of the step's heading. If a heading is renamed its id changes, and its task silently stops appearing
in step mode, so keep the ids in step with your headings.

## Translations

All visible text goes through Docusaurus' `translate()` with ids starting `theme.stepGuide.`. Run
`docusaurus write-translations` to get them.

## Limits

- It works on the rendered page, so a heading only starts a step when it is a direct child of the article or of a
  wrapper named in `wrapperSelectors`. Headings inside anything else (tabs, details, an unlisted component) do not.
- It relies on Docusaurus 3's `.theme-doc-markdown` article element.
- Printing prints the current step. Switch to *Whole guide* first.
- The page logic (`theme/StepGuide/steps.ts`) is tested with `node --test test/steps.test.mjs`.
