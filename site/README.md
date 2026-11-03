# BOS Core documentation site

The site compiles the canonical Markdown files under `../docs/` with [Markdoc](https://markdoc.dev/) and emits static HTML.

```sh
cd site
npm ci
npm run dev
```

The local site is available at `http://127.0.0.1:4173`. Build production files with:

```sh
npm run build
```

`DOCS_BASE_PATH` sets a hosting subpath, and `DOCS_SITE_URL` sets the absolute canonical origin. The GitHub Pages workflow supplies both values for this repository.
