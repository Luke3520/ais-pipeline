// No framework and no build step (ADR-0037): the page is four tables over endpoints that already
// exist. Everything here is read-only.
'use strict';

const PLAUSIBLY_AT_PORT_NM = 5.0; // mirrors PortAttributionThresholds; a heuristic (ADR-0034)

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
 * Hours, or the observed lower bound when the true extent is unknown.
 *
 * The read model sends durationHours as null for an incomplete stop and keeps the measured span in
 * observedDurationHours. Rendering the observed value as a plain number would launder a bound into a
 * measurement, so it is marked instead (ADR-0011).
 */
function hours(row) {
  return row.durationHours !== null && row.durationHours !== undefined
    ? cell(num(row.durationHours, 1), 'n')
    : cell(`<span class="bound">${num(row.observedDurationHours, 1)}</span>`, 'n');
}

/**
 * Waiting or working hours on a port call, marked as a bound when the call is incomplete.
 *
 * StoredPortCall sends these as plain numbers whatever the call's state -- unlike StoredStop, which
 * nulls a figure it will not stand behind. That asymmetry was looked at and deliberately kept, on
 * the grounds that isComplete already carries the signal (docs/review-followups.md). It only carries
 * it if something reads it: an incomplete call's hours are the sum of observed stop durations, so
 * they are a lower bound, and rendering them bare would be exactly the laundering the stops table
 * refuses.
 *
 * Zero is left unmarked. "at least 0.0 hours" is true and says nothing, and on this data it would
 * put three amber markers on two thirds of the rows -- uniform emphasis that buries the bounds that
 * do carry information. The extent column below states completeness outright, so an unmarked zero
 * is never mistaken for a measured one.
 */
function callHours(row, value) {
  return row.isComplete || value === 0
    ? cell(num(value, 1), 'n')
    : cell(`<span class="bound">${num(value, 1)}</span>`, 'n');
}

/** Drift, or ? when too few fixes survived exclusion for it to mean anything (ADR-0025). */
function drift(row) {
  return row.maxDriftNm !== null && row.maxDriftNm !== undefined
    ? cell(num(row.maxDriftNm, 3), 'n')
    : cell('<span class="unknown">?</span>', 'n');
}

/** The port, always with its distance, marked when it is only the nearest one. */
function port(row) {
  if (row.portName === null || row.portName === undefined) {
    return cell('<span class="unknown">none named</span>');
  }

  const label = `${text(row.portName)} ${num(row.portDistanceNm, 1)} nm`;
  return cell(row.plausiblyAtPort ? label : `<span class="nearest">${label}</span>`);
}

async function json(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`${url} returned ${response.status}`);
  }
  return response.json();
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

async function runs() {
  try {
    const rows = await json('/runs');
    fill('runs', rows, (r) => `<tr>
      ${cell(num(r.id), 'n')}
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

async function quality() {
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

async function portCalls() {
  try {
    const rows = await json('/portcalls?limit=100');
    fill('portcalls', rows, (r) => `<tr>
      ${cell(num(r.id), 'n')}
      ${cell(num(r.mmsi), 'n')}
      ${cell(utc(r.arrivedUtc))}
      ${callHours(r, r.waitingHours)}
      ${callHours(r, r.workingHours)}
      ${callHours(r, r.unclassifiedHours)}
      ${cell(r.isComplete ? '<span class="unknown">—</span>' : '<span class="bound-label">open</span>')}
      ${port(r)}
    </tr>`, 8);

    const open = rows.filter((r) => !r.isComplete).length;
    const named = rows.filter((r) => r.portName).length;
    const atPort = rows.filter((r) => r.portDistanceNm !== null
      && r.portDistanceNm <= PLAUSIBLY_AT_PORT_NM).length;

    document.querySelector('#callTally').innerHTML = `
      <div>${num(rows.length)}<span>calls shown</span></div>
      <div>${num(named)}<span>with a port named</span></div>
      <div>${num(atPort)}<span>within ${PLAUSIBLY_AT_PORT_NM} nm of it</span></div>
      <div>${num(open)}<span>extent unknown</span></div>`;
  } catch (e) {
    failed('portcalls', 8, e);
  }
}

async function stops() {
  try {
    const rows = await json('/stops?limit=100');
    fill('stops', rows, (r) => `<tr>
      ${cell(num(r.mmsi), 'n')}
      ${cell(utc(r.startedUtc))}
      ${hours(r)}
      ${drift(r)}
      ${cell(r.statusAgrees
        ? text(r.reportedStatus ?? 'none reported')
        : `<span class="conflict">${text(r.reportedStatus ?? 'none reported')} — contradicts its own speed</span>`)}
    </tr>`, 5);
  } catch (e) {
    failed('stops', 5, e);
  }
}

runs();
quality();
portCalls();
stops();
