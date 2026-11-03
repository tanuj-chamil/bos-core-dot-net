# BOS Core documentation site

The site compiles curated public Markdown files under `content/` with [Markdoc](https://markdoc.dev/) and emits static HTML. Internal engineering records under `../docs/` are not added to website navigation; selected public examples from `../docs/examples/` are copied as downloadable assets.

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
