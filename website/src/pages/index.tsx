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
      'Point every monitored domain’s DMARC record at a single shared mailbox. dotMARC polls it, parses aggregate reports, and attributes them back to the right domain automatically.',
  },
  {
    title: 'Built for MSPs',
    description:
      'Fine-grained, per-domain access control means you can grant an external client visibility into just their own domains — scoped Viewer roles, not an all-or-nothing login.',
  },
  {
    title: 'Self-hosted, your data',
    description:
      'Runs on your own infrastructure — Docker Compose or Azure Container Apps — backed by PostgreSQL. Aggregate report data never leaves your environment.',
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
      'Fine-grained permissions — roles, Group-scoped Viewer grants for external clients, and an Admin/Viewer preset out of the box.',
  },
  {
    label: 'DNS status checks',
    value:
      'Live DNS lookups confirm each monitored domain’s DMARC record is actually in place and pointed at the right mailbox.',
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
