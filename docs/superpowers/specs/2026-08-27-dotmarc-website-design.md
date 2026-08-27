# dotMARC Website Design

## Overview

dotMARC currently has no public-facing website — the README is the only
front door, and it's a developer-facing setup reference rather than
something that pitches the product or gives prospective operators
(predominantly MSPs, per the app's target market) a reason to try it.
This design adds a marketing + docs + blog website, built with
Docusaurus and Tailwind CSS, deployed to `dotmarc.app`.

It deliberately follows the pattern already proven on
`J:\Projects\Heriau\website` — the same major-version stack (Docusaurus
3.x classic preset, TypeScript, Tailwind v4), the same balance of a
bespoke homepage against a reskinned-but-structurally-standard docs/blog
experience, and the same plugin choices
(`@homotechsual/docusaurus-plugin-plausible`,
`@homotechsual/docusaurus-og`) — so implementation risk is "apply a
known-good pattern to new content," not "discover Docusaurus/Tailwind
integration quirks from scratch."

## Goals

- A homepage that pitches dotMARC to its actual audience: self-hosted
  DMARC aggregate report monitoring across multiple domains from one
  mailbox, aimed predominantly at MSPs.
- A docs section that turns the README's existing setup/deployment
  content into a proper browsable reference, plus new coverage of the
  just-shipped permissions/access model.
- A blog mechanism (RSS/Atom feed, reading time) ready for future
  release-note posts — no launch post required now.
- Visual identity consistent with the product itself: dotMARC's existing
  dashboard palette (coral `#e3594f` primary, dark slate `#263141`
  secondary, plus its dark-mode variant), not a new palette invented for
  the marketing site alone.
- Self-hosted fonts (bundled via `@fontsource` packages) — no
  `fonts.googleapis.com` calls, unlike Heriau's own (redundant, both
  `@fontsource` AND a Google Fonts `@import`) setup.
- Deployed to `dotmarc.app` via Cloudflare Pages, with its own
  path-scoped GitHub Actions CI/publish workflows that don't fire on
  changes to the .NET app, and don't get triggered by changes to the
  website.

## Non-goals

- **Replacing the README.** It stays as-is on GitHub — build/test
  commands, quick reference for people already in the repo. The docs
  site is the fuller, browsable version for evaluators and deployers,
  not a replacement; content is *derived from* the README, not a
  cross-link-only stub.
- **Internationalization.** Unlike Heriau (en/cy), this is a single-locale
  (English) site — no locale dropdown, no per-locale build matrix, no
  `i18n` config beyond Docusaurus's default.
- **Playwright visual regression tests.** Skipped for v1 — the site's
  design will move around early on; snapshot tests would need constant
  updating for no real payoff yet. A later, explicit follow-up once the
  design settles.
- **Fully custom docs/blog templates.** Docs and blog keep Docusaurus's
  standard classic-theme structure (sidebar, TOC, prev/next) — reskinned
  via `custom.css`/Tailwind, not rebuilt via swizzled custom layout
  components. Only the homepage is fully bespoke.
- **A first blog post.** The mechanism ships; content is a follow-up.
- **DNS configuration for `dotmarc.app`.** This design produces a
  Cloudflare Pages project ready to receive that custom domain — actually
  pointing the domain at Cloudflare (registrar/DNS changes) is an
  operator action outside what an implementation plan can do.

## Site structure

Three top-level sections, all under `website/` (a new folder in this
repo, with its own `package.json`/`node_modules`, independent of
`dotMARC.sln`):

**Homepage (`/`)** — fully bespoke, Tailwind-first, matching Heriau's
actual homepage pattern (not the default Docusaurus `HomepageFeatures`
scaffold): a hero section pitching dotMARC's value proposition directly
(self-hosted, multi-domain, single-mailbox DMARC monitoring; who it's
for — MSPs managing client domains, but equally usable by a single
organization), a feature-highlight section, and CTAs toward the docs'
deployment guide and the GitHub repo.

**Docs (`/docs`)** — restructured from the README's existing content
into separate, browsable pages rather than one long file:

- *Getting Started* — the two Entra app registrations (mailbox
  application access + dashboard delegated sign-in), the full
  environment-variable configuration table (including
  `InitialAdmins__Emails` and its lockout warning, carried over
  verbatim from the README).
- *Local Development* — Docker Compose modes, database-first local flow,
  build/test commands.
- *Deploy to Azure* — the four-step Bicep deployment flow (parameters,
  deploy, register the OIDC redirect URI, populate Key Vault secrets).
- *Permissions & Access* — new content, not in the README: the
  `Permission`/`Role`/`UserAccess` model, built-in Admin/Viewer roles,
  Group-scoped Viewer grants as the external-client-access mechanism,
  and the `InitialAdmins__Emails` bootstrap flow.
- *Scope* — carried over from the README's Scope section (forensic/RUF
  reports, push notifications, the 12-month rollup job are deliberately
  out of scope).

**Blog (`/blog`)** — Docusaurus's standard blog plugin, RSS/Atom feed,
reading time enabled, matching Heriau's `feedOptions`/`onInlineTags`
etc. configuration. No posts at launch.

## Visual design

- **Palette**: reused from `src/DotMarc/Components/Layout/MainLayout.razor`'s
  existing `MudTheme` — light mode Primary `#e3594f` /
  PrimaryLighten `#efaaa5` / PrimaryDarken `#c9443a`, Secondary
  `#263141` / SecondaryLighten `#3d4f63` / SecondaryDarken `#1a2230`;
  dark mode Primary `#ef8b86` / PrimaryLighten `#fad0cc` / PrimaryDarken
  `#e3594f`, Background `#111827`, Surface `#1e2a3a`. These map onto CSS
  custom properties (mirroring Heriau's `--ifm-color-primary*`
  overrides in `custom.css`) so both Infima's own components and
  Tailwind utility classes read from the same source of truth.
- **Typography**: Inter, self-hosted via the `@fontsource/inter` npm
  package (no Google Fonts CDN) — regular/medium weights for body/UI
  text, black (900) weight for headings, giving clear hierarchy from one
  typeface family rather than a second display font. Reflects a
  self-hosted infrastructure tool's audience rather than Heriau's
  playful Nunito.
- **Layout language**: Heriau's actual visual patterns, recolored —
  radial-gradient hero background, glassmorphic cards (`backdrop-blur`,
  translucent white/dark panels, `rounded-[1.75rem]`-scale corners),
  full dark-mode support via `[data-theme="dark"]` (respecting
  `prefers-color-scheme` by default, matching Heriau's
  `respectPrefersColorScheme: true`).
- **Navbar**: frosted/blurred background, logo-only (no separate title
  text) — matching both Heriau's navbar convention and dotMARC's own
  MudBlazor app bar.
- **Footer**: standard Docusaurus footer with docs/blog/GitHub links,
  keeping the "site made by homotechsual" attribution line Heriau uses.
- **Tailwind integration**: Tailwind v4 via `@tailwindcss/postcss`, with
  `corePlugins.preflight` and `corePlugins.container` disabled so
  Tailwind's utility classes coexist with Infima (Docusaurus's bundled
  CSS framework) instead of fighting its base styles/container
  behavior — the exact configuration Heriau uses, since this is a known
  integration point that needs it.

## Plugins & integrations

- `@homotechsual/docusaurus-plugin-plausible` — privacy-friendly
  analytics, configured with `domain: 'dotmarc.app'`.
- `@homotechsual/docusaurus-og` — auto-generated per-page social share
  images (docs pages, blog posts, standalone pages each get their own
  OG image at build time), matching Heriau's `imageRenderers`
  wiring pattern (though dotMARC's renderer content will need its own
  design — Heriau's renderers are specific to its own brand imagery).
- `onBrokenLinks: 'throw'` — matching Heriau's config, so a broken
  internal link fails the build rather than shipping silently.

## Deployment

A new Cloudflare Pages project under the **Homotechsual** Cloudflare
account (account ID `e5d7b48988c7e30b93f96550fd4f65d5`), with
`dotmarc.app` attached as its custom domain. GitHub
Actions publish workflow modeled directly on Heriau's
`.github/workflows/publish.yml`: checkout, Node setup, `npm ci`
(no Yarn-specific steps, since there's no i18n build matrix here),
`npm run build`, deploy via `cloudflare/wrangler-action`'s
`pages deploy` command. Single build/deploy per push (no per-locale
matrix), PR preview deployments via the same mechanism Heriau uses
(Cloudflare Pages' branch-based preview URLs).

**Path-scoped CI**: both the build-verification workflow and the
publish workflow trigger only on changes under `website/` (via GitHub
Actions' `paths:` filter), so editing docs content doesn't invoke the
.NET solution's `ci.yml`, and changes to `src/DotMarc`/`test/` don't
invoke the website's workflows. This requires two new workflow files
(e.g. `website-ci.yml`, `website-publish.yml`) alongside the existing
`ci.yml`/`publish.yml`, each scoped with a `paths: ['website/**']`
trigger filter.

## Testing

- **Build verification**: the website's CI workflow runs `npm run build`
  (which includes `onBrokenLinks: 'throw'` and a TypeScript typecheck,
  matching Heriau's `prebuild`/`typecheck` scripts) on every push/PR
  touching `website/` — this is the primary automated check for a
  content-and-presentation site, catching broken links, type errors, and
  build failures before merge.
- **No visual regression tests** for v1, per the Non-goals section — a
  deliberate, explicit deferral, not an oversight.
- **No unit tests** — there's no application logic here to unit test;
  this is a static site.
