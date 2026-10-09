import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';
import plausiblePlugin from '@homotechsual/docusaurus-plugin-plausible';
import type {PluginOptions as PlausiblePluginOptions} from '@homotechsual/docusaurus-plugin-plausible';
import faqsPlugin from '@homotechsual/docusaurus-plugin-faqs';
import type {PluginOptions as FaqsPluginOptions} from '@homotechsual/docusaurus-plugin-faqs';
import type * as OpenApiPlugin from "docusaurus-plugin-openapi-docs";

const {docs: docsOgRenderer, pages: pagesOgRenderer, blog: blogOgRenderer} = require('./lib/ImageRenderers.cjs');
const ogPlugin = require('@homotechsual/docusaurus-og');
const {getLatestVersion} = require('./lib/latest-version.cjs');

// Docusaurus gives Rspack a persistent cache in node_modules/.cache even with rspackPersistentCache off, and it isn't
// invalidated when packages change or branches switch, so `start` can crash with "ModuleGraphModule ... not found"
// until `yarn clear`. Turn it off for the dev server only: `docusaurus build` sets NODE_ENV to production before
// loading this file, so builds keep their cache. It's read when the bundler config is built, after this file loads.
if (process.env.NODE_ENV === 'development') {
  process.env.DOCUSAURUS_NO_PERSISTENT_CACHE ??= 'true';
}

const siteTitle = 'dotMARC';
const siteTagline = 'Self-hosted DMARC monitoring for every client domain, from one mailbox.';
const siteDescription =
  'dotMARC is a self-hosted DMARC aggregate report analyzer for monitoring email authentication posture across multiple domains from a single mailbox, built for MSPs managing client domains, and equally usable by a single organization.';
const siteUrl = 'https://dotmarc.app';

const config: Config = {
  title: siteTitle,
  tagline: siteTagline,
  favicon: 'img/favicon.svg',

  future: {
    v4: true, // Improve compatibility with the upcoming Docusaurus v4
    faster: {
      // ssgWorkerThreads defaults to true under future.v4, but its worker
      // threads can still hold a handle on build/__server when the main
      // process tries to delete it, causing a Windows EBUSY race during
      // `docusaurus build` (see task-1-report.md for the reproduction).
      // Static site generation still runs, just on the main thread.
      ssgWorkerThreads: false,
      rspackPersistentCache: false,
    },
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
          // The content folder is still website/blog; only the public route and titles changed. The old
          // /blog/* URLs are redirected to /releases-updates/* by the client-redirects plugin below.
          routeBasePath: 'releases-updates',
          blogTitle: 'Releases and Updates',
          blogDescription: 'Release notes and announcements from dotMARC.',
          blogSidebarTitle: 'Recent releases',
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

  plugins: [
    [
      '@docusaurus/plugin-content-docs',
      {
        id: 'api',
        path: 'api',
        routeBasePath: 'api',
        sidebarPath: './apiSidebar.ts',
        docItemComponent: '@theme/ApiItem',
      },
    ],
    [
      // Keeps every old /blog/* link working (release notes linked from Canny changelog entries, GitHub
      // releases, the app footer and other sites). It writes a small redirect page for each existing
      // /releases-updates route at the matching /blog route, and only does so in a production build.
      '@docusaurus/plugin-client-redirects',
      {
        createRedirects(existingPath: string) {
          if (existingPath === '/releases-updates' || existingPath.startsWith('/releases-updates/')) {
            return [existingPath.replace('/releases-updates', '/blog')];
          }
          return undefined;
        },
      },
    ],
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
    './src/plugins/featureRequests/FeatureRequestsPlugin',
    [
      faqsPlugin,
      {
        path: 'data/faqs',
        routeBasePath: 'faqs',
      } satisfies FaqsPluginOptions,
    ],
    [ 
      'docusaurus-plugin-openapi-docs',
      {
        id: 'api',
        docsPluginId: 'api',
        config: {
          dotmarc: {
            specPath: 'data/openapi/dotmarc-api.json',
            outputDir: 'api',
            sidebarOptions: {
              categoryLinkSource: 'tag',
              groupPathsBy: 'tag',
              sidebarCollapsed: false,
            }
          } satisfies OpenApiPlugin.Options,
        },
      } 
    ],
  ],
  themes: [
    'docusaurus-theme-openapi-docs',
    // Code blocks marked `reference` load a file from GitHub, so the docs show scripts as they are in the repo.
    'docusaurus-theme-github-codeblock',
  ],
  themeConfig: {
    codeblock: {
      showGithubLink: true,
      githubLinkLabel: 'View on GitHub',
    },
    image: 'img/og-backgrounds/pages-gradient.svg',
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
        {to: '/api/dotmarc-api', label: 'API', position: 'left', activeBaseRegex: '^/api/'},
        {to: '/faqs', label: 'FAQs', position: 'left'},
        {to: '/releases-updates', label: 'Releases and Updates', position: 'left'},
        {to: 'https://demo.dotmarc.app/', label: 'Demo', position: 'left'},
        {to: '/feedback', label: 'Feedback & Feature Requests', position: 'left'},
        {
          to: 'https://github.com/homotechsual/dotMARC',
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
            {label: 'Deploy with Docker', to: '/docs/deploy-with-docker'},
            {label: 'Deploy to Azure', to: '/docs/deploy-to-azure'},
            {label: 'Permissions & Access', to: '/docs/permissions-and-access'},
            {label: 'FAQs', to: '/faqs'},
          ],
        },
        {
          title: 'More',
          items: [
            {label: 'Releases and Updates', to: '/releases-updates'},
            {label: 'Demo', to: 'https://demo.dotmarc.app/'},
            {label: 'GitHub', href: 'https://github.com/homotechsual/dotMARC'},
            {label: 'License', to: '/license'},
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} dotMARC.<br /><span class="designedBy">Made with <svg xmlns="http://www.w3.org/2000/svg" class="heart" width="24" height="24" viewBox="0 0 24 24"><path d="M14 20.408c-.492.308-.903.546-1.192.709-.153.086-.308.17-.463.252h-.002a.75.75 0 01-.686 0 16.709 16.709 0 01-.465-.252 31.147 31.147 0 01-4.803-3.34C3.8 15.572 1 12.331 1 8.513 1 5.052 3.829 2.5 6.736 2.5 9.03 2.5 10.881 3.726 12 5.605 13.12 3.726 14.97 2.5 17.264 2.5 20.17 2.5 23 5.052 23 8.514c0 3.818-2.801 7.06-5.389 9.262A31.146 31.146 0 0114 20.408z"/></svg>
        by <a href="https://homotechsual.dev">homotechsual</a></span>.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: [
        "ruby",
        "csharp",
        "php",
        "java",
        "powershell",
        "json",
        "bash",
        "dart",
        "objectivec",
        "r",
      ],
    },
    languageTabs: [
      {
        highlight: "python",
        language: "python",
        logoClass: "python",
      },
      {
        highlight: "bash",
        language: "curl",
        logoClass: "curl",
      },
      {
        highlight: "csharp",
        language: "csharp",
        logoClass: "csharp",
      },
      {
        highlight: "go",
        language: "go",
        logoClass: "go",
      },
      {
        highlight: "javascript",
        language: "nodejs",
        logoClass: "nodejs",
      },
      {
        highlight: "ruby",
        language: "ruby",
        logoClass: "ruby",
      },
      {
        highlight: "php",
        language: "php",
        logoClass: "php",
      },
      {
        highlight: "java",
        language: "java",
        logoClass: "java",
        variant: "unirest",
      },
      {
        highlight: "powershell",
        language: "powershell",
        logoClass: "powershell",
      },
      {
        highlight: "dart",
        language: "dart",
        logoClass: "dart",
      },
      {
        highlight: "javascript",
        language: "javascript",
        logoClass: "javascript",
      },
      {
        highlight: "c",
        language: "c",
        logoClass: "c",
      },
      {
        highlight: "objective-c",
        language: "objective-c",
        logoClass: "objective-c",
      },
      {
        highlight: "ocaml",
        language: "ocaml",
        logoClass: "ocaml",
      },
      {
        highlight: "r",
        language: "r",
        logoClass: "r",
      },
      {
        highlight: "swift",
        language: "swift",
        logoClass: "swift",
      },
      {
        highlight: "kotlin",
        language: "kotlin",
        logoClass: "kotlin",
      },
      {
        highlight: "rust",
        language: "rust",
        logoClass: "rust",
      },
    ],
    api: {
      schemaExpansion: {
        enabled: true,
        default: 1,
        max: 4,
      },
    },
  } satisfies Preset.ThemeConfig,
};

// Async so the latest released version can be looked up at build time. It reaches the docs (<LatestVersion />) and
// the homepage through customFields, so neither needs editing at a release. See lib/latest-version.cjs.
export default async function createConfigAsync(): Promise<Config> {
  const latestVersion: string | null = await getLatestVersion();
  return {...config, customFields: {...config.customFields, latestVersion}};
}
