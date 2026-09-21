// No framework and no build step (ADR-0037, ADR-0047): views over endpoints that already exist.
//
// Everything here is read-only. Charter party terms become query parameters on a GET; nothing is
// posted, stored or uploaded, and `reconcile` stays a CLI verb because it needs a document.
'use strict';

// ---------------------------------------------------------------------------
// the house grammar
//
// A figure the pipeline will not stand behind never prints as a plain number. These four are the
// whole vocabulary, and they mean four different things:
//
//   >=N      measured, but the true extent is unknown -- a lower bound (ADR-0011)
//   ?        not measurable: too few surviving fixes for the geometry to mean anything (ADR-0025)
//   ~Name    the nearest port, not necessarily the one the vessel was at (ADR-0034)
//   too few  never measured: the sample is below the minimum this figure needs (ADR-0043)
//
// The last is a browser-first token, because no CLI verb reports benchmarks yet. It exists rather
// than reusing `?` because "measured but untrustworthy" and "not measured at all" are different
// claims, and the entire point of this grammar is that different claims look different.
// ---------------------------------------------------------------------------

const num = (v, digits = 0) =>
  v === null || v === undefined
    ? '—'
    : v.toLocaleString('en-GB', { minimumFractionDigits: digits, maximumFractionDigits: digits });

const utc = (iso) => (iso ? iso.replace('T', ' ').replace(/(:\d\d)(\.\d+)?Z?$/, '$1') : '—');

const cell = (html, cls) => `<td${cls ? ` class="${cls}"` : ''}>${html}</td>`;

/** Escapes anything that came from the database before it reaches innerHTML. */
const text = (value) =>
  String(value ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

/**
 * An identifier, printed as digits.
 *
 * An MMSI is a nine-digit identity, not a quantity: grouping it as 259,300,000 invites a reader to
 * compare two of them by size, and no shipping document writes one that way.
 */
const id = (value) => text(value);

const bound = (value, digits = 1) => `<span class="bound">${num(value, digits)}</span>`;

/** A figure that was never measured, because the sample never reached its minimum. */
const tooFew = (minimum) =>
  `<span class="too-few" title="needs ${minimum} usable calls">too few</span>`;

/**
 * Hours, or the observed lower bound when the true extent is unknown.
 *
 * The read model sends durationHours as null for an incomplete stop and keeps the measured span in
 * observedDurationHours. Rendering the observed value as a plain number would launder a bound into a
 * measurement, so it is marked instead (ADR-0011).
 */
function hours(row) {
  return row.durationHours !== null && row.durationHours !== undefined
    ? cell(num(row.durationHours, 1), 'n')
    : cell(bound(row.observedDurationHours), 'n');
}

/**
 * Waiting or working hours on a port call, marked as a bound when the call is incomplete.
 *
 * Zero is left unmarked. "at least 0.0 hours" is true and says nothing, and on this data it would
 * put three amber markers on two thirds of the rows -- uniform emphasis that buries the bounds that
 * do carry information. The extent column states completeness outright, so an unmarked zero is
 * never mistaken for a measured one.
 */
function callHours(row, value) {
  return row.isComplete || value === 0
    ? cell(num(value, 1), 'n')
    : cell(bound(value), 'n');
}

/** Drift, or ? when too few fixes survived exclusion for it to mean anything (ADR-0025). */
function drift(row) {
  return row.maxDriftNm !== null && row.maxDriftNm !== undefined
    ? cell(num(row.maxDriftNm, 3), 'n')
    : cell('<span class="unknown">?</span>', 'n');
}

/** The port, always with its distance, marked when it is only the nearest one. */
function portLabel(row) {
  if (row.portName === null || row.portName === undefined) {
    return '<span class="unknown">none named</span>';
  }

  const label = `${text(row.portName)} ${num(row.portDistanceNm, 1)} nm`;
  return row.plausiblyAtPort ? label : `<span class="nearest">${label}</span>`;
}

/**
 * Says so when a list came back exactly full.
 *
 * A list returned at its limit is otherwise indistinguishable from a complete one (ADR-0028). The
 * CLI has said this since it had a list; the browser did not, which made it the one surface that
 * could show a truncated table as though it were the whole story.
 */
function capNotice(element, returned, limit) {
  element.innerHTML = returned >= limit
    ? `Exactly ${num(returned)} row(s) returned, which is the limit — there are probably more.`
    : '';
}

// ---------------------------------------------------------------------------
// fetching
// ---------------------------------------------------------------------------

const LIMIT = 100;

/** Throws on anything but success. For endpoints that have no legitimate failure. */
async function json(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`${url} returned ${response.status}`);
  }
  return response.json();
}

/**
 * Returns the status alongside the body, for endpoints where a refusal is an answer.
 *
 * /portcalls/{id}/laytime declines with 422 when the call has no berth phase or its berth geometry
 * is untrustworthy. That is the pipeline working correctly, and treating it as a thrown error would
 * render a considered refusal as a crash.
 */
async function probe(url) {
  const response = await fetch(url);
  return { ok: response.ok, status: response.status, body: await response.json() };
}

function fill(id, rows, render, columns) {
  const body = document.querySelector(`#${id} tbody`);
  body.innerHTML = rows.length
    ? rows.map(render).join('')
    : `<tr><td colspan="${columns}">nothing to show — run <code>ais ingest</code> and <code>ais detect</code></td></tr>`;
}

function failed(id, columns, error) {
  const body = document.querySelector(`#${id} tbody`);
  body.innerHTML = `<tr><td colspan="${columns}" class="fail">${text(error.message)}</td></tr>`;
}

// ---------------------------------------------------------------------------
// shared state: the thresholds this page states, and the port list
//
// Both are fetched once. /meta projects the C# constants so no threshold is re-declared here
// (ADR-0047); /ports is one array and refetching it per keystroke would be silly.
// ---------------------------------------------------------------------------

let meta = null;
let portsCache = null;

async function loadMeta() {
  meta = meta ?? await json('/meta');
  return meta;
}

async function loadPorts() {
  portsCache = portsCache ?? await json('/ports');
  return portsCache;
}

// ---------------------------------------------------------------------------
// view: port calls
// ---------------------------------------------------------------------------

async function renderCalls() {
  try {
    const rows = await json(`/portcalls?limit=${LIMIT}`);

    fill('portcalls', rows, (r) => `<tr>
      ${cell(id(r.id), 'n')}
      ${cell(id(r.mmsi), 'n')}
      ${cell(utc(r.arrivedUtc))}
      ${callHours(r, r.waitingHours)}
      ${callHours(r, r.workingHours)}
      ${callHours(r, r.unclassifiedHours)}
      ${cell(r.isComplete ? '<span class="unknown">—</span>' : '<span class="bound-label">open</span>')}
      ${cell(portLabel(r))}
      ${cell(`<a class="go" href="#/calls/${r.id}">price it →</a>`)}
    </tr>`, 9);

    capNotice(document.querySelector('#callsCap'), rows.length, LIMIT);

    const open = rows.filter((r) => !r.isComplete).length;
    const named = rows.filter((r) => r.portName).length;

    // Counted from the server's own flag rather than by re-applying a distance threshold here.
    // The page states the threshold (from /meta) but never re-decides it.
    const atPort = rows.filter((r) => r.plausiblyAtPort === true).length;
    const nm = (await loadMeta()).plausiblyAtPortNm;

    document.querySelector('#callTally').innerHTML = `
      <div>${num(rows.length)}<span>calls shown</span></div>
      <div>${num(named)}<span>with a port named</span></div>
      <div>${num(atPort)}<span>within ${num(nm, 1)} nm of it</span></div>
      <div>${num(open)}<span>extent unknown</span></div>`;
  } catch (e) {
    failed('portcalls', 9, e);
  }
}

// ---------------------------------------------------------------------------
// view: one call, priced
// ---------------------------------------------------------------------------

const KIND_CLASS = {
  BeforeCommencement: 'silent',
  Counted: '',
  Excepted: 'excepted',
  OnDemurrage: 'demurrage',
};

async function renderCall(id) {
  const heading = document.querySelector('#callHeading');
  const subhead = document.querySelector('#callSubhead');
  const panel = document.querySelector('#laytime');

  heading.textContent = `Port call ${id}`;
  subhead.textContent = 'loading…';
  panel.innerHTML = '';

  const defaults = (await loadMeta()).charterPartyDefaults;
  const form = document.querySelector('#terms');

  // Defaults come from the API, never typed into the HTML, so the form starts from the same
  // numbers the endpoint would apply if it were sent none.
  if (!form.dataset.filled) {
    form.allowedHours.value = defaults.allowedHours;
    form.ratePerDay.value = defaults.ratePerDay;
    form.turnHours.value = defaults.turnHours;
    form.dataset.filled = 'yes';
  }

  document.querySelector('#termsNote').innerHTML =
    `Defaults are <strong>illustrative tanker terms, not a charter party</strong>: ` +
    `${num(defaults.allowedHours)} hours allowed at ${num(defaults.ratePerDay)} ` +
    `${text(defaults.currency)}/day, ${num(defaults.turnHours)} hours turn time. ` +
    'The engine is correct for the terms it is given, and the terms are an input. ' +
    'They travel in the URL, so this page can be sent to a colleague — and so they appear in ' +
    'this server\'s request log, which is why it runs on your machine and not on the internet.';

  await priceCall(id);
}

function termsQuery() {
  const form = document.querySelector('#terms');
  const query = new URLSearchParams({
    allowedHours: form.allowedHours.value,
    ratePerDay: form.ratePerDay.value,
    turnHours: form.turnHours.value,
  });

  // An empty field means "AIS cannot observe a notice of readiness, assume arrival" -- which the
  // endpoint already does, and says. Sending a blank would be a different request.
  if (form.norUtc.value) {
    query.set('norUtc', `${form.norUtc.value}:00Z`);
  }

  return query;
}

async function priceCall(id) {
  const panel = document.querySelector('#laytime');
  const subhead = document.querySelector('#callSubhead');

  try {
    const result = await probe(`/portcalls/${id}/laytime?${termsQuery()}`);

    if (result.status === 404) {
      subhead.textContent = '';
      panel.innerHTML = `<div class="fail">${text(result.body.error)}</div>`;
      return;
    }

    if (result.status === 422) {
      renderRefusal(panel, subhead, result.body);
      return;
    }

    if (!result.ok) {
      panel.innerHTML = `<div class="fail">${text(result.body.error ?? result.status)}</div>`;
      return;
    }

    renderStatement(panel, subhead, result.body);
  } catch (e) {
    panel.innerHTML = `<div class="fail">${text(e.message)}</div>`;
  }
}

/**
 * A 422 is an answer, not a fault, and renders as one.
 *
 * The server's own sentence is printed verbatim. LaytimeAssessor produces it precisely so that the
 * CLI and this page cannot disagree about what is missing; re-authoring it here would be the drift
 * the shared assessor exists to prevent.
 */
function renderRefusal(panel, subhead, body) {
  // The refusal body carries no vessel or port -- it is a statement about evidence, not about a
  // call -- so the subhead says what happened rather than inventing a description of the call.
  subhead.textContent = 'AIS cannot price this call.';

  const missing = {
    NoBerthPhase:
      'This call is anchorage only. A vessel that never went alongside has no cargo operations, '
      + 'and inventing a berth time from an anchorage would put a fabricated timestamp into a '
      + 'commercial claim.',
    BerthGeometryUntrustworthy:
      'Neither excluding those hours (which favours the charterer) nor counting them (which '
      + 'favours the owner) is supportable, so no figure is produced at all.',
  }[body.refusal] ?? '';

  panel.innerHTML = `
    <div class="refusal">
      <h2>Not priced</h2>
      <p class="detail">${text(body.detail)}.</p>
      ${missing ? `<p class="note">${text(missing)}</p>` : ''}
      <p class="note">
        This is the pipeline declining, not failing. Pick a call with working hours above zero
        from <a href="#/calls">the list</a>.
      </p>
    </div>`;
}

function renderStatement(panel, subhead, s) {
  const port = s.port
    ? `${text(s.port)} ${num(s.portDistanceNm, 1)} nm`
    : 'no port named';

  subhead.innerHTML =
    `mmsi ${id(s.mmsi)} · ${port} · ${utc(s.arrivedUtc)} → ${utc(s.departedUtc)}`
    + (s.isComplete ? '' : ' · <span class="bound-label">open</span>');

  const assumption = s.noticeOfReadinessIsAssumed
    ? `<p class="assumption">
         Notice of readiness <strong>assumed</strong> to be arrival, ${utc(s.noticeOfReadinessUtc)}.
         AIS cannot observe a notice of readiness — no transponder emits one — so this figure rests
         on an assumption rather than an observation.
       </p>`
    : '';

  // Every hour between commencement and completion is on exactly one line, and the lines are shown
  // rather than summarised. A total nobody can decompose is a total nobody can dispute.
  const lines = s.lines.map((l) => `<tr class="${KIND_CLASS[l.kind] ?? ''}">
    ${cell(utc(l.fromUtc))}
    ${cell(utc(l.toUtc))}
    ${cell(num(l.hours, 2), 'n')}
    ${cell(text(l.kind))}
    ${cell(text(l.reason))}
  </tr>`).join('');

  const counted = s.lines
    .filter((l) => l.kind !== 'BeforeCommencement')
    .reduce((total, l) => total + l.hours, 0);
  const elapsed =
    (new Date(s.completedUtc) - new Date(s.commencedUtc)) / 3_600_000;
  const balances = Math.abs(counted - elapsed) < 0.01;

  const rank = s.isComplete
    ? `<a class="go" href="#/ports">Was this wait unusual?</a>`
    : `<span class="note">An open call's hours are a lower bound, so they are not ranked against
         a port's distribution — that would turn a bound into a measurement.</span>`;

  panel.innerHTML = `
    ${assumption}
    <div class="scroll"><table class="statement">
      <thead><tr>
        <th>from (UTC)</th><th>to (UTC)</th><th class="n">hours</th><th>counts as</th><th>why</th>
      </tr></thead>
      <tbody>${lines}</tbody>
    </table></div>

    <p class="note">
      Laytime commenced ${utc(s.commencedUtc)} — ${text(s.commencementReason)}.
      ${balances
        ? 'Every hour between commencement and completion is on exactly one line above.'
        : '<span class="fail">These lines do not decompose the elapsed time.</span>'}
    </p>

    <ul class="tally">
      <div>${num(s.allowedHours, 1)}<span>hours allowed</span></div>
      <div>${num(s.usedHours, 1)}<span>hours used</span></div>
      <div>${num(s.exceptedHours, 1)}<span>hours excepted</span></div>
      <div class="${s.demurrageHours > 0 ? 'alarm' : ''}">${num(s.demurrageHours, 1)}<span>hours on demurrage</span></div>
    </ul>

    <p class="money ${s.demurrageHours > 0 ? 'alarm' : ''}">${text(s.demurrageOwed)}</p>
    <p class="note">
      ${s.demurrageHours > 0
        ? 'Demurrage owed on these terms.'
        : 'No demurrage on these terms.'}
      ${s.hoursSaved > 0
        ? `${num(s.hoursSaved, 1)} hours of the allowance went unused — that is unused allowance,
           not despatch; these terms do not grant it.`
        : ''}
      ${rank}
    </p>`;
}

// ---------------------------------------------------------------------------
// view: port benchmarks
// ---------------------------------------------------------------------------

async function renderPorts() {
  try {
    const rows = await loadPorts();
    const minimums = (await loadMeta()).benchmarkMinimums;

    fill('ports', rows, (r) => `<tr>
      ${cell(`<a href="#/ports/${r.wpiNumber}">${text(r.portName)}</a>`)}
      ${cell(text(r.country))}
      ${cell(num(r.attributedCalls), 'n')}
      ${cell(num(r.usableCalls), 'n')}
      ${cell(num(r.excludedIncomplete), 'n')}
      ${cell(num(r.excludedTooFar), 'n')}
      ${cell(r.medianWaitingHours === null ? tooFew(minimums.forMedian) : num(r.medianWaitingHours, 1), 'n')}
      ${cell(r.p90WaitingHours === null ? tooFew(minimums.forPercentile) : num(r.p90WaitingHours, 1), 'n')}
      ${cell(r.medianWorkingHours === null ? tooFew(minimums.forMedian) : num(r.medianWorkingHours, 1), 'n')}
    </tr>`, 9);

    // The exclusions are rendered as an identity, not merely satisfied: every attributed call is
    // accounted for by exactly one outcome, the same accounting ingest applies to rows.
    const sum = (key) => rows.reduce((total, r) => total + r[key], 0);
    const nm = (await loadMeta()).plausiblyAtPortNm;

    document.querySelector('#portsIdentity').innerHTML =
      `${num(sum('attributedCalls'))} attributed = ${num(sum('usableCalls'))} usable `
      + `+ ${num(sum('excludedIncomplete'))} with an unknown extent `
      + `+ ${num(sum('excludedTooFar'))} further than ${num(nm, 1)} nm from the port named. `
      + `A median needs ${minimums.forMedian} usable calls and a p90 needs `
      + `${minimums.forPercentile}; below those the figure is absent, never thin.`;
  } catch (e) {
    failed('ports', 9, e);
  }
}

// ---------------------------------------------------------------------------
// view: one port, ranked
// ---------------------------------------------------------------------------

let currentWpi = null;

async function renderPort(wpi) {
  currentWpi = wpi;
  const heading = document.querySelector('#portHeading');
  const subhead = document.querySelector('#portSubhead');

  document.querySelector('#ranking').innerHTML = '';

  try {
    const port = await json(`/ports/${wpi}`);
    const minimums = (await loadMeta()).benchmarkMinimums;

    heading.textContent = port.portName;
    subhead.innerHTML =
      `${text(port.country)} · ${num(port.usableCalls)} usable of ${num(port.attributedCalls)} `
      + `attributed · median wait `
      + (port.medianWaitingHours === null
        ? tooFew(minimums.forMedian)
        : `${num(port.medianWaitingHours, 1)} h`);

    document.querySelector('#observationList').textContent =
      port.waitingHoursObserved.length
        ? port.waitingHoursObserved.map((h) => h.toFixed(1)).join('  ')
        : 'no usable calls at this port';
  } catch (e) {
    heading.textContent = `Port ${text(wpi)}`;
    subhead.innerHTML = `<span class="fail">${text(e.message)}</span>`;
  }
}

async function rankWait(hours) {
  const panel = document.querySelector('#ranking');

  try {
    const port = await json(`/ports/${currentWpi}?waitingHours=${hours}`);
    const minimums = (await loadMeta()).benchmarkMinimums;
    const c = port.comparison;

    if (!c || c.percentile === null) {
      panel.innerHTML = `<div class="refusal">
        <h2>Not ranked</h2>
        <p class="detail">
          ${num(port.usableCalls)} usable call(s) at ${text(port.portName)} — a ranking needs
          ${minimums.forMedian}.
        </p>
        <p class="note">
          A position in a distribution this small is an anecdote wearing a percentile's name.
        </p>
      </div>`;
      return;
    }

    panel.innerHTML = `
      <p class="ranking">
        ${num(c.hours, 1)} hours is longer than <strong>${num(c.longerThanCalls)} of
        ${num(c.ofCalls)}</strong> calls — the ${num(c.percentile, 0)}th percentile.
      </p>
      <p class="note">
        A position, not a verdict. Whether that wait was reasonable depends on the charter party,
        the berth and the cargo, none of which AIS can see.
      </p>`;
  } catch (e) {
    panel.innerHTML = `<div class="fail">${text(e.message)}</div>`;
  }
}

// ---------------------------------------------------------------------------
// view: how these numbers were made
// ---------------------------------------------------------------------------

async function renderRuns() {
  try {
    const rows = await json('/runs');
    fill('runs', rows, (r) => `<tr>
      ${cell(id(r.id), 'n')}
      ${cell(text(r.sourceFile))}
      ${cell(num(r.rowsRead), 'n')}
      ${cell(num(r.rowsInserted), 'n')}
      ${cell(num(r.rowsDuplicateInFile + r.rowsDuplicatePriorRun), 'n')}
      ${cell(num(r.rowsQuarantined), 'n')}
    </tr>`, 6);
  } catch (e) {
    failed('runs', 6, e);
  }
}

async function renderQuality() {
  try {
    const rows = await json('/quality');
    fill('quality', rows, (r) => `<tr${r.silent ? ' class="silent"' : ''}>
      ${cell(text(r.ruleId))}
      ${cell(num(r.quarantined), 'n')}
      ${cell(num(r.flagged), 'n')}
      ${cell(text(r.description))}
    </tr>`, 4);
  } catch (e) {
    failed('quality', 4, e);
  }

  // R10 is counted over stops rather than rows, so it is reported beside the table and not in it
  // (ADR-0036). Derived here from /stops for the same reason the CLI reports it separately.
  try {
    const all = await json('/stops?limit=1000');
    const conflicting = all.filter((s) => !s.statusAgrees).length;
    const share = all.length ? ((100 * conflicting) / all.length).toFixed(1) : '0.0';
    document.querySelector('#r10').innerHTML =
      `<strong>R10</strong> ${num(conflicting)} of ${num(all.length)} stops shown (${share}%) report a ` +
      'status contradicting their own speed. Counted over stops, not rows: both readings are stored ' +
      'and neither wins.';
  } catch (e) {
    document.querySelector('#r10').innerHTML = `<span class="fail">${text(e.message)}</span>`;
  }
}

async function renderStops() {
  try {
    const rows = await json(`/stops?limit=${LIMIT}`);
    fill('stops', rows, (r) => `<tr>
      ${cell(id(r.mmsi), 'n')}
      ${cell(utc(r.startedUtc))}
      ${hours(r)}
      ${drift(r)}
      ${cell(r.statusAgrees
        ? text(r.reportedStatus ?? 'none reported')
        : `<span class="conflict">${text(r.reportedStatus ?? 'none reported')} — contradicts its own speed</span>`)}
    </tr>`, 5);

    capNotice(document.querySelector('#stopsCap'), rows.length, LIMIT);
  } catch (e) {
    failed('stops', 5, e);
  }
}

let pipelineLoaded = false;

function renderPipeline() {
  if (pipelineLoaded) {
    return;
  }
  pipelineLoaded = true;
  renderRuns();
  renderQuality();
  renderStops();
}

// ---------------------------------------------------------------------------
// routing
//
// The hash carries the route so a priced statement is a link. A demurrage figure that cannot be
// sent to a colleague is a demo rather than a tool, and the terms are already in the URL.
// ---------------------------------------------------------------------------

function show(view) {
  document.querySelectorAll('[data-view]').forEach((section) => {
    section.hidden = section.dataset.view !== view;
  });

  document.querySelectorAll('[data-nav]').forEach((link) => {
    link.toggleAttribute('aria-current', link.dataset.nav === view);
  });
}

let callsLoaded = false;
let portsLoaded = false;

function route() {
  const parts = (location.hash.replace(/^#\/?/, '').split('?')[0] || 'calls').split('/');

  if (parts[0] === 'ports' && parts[1]) {
    show('port');
    renderPort(parts[1]);
    return;
  }

  if (parts[0] === 'ports') {
    show('ports');
    if (!portsLoaded) {
      portsLoaded = true;
      renderPorts();
    }
    return;
  }

  if (parts[0] === 'calls' && parts[1]) {
    show('call');
    renderCall(parts[1]);
    return;
  }

  if (parts[0] === 'pipeline') {
    show('pipeline');
    renderPipeline();
    return;
  }

  show('calls');
  if (!callsLoaded) {
    callsLoaded = true;
    renderCalls();
  }
}

document.querySelector('#terms').addEventListener('submit', (event) => {
  event.preventDefault();
  priceCall(location.hash.split('/')[2]);
});

document.querySelector('#rank').addEventListener('submit', (event) => {
  event.preventDefault();
  rankWait(document.querySelector('#rank').waitingHours.value);
});

window.addEventListener('hashchange', route);
route();
