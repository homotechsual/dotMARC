import type {ReactNode} from 'react';
import Link from '@docusaurus/Link';
import Layout from '@theme/Layout';
import Head from '@docusaurus/Head';
import ThemedImage from '@theme/ThemedImage';
import useBaseUrl from '@docusaurus/useBaseUrl';
import {useLatestVersion} from '@site/src/components/LatestVersion';
import {siDiscord, siDocker, siGithub} from 'simple-icons';
import styles from './index.module.css';

// Brand marks come from simple-icons. The play and book shapes are plain Material icons, which have no brand to show. They draw in the button's text colour and are hidden from screen readers, since the label says it all.
const iconPaths = {
  demo: 'M8 5v14l11-7z',
  docs: 'M21 5c-1.11-.35-2.33-.5-3.5-.5-1.95 0-4.05.4-5.5 1.5-1.45-1.1-3.55-1.5-5.5-1.5S2.45 4.9 1 6v14.65c0 .25.25.5.5.5.1 0 .15-.05.25-.05C3.1 20.45 5.05 20 6.5 20c1.95 0 4.05.4 5.5 1.5 1.35-.85 3.8-1.5 5.5-1.5 1.65 0 3.35.3 4.75 1.05.1.05.15.05.25.05.25 0 .5-.25.5-.5V6c-.6-.45-1.25-.75-2-1zm0 13.5c-1.1-.35-2.3-.5-3.5-.5-1.7 0-4.15.65-5.5 1.5V8c1.35-.85 3.8-1.5 5.5-1.5 1.2 0 2.4.15 3.5.5v11.5z',
  discord: siDiscord.path,
  github: siGithub.path,
  docker: siDocker.path,
};

function Icon({name}: {name: keyof typeof iconPaths}) {
  return (
    <svg className={styles.icon} viewBox="0 0 24 24" width="22" height="22" fill="currentColor" aria-hidden="true">
      <path d={iconPaths[name]} />
    </svg>
  );
}

// The outline Azure mark from Microsoft's own "Deploy to Azure" button (https://aka.ms/deploytoazurebutton), saved as
// static/img/azure.svg and drawn in the button's text colour.
function AzureIcon() {
  const mask = `url(${useBaseUrl('/img/azure.svg')})`;
  return <span className={styles.azureIcon} style={{WebkitMaskImage: mask, maskImage: mask}} aria-hidden="true" />;
}

const heroCopy = {
  title: 'DMARC monitoring for every domain you manage.',
  subtitle:
    'dotMARC is a self-hosted DMARC aggregate report analyzer. Point every client domain at one mailbox, and see authentication posture across all of them from a single dashboard.',
};

// Illustrative records, using the same example domains as the demo.
const records = [
  {host: '_dmarc.aurora-retail.example', policy: 'quarantine'},
  {host: '_dmarc.brightline-legal.example', policy: 'none'},
  {host: '_dmarc.cobalt-freight.example', policy: 'reject'},
];

type Figure = {
  light: string;
  dark: string;
  alt: string;
  caption: string;
  crop: 'cropAccess' | 'cropHealth';
};

// Four things, each in a sentence or two. A screenshot, where there is one, sits directly under its row.
const features: {title: string; description: string; figure?: Figure}[] = [
  {
    title: 'One mailbox, every domain',
    description:
      'Every domain’s DMARC record points at one shared mailbox. dotMARC polls it, parses the aggregate reports without counting any twice, and files each under the right domain.',
  },
  {
    title: 'Every domain on one dashboard',
    description:
      'Pass and fail rates, sources and DNS record status for all your domains, filterable by Group and Tag. Live lookups confirm each DMARC record exists and points at the right mailbox.',
    figure: {
      light: '/img/screenshots/domain-detail.png',
      dark: '/img/screenshots/domain-detail-dark.png',
      alt: 'The Domain health panel for one domain, with DMARC, MTA-STS, SPF, MX, DKIM and DNS provider checks',
      caption: 'Each domain lists its DMARC, MTA-STS, SPF, MX and DKIM checks, with the reason when one fails.',
      crop: 'cropHealth',
    },
  },
  {
    title: 'Access per client',
    description:
      'Give a client a Viewer role on just their own domains, not an all-or-nothing login. Admin and Viewer roles come built in.',
    figure: {
      light: '/img/screenshots/manage-access.png',
      dark: '/img/screenshots/manage-access-dark.png',
      alt: 'The Access grants table in dotMARC, showing an admin with access to all groups and a viewer limited to one client’s group',
      caption: 'A client signs in as a Viewer and sees only their own group of domains. Admins see everything.',
      crop: 'cropAccess',
    },
  },
  {
    title: 'On your infrastructure',
    description:
      'Runs with Docker Compose, or on Azure Container Apps, backed by PostgreSQL. Report data stays in your environment.',
  },
];

function FeatureFigure({figure}: {figure: Figure}) {
  return (
    <figure className={styles.figure}>
      <div className={`${styles.crop} ${styles[figure.crop]}`}>
        <ThemedImage alt={figure.alt} sources={{light: useBaseUrl(figure.light), dark: useBaseUrl(figure.dark)}} />
      </div>
      <figcaption>{figure.caption}</figcaption>
    </figure>
  );
}

function Hero() {
  const latestVersion = useLatestVersion();
  const releaseNotesPath = latestVersion ? `/releases-updates/v${latestVersion.replaceAll('.', '-')}` : '/releases-updates';

  return (
    <div className={styles.hero}>
      <div>
        {latestVersion ? (
          <Link className={styles.release} to={releaseNotesPath}>
            <strong>dotMARC v{latestVersion} is out</strong>
            <span>Read what changed in this release</span>
          </Link>
        ) : null}
        <h1 className={styles.title}>{heroCopy.title}</h1>
        <p className={styles.lead}>{heroCopy.subtitle}</p>
        <div className={styles.actions}>
          <Link className={`${styles.button} ${styles.demo}`} to="https://demo.dotmarc.app/">
            <Icon name="demo" />
            Try the demo
          </Link>
          <Link className={`${styles.button} ${styles.secondary}`} to="/docs/getting-started">
            <Icon name="docs" />
            Read the docs
          </Link>
          <Link className={`${styles.button} ${styles.discord}`} to="https://discord.homotechsual.dev">
            <Icon name="discord" />
            Join the Discord
          </Link>
          <Link className={`${styles.button} ${styles.github}`} to="https://github.com/homotechsual/dotMARC">
            <Icon name="github" />
            View on GitHub
          </Link>
        </div>
      </div>
      <div className={styles.records}>
        <pre className={styles.recordBlock} aria-label="Example DMARC records for three client domains, all reporting to one mailbox">
          {records.map(record => (
            <code key={record.host}>
              <span className={styles.host}>{record.host}</span> <span className={styles.type}>TXT</span>
              {'\n  v=DMARC1; p='}
              {record.policy}
              {';\n  rua=mailto:'}
              <span className={styles.mailbox}>dmarc@msp.example</span>
            </code>
          ))}
        </pre>
        <p className={styles.recordCaption}>
          Each client publishes one record. Every report lands in the same mailbox.
        </p>
      </div>
    </div>
  );
}

export default function Home(): ReactNode {
  const pageUrl = 'https://dotmarc.app/';

  return (
    <Layout title={heroCopy.title} description={heroCopy.subtitle}>
      <Head>
        <meta property="og:type" content="website" />
        <meta property="og:url" content={pageUrl} />
        <meta name="twitter:card" content="summary_large_image" />
        <meta name="twitter:title" content={heroCopy.title} />
        <meta name="twitter:description" content={heroCopy.subtitle} />
      </Head>
      <main className={styles.wrap}>
        <Hero />

        <div className={styles.shot}>
          <ThemedImage
            alt="dotMARC dashboard showing pass/fail rates, DNS status, and polling status across monitored domains"
            sources={{
              light: useBaseUrl('/img/screenshots/dashboard-admin.png'),
              dark: useBaseUrl('/img/screenshots/dashboard-admin-dark.png'),
            }}
          />
        </div>

        <section className={styles.section}>
          <h2 className={styles.sectionTitle}>What you get</h2>
          <div className={styles.list}>
            {features.map(feature => (
              <div key={feature.title} className={styles.row}>
                <h3 className={styles.rowTitle}>{feature.title}</h3>
                <p className={styles.rowText}>{feature.description}</p>
                {feature.figure ? <FeatureFigure figure={feature.figure} /> : null}
              </div>
            ))}
          </div>
        </section>

        <section className={styles.story}>
          <h2 className={styles.sectionTitle}>Why dotMARC exists</h2>
          <p>
            dotMARC started as the scripts we used internally to read DMARC reports for our clients’ domains. They
            worked, but they were ours alone. The hosted tools we looked at weren’t much better for an MSP: pricing
            that climbed with every domain we added, and feature lists that kept growing into things we never asked a
            DMARC tool to do. So we turned the scripts into an application that does one job.
          </p>
          <p>
            dotMARC is free software under the <Link to="/license">AGPL-3.0</Link>, and it will stay that way. The{' '}
            <Link to="https://github.com/homotechsual/dotMARC">source is on GitHub</Link>, and the{' '}
            <Link to="https://discord.homotechsual.dev">Discord</Link> is the place to ask a question or suggest a
            change.
          </p>
        </section>

        <section className={styles.start}>
          <div>
            <h2 className={styles.startTitle}>Run it yourself</h2>
            <p>
              Start with Docker Compose, or deploy the included Bicep template to Azure Container Apps. The docs
              cover both from start to finish.
            </p>
          </div>
          <div className={styles.startLinks}>
            <Link className={`${styles.button} ${styles.docker}`} to="/docs/deploy-with-docker">
              <Icon name="docker" />
              Deploy with Docker
            </Link>
            <Link className={`${styles.button} ${styles.azure}`} to="/docs/deploy-to-azure">
              <AzureIcon />
              Deploy to Azure
            </Link>
          </div>
        </section>
      </main>
    </Layout>
  );
}
