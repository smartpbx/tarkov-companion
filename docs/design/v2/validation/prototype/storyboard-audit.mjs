#!/usr/bin/env node
// Deterministic, dependency-free Chromium audit for the participant storyboards. It crawls every
// generated route/link in both variants and exercises the state/correction/queue regressions that
// static syntax checks cannot see. Set CHROMIUM_PATH when Chromium is not on PATH.
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repositoryRoot = fs.realpathSync(process.cwd());
const docsRoot = fs.realpathSync(path.join(repositoryRoot, 'docs'));
const prototypeRoot = fs.realpathSync(path.join(docsRoot, 'design/v2/validation/prototype'));
const storyboardSource = fs.readFileSync(path.join(prototypeRoot, 'storyboard.js'), 'utf8');
const contentSource = fs.readFileSync(path.join(prototypeRoot, 'content.js'), 'utf8');
let browser;
let userData;
let checks = 0;
let navigationId = 0;

function assert(condition, message) {
  checks += 1;
  if (!condition) throw new Error(message);
}

function findChromium() {
  const explicit = process.env.CHROMIUM_PATH;
  if (explicit) return explicit;
  for (const command of ['chromium', 'chromium-browser', 'google-chrome', 'google-chrome-stable']) {
    const found = spawnSync('which', [command], { encoding: 'utf8' });
    if (found.status === 0 && found.stdout.trim()) return found.stdout.trim();
  }
  const cache = path.join(process.env.HOME || '/nonexistent', '.cache/ms-playwright');
  if (fs.existsSync(cache)) {
    for (const directory of fs.readdirSync(cache).sort().reverse()) {
      const candidates = [
        path.join(cache, directory, 'chrome-headless-shell-linux64/chrome-headless-shell'),
        path.join(cache, directory, 'chrome-linux64/chrome')
      ];
      for (const candidate of candidates) if (fs.existsSync(candidate)) return candidate;
    }
  }
  throw new Error('Chromium not found. Set CHROMIUM_PATH to a Chromium or Chrome executable.');
}

function delay(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

async function waitForFile(file) {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (fs.existsSync(file)) return;
    if (browser.exitCode !== null) throw new Error(`Chromium exited before DevTools was ready (${browser.exitCode}).`);
    await delay(50);
  }
  throw new Error('Timed out waiting for Chromium DevTools endpoint.');
}

class Cdp {
  constructor(url) {
    this.url = url;
    this.nextId = 1;
    this.pending = new Map();
    this.waiters = new Map();
  }

  async open() {
    this.socket = new WebSocket(this.url);
    await new Promise((resolve, reject) => {
      this.socket.addEventListener('open', resolve, { once: true });
      this.socket.addEventListener('error', reject, { once: true });
    });
    this.socket.addEventListener('message', (event) => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const pending = this.pending.get(message.id);
        if (!pending) return;
        this.pending.delete(message.id);
        if (message.error) pending.reject(new Error(message.error.message)); else pending.resolve(message.result);
        return;
      }
      const waiter = this.waiters.get(message.method);
      if (waiter) { this.waiters.delete(message.method); waiter(message.params); }
    });
  }

  send(method, params = {}) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  event(method) {
    return new Promise((resolve) => this.waiters.set(method, resolve));
  }

  async navigate(url) {
    const loaded = this.event('Page.loadEventFired');
    await this.send('Page.navigate', { url });
    await loaded;
    await delay(40);
  }

  async evaluate(expression) {
    const result = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  }

  close() { this.socket.close(); }
}

async function createPage(initialUrl) {
  if (!userData) throw new Error('Audit profile was not created inside the cleanup boundary.');
  const endpointFile = path.join(userData, 'DevToolsActivePort');
  browser = spawn(findChromium(), [
    '--headless=new', '--no-sandbox', '--disable-gpu', '--disable-background-networking',
    '--disable-component-update', '--disable-default-apps', '--disable-sync', '--metrics-recording-only',
    '--no-first-run', '--allow-file-access-from-files', '--remote-debugging-port=0',
    `--user-data-dir=${userData}`, 'about:blank'
  ], { stdio: ['ignore', 'ignore', 'pipe'] });
  await waitForFile(endpointFile);
  const [port] = fs.readFileSync(endpointFile, 'utf8').trim().split(/\s+/);
  const response = await fetch(`http://127.0.0.1:${port}/json/new?${encodeURIComponent(initialUrl)}`, { method: 'PUT' });
  if (!response.ok) throw new Error(`Could not create Chromium target: ${response.status}`);
  const target = await response.json();
  const page = new Cdp(target.webSocketDebuggerUrl);
  await page.open();
  await page.send('Page.enable');
  await page.send('Runtime.enable');
  return page;
}

function pageUrl(file, route = '') {
  navigationId += 1;
  return `${pathToFileURL(path.join(prototypeRoot, file)).href}?audit=${navigationId}#/${route}`;
}

async function click(page, selector) {
  const clicked = await page.evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) return false; el.click(); return true; })()`);
  assert(clicked, `Missing control: ${selector}`);
  await delay(60);
}

async function select(page, selector, value) {
  const changed = await page.evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) return false;
    const value = ${JSON.stringify(value)};
    if (el.type === 'checkbox') el.checked = Boolean(value);
    else if (el.type === 'radio') el.checked = true;
    else el.value = value;
    el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  assert(changed, `Missing select/radio: ${selector}`);
  await delay(30);
}

function insideDocs(filePath) {
  if (!fs.existsSync(filePath)) return false;
  const relative = path.relative(docsRoot, fs.realpathSync(filePath));
  return Boolean(relative) && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}

async function crawlLinks(page, variant) {
  const pending = new Set(variant.seeds);
  const visited = new Set();
  let edges = 0;
  while (pending.size) {
    const route = pending.values().next().value;
    pending.delete(route);
    if (visited.has(route)) continue;
    visited.add(route);
    assert(visited.size <= 80, `${variant.file} route crawl exceeded its bound`);
    await page.navigate(pageUrl(variant.file, route));
    const snapshot = await page.evaluate(`({
      heading: document.querySelector('#main h1')?.textContent || '',
      links: Array.from(document.querySelectorAll('[href], [src], object[data]')).map((el) => ({
        tag: el.tagName, raw: el.getAttribute('href') || el.getAttribute('src') || el.getAttribute('data'),
        absolute: el.href || el.src || el.data
      }))
    })`);
    assert(snapshot.heading, `${variant.file}#/${route} did not render a heading`);
    for (const link of snapshot.links) {
      edges += 1;
      const raw = link.raw;
      assert(!/^(?:https?:|mailto:|javascript:)/i.test(raw), `${variant.file}#/${route} generated external link ${raw}`);
      if (/^#\//.test(raw)) {
        pending.add(raw.slice(2));
      } else if (raw === '#main' || /^data:/i.test(raw)) {
        continue;
      } else {
        const targetUrl = new URL(link.absolute);
        targetUrl.hash = '';
        targetUrl.search = '';
        const target = fileURLToPath(targetUrl);
        assert(insideDocs(target), `${variant.file}#/${route} generated link outside canonical docs/: ${raw}`);
      }
    }
  }
  return { routes: visited.size, edges };
}

async function auditPlanSkip(page, variant) {
  await page.navigate(pageUrl(variant.file, variant.plan));
  await select(page, '#mod-state', 'loading');
  await click(page, '#mod-apply-state');
  await click(page, '.banner-state.loading [data-action="recover"]');
  const state = await page.evaluate(`({ text: document.querySelector('#main').textContent,
    busy: Boolean(document.querySelector('[aria-busy="true"]')),
    loading: Boolean(document.querySelector('.banner-state.loading')),
    focus: document.activeElement?.id })`);
  assert(!state.loading && !state.busy, `${variant.file}: Skip route estimate remained Loading`);
  assert(state.text.includes('Route estimate skipped.'), `${variant.file}: skipped route state is not truthful`);
  assert(state.focus === 'proute-h', `${variant.file}: skipped route did not focus Route estimate heading`);
}

async function auditRetrySave(page, variant) {
  await page.navigate(pageUrl(variant.file, variant.history));
  await click(page, '[data-action="journey"][data-journey="J6"]');
  await select(page, '#mod-failsave', true);
  await click(page, '[data-action="correct"]');
  await select(page, '#dialog input[name="fix"][value="power-cord"]', 'power-cord');
  await click(page, '#dialog-actions button[value="save"]');
  let state = await page.evaluate(`({ failed: Boolean(document.querySelector('.banner-state.failed')),
    draft: document.querySelector('#cable-state')?.textContent || '', focus: document.activeElement?.textContent || '' })`);
  assert(state.failed && state.draft.includes('draft: not saved yet'), `${variant.file}: failed save did not keep its draft`);
  assert(state.focus.trim() === 'Retry save', `${variant.file}: failed save did not focus Retry save`);
  await click(page, '[data-action="recover"][data-key="debrief"]');
  state = await page.evaluate(`({ failed: Boolean(document.querySelector('.banner-state.failed')),
    saved: document.querySelector('#cable-state')?.textContent || '', focus: document.activeElement?.textContent || '' })`);
  assert(!state.failed && state.saved.includes('to Power cord at 18:55:20'), `${variant.file}: Retry save did not enter Debrief success`);
  assert(state.focus.trim() === 'Undo correction', `${variant.file}: Retry save did not focus Undo correction`);
}

async function auditCorrections(page, variant) {
  const expected = {
    'military-cable': { identity: 'Military cable', summary: '6 of 6 container items: TAKE 3 · SWAP 1 · LEAVE 2',
      decision: 'LEAVE', reasons: ['Identity manually confirmed', 'Below your value band', 'No quest, hideout or pin needs it'],
      change: 'Would become TAKE if you pin it or a quest or hideout need is added.', size: '2×1',
      value: '₽22,000', perSquare: '₽11,000', details: '/military-cable', container: '6×4: 6 items using 9 squares.' },
    'power-cord': { identity: 'Power cord', summary: '6 of 6 container items: TAKE 3 · SWAP 1 · LEAVE 2',
      decision: 'LEAVE', reasons: ['Identity manually corrected', 'Below your value band', 'No quest, hideout or pin needs it'],
      change: 'Would become TAKE if you pin it or a quest or hideout need is added.', size: '1×2',
      value: '₽16,200', perSquare: '₽8,100', details: '/power-cord', container: '6×4: 6 items using 9 squares.' },
    'not-an-item': { identity: 'Not an item', summary: '5 of 5 container items: TAKE 3 · SWAP 1 · LEAVE 1 · 1 region EXCLUDED as not an item',
      decision: 'EXCLUDED', reasons: ['Region manually marked as not an item', 'Excluded from item, space and value totals'],
      change: 'Undo restores the original uncertain Military cable result.', size: 'Not applicable',
      value: 'Not applicable', perSquare: 'Not applicable', details: null,
      container: '6×4: 5 items using 7 squares; one analysed region is excluded as not an item.' }
  };
  for (const [choice, values] of Object.entries(expected)) {
    await page.navigate(pageUrl(variant.file, 'raid/loot'));
    await click(page, '[data-action="correct"]');
    await select(page, `#dialog input[name="fix"][value="${choice}"]`, choice);
    await click(page, '#dialog-actions button[value="save"]');
    const result = await page.evaluate(`(() => { const row = document.querySelector('[data-loot-identity=${JSON.stringify(values.identity)}]');
      const facts = Array.from(document.querySelectorAll('#loot-sum-h ~ .facts dt'));
      return {
      summary: document.querySelector('#loot-sum-h')?.textContent || '', text: document.querySelector('#main').textContent,
      cells: row ? Array.from(row.cells).map((cell) => cell.textContent.trim()) : [],
      decision: row?.querySelector('.decision')?.textContent || '',
      reasons: row ? Array.from(row.querySelectorAll('ol li')).map((item) => item.textContent.trim()) : [],
      change: row?.querySelector('details p')?.textContent || '',
      href: row?.querySelector('a[href]')?.getAttribute('href') || null,
      container: facts.find((term) => term.textContent === 'Container')?.nextElementSibling?.textContent || '',
      correction: document.querySelector('#loot-correction-h')?.parentElement.textContent || ''
    }; })()`);
    assert(result.summary === values.summary, `${variant.file}/${choice}: inconsistent decision summary: ${result.summary}`);
    assert(result.decision === values.decision, `${variant.file}/${choice}: decision did not follow identity`);
    assert(values.reasons.every((reason) => result.reasons.includes(reason)), `${variant.file}/${choice}: reasons did not follow identity`);
    assert(result.change === values.change, `${variant.file}/${choice}: change copy did not follow identity`);
    assert(result.cells.length === 7 && result.cells[3] === values.size && result.cells[4] === values.value && result.cells[5] === values.perSquare, `${variant.file}/${choice}: dimensions or values did not follow identity`);
    assert((values.details === null && result.href === null) || result.href?.endsWith(values.details), `${variant.file}/${choice}: wrong details target ${result.href}`);
    assert(result.container === values.container, `${variant.file}/${choice}: container count or dimensions were stale: ${result.container}`);
    assert(!result.text.includes('Unknown') && !result.text.includes('REVIEW') && !result.text.includes('Correct match'), `${variant.file}/${choice}: stale REVIEW/Correct/Unknown content remained`);
    assert(result.correction.includes('18:41:20'), `${variant.file}/${choice}: in-raid correction used a future simulation time`);
    await page.evaluate(`location.hash = ${JSON.stringify(`#/${variant.history}`)}`);
    await delay(60);
    const timeline = await page.evaluate(`document.querySelector('[aria-labelledby="tl-h"]')?.textContent || ''`);
    assert(timeline.includes(values.summary) && !timeline.includes('REVIEW'), `${variant.file}/${choice}: Debrief retained the stale loot summary`);

    // The same complete choice set is offered from J6. Exercise every option independently so the
    // post-raid correction time and shared summary cannot regress for a less common choice.
    await page.navigate(pageUrl(variant.file, variant.history));
    await click(page, '[data-action="correct"]');
    await select(page, `#dialog input[name="fix"][value="${choice}"]`, choice);
    await click(page, '#dialog-actions button[value="save"]');
    const postRaid = await page.evaluate(`({ state: document.querySelector('#cable-state')?.textContent || '',
      timeline: document.querySelector('[aria-labelledby="tl-h"]')?.textContent || '' })`);
    assert(postRaid.state.includes(values.identity) && postRaid.state.includes('18:55:20'), `${variant.file}/${choice}: J6 correction used the wrong identity or simulation time`);
    assert(postRaid.timeline.includes(values.summary) && !postRaid.timeline.includes('REVIEW'), `${variant.file}/${choice}: J6 summary did not follow the selected identity`);
  }
}

async function auditPositionOrder(page, variant) {
  await page.navigate(pageUrl(variant.file, 'raid'));
  await click(page, '[data-action="open-capture"]');
  await select(page, '#dialog input[name="intent"][value="loot"]', 'loot');
  await click(page, '#dialog-actions button[value="arm"]');
  await select(page, '#mod-shot', 'mismatch');
  await click(page, '#mod-shoot');
  await select(page, '#mod-shot', 'position');
  await click(page, '#mod-shoot');
  let state = await page.evaluate(`document.querySelector('[data-device="desktop"]')?.textContent || ''`);
  assert(state.includes('1 capture needs a decision') && state.includes('1 capture waits unread'), `${variant.file}: position did not wait behind queue head`);
  await click(page, '[data-action="decide"]');
  await click(page, '#dialog-actions button[value="skip"]');
  await delay(80);
  state = await page.evaluate(`({ tray: document.querySelector('[data-device="desktop"]')?.textContent || '',
    main: document.querySelector('#main').textContent })`);
  assert(!state.tray.includes('needs a decision') && !state.tray.includes('waits unread'), `${variant.file}: position entered Needs a decision or remained queued`);
  assert(state.tray.includes('Armed: Loot decision'), `${variant.file}: position consumed the armed intent`);
  assert(state.main.includes('just updated'), `${variant.file}: position did not publish at its ordered turn`);
}

async function auditStashOverlap(page, variant) {
  await page.navigate(pageUrl(variant.file, variant.stash));
  await select(page, '#mod-shot', 'match');
  await click(page, '#mod-shoot');
  await delay(3400);
  const observed = await page.evaluate(`({
    captureList: document.querySelector('#guide-h')?.parentElement.textContent || '',
    table: document.querySelector('#observed-h')?.parentElement.textContent || ''
  })`);
  assert(observed.captureList.includes('Rows 37 to 60 at 18:41:11'), `${variant.file}: capture 3 runtime timestamp diverged from the stash summary`);
  assert(observed.table.includes('Rows 37 to 42') && observed.table.includes('Observed in captures 2 and 3; overlap merged'), `${variant.file}: capture 2/3 overlap rows 37-42 were omitted or not deduplicated`);
  assert(observed.table.includes('Screenshot 3 at 18:41:11') && !observed.table.includes('18:21:04'), `${variant.file}: capture 3 provenance used a conflicting timestamp`);
}

const variants = [
  { file: 'variant-a.html', first: 'setup', seeds: ['setup', 'raid/loot'], plan: 'plan', history: 'debrief', stash: 'intel/stash' },
  { file: 'variant-b.html', first: 'home', seeds: ['home', 'raid/loot', 'search'], plan: 'prepare', history: 'history', stash: 'prepare/stash' }
];

let page;
try {
  // Static guards and all runtime work are inside one cleanup boundary. The guards run before the
  // repository-local Chromium profile exists; every path after its creation proves its removal.
  const admission = storyboardSource.indexOf('admitClipboardAtArrival(cap);');
  const enqueue = storyboardSource.indexOf('c.queue.push({ cap: cap, kind: kind });');
  assert(admission >= 0 && admission < enqueue, 'Clipboard payload is not admitted before waiting in the queue');
  assert(storyboardSource.includes('contentHashed: false'), 'Clipboard admission no longer records un-hashed transient bytes');
  assert(storyboardSource.includes("if (!cap.atQueueHead) throw new Error('Capture source settlement and hashing require the ordered queue head.')"), 'Settlement/hash queue-head guard is missing');
  assert(contentSource.includes("{ rows: 'Rows 37 to 42', cells: 'Observed in captures 2 and 3; overlap merged'"), 'Stash data no longer preserves the capture 2/3 overlap rows');
  assert(contentSource.includes("Screenshot 3 at 18:41:11") && !contentSource.includes('18:21:04'), 'Stash data no longer uses the capture 3 runtime timestamp consistently');
  userData = fs.mkdtempSync(path.join(prototypeRoot, '.storyboard-audit-'));
  assert(fs.realpathSync(path.dirname(userData)) === prototypeRoot, 'Audit profile escaped the prototype directory');
  page = await createPage(pageUrl(variants[0].file, variants[0].first));
  let routes = 0;
  let edges = 0;
  for (const variant of variants) {
    const crawled = await crawlLinks(page, variant);
    routes += crawled.routes;
    edges += crawled.edges;
    await auditPlanSkip(page, variant);
    await auditRetrySave(page, variant);
    await auditCorrections(page, variant);
    await auditPositionOrder(page, variant);
    await auditStashOverlap(page, variant);
  }
  console.log(`Storyboard audit passed: ${checks} assertions, ${routes} rendered routes and ${edges} participant-facing link/asset edges across both variants.`);
} finally {
  try {
    if (page) page.close();
  } finally {
    try {
      if (browser && browser.exitCode === null) {
        const exited = once(browser, 'exit');
        browser.kill('SIGTERM');
        await Promise.race([exited, delay(2000)]);
      }
    } finally {
      if (userData) {
        const profile = userData;
        fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
        if (fs.existsSync(profile)) throw new Error(`Storyboard audit profile was not cleaned up: ${profile}`);
      }
    }
  }
}
