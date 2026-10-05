// @ts-check
import { defineConfig } from 'astro/config';

// Static output, deliberately. The pipeline is batch -- the DMA archive publishes with a
// three-day lag -- so there is nothing for a server to render freshly. The site compiles
// site/src/data/pipeline.json in at build time and ships files (ADR-0039).
export default defineConfig({
  output: 'static',
  // Directory format, so /arrivals resolves to arrivals/index.html on ANY static host. The
  // 'file' alternative emits arrivals.html and relies on the host rewriting extensionless URLs,
  // which Netlify does and GitHub Pages does not -- a link that works locally and 404s once
  // deployed is exactly the failure that is hardest to notice.
  build: { format: 'directory' },
  // GitHub Pages serves a project site under /<repo>/. The Pages workflow sets SITE_BASE; every
  // other build (local, CI's nav check) is at the root, so the default stays '/'. Internal links
  // all go through src/url.ts, which reads this.
  base: process.env.SITE_BASE ?? '/',
  devToolbar: { enabled: false },
});
