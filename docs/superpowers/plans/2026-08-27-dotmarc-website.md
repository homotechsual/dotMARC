# dotMARC Website Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and deploy a marketing + docs + blog website for dotMARC at `dotmarc.app`, using Docusaurus + Tailwind CSS in a new `website/` folder in this repo.

**Architecture:** A new, self-contained Docusaurus 3.x (classic preset) + TypeScript + Tailwind v4 project under `website/`, following the exact stack and integration pattern already proven on `J:\Projects\Heriau\website` (Tailwind preflight/container disabled to coexist with Infima, `@tailwindcss/postcss`). The homepage is fully bespoke Tailwind markup; docs and blog keep Docusaurus's standard classic-theme structure, reskinned via `custom.css` to dotMARC's existing dashboard brand palette. Deploys to Cloudflare Pages (Homotechsual account) via path-scoped GitHub Actions workflows that only trigger on changes under `website/`.

**Tech Stack:** Docusaurus 3.10.2 (classic preset), TypeScript, Tailwind CSS v4 (`@tailwindcss/postcss`), React 19, `@fontsource/inter`, `@homotechsual/docusaurus-plugin-plausible`, `@homotechsual/docusaurus-og`, Cloudflare Pages (`cloudflare/wrangler-action`), npm.

**Spec:** `docs/superpowers/specs/2026-08-27-dotmarc-website-design.md`

## Global Constraints

- Everything lives under `website/` — a self-contained npm project, independent of `dotMARC.sln`. Nothing outside `website/` (besides two new `.github/workflows/*.yml` files) is touched.
- No Google Fonts CDN calls anywhere — all fonts self-hosted via `@fontsource` packages, bundled at build time.
- Brand palette is dotMARC's existing dashboard palette, not a new one: light-mode Primary `#e3594f` / PrimaryLighten `#efaaa5` / PrimaryDarken `#c9443a`, Secondary `#263141` / SecondaryLighten `#3d4f63` / SecondaryDarken `#1a2230`; dark-mode Primary `#ef8b86` / PrimaryLighten `#fad0cc` / PrimaryDarken `#e3594f`, Background `#111827`, Surface `#1e2a3a` (from `src/DotMarc/Components/Layout/MainLayout.razor`'s `MudTheme`).
- Single locale (English only) — no `i18n` config beyond Docusaurus's default, no locale dropdown, no per-locale build matrix.
- Docs and blog use Docusaurus's standard classic-theme structure (sidebar, TOC, prev/next) — reskinned via CSS/Tailwind, never swizzled into fully custom layouts. Only the homepage (`src/pages/index.tsx`) is fully bespoke.
- Package manager is npm (not yarn) — this project has no existing JS tooling convention to follow, and the CI/deploy tasks are written around `npm ci`.
- `onBrokenLinks: 'throw'` — a broken internal link fails the build.
- No Playwright/visual-regression tests, no first blog post, no DNS configuration — all explicitly out of scope per the spec's Non-goals.
- Production domain is `dotmarc.app`; Cloudflare account is Homotechsual (account ID `e5d7b48988c7e30b93f96550fd4f65d5`).
- GitHub repo is `homotechsual/dotMARC` (confirmed via `git remote -v`) — used for `editUrl`/`organizationName`/`projectName` config and the GHCR image reference already used elsewhere in this repo.

---

### Task 1: Scaffold — Docusaurus + TypeScript + Tailwind + brand theming

**Files:**
- Create: `website/package.json`
- Create: `website/tsconfig.json`
- Create: `website/docusaurus.config.ts`
- Create: `website/sidebars.ts`
- Create: `website/tailwind.config.js`
- Create: `website/postcss.config.js`
- Create: `website/src/css/custom.css`
- Create: `website/.gitignore`
- Create: `website/static/img/favicon.svg`
- Create: `website/static/img/logo-light.svg`
- Create: `website/static/img/logo-dark.svg`

**Interfaces:**
- Produces: the base `docusaurus.config.ts` (site metadata, navbar, footer, `theme.customCss`) that Tasks 3 (docs `sidebarPath`/`routeBasePath` reference), 4 (blog config), and 5 (plugins array) all extend. Produces the brand CSS custom properties (`--ifm-color-primary*` etc.) in `custom.css` that Task 2's homepage and Tasks 3/4's docs/blog pages inherit.

- [ ] **Step 1: Create the project scaffold files**

Create `website/package.json`:

```json
{
  "name": "dotmarc-website",
  "version": "0.0.0",
  "private": true,
  "scripts": {
    "docusaurus": "docusaurus",
    "start": "docusaurus start",
    "build": "docusaurus build",
    "swizzle": "docusaurus swizzle",
    "clear": "docusaurus clear",
    "serve": "docusaurus serve",
    "write-translations": "docusaurus write-translations",
    "write-heading-ids": "docusaurus write-heading-ids",
    "typecheck": "tsc",
    "prebuild": "tsc --project tsconfig.json"
  },
  "dependencies": {
    "@docusaurus/core": "3.10.2",
    "@docusaurus/preset-classic": "3.10.2",
    "@fontsource/inter": "^5.2.5",
    "@mdx-js/react": "^3.1.1",
    "@tailwindcss/postcss": "^4.3.2",
    "autoprefixer": "^10.5.2",
    "clsx": "^2.1.1",
    "postcss": "^8.5.18",
    "prism-react-renderer": "^2.4.1",
    "react": "^19.2.7",
    "react-dom": "^19.2.7",
    "tailwindcss": "^4.3.2"
  },
  "devDependencies": {
    "@docusaurus/module-type-aliases": "3.10.2",
    "@docusaurus/tsconfig": "3.10.2",
    "@docusaurus/types": "3.10.2",
    "@types/react": "^19.2.17",
    "typescript": "~6.0.3"
  },
  "browserslist": {
    "production": [
      ">0.5%",
      "not dead",
      "not op_mini all"
    ],
    "development": [
      "last 3 chrome version",
      "last 3 firefox version",
      "last 5 safari version"
    ]
  },
  "engines": {
    "node": ">=20.0"
  }
}
```

Create `website/tsconfig.json`:

```json
{
  "extends": "@docusaurus/tsconfig",
  "compilerOptions": {
    "baseUrl": "."
  }
}
```

Create `website/tailwind.config.js`:

```js
/** @type {import('tailwindcss').Config} */
module.exports = {
    content: ["./src/**/*.{js,jsx,ts,tsx,md,mdx}", "./docs/**/*.{md,mdx}", "./blog/**/*.{md,mdx}"],
    theme: { extend: {} },
    plugins: [],
    darkMode: ["class", '[data-theme="dark"]'], // Support dark mode
    corePlugins: {
        preflight: false,
        container: false,
    }, // Prevent Tailwind base/container collisions with Docusaurus defaults
}
```

Create `website/postcss.config.js`:

```js
module.exports = {
	plugins: {
		'@tailwindcss/postcss': {},
		autoprefixer: {},
	}
}
```

Create `website/.gitignore`:

```
# Dependencies
/node_modules

# Production
/build

# Generated files
.docusaurus
.cache-loader

# Misc
.DS_Store
.env.local
.env.development.local
.env.test.local
.env.production.local

npm-debug.log*
```

- [ ] **Step 2: Create the brand assets**

Create `website/static/img/favicon.svg` (a rounded square with the brand palette — Docusaurus accepts an SVG favicon path directly):

```svg
<svg width="64" height="64" viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg">
  <rect width="64" height="64" rx="14" fill="#263141"/>
  <circle cx="32" cy="32" r="16" fill="#e3594f"/>
</svg>
```

Create `website/static/img/logo-light.svg` (navbar logo for light mode — a dot mark plus wordmark, using a universally-available font stack so it renders correctly without waiting on the custom web font):

```svg
<svg width="180" height="40" viewBox="0 0 180 40" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="dotMARC">
  <circle cx="14" cy="20" r="10" fill="#e3594f"/>
  <text x="32" y="27" font-family="Arial, Helvetica, sans-serif" font-size="22" font-weight="800" fill="#263141">dotMARC</text>
</svg>
```

Create `website/static/img/logo-dark.svg` (navbar logo for dark mode):

```svg
<svg width="180" height="40" viewBox="0 0 180 40" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="dotMARC">
  <circle cx="14" cy="20" r="10" fill="#ef8b86"/>
  <text x="32" y="27" font-family="Arial, Helvetica, sans-serif" font-size="22" font-weight="800" fill="#fcfcfc">dotMARC</text>
</svg>
```

- [ ] **Step 3: Create the brand CSS**

Create `website/src/css/custom.css`:

```css
/**
 * Any CSS included here will be global. The classic template
 * bundles Infima by default. Infima is a CSS framework designed to
 * work well for content-centric websites.
 */
@import '@fontsource/inter/400.css';
@import '@fontsource/inter/500.css';
@import '@fontsource/inter/600.css';
@import '@fontsource/inter/900.css';
@import 'tailwindcss/theme';
@import 'tailwindcss/utilities';

/* dotMARC's existing dashboard brand palette (src/DotMarc/Components/Layout/MainLayout.razor's MudTheme) */
:root {
  --ifm-font-family-base: 'Inter', 'Segoe UI', sans-serif;
  --ifm-font-family-monospace: 'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, monospace;
  --ifm-color-primary: #e3594f;
  --ifm-color-primary-dark: #c9443a;
  --ifm-color-primary-darker: #b93d33;
  --ifm-color-primary-darkest: #99332a;
  --ifm-color-primary-light: #e87971;
  --ifm-color-primary-lighter: #eb8981;
  --ifm-color-primary-lightest: #efaaa5;
  --ifm-code-font-size: 95%;
  --ifm-background-surface-color: #fcfcfc;
  --docusaurus-highlighted-code-line-bg: rgba(227, 89, 79, 0.1);
}

[data-theme='dark'] {
  --ifm-color-primary: #ef8b86;
  --ifm-color-primary-dark: #e3594f;
  --ifm-color-primary-darker: #db473c;
  --ifm-color-primary-darkest: #b93d33;
  --ifm-color-primary-light: #f2a19d;
  --ifm-color-primary-lighter: #f6c1be;
  --ifm-color-primary-lightest: #fad0cc;
  --ifm-background-surface-color: #1e2a3a;
  --docusaurus-highlighted-code-line-bg: rgba(239, 139, 134, 0.18);
}

html {
  scroll-behavior: smooth;
}

body {
  height: auto;
  min-height: 100vh;
  font-family: var(--ifm-font-family-base);
  background-color: #f7f8fa;
  position: relative;
}

body::before {
  content: '';
  position: fixed;
  inset: 0;
  z-index: -1;
  pointer-events: none;
  background:
    radial-gradient(circle at top left, rgba(38, 49, 65, 0.1), transparent 34%),
    radial-gradient(circle at top right, rgba(227, 89, 79, 0.1), transparent 28%),
    linear-gradient(180deg, #fcfcfc 0%, #ffffff 34%, #f7f8fa 100%);
}

[data-theme='dark'] body {
  background-color: #111827;
}

[data-theme='dark'] body::before {
  background:
    radial-gradient(circle at top left, rgba(239, 139, 134, 0.12), transparent 34%),
    radial-gradient(circle at top right, rgba(61, 79, 99, 0.16), transparent 28%),
    linear-gradient(180deg, #0d141f 0%, #111827 36%, #1e2a3a 100%);
}

.main-wrapper {
  background: transparent;
}

.theme-layout-main {
  background: inherit;
}

.navbar {
  -webkit-backdrop-filter: blur(18px);
  backdrop-filter: blur(18px);
  background: rgba(255, 255, 255, 0.75);
}

[data-theme='dark'] .navbar {
  background: rgba(17, 24, 39, 0.85);
}

.navbar__title {
  display: none !important;
}

.navbar__logo {
  display: inline-flex;
  align-items: center;
  height: auto;
}

.navbar__logo img,
.navbar__logo svg {
  display: block;
  height: 2.4rem;
  width: auto;
  max-height: none;
}

.button--primary {
  --ifm-button-background-color: #e3594f;
  --ifm-button-border-color: #e3594f;
  --ifm-button-color: #fcfcfc;
}

.button--primary:hover {
  --ifm-button-background-color: #c9443a;
  --ifm-button-border-color: #c9443a;
}

::selection {
  background: rgba(227, 89, 79, 0.2);
  color: #263141;
}

[data-theme='dark'] ::selection {
  background: rgba(239, 139, 134, 0.28);
  color: #fcfcfc;
}
```

- [ ] **Step 4: Create the base Docusaurus config**

Create `website/docusaurus.config.ts`:

```ts
import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const siteTitle = 'dotMARC';
const siteTagline = 'Self-hosted DMARC monitoring for every client domain, from one mailbox.';
const siteDescription =
  'dotMARC is a self-hosted DMARC aggregate report analyzer for monitoring email authentication posture across multiple domains from a single mailbox — built for MSPs managing client domains, and equally usable by a single organization.';
const siteUrl = 'https://dotmarc.app';

const config: Config = {
  title: siteTitle,
  tagline: siteTagline,
  favicon: 'img/favicon.svg',

  future: {
    v4: true, // Improve compatibility with the upcoming Docusaurus v4
  },

  url: siteUrl,
  baseUrl: '/',
  trailingSlash: true,

  organizationName: 'homotechsual',
  projectName: 'dotMARC',

  onBrokenLinks: 'throw',

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: 'docs',
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/homotechsual/dotMARC/tree/main/website/',
        },
        blog: {
          routeBasePath: 'blog',
          showReadingTime: true,
          feedOptions: {
            type: ['rss', 'atom'],
            xslt: true,
          },
          editUrl: 'https://github.com/homotechsual/dotMARC/tree/main/website/',
          onInlineTags: 'warn',
          onInlineAuthors: 'warn',
          onUntruncatedBlogPosts: 'warn',
        },
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    image: 'img/favicon.svg',
    colorMode: {
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: '',
      logo: {
        alt: siteTitle,
        href: '/',
        src: 'img/logo-light.svg',
        srcDark: 'img/logo-dark.svg',
      },
      items: [
        {to: '/docs/getting-started', label: 'Docs', position: 'left'},
        {to: '/blog', label: 'Blog', position: 'left'},
        {
          href: 'https://github.com/homotechsual/dotMARC',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            {label: 'Getting Started', to: '/docs/getting-started'},
            {label: 'Deploy to Azure', to: '/docs/deploy-to-azure'},
            {label: 'Permissions & Access', to: '/docs/permissions-and-access'},
          ],
        },
        {
          title: 'More',
          items: [
            {label: 'Blog', to: '/blog'},
            {label: 'GitHub', href: 'https://github.com/homotechsual/dotMARC'},
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} dotMARC.<br />Site made by <a href="https://homotechsual.dev">homotechsual</a>.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
```

- [ ] **Step 5: Create the sidebar stub**

Create `website/sidebars.ts`:

```ts
import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  // Docusaurus generates the sidebar from the docs folder structure and each page's
  // sidebar_position front matter (set in Task 3).
  docsSidebar: [{type: 'autogenerated', dirName: '.'}],
};

export default sidebars;
```

- [ ] **Step 6: Install dependencies and verify the build**

Run: `cd website && npm install`
Expected: installs cleanly, produces `package-lock.json`.

Run: `npm run build`
Expected: FAILS — there's no `docs/intro`-equivalent content yet (the classic preset's docs plugin needs at least one doc, and no `src/pages/index` exists yet either since Tasks 2/3 haven't run). This is expected at this point in the plan; do not try to fix it here.

- [ ] **Step 7: Add a placeholder doc and page so the scaffold itself is verifiable**

Create `website/docs/getting-started.md` (temporary placeholder — Task 3 replaces this with the real content):

```markdown
---
sidebar_position: 1
---

# Getting Started

Placeholder — replaced with real content in Task 3.
```

Create `website/src/pages/index.tsx` (temporary placeholder — Task 2 replaces this with the real homepage):

```tsx
import type {ReactNode} from 'react';
import Layout from '@theme/Layout';

export default function Home(): ReactNode {
  return (
    <Layout title="dotMARC" description="Placeholder — replaced in Task 2.">
      <main className="container mx-auto px-4 py-16">
        <h1>dotMARC</h1>
      </main>
    </Layout>
  );
}
```

- [ ] **Step 8: Verify the build succeeds**

Run: `npm run build`
Expected: SUCCESS — `website/build/` is produced with no errors, no broken-link failures.

Run: `npm run typecheck`
Expected: SUCCESS — no TypeScript errors.

- [ ] **Step 9: Commit**

```bash
git add website/package.json website/package-lock.json website/tsconfig.json website/docusaurus.config.ts website/sidebars.ts website/tailwind.config.js website/postcss.config.js website/src/css/custom.css website/.gitignore website/static/img/favicon.svg website/static/img/logo-light.svg website/static/img/logo-dark.svg website/docs/getting-started.md website/src/pages/index.tsx
git commit -m "Scaffold the dotMARC website: Docusaurus, Tailwind, brand theming"
```

---

### Task 2: Homepage

**Files:**
- Modify: `website/src/pages/index.tsx` (replaces Task 1's placeholder)

**Interfaces:**
- Consumes: brand CSS custom properties from Task 1's `custom.css`; the `docusaurus.config.ts` navbar/footer from Task 1.
- Produces: nothing later tasks depend on directly — the homepage is a leaf page.

No automated test — this is presentational content, verified by build success and a manual dev-server check (this codebase-to-be has no component-rendering test framework, same reasoning as this repo's Blazor UI tasks).

- [ ] **Step 1: Write the homepage**

Replace `website/src/pages/index.tsx` in full:

```tsx
import type {ReactNode} from 'react';
import Link from '@docusaurus/Link';
import Layout from '@theme/Layout';
import Head from '@docusaurus/Head';

const heroCopy = {
  eyebrow: 'dotMARC',
  title: 'DMARC monitoring for every domain you manage.',
  subtitle:
    'dotMARC is a self-hosted DMARC aggregate report analyzer. Point every client domain at one mailbox, and see authentication posture across all of them from a single dashboard.',
  primaryCta: 'Read the docs',
  secondaryCta: 'View on GitHub',
  description:
    'Built for MSPs managing DMARC across many client domains — and equally usable for a single organization watching its own.',
};

const features = [
  {
    title: 'One mailbox, every domain',
    description:
      'Point every monitored domain\u2019s DMARC record at a single shared mailbox. dotMARC polls it, parses aggregate reports, and attributes them back to the right domain automatically.',
  },
  {
    title: 'Built for MSPs',
    description:
      'Fine-grained, per-domain access control means you can grant an external client visibility into just their own domains \u2014 scoped Viewer roles, not an all-or-nothing login.',
  },
  {
    title: 'Self-hosted, your data',
    description:
      'Runs on your own infrastructure \u2014 Docker Compose or Azure Container Apps \u2014 backed by PostgreSQL. Aggregate report data never leaves your environment.',
  },
];

const capabilities = [
  {
    label: 'Report ingestion',
    value:
      'Automatic polling and parsing of DMARC aggregate (RUA) reports, deduplicated so a message is never processed twice.',
  },
  {
    label: 'Multi-domain dashboard',
    value:
      'Pass/fail rates, source breakdowns, and DNS record status across every monitored domain, filterable by Group and Tag.',
  },
  {
    label: 'Access control',
    value:
      'Fine-grained permissions \u2014 roles, Group-scoped Viewer grants for external clients, and an Admin/Viewer preset out of the box.',
  },
  {
    label: 'DNS status checks',
    value:
      'Live DNS lookups confirm each monitored domain\u2019s DMARC record is actually in place and pointed at the right mailbox.',
  },
];

function HomepageHeader() {
  return (
    <header className="relative overflow-hidden pt-12 pb-6 sm:pt-16 lg:pt-24">
      <div className="pointer-events-none absolute inset-x-0 top-0 h-[34rem] bg-[radial-gradient(circle_at_top_left,rgba(38,49,65,0.16),transparent_35%),radial-gradient(circle_at_top_right,rgba(227,89,79,0.18),transparent_28%)]" />
      <div className="container relative mx-auto px-4">
        <div className="max-w-3xl">
          <div className="inline-flex items-center gap-2 rounded-full border border-[rgba(38,49,65,0.14)] bg-white/80 px-4 py-2 text-xs font-extrabold uppercase tracking-[0.22em] text-[#263141] shadow-[0_12px_30px_rgba(38,49,65,0.08)] backdrop-blur dark:border-white/10 dark:bg-[#1e2a3a]/80 dark:text-[#fad0cc]">
            <span className="h-2 w-2 rounded-full bg-[#e3594f]" />
            {heroCopy.eyebrow}
          </div>
          <h1 className="mt-5 max-w-4xl text-5xl font-black tracking-[-0.06em] text-[#161e29] sm:text-6xl lg:text-7xl dark:text-[#fcfcfc]">
            {heroCopy.title}
          </h1>
          <p className="mt-5 max-w-2xl text-lg leading-8 text-[#3d4f63] sm:text-xl dark:text-[#c7d0dc]">
            {heroCopy.subtitle}
          </p>
          <p className="mt-4 max-w-2xl text-base leading-7 text-[#5b6b7d] dark:text-[#9fb0c2]">
            {heroCopy.description}
          </p>
          <div className="mt-8 flex flex-wrap gap-3">
            <Link
              className="inline-flex items-center justify-center rounded-full bg-[#e3594f] px-6 py-3 text-sm font-bold text-white shadow-[0_16px_36px_rgba(227,89,79,0.28)] transition-transform duration-200 hover:-translate-y-0.5 hover:bg-[#c9443a] hover:no-underline"
              to="/docs/getting-started">
              {heroCopy.primaryCta}
            </Link>
            <Link
              className="inline-flex items-center justify-center rounded-full border border-[#263141]/15 bg-white/80 px-6 py-3 text-sm font-bold text-[#161e29] shadow-[0_16px_36px_rgba(38,49,65,0.08)] transition-transform duration-200 hover:-translate-y-0.5 hover:border-[#263141]/25 hover:bg-white hover:text-[#e3594f] hover:no-underline dark:border-white/10 dark:bg-[#1e2a3a]/80 dark:text-[#fcfcfc] dark:hover:text-[#ef8b86]"
              href="https://github.com/homotechsual/dotMARC">
              {heroCopy.secondaryCta}
            </Link>
          </div>
        </div>
      </div>
    </header>
  );
}

export default function Home(): ReactNode {
  const pageUrl = 'https://dotmarc.app/';

  return (
    <Layout title="dotMARC" description={heroCopy.description}>
      <Head>
        <meta property="og:type" content="website" />
        <meta property="og:title" content="dotMARC" />
        <meta property="og:description" content={heroCopy.description} />
        <meta property="og:url" content={pageUrl} />
        <meta name="twitter:card" content="summary_large_image" />
        <meta name="twitter:title" content="dotMARC" />
        <meta name="twitter:description" content={heroCopy.description} />
      </Head>
      <HomepageHeader />
      <main className="relative z-10 mx-auto max-w-7xl px-4 pb-16 sm:px-6 lg:px-8 lg:pb-24">
        <section className="mt-10 grid gap-5 md:grid-cols-3">
          {features.map(feature => (
            <div
              key={feature.title}
              className="rounded-[1.75rem] border border-white/70 bg-white/80 p-6 shadow-[0_18px_50px_rgba(38,49,65,0.08)] backdrop-blur dark:border-white/10 dark:bg-[#1e2a3a]/85">
              <h2 className="text-xl font-black tracking-[-0.03em] text-[#161e29] dark:text-white">
                {feature.title}
              </h2>
              <p className="mt-3 text-sm leading-7 text-[#5b6b7d] dark:text-[#b7c4d3]">
                {feature.description}
              </p>
            </div>
          ))}
        </section>

        <section className="mt-6 rounded-[1.75rem] border border-white/70 bg-[#263141] p-7 text-white shadow-[0_22px_60px_rgba(38,49,65,0.22)] dark:border-white/10">
          <div className="text-xs font-bold uppercase tracking-[0.2em] text-[#fad0cc]">
            What's inside
          </div>
          <h2 className="mt-3 text-3xl font-black tracking-[-0.05em]">
            Everything you need to run DMARC monitoring yourself.
          </h2>
          <div className="mt-6 grid gap-4 sm:grid-cols-2">
            {capabilities.map(item => (
              <div
                key={item.label}
                className="rounded-2xl border border-white/10 bg-white/10 p-4">
                <div className="text-xs font-bold uppercase tracking-[0.18em] text-[#fad0cc]">
                  {item.label}
                </div>
                <p className="mt-2 text-sm leading-7 text-white/85">{item.value}</p>
              </div>
            ))}
          </div>
        </section>

        <section className="mt-6 rounded-[1.75rem] border border-white/70 bg-white/85 p-7 shadow-[0_18px_50px_rgba(38,49,65,0.08)] backdrop-blur dark:border-white/10 dark:bg-[#1e2a3a]/85">
          <div className="grid gap-6 lg:grid-cols-[1fr_auto] lg:items-center">
            <div>
              <div className="text-xs font-bold uppercase tracking-[0.2em] text-[#e3594f] dark:text-[#fad0cc]">
                Get started
              </div>
              <h2 className="mt-2 text-2xl font-black tracking-[-0.04em] text-[#161e29] dark:text-white">
                Self-hosted, open source, ready to deploy.
              </h2>
              <p className="mt-3 max-w-2xl text-sm leading-7 text-[#3d4f63] dark:text-[#c7d0dc]">
                Run it with Docker Compose in minutes, or deploy the included Bicep template to
                Azure Container Apps. The docs cover both paths end to end.
              </p>
            </div>
            <div className="flex flex-wrap gap-3 lg:justify-end">
              <Link
                to="/docs/getting-started"
                className="inline-flex items-center justify-center rounded-full bg-[#e3594f] px-5 py-2.5 text-sm font-bold text-white hover:bg-[#c9443a] hover:no-underline">
                Read the docs
              </Link>
              <Link
                to="/docs/deploy-to-azure"
                className="inline-flex items-center justify-center rounded-full border border-[#263141]/20 bg-white px-5 py-2.5 text-sm font-bold text-[#161e29] hover:border-[#263141]/40 hover:bg-white hover:text-[#e3594f] hover:no-underline dark:border-white/15 dark:bg-[#1e2a3a] dark:text-white dark:hover:bg-white dark:hover:text-[#263141]">
                Deploy to Azure
              </Link>
            </div>
          </div>
        </section>
      </main>
    </Layout>
  );
}
```

- [ ] **Step 2: Verify the build**

Run: `cd website && npm run build`
Expected: SUCCESS, no errors.

- [ ] **Step 3: Manual verification**

If a browser is available in this environment: `npm run start`, open the homepage, confirm the hero renders, buttons link to `/docs/getting-started` and the GitHub repo, and toggling dark mode (the navbar's color-mode switch) recolors the page correctly. If not possible in this environment, report clearly that this step was skipped and why — not a blocker.

- [ ] **Step 4: Commit**

```bash
git add website/src/pages/index.tsx
git commit -m "Add the dotMARC homepage"
```

---

### Task 3: Docs content

**Files:**
- Modify: `website/docs/getting-started.md` (replaces Task 1's placeholder)
- Create: `website/docs/local-development.md`
- Create: `website/docs/deploy-to-azure.md`
- Create: `website/docs/permissions-and-access.md`
- Create: `website/docs/scope.md`

**Interfaces:**
- Consumes: Task 1's `sidebars.ts` (autogenerated from this folder + each page's `sidebar_position`).
- Produces: the five doc routes (`/docs/getting-started`, `/docs/local-development`, `/docs/deploy-to-azure`, `/docs/permissions-and-access`, `/docs/scope`) that Task 1's navbar/footer and Task 2's homepage already link to by these exact paths — do not rename any of these five files/slugs, or those existing links break.

No automated test — content pages, verified by build success (broken internal links fail the build per `onBrokenLinks: 'throw'`) and a manual read-through.

- [ ] **Step 1: Getting Started**

Replace `website/docs/getting-started.md` in full (content adapted from this repo's `README.md`, sections "One-time setup," "Configure," and "Run"):

```markdown
---
sidebar_position: 1
---

# Getting Started

dotMARC needs **two separate** Entra app registrations — do not reuse one for both purposes.

## 1. Mailbox access (app-only)

1. **App registrations** → **New registration**, name it e.g. `dotmarc-mailbox`.
2. **API permissions** → add Microsoft Graph **Application** permission `Mail.Read`, then grant
   admin consent.
3. **Certificates & secrets** → create a client secret.
4. Restrict this app's mailbox access via an Exchange **Application Access Policy**. Exchange
   requires the policy scope to be a **security principal** (for example a mail-enabled security
   group), not the mailbox itself. Create a dedicated group for the DMARC reports mailbox, add
   that mailbox to the group, and then scope the policy to the group:

   ```powershell
   Connect-ExchangeOnline -Organization <your-tenant>

   $group = New-DistributionGroup -Name "dotMARC DMARC Reports Scope" -Type Security -Alias dotmarc-dmarc-reports
   Add-DistributionGroupMember -Identity $group.Identity -Member "dmarc-reports@contoso.com"

   New-ApplicationAccessPolicy -AppId <client-id> -PolicyScopeGroupId $group.ObjectId -AccessRight RestrictAccess -Description "dotMARC: restrict to DMARC reports mailbox only"
   ```

   If you already have the mail-enabled security group, use its `ObjectId` instead of the mailbox
   address. Do not pass the mailbox address directly to `-PolicyScopeGroupId`.

## 2. Dashboard sign-in (delegated)

1. **App registrations** → **New registration**, name it e.g. `dotmarc-dashboard`.
2. **Authentication** → add a **Web** platform redirect URI:
   `https://<your-deployment-host>/signin-oidc`.
3. **Certificates & secrets** → create a client secret. Microsoft.Identity.Web wires this app up
   via the standard confidential-client authorization-code flow, so the token exchange after
   sign-in needs this secret even though the app itself is only used for interactive sign-in.
4. No API permissions needed beyond the default `User.Read`.

## Configure

Set via environment variables (double-underscore nesting):

| Variable | Description |
| --- | --- |
| `Graph__ClientId` | Mailbox app registration's client ID |
| `Graph__TenantId` | Your tenant ID |
| `Graph__ClientSecret` | Mailbox app registration's client secret |
| `Graph__MailboxAddress` | The shared mailbox address receiving DMARC reports |
| `Graph__PollIntervalSeconds` | Default `300` |
| `EntraId__TenantId` | Your tenant ID |
| `EntraId__ClientId` | Dashboard app registration's client ID |
| `EntraId__ClientSecret` | Dashboard app registration's client secret |
| `ConnectionStrings__DotMarc` | PostgreSQL connection string; defaults to `Host=localhost;Database=dotmarc;Username=dotmarc;Password=dotmarc` |
| `InitialAdmins__Emails` | Comma-separated list of email addresses granted the Admin role the very first time the app starts with no existing access grants — either a genuinely fresh install, or this app's first deploy of the permissions feature to an existing live environment. Only takes effect while the `UserAccess` table is empty; harmless to leave set afterwards. |

:::danger Set `InitialAdmins__Emails` before deploying this feature
Authorization is deny-by-default: with no existing access grants, the fallback policy locks out
every user, including the operator, unless `InitialAdmins__Emails` seeds at least one Admin grant
on that first startup. If you deploy without it, recovery requires direct database access to
insert a `UserAccess` row pointing at a locked `Admin` role.
:::

## Run

```powershell
$env:GRAPH_CLIENT_ID = '...'
$env:GRAPH_TENANT_ID = '...'
$env:GRAPH_CLIENT_SECRET = '...'
$env:GRAPH_MAILBOX_ADDRESS = '...'
$env:ENTRAID_TENANT_ID = '...'
$env:ENTRAID_CLIENT_ID = '...'
$env:ENTRAID_CLIENT_SECRET = '...'
docker compose up
```

This runs dotMARC and a PostgreSQL 18 database together, with Postgres data persisted in a named
Docker volume (`dotmarc-postgres-data`). Set the required environment variables from the setup
steps above (or put them in a `.env` file next to `docker-compose.yml` — compose reads that
automatically).

### Reverse proxy / TLS termination

The container listens on plain HTTP on port 8080; it expects a TLS-terminating reverse proxy
(nginx, Traefik, an Azure/AWS load balancer, etc.) in front of it, forwarding `X-Forwarded-For` and
`X-Forwarded-Proto`. Without this, sign-in fails with AADSTS50011 because the redirect URI sent to
Entra doesn't match the `https://` one registered on the dashboard app registration.

Point each monitored domain's DMARC record's `rua=` tag at the same mailbox this app polls, e.g.:

```txt
v=DMARC1; p=quarantine; rua=mailto:dmarc-reports@yourtenant.com
```

Next: [Local Development](./local-development.md) to run and test the app from source, or
[Deploy to Azure](./deploy-to-azure.md) to run it in production.
```

- [ ] **Step 2: Local Development**

Create `website/docs/local-development.md`:

```markdown
---
sidebar_position: 2
---

# Local Development

The repo is designed to work in two common local modes: run the full stack with Docker Compose, or
run the app locally while connecting to a PostgreSQL instance on `localhost:5432`.

## Build and test

```powershell
dotnet restore dotMARC.sln
dotnet build dotMARC.sln
dotnet test dotMARC.sln
```

`dotnet test` uses Testcontainers.PostgreSql, so Docker must be running; the first run pulls the
`postgres:18` image automatically.

## Local database-first flow

To start just the database for local debugging:

```powershell
docker compose up postgres
```

This publishes PostgreSQL on `localhost:5432` using the default development connection values from
`src/DotMarc/appsettings.json`: database `dotmarc`, username `dotmarc`, and password `dotmarc`.

If you want to run the app directly on the host instead of using the container stack, use:

```powershell
dotnet run --project src/DotMarc/DotMarc.csproj
```

The app expects the same PostgreSQL connection settings as the Docker Compose setup.

## Full stack with Docker Compose

```powershell
docker compose up --build
```

This starts dotMARC together with PostgreSQL in one command, using the repo's `docker-compose.yml`
configuration and the environment variables described in [Getting Started](./getting-started.md).
```

- [ ] **Step 3: Deploy to Azure**

Create `website/docs/deploy-to-azure.md`:

```markdown
---
sidebar_position: 3
---

# Deploy to Azure

`infra/main.bicep` provisions everything needed to run dotMARC on Azure:

* A **Container Apps environment** (backed by a Log Analytics workspace) and a **Container App**
  running the published dotMARC image, with a system-assigned managed identity, sticky sessions
  (required for Blazor Server), and a single replica.
* An **Azure Database for PostgreSQL Flexible Server** (`Standard_B1ms`, PostgreSQL 18) with a
  `dotmarc` database and a firewall rule allowing Azure services.
* A **Key Vault** (RBAC-authorized), with the Container App's managed identity granted the
  `Key Vault Secrets User` role.

Before deploying, complete the two Entra app registrations described in
[Getting Started](./getting-started.md) — the Bicep template takes the same non-secret client
IDs/tenant IDs as deployment parameters, and the client secrets are set into Key Vault after
deployment (see below).

## 1. Fill in the parameters

Copy `infra/main.parameters.json` and replace the `REPLACE_ME` placeholders — as checked in, the
file contains placeholder values only and is **not meant to be deployed as-is**. At minimum, set
`postgresAdminPassword`, `graphClientId`, `graphTenantId`, `graphMailboxAddress`,
`entraIdTenantId`, and `entraIdClientId`. For `containerImage`, use the GHCR image published by
CI/CD — `ghcr.io/homotechsual/dotmarc:latest`, or a specific version tag from a release — rather
than building your own.

Alternatively, leave the file untouched and pass overrides inline with `--parameters key=value` on
the command below.

## 2. Deploy

```powershell
$RG = 'your-dotmarc-resourcegroup'
az group create --name $RG --location uksouth
az deployment group create `
  --resource-group $RG `
  --template-file infra/main.bicep `
  --parameters infra/main.parameters.json
```

## 3. Register the deployed hostname as an OIDC redirect URI

The Container App's hostname isn't known until after deployment (Container Apps assigns its FQDN
from the environment's own DNS suffix), so the redirect URI registered in the dashboard sign-in app
registration during one-time setup can't be filled in ahead of time. Read the deployed URL from the
template's `containerAppUrl` output:

```powershell
az deployment group show --resource-group $RG --name main --query properties.outputs.containerAppUrl.value -o tsv
```

Then, in the `dotmarc-dashboard` app registration's **Authentication** blade, add a **Web**
platform redirect URI of `<that URL>/signin-oidc` (alongside or replacing the placeholder one
added during one-time setup). Sign-in will fail with AADSTS50011 until this is done.

## 4. Populate the Key Vault secrets

The template deliberately provisions three Key Vault secrets — `Graph-ClientSecret`,
`EntraId-ClientSecret`, and `ConnectionStrings-DotMarc` — empty, rather than accepting secret
material as deployment parameters (which would put it on the command line or in a parameters
file). Until these are set, the app can't sign in or reach Postgres. Populate them directly:

```powershell
$RG = 'dotmarc-rg'
$KV = az deployment group show --resource-group $RG --name main --query properties.outputs.keyVaultName.value -o tsv
$PG_FQDN = az deployment group show --resource-group $RG --name main --query properties.outputs.postgresServerFqdn.value -o tsv

az keyvault secret set --vault-name $KV --name Graph-ClientSecret --value "<graph app client secret>"
az keyvault secret set --vault-name $KV --name EntraId-ClientSecret --value "<entra id app client secret>"
az keyvault secret set --vault-name $KV --name ConnectionStrings-DotMarc `
  --value "Host=$PG_FQDN;Database=dotmarc;Username=<postgresAdminUsername>;Password=<postgresAdminPassword>;Ssl Mode=Require"

$APP = az deployment group show --resource-group $RG --name main --query properties.outputs.containerAppName.value -o tsv
$REVISION = az containerapp show --resource-group $RG --name $APP --query properties.latestRevisionName -o tsv
az containerapp revision restart --resource-group $RG --name $APP --revision $REVISION
```

Substitute the two Entra app registration client secrets created in the one-time setup steps
above, and the `postgresAdminUsername`/`postgresAdminPassword` values used in step 1. The
container app's secrets reference these by versionless Key Vault URL, so
`az containerapp revision restart` forces it to re-fetch them immediately rather than waiting for
their normal refresh cycle.

:::danger Don't forget InitialAdmins\_\_Emails
Set `InitialAdmins__Emails` (see [Getting Started](./getting-started.md#configure)) before this
deployment's first startup — without it, the tightened authorization policy locks out every user,
including you.
:::
```

- [ ] **Step 4: Permissions & Access**

Create `website/docs/permissions-and-access.md`:

```markdown
---
sidebar_position: 4
---

# Permissions & Access

dotMARC uses a fine-grained permissions model so internal staff and external clients can be
granted access the same way — by email, with an optional scope — rather than an all-or-nothing
login.

## Roles

A **Role** is a named bundle of permissions. Two are built in:

- **Admin** — every permission, locked (its name and permission set can never be changed or
  deleted).
- **Viewer** — read-only (`DomainsView`, `GroupsView`, `TagsView`), and the only role that can be
  restricted to specific Groups.

Admins can also create custom roles covering any subset of the available permissions — domain
management, Group/Tag management, and access management are each independently grantable.

## Granting access

Access is granted from the **Manage Access** page: type an email, pick a role, and — if the role
is Viewer — optionally pick which Groups they can see. This works identically whether the person
is internal staff or an external client contact.

- A grant made before someone's first sign-in stays pending until they actually authenticate, at
  which point it binds to their stable Entra object ID.
- A Viewer scoped to specific Groups can only ever see domains in those Groups — enforced at the
  data-query level, not just hidden in the UI, so this holds even via direct URL navigation to a
  domain outside their scope.
- An unscoped Viewer grant (no Groups selected) sees every domain, the same as before scoping was
  introduced.

## Bootstrapping the first Admin

See [Getting Started](./getting-started.md#configure) and
[Deploy to Azure](./deploy-to-azure.md) — the `InitialAdmins__Emails` environment variable grants
Admin to the listed emails the very first time the app starts with no existing access grants. This
is the only way to establish the first Admin; set it before your first deploy.
```

- [ ] **Step 5: Scope**

Create `website/docs/scope.md`:

```markdown
---
sidebar_position: 5
---

# Scope

dotMARC deliberately does not cover everything a DMARC tool could. Out of scope for this build:

- **Forensic (RUF) reports.** Only DMARC aggregate (RUA) reports are ingested and parsed.
- **Push notifications.** No email digests or real-time alerts — dotMARC is a dashboard you check,
  not a system that pages you.
- **Long-term raw-data rollups.** There's no 12-month historical raw-data aggregation job; reports
  are retained and queryable, but no separate rollup pipeline summarizes older data down.

If any of these matter for your use case, they're reasonable things to build on top of dotMARC's
existing data model, but they aren't part of what ships today.
```

- [ ] **Step 6: Verify the build**

Run: `cd website && npm run build`
Expected: SUCCESS — all internal links (`./local-development.md`, `./deploy-to-azure.md`, etc., plus the navbar/footer's `/docs/getting-started` etc. from Task 1) resolve; `onBrokenLinks: 'throw'` would otherwise fail the build.

- [ ] **Step 7: Commit**

```bash
git add website/docs/getting-started.md website/docs/local-development.md website/docs/deploy-to-azure.md website/docs/permissions-and-access.md website/docs/scope.md
git commit -m "Add docs content: getting started, local dev, Azure deploy, permissions, scope"
```

---

### Task 4: Blog setup

**Files:**
- Create: `website/blog/authors.yml`
- Create: `website/blog/tags.yml`

**Interfaces:**
- Consumes: Task 1's `docusaurus.config.ts` blog preset config (`routeBasePath: 'blog'`, already set).
- Produces: the `/blog` route (empty index, no posts) — nothing later tasks depend on.

No automated test — configuration/content files, verified by build success.

- [ ] **Step 1: Create the blog author registry**

Create `website/blog/authors.yml` (empty registry — filled in whenever the first post is written, per the spec's explicit deferral of blog content):

```yaml
# Blog post authors are declared here and referenced from each post's front matter
# (e.g. `authors: [someone]`). Empty until the first post is written.
```

- [ ] **Step 2: Create the blog tags registry**

Create `website/blog/tags.yml`:

```yaml
# Blog post tags are declared here and referenced from each post's front matter
# (e.g. `tags: [release]`). Empty until the first post is written.
```

- [ ] **Step 3: Verify the build**

Run: `cd website && npm run build`
Expected: SUCCESS — the blog plugin accepts an empty `blog/` folder (besides the two registry files) and produces an empty `/blog` index rather than erroring.

If the build fails because the blog plugin requires at least one post: create
`website/blog/2026-08-27-welcome.md` with genuinely minimal, real content (not a placeholder
stub) —

```markdown
---
slug: welcome
title: Welcome to the dotMARC blog
authors: []
tags: []
---

This is where dotMARC release notes and announcements will show up. Nothing to report yet — check
back after the next release.
```

— but only add this file if `npm run build` actually fails without it; report in your task summary
which path was taken.

- [ ] **Step 4: Commit**

```bash
git add website/blog/
git commit -m "Set up the dotMARC blog (empty, ready for future posts)"
```

---

### Task 5: Plugins — Plausible analytics + auto-generated OG images

**Files:**
- Modify: `website/docusaurus.config.ts`
- Modify: `website/package.json` (add two dependencies)
- Create: `website/lib/ImageRenderers.cjs`
- Create: `website/static/img/og-backgrounds/docs-gradient.svg`
- Create: `website/static/img/og-backgrounds/pages-gradient.svg`
- Create: `website/static/img/og-backgrounds/blog-gradient.svg`

**Interfaces:**
- Consumes: Task 1's `docusaurus.config.ts` (adds to its existing `plugins: []` array and `themeConfig.image` fallback), Task 1's brand palette.
- Produces: nothing later tasks depend on — this is the last content/config task before CI/deploy.

No automated test — plugin wiring and generated-asset content, verified by build success (the OG plugin runs at build time and would fail the build if misconfigured) and a manual check of one generated image.

- [ ] **Step 1: Add the plugin dependencies**

Add to `website/package.json`'s `dependencies` (keep alphabetical order among the existing entries):

```json
    "@homotechsual/docusaurus-og": "1.0.0-alpha.133-homotechsual.1",
    "@homotechsual/docusaurus-plugin-plausible": "^1.1.1",
```

Run: `cd website && npm install`
Expected: installs both packages cleanly.

- [ ] **Step 2: Create the OG background SVGs**

Create `website/static/img/og-backgrounds/docs-gradient.svg` (dark slate gradient, used behind docs-page OG cards):

```svg
<svg width="1200" height="630" viewBox="0 0 1200 630" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="g" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#161e29"/>
      <stop offset="55%" stop-color="#263141"/>
      <stop offset="100%" stop-color="#3d4f63"/>
    </linearGradient>
  </defs>
  <rect width="1200" height="630" fill="url(#g)"/>
</svg>
```

Create `website/static/img/og-backgrounds/pages-gradient.svg` (light gradient, used for general pages):

```svg
<svg width="1200" height="630" viewBox="0 0 1200 630" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="g" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#fcfcfc"/>
      <stop offset="45%" stop-color="#f7f8fa"/>
      <stop offset="100%" stop-color="#efaaa5"/>
    </linearGradient>
  </defs>
  <rect width="1200" height="630" fill="url(#g)"/>
</svg>
```

Create `website/static/img/og-backgrounds/blog-gradient.svg` (coral-to-slate gradient, used for blog posts/index):

```svg
<svg width="1200" height="630" viewBox="0 0 1200 630" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <linearGradient id="g" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#c9443a"/>
      <stop offset="55%" stop-color="#e3594f"/>
      <stop offset="100%" stop-color="#263141"/>
    </linearGradient>
  </defs>
  <rect width="1200" height="630" fill="url(#g)"/>
</svg>
```

- [ ] **Step 3: Create the OG image renderer**

Create `website/lib/ImageRenderers.cjs` — a dotMARC-specific adaptation of Heriau's renderer
(`J:\Projects\Heriau\website\lib\ImageRenderers.cjs`), simplified since dotMARC has no
locale/game-specific complexity — just three page categories (docs, pages, blog), one locale, one
brand:

```js
const React = require('react');
const {readFileSync} = require('fs');
const {join} = require('path');

const WIDTH = 1200;
const HEIGHT = 630;

function loadFont(...candidates) {
  for (const filePath of candidates) {
    try {
      return readFileSync(filePath);
    } catch {
      // Try next path.
    }
  }

  throw new Error(`Unable to load Inter font. Tried: ${candidates.join(', ')}`);
}

const inter400 = loadFont(
  join(process.cwd(), 'node_modules', '@fontsource', 'inter', 'files', 'inter-latin-400-normal.woff'),
);
const inter600 = loadFont(
  join(process.cwd(), 'node_modules', '@fontsource', 'inter', 'files', 'inter-latin-600-normal.woff'),
);
const inter900 = loadFont(
  join(process.cwd(), 'node_modules', '@fontsource', 'inter', 'files', 'inter-latin-900-normal.woff'),
);

const baseOptions = {
  width: WIDTH,
  height: HEIGHT,
  fonts: [
    {name: 'Inter', data: inter400, weight: 400, style: 'normal'},
    {name: 'Inter', data: inter600, weight: 600, style: 'normal'},
    {name: 'Inter', data: inter900, weight: 900, style: 'normal'},
  ],
};

const h = React.createElement;

function normalizeChildren(children) {
  return children.flat(Infinity).filter(child => child !== null && child !== undefined && child !== false);
}

function div(style, ...children) {
  return h('div', {style}, ...normalizeChildren(children));
}

function clampText(text, max = 220) {
  if (!text) {
    return '';
  }
  const normalized = String(text).replace(/\s+/g, ' ').trim();
  if (normalized.length <= max) {
    return normalized;
  }
  return `${normalized.slice(0, max - 1).trimEnd()}...`;
}

function toDataUrl(filePath) {
  try {
    const content = readFileSync(filePath);
    return `data:image/svg+xml;base64,${content.toString('base64')}`;
  } catch {
    return undefined;
  }
}

function backgroundDataUrl(name) {
  return toDataUrl(join(process.cwd(), 'static', 'img', 'og-backgrounds', `${name}.svg`));
}

function root(backgroundImage, content) {
  return div(
    {
      display: 'flex',
      width: WIDTH,
      height: HEIGHT,
      fontFamily: 'Inter',
      position: 'relative',
      overflow: 'hidden',
    },
    backgroundImage
      ? h('img', {
          src: backgroundImage,
          alt: '',
          style: {position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'cover'},
        })
      : null,
    content,
  );
}

function badge(text, background, color) {
  return div(
    {
      display: 'flex',
      alignSelf: 'flex-start',
      padding: '8px 14px',
      borderRadius: 999,
      background,
      color,
      fontSize: 22,
      fontWeight: 700,
    },
    text,
  );
}

const docs = data => {
  const title = clampText(data.metadata.title, 90);
  const description = clampText(data.metadata.description, 180);

  return [
    root(
      backgroundDataUrl('docs-gradient'),
      div(
        {
          display: 'flex',
          flexDirection: 'column',
          justifyContent: 'space-between',
          width: '100%',
          height: '100%',
          padding: 54,
          boxSizing: 'border-box',
        },
        div(
          {display: 'flex', flexDirection: 'column', gap: 14},
          badge('Docs', 'rgba(255,255,255,0.14)', '#fcfcfc'),
          div({display: 'flex', fontSize: 62, lineHeight: 1.04, fontWeight: 900, letterSpacing: -1.1, color: '#fcfcfc'}, title),
          description
            ? div({display: 'flex', maxWidth: 980, fontSize: 29, lineHeight: 1.24, color: '#c7d0dc'}, description)
            : null,
        ),
        div({display: 'flex', fontSize: 28, fontWeight: 700, color: '#fad0cc'}, 'dotMARC'),
      ),
    ),
    baseOptions,
  ];
};

const pages = data => {
  const title = clampText(data.metadata.title || 'dotMARC', 90);
  const description = clampText(data.metadata.description, 180);

  return [
    root(
      backgroundDataUrl('pages-gradient'),
      div(
        {
          display: 'flex',
          flexDirection: 'column',
          justifyContent: 'space-between',
          width: '100%',
          height: '100%',
          padding: 54,
          boxSizing: 'border-box',
        },
        div(
          {display: 'flex', flexDirection: 'column', gap: 14},
          badge('dotMARC', '#263141', '#fcfcfc'),
          div({display: 'flex', fontSize: 64, lineHeight: 1.02, fontWeight: 900, letterSpacing: -1.15, color: '#161e29'}, title),
          description
            ? div({display: 'flex', maxWidth: 980, fontSize: 30, lineHeight: 1.22, color: '#3d4f63'}, description)
            : null,
        ),
        div({display: 'flex', fontSize: 28, color: '#e3594f', fontWeight: 800}, 'dotmarc.app'),
      ),
    ),
    baseOptions,
  ];
};

const blog = data => {
  const pageData = data?.data || {};
  const metadata = pageData?.metadata || {};
  const isPost = data.pageType === 'post';
  const title = clampText(isPost ? metadata.title : 'dotMARC Blog', 90);
  const description = clampText(isPost ? metadata.description : 'Release notes and announcements from dotMARC.', 170);

  return [
    root(
      backgroundDataUrl('blog-gradient'),
      div(
        {
          display: 'flex',
          flexDirection: 'column',
          justifyContent: 'space-between',
          width: '100%',
          height: '100%',
          padding: 54,
          boxSizing: 'border-box',
        },
        div(
          {display: 'flex', flexDirection: 'column', gap: 14},
          badge('Blog', 'rgba(255,255,255,0.16)', '#fcfcfc'),
          div({display: 'flex', fontSize: 62, lineHeight: 1.04, fontWeight: 900, letterSpacing: -1.1, color: '#fcfcfc'}, title),
          description
            ? div({display: 'flex', maxWidth: 1000, fontSize: 29, lineHeight: 1.22, color: '#fbe4e2'}, description)
            : null,
        ),
        div({display: 'flex', fontSize: 28, fontWeight: 700, color: '#fcfcfc'}, 'dotMARC'),
      ),
    ),
    baseOptions,
  ];
};

module.exports = {docs, pages, blog};
```

- [ ] **Step 4: Wire both plugins into the Docusaurus config**

In `website/docusaurus.config.ts`, add near the top (after the existing `prism-react-renderer`
import):

```ts
import plausiblePlugin from '@homotechsual/docusaurus-plugin-plausible';
import type {PluginOptions as PlausiblePluginOptions} from '@homotechsual/docusaurus-plugin-plausible';

const {docs: docsOgRenderer, pages: pagesOgRenderer, blog: blogOgRenderer} = require('./lib/ImageRenderers.cjs');
const ogPlugin = require('@homotechsual/docusaurus-og');
```

Replace the currently-empty `presets` array's sibling — add a `plugins` array right after
`presets: [...]` closes (before `themeConfig:`):

```ts
  plugins: [
    [
      plausiblePlugin,
      {
        domain: 'dotmarc.app',
      } satisfies PlausiblePluginOptions,
    ],
    [
      ogPlugin,
      {
        path: './og-img',
        imageRenderers: {
          'docusaurus-plugin-content-docs': docsOgRenderer,
          'docusaurus-plugin-content-pages': pagesOgRenderer,
          'docusaurus-plugin-content-blog': blogOgRenderer,
        },
      },
    ],
  ],
```

- [ ] **Step 5: Point the static OG fallback at the new background asset**

Now that `website/static/img/og-backgrounds/pages-gradient.svg` exists, update
`website/docusaurus.config.ts`'s `themeConfig.image` from Task 1's placeholder
(`'img/favicon.svg'` — a tiny icon, not a usable OG-card image) to
`'img/og-backgrounds/pages-gradient.svg'`. This is the fallback social-share image for any route
the OG plugin's `imageRenderers` don't cover (the OG plugin overrides per-page images itself for
docs/pages/blog routes, so this fallback is rarely hit, but it should be a real card-shaped image
rather than the favicon when it is).

- [ ] **Step 6: Verify the build**

Run: `cd website && npm run build`
Expected: SUCCESS. Check that `website/build/og-img/` (or wherever the OG plugin writes its
output — confirm the actual output path from the build log) contains generated PNG/JPEG files for
at least the homepage and one docs page.

If the build fails because `@homotechsual/docusaurus-og` needs different `imageRenderers` argument
shapes than shown above, or a different plugin option name: check the plugin's actual type
definitions (`node_modules/@homotechsual/docusaurus-og`) rather than guessing further, and adjust
to match — report any such deviation clearly in your task summary.

- [ ] **Step 6: Manual verification**

If a browser is available: `npm run start`, view a docs page's source/dev tools to confirm an
`og:image` meta tag pointing at a generated (not the static fallback) image. If not possible,
report that this step was skipped.

- [ ] **Step 7: Commit**

```bash
git add website/docusaurus.config.ts website/package.json website/package-lock.json website/lib/ImageRenderers.cjs website/static/img/og-backgrounds/
git commit -m "Wire up Plausible analytics and auto-generated OG images"
```

---

### Task 6: CI/CD — path-scoped workflows + Cloudflare Pages deploy

**Files:**
- Create: `.github/workflows/website-ci.yml`
- Create: `.github/workflows/website-publish.yml`

**Interfaces:**
- Consumes: the completed `website/` project from Tasks 1-5 (`npm run build` must succeed).
- Produces: nothing later tasks depend on — this is the final task.

No automated test in the traditional sense — this task's own "test" is the workflow actually
running successfully on GitHub Actions once pushed, which can't be verified locally. Verify what
can be verified locally (YAML validity, the build command it invokes) and note the rest as
requiring a live push to confirm.

- [ ] **Step 1: Create the build-verification workflow**

Create `.github/workflows/website-ci.yml`:

```yaml
name: Website CI

on:
  push:
    branches:
      - main
    paths:
      - 'website/**'
      - '.github/workflows/website-ci.yml'
  pull_request:
    paths:
      - 'website/**'
      - '.github/workflows/website-ci.yml'

concurrency:
  group: website-ci-${{ github.workflow }}-${{ github.event.pull_request.head.ref || github.ref }}
  cancel-in-progress: true

jobs:
  build:
    name: Build
    runs-on: ubuntu-latest
    timeout-minutes: 15
    defaults:
      run:
        working-directory: website

    steps:
      - name: Checkout
        uses: actions/checkout@v7

      - name: Setup Node
        uses: actions/setup-node@v6
        with:
          node-version: lts/*
          cache: npm
          cache-dependency-path: website/package-lock.json

      - name: Install dependencies
        run: npm ci

      - name: Typecheck
        run: npm run typecheck

      - name: Build
        run: npm run build
```

- [ ] **Step 2: Create the publish workflow**

Create `.github/workflows/website-publish.yml` (modeled on
`J:\Projects\Heriau\website\.github\workflows\publish.yml`, simplified — no i18n build matrix, no
Cloudflare Worker to deploy alongside it):

```yaml
name: Website Publish

on:
  push:
    branches:
      - main
    paths:
      - 'website/**'
      - '.github/workflows/website-publish.yml'
  pull_request:
    types:
      - opened
      - synchronize
    paths:
      - 'website/**'
      - '.github/workflows/website-publish.yml'
  workflow_dispatch:

permissions:
  contents: read
  deployments: write
  pull-requests: write

jobs:
  deploy:
    name: Deploy
    runs-on: ubuntu-latest
    timeout-minutes: 15
    defaults:
      run:
        working-directory: website
    env:
      CF_PAGES_PROJECT: ${{ vars.CF_PAGES_PROJECT_DOTMARC || 'dotmarc-website' }}

    concurrency:
      group: website-publish-${{ github.workflow }}-${{ github.event.pull_request.head.ref || github.ref }}
      cancel-in-progress: true

    if: ${{ github.event_name != 'pull_request' || github.event.pull_request.head.repo.fork == false }}

    steps:
      - name: Checkout
        uses: actions/checkout@v7
        with:
          ref: ${{ github.event.pull_request.head.sha || github.sha }}

      - name: Setup Node
        uses: actions/setup-node@v6
        with:
          node-version: lts/*
          cache: npm
          cache-dependency-path: website/package-lock.json

      - name: Install dependencies
        run: npm ci

      - name: Build
        run: npm run build

      - name: Publish to Cloudflare Pages
        id: cloudflare-pages-deploy
        uses: cloudflare/wrangler-action@v4
        with:
          accountId: ${{ secrets.CLOUDFLARE_ACCOUNT_ID }}
          apiToken: ${{ secrets.CLOUDFLARE_API_TOKEN }}
          workingDirectory: website
          command: pages deploy build --project-name=${{ env.CF_PAGES_PROJECT }} --branch=${{ github.event_name == 'pull_request' && github.head_ref || github.ref_name }}
          gitHubToken: ${{ secrets.GITHUB_TOKEN }}

      - name: Resolve deployment URL
        id: deployment-url
        shell: bash
        env:
          DEPLOYMENT_URL: ${{ steps.cloudflare-pages-deploy.outputs.deployment-url }}
          ALIAS_URL: ${{ steps.cloudflare-pages-deploy.outputs.pages-deployment-alias-url }}
        run: |
          preview_url="$ALIAS_URL"
          if [ -z "$preview_url" ] || [ "$preview_url" = "undefined" ]; then
            preview_url="$DEPLOYMENT_URL"
          fi
          echo "production_url=https://dotmarc.app" >> "$GITHUB_OUTPUT"
          echo "deployment_url=$DEPLOYMENT_URL" >> "$GITHUB_OUTPUT"
          echo "preview_url=$preview_url" >> "$GITHUB_OUTPUT"

      - name: Write deployment summary
        shell: bash
        env:
          ENVIRONMENT: ${{ steps.cloudflare-pages-deploy.outputs.pages-environment }}
          PRODUCTION_URL: ${{ steps.deployment-url.outputs.production_url }}
          DEPLOYMENT_URL: ${{ steps.deployment-url.outputs.deployment_url }}
          PREVIEW_URL: ${{ steps.deployment-url.outputs.preview_url }}
        run: |
          {
            echo "## Cloudflare Pages deployment"
            echo
            echo "| Name | Value |"
            echo "| --- | --- |"
            if [ "${{ github.event_name }}" = "push" ] && [ "${{ github.ref }}" = "refs/heads/main" ]; then
              echo "| Production URL | [$PRODUCTION_URL]($PRODUCTION_URL) |"
            else
              echo "| Preview URL | [$PREVIEW_URL]($PREVIEW_URL) |"
            fi
            echo "| Deployment URL | [$DEPLOYMENT_URL]($DEPLOYMENT_URL) |"
            echo "| Environment | $ENVIRONMENT |"
          } >> "$GITHUB_STEP_SUMMARY"

      - name: Create PR comment
        if: ${{ github.event_name == 'pull_request' }}
        uses: mshick/add-pr-comment@v3
        with:
          message: |
            ### Cloudflare Pages preview deployed

            | Item | Value |
            | --- | --- |
            | Preview URL | [${{ steps.deployment-url.outputs.preview_url }}](${{ steps.deployment-url.outputs.preview_url }}) |
            | Deployment URL | [${{ steps.deployment-url.outputs.deployment_url }}](${{ steps.deployment-url.outputs.deployment_url }}) |
            | Environment | ${{ steps.cloudflare-pages-deploy.outputs.pages-environment }} |
```

- [ ] **Step 3: Validate the workflow YAML**

Run: `cd .github/workflows && python3 -c "import yaml,sys; [yaml.safe_load(open(f)) for f in ['website-ci.yml','website-publish.yml']]; print('valid')"` (or any available YAML validator/linter — the point is confirming both files parse as valid YAML before committing, since a syntax error here would only surface on a live push).

Expected: `valid` (or equivalent no-error output).

- [ ] **Step 4: Report setup steps that need the repo owner, not this task**

This task cannot itself: create the Cloudflare Pages project (`CF_PAGES_PROJECT_DOTMARC` repo
variable, defaulting to `dotmarc-website` if unset — matches the workflow above), set the
`CLOUDFLARE_ACCOUNT_ID`/`CLOUDFLARE_API_TOKEN` repo secrets, or attach the `dotmarc.app` custom
domain to that Cloudflare Pages project. Note this clearly in your task report as required manual
setup before `website-publish.yml` can succeed on a real push — this is expected and matches the
spec's explicit Non-goal ("DNS configuration for dotmarc.app... an operator action outside what an
implementation plan can do").

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/website-ci.yml .github/workflows/website-publish.yml
git commit -m "Add path-scoped CI/publish workflows for the dotMARC website"
```
