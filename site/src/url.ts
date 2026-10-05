// Every internal link goes through here, so the same build works at a domain root (local, CI) and
// under GitHub Pages' /ais-pipeline/ project path. A bare href="/ports" is right in one and a 404
// in the other -- the failure astro.config.mjs's directory format already guards against once.
export const url = (path: string): string =>
  `${import.meta.env.BASE_URL.replace(/\/$/, '')}${path}`;
