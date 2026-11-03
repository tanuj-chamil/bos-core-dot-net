import Markdoc from '@markdoc/markdoc';
import { cp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const siteDirectory = path.dirname(fileURLToPath(import.meta.url));
const rootDirectory = path.resolve(siteDirectory, '..');
const outputDirectory = path.resolve(siteDirectory, process.env.DOCS_OUTPUT_DIR || 'dist');
const basePath = normalizeBasePath(process.env.DOCS_BASE_PATH || '');
const siteUrl = (process.env.DOCS_SITE_URL || 'https://tanuj-chamil.github.io/bos-core-dot-net').replace(/\/$/, '');
const repositoryUrl = 'https://github.com/tanuj-chamil/bos-core-dot-net';

const pages = [
  page('site/content/index.md', '', 'Overview', 'Start', 'Build single-tenant business applications with versioned records, operations, and YAML workflows.'),
  page('site/content/quickstart.md', 'quickstart', 'Quickstart', 'Start', 'Initialize BOS Core, sign in securely, and make your first API request.'),
  page('site/content/api.md', 'guides/api', 'API conventions', 'Build', 'Call BOS methods, handle errors, use idempotency keys, and discover available contracts.'),
  page('site/content/authentication.md', 'guides/authentication', 'Authentication', 'Build', 'Authenticate browser users and service clients without exposing credentials.'),
  page('site/content/records.md', 'guides/records', 'Types and records', 'Build', 'Publish versioned data definitions and safely create, query, update, and delete records.'),
  page('site/content/workflows.md', 'guides/workflows', 'Workflow engine', 'Build', 'Compose registered operations into bounded, atomic YAML workflows.'),
  page('site/content/material-request.md', 'tutorials/material-request', 'Material Request tutorial', 'Tutorial', 'Publish and exercise the included Material Request schemas and submission workflow.'),
  page('site/content/deployment.md', 'operations/deployment', 'Secure deployment', 'Operate', 'Configure HTTPS, listeners, origins, protected files, and native extraction.'),
  page('site/content/backup.md', 'operations/backup', 'Backup and recovery', 'Operate', 'Create verified offline backups, restore safely, and recover administrator access.'),
  page('site/content/cli.md', 'reference/cli', 'CLI reference', 'Reference', 'Use the supported BOS Core operator commands and understand their exit status.')
];

const sourceToRoute = new Map(pages.map((item) => [item.source, item.route]));

await rm(outputDirectory, { recursive: true, force: true });
await mkdir(path.join(outputDirectory, 'assets'), { recursive: true });
await cp(path.join(siteDirectory, 'src', 'styles.css'), path.join(outputDirectory, 'assets', 'styles.css'));
await cp(path.join(siteDirectory, 'src', 'site.js'), path.join(outputDirectory, 'assets', 'site.js'));
await cp(path.join(siteDirectory, 'src', 'favicon.svg'), path.join(outputDirectory, 'favicon.svg'));
await cp(path.join(rootDirectory, 'docs', 'examples'), path.join(outputDirectory, 'examples'), { recursive: true });

const renderedPages = [];
for (const item of pages) {
  const sourcePath = path.join(rootDirectory, item.source);
  const source = await readFile(sourcePath, 'utf8');
  const ast = Markdoc.parse(source);
  const errors = Markdoc.validate(ast);

  if (errors.length > 0) {
    throw new Error(`Markdoc validation failed for ${item.source}:\n${errors.map(formatValidationError).join('\n')}`);
  }

  const headings = extractHeadings(source);
  const transformed = Markdoc.transform(ast);
  let content = Markdoc.renderers.html(transformed);
  content = attachHeadingIds(content, headings);
  content = rewriteLinks(content, item);
  content = enhanceContent(content);

  const html = renderPage(item, content, headings);
  const pageDirectory = path.join(outputDirectory, item.route);
  await mkdir(pageDirectory, { recursive: true });
  await writeFile(path.join(pageDirectory, 'index.html'), html);
  renderedPages.push({ ...item, headings });
}

await writeFile(path.join(outputDirectory, 'search-index.json'), JSON.stringify(buildSearchIndex(renderedPages)));
await writeFile(path.join(outputDirectory, '404.html'), renderNotFoundPage());
await writeFile(path.join(outputDirectory, '.nojekyll'), '');
await writeFile(path.join(outputDirectory, 'sitemap.xml'), renderSitemap());
await writeFile(path.join(outputDirectory, 'robots.txt'), `User-agent: *\nAllow: /\nSitemap: ${siteUrl}${withBaseFile('sitemap.xml')}\n`);

console.log(`Built ${pages.length} Markdoc pages in ${path.relative(rootDirectory, outputDirectory)}`);

function page(source, route, title, section, description) {
  return { source, route, title, section, description };
}

function normalizeBasePath(value) {
  if (!value || value === '/') return '';
  return `/${value.replace(/^\/+|\/+$/g, '')}`;
}

function withBase(route = '') {
  const normalized = route ? `/${route.replace(/^\/+|\/+$/g, '')}` : '';
  return `${basePath}${normalized}/` || '/';
}

function withBaseFile(file) {
  return `${basePath}/${file.replace(/^\/+/, '')}`;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}

function stripInlineMarkdown(value) {
  return value
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\[([^\]]+)\]\([^\)]+\)/g, '$1')
    .replace(/[\*_~]/g, '')
    .replace(/\\$/g, '')
    .trim();
}

function slugify(value) {
  return stripInlineMarkdown(value)
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '') || 'section';
}

function extractHeadings(source) {
  const headings = [];
  const used = new Map();
  let inFence = false;

  for (const line of source.split(/\r?\n/)) {
    if (/^\s*```/.test(line)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;

    const match = /^(#{1,3})\s+(.+?)\s*#*$/.exec(line);
    if (!match) continue;

    const text = stripInlineMarkdown(match[2]);
    const base = slugify(text);
    const count = used.get(base) || 0;
    used.set(base, count + 1);
    headings.push({ level: match[1].length, text, id: count === 0 ? base : `${base}-${count + 1}` });
  }

  return headings;
}

function attachHeadingIds(html, headings) {
  let index = 0;
  return html.replace(/<h([1-3])>([\s\S]*?)<\/h\1>/g, (match, level, content) => {
    const heading = headings[index++];
    if (!heading) return match;
    return `<h${level} id="${heading.id}">${content}<a class="heading-anchor" href="#${heading.id}" aria-label="Link to ${escapeHtml(heading.text)}">#</a></h${level}>`;
  });
}

function rewriteLinks(html, currentPage) {
  return html.replace(/href="([^"]+)"/g, (match, encodedHref) => {
    const href = encodedHref.replaceAll('&amp;', '&');
    if (/^(?:[a-z]+:|\/\/|#)/i.test(href)) return match;

    const [pathname, fragment = ''] = href.split('#', 2);
    if (!pathname) return match;

    const resolvedSource = path.posix.normalize(path.posix.join(path.posix.dirname(currentPage.source), pathname));
    const markdownSource = resolvedSource.replace(/\.md$/i, '.md');
    const targetRoute = sourceToRoute.get(markdownSource);
    let target;

    if (targetRoute !== undefined) {
      target = withBase(targetRoute);
    } else if (resolvedSource.startsWith('docs/examples/')) {
      target = withBaseFile(`examples/${path.posix.basename(resolvedSource)}`);
    } else {
      target = `${repositoryUrl}/blob/main/${resolvedSource}`;
    }

    return `href="${escapeHtml(fragment ? `${target}#${fragment}` : target)}"`;
  });
}

function enhanceContent(html) {
  return html
    .replace(/<table>/g, '<div class="table-wrap" tabindex="0"><table>')
    .replace(/<\/table>/g, '</table></div>')
    .replace(/<pre([^>]*)>/g, '<div class="code-block"><pre$1>')
    .replace(/<\/pre>/g, '</pre><button class="copy-button" type="button" aria-label="Copy code">Copy</button></div>');
}

function formatValidationError(error) {
  const location = error.lines?.length ? `line ${error.lines[0]}` : 'unknown line';
  return `- ${location}: ${error.error?.message || error.error || error}`;
}

function renderPage(item, content, headings) {
  const toc = headings.filter((heading) => heading.level === 2 || heading.level === 3);
  const currentIndex = pages.indexOf(item);
  const previous = pages[currentIndex - 1];
  const next = pages[currentIndex + 1];
  const isHome = item.route === '';

  return documentShell({
    title: item.title,
    description: item.description,
    route: item.route,
    body: `
      <div class="mobile-scrim" data-nav-close></div>
      <div class="docs-shell">
        ${renderNavigation(item)}
        <main id="main-content" class="main-content" tabindex="-1">
          <article class="article">
            <div class="breadcrumbs"><a href="${withBase()}">Docs</a><span aria-hidden="true">/</span><span>${escapeHtml(item.section)}</span></div>
            ${isHome ? renderQuickPath() : ''}
            <div class="prose">${content}</div>
            ${renderPageNavigation(previous, next)}
          </article>
        </main>
        ${renderTableOfContents(toc)}
      </div>`,
    includeSearch: true
  });
}

function renderNavigation(currentPage) {
  const sections = [...new Set(pages.map((item) => item.section))];
  return `<aside id="site-navigation" class="site-navigation" aria-label="Documentation navigation">
    <div class="nav-scroll">
      ${sections.map((section) => `<div class="nav-group">
        <h2>${escapeHtml(section)}</h2>
        <ul>${pages.filter((item) => item.section === section).map((item) => `<li><a href="${withBase(item.route)}"${item === currentPage ? ' aria-current="page"' : ''}>${escapeHtml(item.title)}</a></li>`).join('')}</ul>
      </div>`).join('')}
    </div>
  </aside>`;
}

function renderTableOfContents(headings) {
  if (headings.length === 0) return '<aside class="page-toc" aria-hidden="true"></aside>';
  return `<aside class="page-toc" aria-label="On this page">
    <h2>On this page</h2>
    <nav><ul>${headings.map((heading) => `<li class="toc-level-${heading.level}"><a href="#${heading.id}">${escapeHtml(heading.text)}</a></li>`).join('')}</ul></nav>
  </aside>`;
}

function renderQuickPath() {
  return `<nav class="quick-path" aria-label="Quick paths">
    <a href="${withBase('quickstart')}"><strong>Get started</strong><span>Run BOS Core locally</span></a>
    <a href="${withBase('guides/api')}"><strong>Call the API</strong><span>Requests and responses</span></a>
    <a href="${withBase('guides/workflows')}"><strong>Build a workflow</strong><span>Compose operations safely</span></a>
  </nav>`;
}

function renderPageNavigation(previous, next) {
  return `<nav class="page-navigation" aria-label="Adjacent pages">
    ${previous ? `<a class="previous" href="${withBase(previous.route)}"><span>Previous</span><strong>${escapeHtml(previous.title)}</strong></a>` : '<span></span>'}
    ${next ? `<a class="next" href="${withBase(next.route)}"><span>Next</span><strong>${escapeHtml(next.title)}</strong></a>` : '<span></span>'}
  </nav>`;
}

function documentShell({ title, description, route, body, includeSearch }) {
  const canonical = `${siteUrl}${withBase(route)}`;
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="description" content="${escapeHtml(description)}">
  <meta name="theme-color" content="#f7f8fb">
  <meta property="og:title" content="${escapeHtml(title)} · BOS Core">
  <meta property="og:description" content="${escapeHtml(description)}">
  <meta property="og:type" content="website">
  <meta property="og:url" content="${canonical}">
  <link rel="canonical" href="${canonical}">
  <link rel="icon" href="${withBaseFile('favicon.svg')}" type="image/svg+xml">
  <link rel="stylesheet" href="${withBaseFile('assets/styles.css')}">
  <title>${escapeHtml(title)} · BOS Core</title>
</head>
<body>
  <a class="skip-link" href="#main-content">Skip to content</a>
  <header class="topbar">
    <button class="menu-button" type="button" aria-label="Open navigation" aria-controls="site-navigation" aria-expanded="false" data-nav-toggle>
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16"/></svg>
    </button>
    <a class="wordmark" href="${withBase()}"><span class="wordmark-mark" aria-hidden="true"><i></i><i></i><i></i><i></i></span><span>BOS Core</span><b>Docs</b></a>
    <div class="topbar-actions">
      ${includeSearch ? `<button class="search-button" type="button" data-search-open><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5"/><path d="m16 16 4 4"/></svg><span>Search docs</span><kbd>Ctrl K</kbd></button>` : ''}
      <a class="github-link" href="${repositoryUrl}" aria-label="BOS Core on GitHub"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2.7a9.6 9.6 0 0 0-3 18.7c.5.1.7-.2.7-.5v-1.9c-2.8.6-3.4-1.2-3.4-1.2-.5-1.2-1.1-1.5-1.1-1.5-.9-.7.1-.7.1-.7 1 .1 1.6 1 1.6 1 .9 1.6 2.4 1.1 2.9.9.1-.7.4-1.1.7-1.4-2.2-.3-4.6-1.1-4.6-4.8 0-1.1.4-2 1-2.6-.1-.3-.4-1.3.1-2.6 0 0 .8-.3 2.7 1a9.3 9.3 0 0 1 4.9 0c1.9-1.3 2.7-1 2.7-1 .5 1.3.2 2.3.1 2.6.6.7 1 1.5 1 2.6 0 3.7-2.3 4.5-4.6 4.8.4.3.7.9.7 1.7v2.6c0 .4.2.6.7.5A9.6 9.6 0 0 0 12 2.7Z"/></svg></a>
    </div>
  </header>
  <div class="status-strip" role="note"><span>1.0.0-preview.1</span><strong>Preview</strong><span>APIs may change before 1.0</span></div>
  ${body}
  ${includeSearch ? renderSearchDialog() : ''}
  <script>window.__BOS_DOCS_BASE__ = ${JSON.stringify(basePath)};</script>
  <script src="${withBaseFile('assets/site.js')}" defer></script>
</body>
</html>`;
}

function renderSearchDialog() {
  return `<dialog class="search-dialog" data-search-dialog aria-labelledby="search-title">
    <div class="search-panel">
      <div class="search-field">
        <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5"/><path d="m16 16 4 4"/></svg>
        <label class="visually-hidden" id="search-title" for="site-search">Search documentation</label>
        <input id="site-search" type="search" placeholder="Search documentation" autocomplete="off" data-search-input>
        <button type="button" data-search-close aria-label="Close search">Esc</button>
      </div>
      <div class="search-results" data-search-results aria-live="polite"><p>Start typing to search pages and sections.</p></div>
    </div>
  </dialog>`;
}

function buildSearchIndex(items) {
  return items.map((item) => ({
    title: item.title,
    section: item.section,
    description: item.description,
    href: withBase(item.route),
    headings: item.headings.filter((heading) => heading.level === 2).map((heading) => ({ title: heading.text, href: `${withBase(item.route)}#${heading.id}` }))
  }));
}

function renderNotFoundPage() {
  return documentShell({
    title: 'Page not found',
    description: 'The requested BOS Core documentation page was not found.',
    route: '404',
    body: `<main id="main-content" class="not-found" tabindex="-1"><p class="error-code">404</p><h1>That page is not in the record.</h1><p>The link may be out of date, or the page may have moved.</p><a class="primary-link" href="${withBase()}">Return to the documentation</a></main>`,
    includeSearch: false
  });
}

function renderSitemap() {
  const urls = pages.map((item) => `  <url><loc>${siteUrl}${withBase(item.route)}</loc></url>`).join('\n');
  return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}\n</urlset>\n`;
}
