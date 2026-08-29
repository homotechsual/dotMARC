const React = require('react');
const {readFileSync, existsSync} = require('fs');
const {join} = require('path');

const BACKGROUND_MIME_TYPES = {
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
};

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

function toDataUrl(filePath, mime) {
  try {
    const content = readFileSync(filePath);
    return `data:${mime};base64,${content.toString('base64')}`;
  } catch {
    return undefined;
  }
}

function backgroundDataUrl(name) {
  const dir = join(process.cwd(), 'static', 'img', 'og-backgrounds');
  for (const [ext, mime] of Object.entries(BACKGROUND_MIME_TYPES)) {
    const filePath = join(dir, `${name}${ext}`);
    if (existsSync(filePath)) {
      return toDataUrl(filePath, mime);
    }
  }
  return undefined;
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
  // Docusaurus appends " | dotMARC" to the <title> tag automatically (see
  // titleFormatterUtils.tsx); strip it so the image's baked-in headline
  // stays as the clean page title rather than repeating the site name.
  const rawTitle = (data.metadata.title || 'dotMARC').replace(/\s*\|\s*dotMARC\s*$/, '');
  const title = clampText(rawTitle, 90);
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
          {
            display: 'flex',
            flexDirection: 'column',
            gap: 14,
            maxWidth: 700,
            background: 'rgba(11,15,20,0.66)',
            borderRadius: 20,
            padding: '22px 26px',
          },
          badge('dotMARC', 'rgba(255,255,255,0.14)', '#fcfcfc'),
          div({display: 'flex', fontSize: 60, lineHeight: 1.05, fontWeight: 900, letterSpacing: -1.1, color: '#fcfcfc'}, title),
          description
            ? div({display: 'flex', fontSize: 27, lineHeight: 1.28, color: '#c7d0dc'}, description)
            : null,
        ),
        div({display: 'flex', fontSize: 28, color: '#ef8b86', fontWeight: 800}, 'dotmarc.app'),
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
