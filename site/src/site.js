const navToggle = document.querySelector('[data-nav-toggle]');
const navCloseTargets = document.querySelectorAll('[data-nav-close]');
const navigation = document.querySelector('.site-navigation');
const navigationBackground = [
  document.querySelector('.main-content'),
  document.querySelector('.wordmark'),
  document.querySelector('.topbar-actions')
].filter(Boolean);
const mobileNavigation = window.matchMedia('(max-width: 50rem)');

function setNavigation(open, restoreFocus = false) {
  const mobileOpen = mobileNavigation.matches && open;
  document.body.classList.toggle('nav-open', open);
  navToggle?.setAttribute('aria-expanded', String(open));
  navToggle?.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
  if (navigation) {
    navigation.inert = mobileNavigation.matches && !open;
    navigation.toggleAttribute('aria-hidden', mobileNavigation.matches && !open);
  }
  navigationBackground.forEach((element) => { element.inert = mobileOpen; });

  if (mobileOpen) {
    requestAnimationFrame(() => navigation?.querySelector('[aria-current="page"], a')?.focus());
  } else if (restoreFocus) {
    navToggle?.focus();
  }
}

navToggle?.addEventListener('click', () => setNavigation(!document.body.classList.contains('nav-open')));
navCloseTargets.forEach((target) => target.addEventListener('click', () => setNavigation(false, true)));
document.querySelectorAll('.site-navigation a').forEach((link) => link.addEventListener('click', () => setNavigation(false)));
mobileNavigation.addEventListener('change', () => setNavigation(false));
setNavigation(false);

const copyButtons = document.querySelectorAll('.copy-button');
copyButtons.forEach((button) => button.addEventListener('click', async () => {
  const code = button.parentElement?.querySelector('code')?.textContent || '';
  try {
    await navigator.clipboard.writeText(code);
    button.textContent = 'Copied';
    setTimeout(() => { button.textContent = 'Copy'; }, 1500);
  } catch {
    button.textContent = 'Select to copy';
  }
}));

const tocLinks = [...document.querySelectorAll('.page-toc a')];
const observedHeadings = tocLinks.map((link) => document.getElementById(link.hash.slice(1))).filter(Boolean);
if (observedHeadings.length > 0 && 'IntersectionObserver' in window) {
  const observer = new IntersectionObserver((entries) => {
    const visible = entries.filter((entry) => entry.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
    if (!visible) return;
    tocLinks.forEach((link) => link.toggleAttribute('aria-current', link.hash === `#${visible.target.id}`));
  }, { rootMargin: '-18% 0px -72% 0px' });
  observedHeadings.forEach((heading) => observer.observe(heading));
}

const dialog = document.querySelector('[data-search-dialog]');
const searchInput = document.querySelector('[data-search-input]');
const searchResults = document.querySelector('[data-search-results]');
let searchIndex;
let searchStatus = 'idle';

async function openSearch() {
  if (!dialog) return;
  dialog.showModal();
  searchInput?.focus();
  if (!searchIndex && searchStatus !== 'loading') {
    searchStatus = 'loading';
    renderSearchResults();
    const base = window.__BOS_DOCS_BASE__ || '';
    try {
      const response = await fetch(`${base}/search-index.json`);
      if (!response.ok) throw new Error(`Search index returned ${response.status}`);
      searchIndex = await response.json();
      searchStatus = 'ready';
    } catch {
      searchStatus = 'error';
    }
    renderSearchResults();
  }
}

function closeSearch() {
  dialog?.close();
}

document.querySelectorAll('[data-search-open]').forEach((button) => button.addEventListener('click', openSearch));
document.querySelector('[data-search-close]')?.addEventListener('click', closeSearch);
dialog?.addEventListener('click', (event) => {
  if (event.target === dialog) closeSearch();
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && document.body.classList.contains('nav-open')) {
    event.preventDefault();
    setNavigation(false, true);
  } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault();
    openSearch();
  } else if (event.key === '/' && !['INPUT', 'TEXTAREA'].includes(document.activeElement?.tagName)) {
    event.preventDefault();
    openSearch();
  }
});

searchInput?.addEventListener('input', renderSearchResults);

function renderSearchResults() {
  const query = searchInput.value.trim().toLowerCase();
  if (searchStatus === 'loading') {
    searchResults.innerHTML = '<p>Loading search index…</p>';
    return;
  }
  if (searchStatus === 'error') {
    searchResults.innerHTML = '<p><strong>Search is unavailable.</strong><br>Check the connection, close search, and try again.</p>';
    return;
  }
  if (!query) {
    searchResults.innerHTML = '<p>Start typing to search pages and sections.</p>';
    return;
  }

  const terms = query.split(/\s+/);
  const matches = [];
  for (const page of searchIndex || []) {
    const pageText = `${page.title} ${page.description} ${page.section}`.toLowerCase();
    if (terms.every((term) => pageText.includes(term))) matches.push({ title: page.title, context: page.description, href: page.href, type: page.section, score: 2 });
    for (const heading of page.headings) {
      const headingText = `${heading.title} ${page.title}`.toLowerCase();
      if (terms.every((term) => headingText.includes(term))) matches.push({ title: heading.title, context: page.title, href: heading.href, type: 'Section', score: 1 });
    }
  }

  matches.sort((a, b) => b.score - a.score || a.title.localeCompare(b.title));
  searchResults.innerHTML = matches.length
    ? `<ul>${matches.slice(0, 10).map((match) => `<li><a href="${match.href}"><span><small>${escapeHtml(match.type)}</small><strong>${highlight(match.title, terms)}</strong><em>${escapeHtml(match.context)}</em></span><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 5 7 7-7 7"/></svg></a></li>`).join('')}</ul>`
    : `<p>No results for <strong>${escapeHtml(query)}</strong>.</p>`;
}

function escapeHtml(value) {
  const element = document.createElement('span');
  element.textContent = value;
  return element.innerHTML;
}

function highlight(value, terms) {
  let output = escapeHtml(value);
  for (const term of terms) {
    const escaped = term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    output = output.replace(new RegExp(`(${escaped})`, 'ig'), '<mark>$1</mark>');
  }
  return output;
}
