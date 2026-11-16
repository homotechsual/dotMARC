const React = require('react');
const {readFileSync} = require('fs');
const {join} = require('path');

const WIDTH = 1200;
const HEIGHT = 630;

// Flat navy, the same ink as the site's dark surfaces, with a coral bar for the accent. No gradients.
const INK = '#1b2432';
const CORAL = '#ff9a92';

function loadFont(packageName, file) {
  const filePath = join(process.cwd(), 'node_modules', '@fontsource', packageName, 'files', file);
  try {
    return readFileSync(filePath);
  } catch {
    throw new Error(`Unable to load font ${file}. Tried: ${filePath}`);
  }
}

const plex400 = loadFont('ibm-plex-sans', 'ibm-plex-sans-latin-400-normal.woff');
const plex600 = loadFont('ibm-plex-sans', 'ibm-plex-sans-latin-600-normal.woff');
const plexMono400 = loadFont('ibm-plex-mono', 'ibm-plex-mono-latin-400-normal.woff');

const baseOptions = {
  width: WIDTH,
  height: HEIGHT,
  fonts: [
    {name: 'IBM Plex Sans', data: plex400, weight: 400, style: 'normal'},
    {name: 'IBM Plex Sans', data: plex600, weight: 600, style: 'normal'},
    {name: 'IBM Plex Mono', data: plexMono400, weight: 400, style: 'normal'},
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

// The shared layout: a coral bar down the left edge, the section name, the title and description, and the site name.
function card(section, title, description, aside) {
  return div(
    {
      display: 'flex',
      width: WIDTH,
      height: HEIGHT,
      background: INK,
      fontFamily: 'IBM Plex Sans',
      color: '#eef1f5',
    },
    div({display: 'flex', width: 14, height: HEIGHT, background: CORAL}),
    div(
      {
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'space-between',
        flex: 1,
        padding: '56px 64px',
        boxSizing: 'border-box',
      },
      div(
        {display: 'flex', flexDirection: 'column', gap: 18},
        div({display: 'flex', fontSize: 28, color: '#a9b5c4'}, section),
        div(
          {
            display: 'flex',
            maxWidth: aside ? 640 : 1000,
            fontSize: aside ? 54 : 64,
            lineHeight: 1.1,
            fontWeight: 600,
            letterSpacing: -1,
          },
          title,
        ),
        description
          ? div(
              {
                display: 'flex',
                maxWidth: aside ? 640 : 980,
                fontSize: aside ? 25 : 29,
                lineHeight: 1.35,
                color: '#a9b5c4',
              },
              description,
            )
          : null,
      ),
      div({display: 'flex', fontSize: 30, fontWeight: 600, color: CORAL}, 'dotMARC'),
    ),
    aside,
  );
}

const docs = data => {
  const title = clampText(data.metadata.title, 90);
  const description = clampText(data.metadata.description, 180);

  return [card('Docs', title, description), baseOptions];
};

// The home page and other pages show the DMARC record that every client domain publishes, as on the home page.
const recordLine = (text, color) => div({display: 'flex', whiteSpace: 'pre', color: color || '#d6dde7'}, text);

const pages = data => {
  // Docusaurus appends " | dotMARC" to the <title> tag automatically (see
  // titleFormatterUtils.tsx); strip it so the image's baked-in headline
  // stays as the clean page title rather than repeating the site name.
  const rawTitle = (data.metadata.title || 'dotMARC').replace(/\s*\|\s*dotMARC\s*$/, '');
  const title = clampText(rawTitle, 90);
  const record = div(
    {
      display: 'flex',
      flexDirection: 'column',
      alignSelf: 'center',
      marginRight: 56,
      padding: '28px 30px',
      width: 400,
      background: '#0f151e',
      borderRadius: 6,
      fontFamily: 'IBM Plex Mono',
      fontSize: 19,
      lineHeight: 1.55,
    },
    recordLine('_dmarc.aurora-retail.example', '#ffffff'),
    recordLine('  v=DMARC1; p=quarantine;'),
    recordLine('  rua=mailto:dmarc@msp.example', CORAL),
    div({display: 'flex', height: 16}),
    recordLine('_dmarc.cobalt-freight.example', '#ffffff'),
    recordLine('  v=DMARC1; p=reject;'),
    recordLine('  rua=mailto:dmarc@msp.example', CORAL),
  );

  // The record takes a third of the card, so the title carries it alone: a cut-off description would read badly.
  return [card('', title, '', record), baseOptions];
};

const blog = data => {
  const pageData = data?.data || {};
  const metadata = pageData?.metadata || {};
  const isPost = data.pageType === 'post';
  const title = clampText(isPost ? metadata.title : 'dotMARC Releases and Updates', 90);
  const description = clampText(isPost ? metadata.description : 'Release notes and announcements from dotMARC.', 170);

  return [card('Releases and Updates', title, description), baseOptions];
};

module.exports = {docs, pages, blog};
