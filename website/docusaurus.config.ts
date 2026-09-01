import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';
import plausiblePlugin from '@homotechsual/docusaurus-plugin-plausible';
import type {PluginOptions as PlausiblePluginOptions} from '@homotechsual/docusaurus-plugin-plausible';

const {docs: docsOgRenderer, pages: pagesOgRenderer, blog: blogOgRenderer} = require('./lib/ImageRenderers.cjs');
const ogPlugin = require('@homotechsual/docusaurus-og');

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

  themeConfig: {
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
        {to: '/blog', label: 'Blog', position: 'left'},
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
            {label: 'Deploy to Azure', to: '/docs/deploy-to-azure'},
            {label: 'Permissions & Access', to: '/docs/permissions-and-access'},
          ],
        },
        {
          title: 'More',
          items: [
            {label: 'Blog', to: '/blog'},
            {label: 'Demo', to: 'https://demo.dotmarc.app/'},
            {label: 'GitHub', href: 'https://github.com/homotechsual/dotMARC'},
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} dotMARC.<br /><span class="designedBy">Made with <svg xmlns="http://www.w3.org/2000/svg" class="heart" width="24" height="24" viewBox="0 0 24 24"><path d="M14 20.408c-.492.308-.903.546-1.192.709-.153.086-.308.17-.463.252h-.002a.75.75 0 01-.686 0 16.709 16.709 0 01-.465-.252 31.147 31.147 0 01-4.803-3.34C3.8 15.572 1 12.331 1 8.513 1 5.052 3.829 2.5 6.736 2.5 9.03 2.5 10.881 3.726 12 5.605 13.12 3.726 14.97 2.5 17.264 2.5 20.17 2.5 23 5.052 23 8.514c0 3.818-2.801 7.06-5.389 9.262A31.146 31.146 0 0114 20.408z"/></svg>
        by <a href="https://homotechsual.dev">homotechsual</a></span>.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
