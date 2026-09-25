import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

// Written by hand, not auto-generated from the file list, so the order follows the journey of someone running
// dotMARC (get started, deploy, monitor, connect, administer) and not the order pages happened to be written in.
// The URLs are unchanged by this: they still come from the file names, so existing links keep working.
const sidebars: SidebarsConfig = {
  docsSidebar: [
    'getting-started',
    {
      type: 'category',
      label: 'Deploy dotMARC',
      collapsed: false,
      link: {
        type: 'generated-index',
        title: 'Deploy dotMARC',
        description: 'Install dotMARC and put it on the internet, on your own Docker host or in Azure.',
      },
      items: ['deploy-with-docker', 'deploy-to-azure'],
    },
    {
      type: 'category',
      label: 'Monitor your domains',
      collapsed: false,
      link: {
        type: 'generated-index',
        title: 'Monitor your domains',
        description: 'What needs to be in place in DNS, and how dotMARC can set it up for you.',
      },
      items: ['dmarc-and-mta-sts', 'dns-provider-push', 'mta-sts'],
    },
    {
      type: 'category',
      label: 'Alerts and integrations',
      collapsed: false,
      link: {
        type: 'generated-index',
        title: 'Alerts and integrations',
        description: 'Be told when something needs attention, and open tickets in HaloPSA automatically.',
      },
      items: ['alerts', 'psa-integration'],
    },
    {
      type: 'category',
      label: 'Administer dotMARC',
      collapsed: false,
      link: {
        type: 'generated-index',
        title: 'Administer dotMARC',
        description: 'Control who can see what, keep dotMARC up to date, and see what the server is doing.',
      },
      items: ['permissions-and-access', 'updating', 'server-logs'],
    },
    {
      type: 'category',
      label: 'About the project',
      collapsed: true,
      link: {
        type: 'generated-index',
        title: 'About the project',
        description: 'What dotMARC is for, and how to build it, change it and release it.',
      },
      items: ['scope', 'local-development', 'releasing'],
    },
  ],
};

export default sidebars;
