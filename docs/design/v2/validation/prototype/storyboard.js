/*
 * Navigation storyboards for issue #265: one script, two variants, one sample content object.
 *
 * What this is for: letting a moderator watch whether people find things, read the vocabulary
 * the way it is meant, and recover from the capture and sync edge cases. What it is not: the
 * Avalonia application, its accessibility tree, or its visual design. Browser semantics are the
 * nearest cheap stand-in for a keyboard and screen-reader walkthrough, and nothing more.
 *
 * Rules this file keeps, because each one is something the sessions measure:
 * - A background event never moves focus. Only the participant's own action does.
 * - Every dialog returns focus to whatever opened it, or to the page heading if that is gone.
 * - A screenshot is never dropped silently: skipped, duplicate and undecided captures stay listed.
 * - Nothing here presses a game key, reads a file, or makes a network request.
 */
(function () {
  'use strict';

  var C = window.STORYBOARD_CONTENT;
  var variantId = document.body.getAttribute('data-variant') === 'b' ? 'b' : 'a';

  var VARIANTS = {
    a: {
      name: 'Storyboard A',
      nav: [
        { id: 'raid', label: 'Raid' }, { id: 'intel', label: 'Intel' }, { id: 'plan', label: 'Plan' },
        { id: 'team', label: 'Team' }, { id: 'debrief', label: 'Debrief' }
      ],
      utility: { id: 'setup', label: 'Setup & Admin' },
      first: 'setup',
      search: false,
      stashBase: 'intel',
      planId: 'plan', planLabel: 'Plan',
      historyId: 'debrief', historyLabel: 'Debrief'
    },
    b: {
      name: 'Storyboard B',
      nav: [
        { id: 'home', label: 'Home' }, { id: 'raid', label: 'Raid' }, { id: 'prepare', label: 'Prepare' },
        { id: 'team', label: 'Team' }, { id: 'history', label: 'History' }
      ],
      utility: { id: 'setup', label: 'Setup' },
      first: 'home',
      search: true,
      stashBase: 'prepare',
      planId: 'prepare', planLabel: 'Prepare',
      historyId: 'history', historyLabel: 'History'
    }
  };
  var V = VARIANTS[variantId];

  // Which state-matrix row a route belongs to.
  var STATE_KEY = { home: 'setup', setup: 'setup', raid: 'raid', intel: 'intel', search: 'intel',
    plan: 'plan', prepare: 'plan', team: 'team', tablet: 'team', debrief: 'debrief', history: 'debrief' };
  var STATE_NAMES = { success: 'Success', empty: 'Empty', loading: 'Loading', offline: 'Offline',
    stale: 'Stale', partial: 'Partial', denied: 'Permission denied', failed: 'Failed' };

  var S; // session state, reset by resetSession()

  function resetSession() {
    S = {
      profileChosen: false,
      capture: { armed: null, rev: 18, origin: 'desktop', seq: 2, history: [
        { n: 1, time: '18:20:02', intent: 'stash', outcome: 'Analysed as Full stash', result: 'stash' },
        { n: 2, time: '18:20:31', intent: 'stash', outcome: 'Analysed as Full stash', result: 'stash' }
      ], pending: [], running: null, lootReady: false, lootTime: null },
      stashStep: 0,
      stateBy: { raid: 'success', intel: 'success', plan: 'success', team: 'success', debrief: 'success', setup: 'success' },
      mapView: 'map',
      route: 'lower',
      correction: null,
      draft: null,
      failNextSave: false,
      tablet: { mode: 'follow', desktopRev: 42, desktopView: 'Raid · Customs', tabletView: 'Raid · Customs', lastAck: 'Rev 42 shown on desktop' },
      marks: C.team.marks.slice(),
      intelOrigin: null,
      query: '',
      postRaid: false,
      queue: []
    };
  }

  // Kept outside the session: a reset must not undo a participant's own display or shortcut choice.
  var shortcutsOn = true;

  // ---------------------------------------------------------------- helpers

  function esc(value) {
    return String(value).replace(/[&<>"']/g, function (ch) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
    });
  }
  function rub(n) { return '₽' + Number(n).toLocaleString('en-GB'); }
  function $(sel, root) { return (root || document).querySelector(sel); }
  function intentLabel(id) {
    for (var i = 0; i < C.intents.length; i++) { if (C.intents[i].id === id) return C.intents[i].label; }
    return id;
  }
  function reduceMotion() {
    return document.body.classList.contains('rm') ||
      (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  }

  // One timer per region: an assertive message must not cancel a polite one already queued.
  var announceTimers = {};
  function announce(text, assertive) {
    var id = assertive ? 'status-assertive' : 'status-polite';
    var region = document.getElementById(id);
    region.textContent = '';
    clearTimeout(announceTimers[id]);
    announceTimers[id] = setTimeout(function () { region.textContent = text; }, 60);
  }

  // ---------------------------------------------------------------- routing

  function parse(hash) {
    var parts = (hash || '').replace(/^#\/?/, '').split('/').filter(Boolean);
    var route = { parts: parts, ws: parts[0] || V.first, intel: null };
    var at = parts.indexOf('intel');
    // Variant B opens Intel beside whatever you were doing: ".../intel/<item>".
    if (variantId === 'b' && at > 0 && parts[at + 1]) {
      route.intel = parts[at + 1];
      route.parts = parts.slice(0, at);
      route.ws = route.parts[0];
    }
    return route;
  }
  function href(path) { return '#/' + path; }
  function intelHref(itemId) {
    if (variantId === 'a') return href('intel/item/' + itemId);
    var base = parse(location.hash).parts.join('/') || V.first;
    return href(base + '/intel/' + itemId);
  }
  function go(path) { location.hash = href(path); }

  // ---------------------------------------------------------------- dialogs

  var dialogOpener = null;
  var dialogHandler = null;

  function openDialog(opts) {
    var dialog = $('#dialog');
    dialogOpener = opts.returnFocus || document.activeElement;
    dialogHandler = opts.onClose || null;
    $('#dialog-title').textContent = opts.title;
    $('#dialog-content').innerHTML = opts.body;
    $('#dialog-actions').innerHTML = opts.actions.map(function (a) {
      return '<button type="submit" value="' + esc(a.value) + '"' + (a.primary ? ' class="primary"' : ' formnovalidate') + '>' + esc(a.label) + '</button>';
    }).join('');
    dialog.returnValue = '';
    if (opts.describedBy) dialog.setAttribute('aria-describedby', opts.describedBy); else dialog.removeAttribute('aria-describedby');
    if (typeof dialog.showModal === 'function') dialog.showModal(); else dialog.setAttribute('open', '');
    // Start on the heading, so Enter cannot commit a choice the participant has not read.
    $('#dialog-title').focus();
  }

  function initDialog() {
    var dialog = $('#dialog');
    dialog.addEventListener('cancel', function () { dialog.returnValue = 'cancel'; });
    // Enter on a radio or text field would submit with the first button, which is Skip or Cancel.
    dialog.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && e.target.matches('input[type=radio], input[type=checkbox], input[type=text], select')) e.preventDefault();
    });
    dialog.addEventListener('close', function () {
      var value = dialog.returnValue || 'cancel';
      var data = new FormData($('form', dialog));
      var handler = dialogHandler;
      var opener = dialogOpener;
      var openerId = opener && opener.id;
      dialogHandler = null;
      var next = handler ? handler(value, data) : null;
      if (next && next.focus) { next.focus(); return; }
      if (opener && document.body.contains(opener)) { opener.focus(); return; }
      // The moderator panel is rebuilt on every render, so find its controls again by id.
      if (openerId && document.getElementById(openerId)) { document.getElementById(openerId).focus(); return; }
      var h1 = $('#main h1');
      if (h1) h1.focus();
    });
  }

  // ---------------------------------------------------------------- chrome

  function renderChrome(route) {
    var tablet = route.ws === 'tablet';
    $('#sample-tag').textContent = C.sampleNotice;
    var bits = [];
    bits.push('<p class="context-item"><strong>' + esc(S.profileChosen ? C.profile.chosen : C.profile.none) + '</strong></p>');
    bits.push('<p class="context-item">Local time <strong>' + esc(S.postRaid ? C.postRaid.localTime : C.localTime) + '</strong></p>');
    bits.push(S.postRaid ? '<p class="context-item">' + esc(C.postRaid.state) + '</p>'
      : '<p class="context-item">' + esc(C.raid.state) + ' · ' + esc(C.raid.map) + ' · ' + esc(C.raid.side) + ' · elapsed <strong>' + esc(C.raid.elapsed) + '</strong></p>');
    var needs = S.profileChosen ? 0 : 1;
    bits.push('<p class="context-item"><a href="' + href(variantId === 'a' ? 'setup' : 'home') + '">' +
      (needs ? '1 setup item needs action' : 'Setup: nothing needs action') + '</a></p>');
    if (variantId === 'b') bits.push('<p class="context-item"><a href="' + href('setup') + '">Setup</a></p>');
    if (V.search && !tablet) {
      bits.push('<form class="search-form" role="search" id="search-form"><label for="global-search">Search</label>' +
        '<input type="search" id="global-search" name="q" value="' + esc(S.query) + '" placeholder="Items, ammo, keys, quests">' +
        '<button type="submit">Search</button></form>');
    }
    if (!tablet) bits.push(captureTray('desktop'));
    $('#context-bar').innerHTML = bits.join('');
    $('#rail').hidden = tablet;

    var cur = route.ws;
    if (cur === 'search') cur = null;
    var html = '<ul>' + V.nav.map(function (n) {
      return '<li><a href="' + href(n.id) + '"' + (cur === n.id ? ' aria-current="page"' : '') + '>' + esc(n.label) + '</a></li>';
    }).join('') + '</ul>';
    if (variantId === 'a') {
      html += '<h2 id="rail-setup">Setup</h2><ul aria-labelledby="rail-setup"><li><a href="' + href('setup') + '"' +
        (cur === 'setup' ? ' aria-current="page"' : '') + '>' + esc(V.utility.label) + '</a></li></ul>';
    }
    $('#rail').innerHTML = html;
  }

  function captureTray(device) {
    var c = S.capture;
    var armed = c.armed ? 'Armed: <strong>' + esc(intentLabel(c.armed)) + '</strong> (rev ' + c.rev + ', set on ' + esc(c.origin) + ')'
      : 'Not armed: screenshots use Auto-detect';
    var pending = c.pending.length ? ' · <strong>' + c.pending.length + (c.pending.length === 1 ? ' capture needs' : ' captures need') + ' a decision</strong>' : '';
    return '<div class="capture-tray" data-device="' + device + '"><p class="context-item" id="capture-state-' + device + '">' + armed + pending + '</p>' +
      (c.pending.length ? '<button type="button" id="decide-' + device + '" data-action="decide" aria-describedby="capture-state-' + device + '">Decide</button>' : '') +
      '<button type="button" class="primary" data-action="open-capture" aria-describedby="capture-state-' + device + '">Capture</button></div>';
  }

  // ---------------------------------------------------------------- shared fragments

  function stateBanner(key) {
    var st = S.stateBy[key];
    if (!st || st === 'success' || !C.states[key]) return '';
    var cell = C.states[key][st];
    return '<section class="banner-state ' + st + '" aria-labelledby="state-h">' +
      '<h2 id="state-h">' + esc(STATE_NAMES[st]) + '</h2><p>' + esc(cell[0]) + '</p>' +
      '<div class="actions"><button type="button" data-action="recover" data-key="' + key + '">' + esc(cell[1]) + '</button></div></section>';
  }

  function mapToggle(id) {
    return '<div class="map-toggle" role="group" aria-label="Show ' + esc(id) + ' as">' +
      '<button type="button" data-action="map-view" data-view="map" aria-pressed="' + (S.mapView === 'map') + '">Map</button>' +
      '<button type="button" data-action="map-view" data-view="list" aria-pressed="' + (S.mapView === 'list') + '">List</button></div>';
  }

  function svgMap(id, routePoints, altPoints, caption, youAge) {
    var z = C.raid.zones.map(function (zone) {
      var cls = zone.level.replace(' ', '-');
      return '<rect class="zone ' + cls + '" x="' + zone.x + '" y="' + zone.y + '" width="' + zone.w + '" height="' + zone.h + '"></rect>' +
        '<text x="' + (zone.x + 3) + '" y="' + (zone.y + 11) + '">' + esc(zone.name) + '</text>' +
        '<text x="' + (zone.x + 3) + '" y="' + (zone.y + 22) + '">' + esc(zone.level) + '</text>';
    }).join('');
    var ex = C.raid.extracts.map(function (e) {
      return '<polygon class="extract" points="' + e.x + ',' + (e.y - 7) + ' ' + (e.x + 7) + ',' + e.y + ' ' + e.x + ',' + (e.y + 7) + ' ' + (e.x - 7) + ',' + e.y + '"></polygon>' +
        '<text x="' + (e.x - 20) + '" y="' + (e.y + 18) + '">' + esc(e.name) + '</text>';
    }).join('');
    return '<figure class="map-figure"><svg viewBox="0 0 420 260" role="img" aria-labelledby="' + id + '-t ' + id + '-d">' +
      '<title id="' + id + '-t">' + esc(caption) + '</title>' +
      '<desc id="' + id + '-d">Schematic, not to scale. The List view has the same information as text.</desc>' +
      '<defs><pattern id="hatch-dense" width="5" height="5" patternUnits="userSpaceOnUse"><line x1="0" y1="5" x2="5" y2="0"></line></pattern>' +
      '<pattern id="hatch-sparse" class="sparse" width="10" height="10" patternUnits="userSpaceOnUse"><line x1="0" y1="10" x2="10" y2="0"></line></pattern></defs>' +
      z + (altPoints ? '<polyline class="route alt" points="' + altPoints + '"></polyline>' : '') +
      '<polyline class="route" points="' + routePoints + '"></polyline>' + ex +
      '<circle class="you" cx="' + C.raid.you.x + '" cy="' + C.raid.you.y + '" r="5"></circle>' +
      '<text x="' + (C.raid.you.x + 8) + '" y="' + (C.raid.you.y + 3) + '">You (' + esc(youAge || '1 min old') + ')</text>' +
      '<text class="map-model-label" x="4" y="256">Modelled traffic · not live</text>' +
      '</svg><figcaption class="note">Hatched and outlined zones are modelled traffic levels, labelled in text. Diamonds are extracts.</figcaption></figure>';
  }

  // The List view carries everything the map shows, as text, for both Raid and Plan.
  function mapList(routeHeading, steps) {
    var r = C.raid;
    return '<h3>Modelled traffic by zone</h3><ul>' + r.zones.map(function (z) { return '<li>' + esc(z.name) + ': ' + esc(z.level) + '</li>'; }).join('') +
      '</ul><h3>Extracts</h3><ul>' + r.extracts.map(function (e) { return '<li>' + esc(e.name) + ': ' + esc(e.status) + '</li>'; }).join('') +
      '</ul><h3>' + esc(routeHeading) + '</h3><ol>' + steps.map(function (v) { return '<li>' + esc(v) + '</li>'; }).join('') + '</ol>';
  }

  function modelFacts(m) {
    return '<p><span class="model-label">' + esc(m.label) + '</span></p><dl class="facts">' +
      '<dt>Source</dt><dd>' + esc(m.source) + '</dd>' +
      '<dt>Data through</dt><dd>' + esc(m.dataThrough) + '</dd>' +
      '<dt>Generated</dt><dd>' + esc(m.generated) + '</dd>' +
      '<dt>Coverage</dt><dd>' + esc(m.coverage) + '</dd>' +
      '<dt>Confidence</dt><dd>' + esc(m.confidence) + '</dd>' +
      '<dt>Model version</dt><dd>' + esc(m.version) + '</dd>' +
      '<dt>Why shown</dt><dd>' + esc(m.why) + '</dd></dl>';
  }

  function page(title, lede, body, key) {
    return '<div class="page-head"><h1 tabindex="-1">' + esc(title) + '</h1>' + (lede ? '<p>' + lede + '</p>' : '') + '</div>' +
      stateBanner(key) + (S.stateBy[key] === 'empty' || S.stateBy[key] === 'loading'
        ? '<section class="panel" aria-labelledby="remainder-h" ' + (S.stateBy[key] === 'loading' ? 'aria-busy="true"' : '') +
          '><h2 id="remainder-h">Still available</h2><p>' + esc(C.states[key][S.stateBy[key]][0]) + '</p></section>'
        : body);
  }

  // ---------------------------------------------------------------- views

  function readinessList() {
    return '<ul class="checklist">' + C.readiness.map(function (r) {
      var status = r.status, text = r.statusText, action = r.action;
      if (r.id === 'profile' && S.profileChosen) { status = 'ok'; text = 'Chosen'; action = 'Change profile'; }
      return '<li><div><h3>' + esc(r.label) + '</h3></div><span class="status ' + status + '">' + esc(text) + '</span>' +
        '<p class="detail">' + esc(r.id === 'profile' && S.profileChosen ? C.profile.chosen : r.detail) + '</p>' +
        '<div class="actions"><button type="button" data-action="readiness" data-id="' + r.id + '">' + esc(action) +
        '<span class="visually-hidden"> for ' + esc(r.label) + '</span></button></div></li>';
    }).join('') + '</ul>';
  }

  function privacyPanel() {
    return '<section class="panel" aria-labelledby="privacy-h"><h2 id="privacy-h">Privacy at a glance</h2><ul>' +
      C.privacy.map(function (p) { return '<li>' + esc(p) + '</li>'; }).join('') + '</ul></section>';
  }

  function viewSetup() {
    var needs = S.profileChosen ? 'Nothing needs action.' : '1 item needs action.';
    var body = '<div class="split"><section class="panel" aria-labelledby="ready-h"><h2 id="ready-h">Get ready</h2>' +
      '<p>' + needs + ' Everything else can wait, and sample data works before any of it.</p>' + readinessList() +
      '<div class="actions"><button type="button" class="primary" data-action="sample">Explore with sample data</button></div></section>' +
      '<div class="grid"><nav class="panel" aria-labelledby="sections-h"><h2 id="sections-h">Setup and Admin sections</h2><ul>' +
      C.setupSections.map(function (s) { return '<li><a href="#/setup" data-action="stub" data-name="' + esc(s) + '">' + esc(s) + '</a></li>'; }).join('') +
      '</ul></nav>' + privacyPanel() + '</div></div>';
    return page(variantId === 'a' ? 'Setup & Admin' : 'Setup', 'Readiness, privacy, recovery and diagnostics.', body, 'setup');
  }

  function viewHome() {
    var needs = S.profileChosen ? 'Nothing needs action.' : '1 item needs action.';
    var body = '<div class="split"><section class="panel" aria-labelledby="ready-h"><h2 id="ready-h">Get ready</h2>' +
      '<p>' + needs + ' Everything else can wait, and sample data works before any of it.</p>' + readinessList() +
      '<div class="actions"><button type="button" class="primary" data-action="sample">Explore with sample data</button>' +
      '<a class="button" href="' + href('setup') + '">All setup</a></div></section>' +
      '<div class="grid"><section class="panel" aria-labelledby="continue-h"><h2 id="continue-h">Continue</h2><ul>' +
      '<li><a href="' + href('prepare') + '">Plan: Customs progression</a> <span class="muted">(' + esc(C.plan.requirementSummary) + ')</span></li>' +
      (S.capture.lootReady ? '<li><a href="' + href('raid/loot') + '">Last capture: Loot decision at ' + esc(S.capture.lootTime) + '</a></li>' : '<li>No capture this raid yet</li>') +
      '<li><a href="' + href('history') + '">Last raid: Customs, PMC, 18:30</a></li></ul></section>' +
      privacyPanel() + '</div></div>';
    return page('Home', 'What needs you now, and where you left off.', body, 'setup');
  }

  function viewRaid() {
    var r = C.raid;
    var routes = r.routes;
    var sel = routes[0].id === S.route ? routes[0] : routes[1];
    var alt = sel === routes[0] ? routes[1] : routes[0];
    var st = S.stateBy.raid;
    var stale = st === 'stale';
    // A failed map draw leaves the list, so the page must not keep drawing the map it says failed.
    var mapOrList = S.mapView === 'map' && st !== 'failed'
      ? svgMap('raidmap', sel.points, alt.points, 'Customs schematic with ' + sel.name + ' route', stale ? '6 min old' : null)
      : mapList('Selected route', sel.via);
    var lastCapture = S.capture.lootReady
      ? '<p>Loot decision from your screenshot at ' + esc(S.capture.lootTime) + ': TAKE 3 · SWAP 1 · LEAVE 1 · REVIEW 1.</p><a class="button" href="' + href('raid/loot') + '">Open loot decision</a>'
      : '<p>No capture this raid yet.</p>';
    var body = '<div class="split"><section class="panel" aria-labelledby="map-h"><div class="page-head"><h2 id="map-h">Customs</h2>' + mapToggle('Customs') + '</div>' +
      mapOrList + '</section><div class="grid">' +
      '<section class="panel" aria-labelledby="now-h"><h2 id="now-h">Now</h2><dl class="facts">' +
      '<dt>Raid</dt><dd>' + esc(r.state) + ' · ' + esc(r.map) + ' · ' + esc(r.side) + ' (' + esc(r.stateSource) + ')</dd>' +
      '<dt>Elapsed</dt><dd>' + esc(r.elapsed) + '</dd><dt>Time left</dt><dd>' + esc(r.remaining) + '</dd>' +
      '<dt>Position</dt><dd>' + esc(stale ? 'From your screenshot at 18:36:40 (6 min old)' : r.position) + '</dd></dl>' +
      '<div class="actions"><button type="button" data-action="stub" data-name="Correct raid state">Correct raid state</button></div></section>' +
      '<section class="panel" aria-labelledby="routes-h"><h2 id="routes-h">Routes to extract</h2><fieldset><legend>Route</legend><ul class="radio-list">' +
      routes.map(function (rt) {
        return '<li><label><input type="radio" name="route" data-action="route" value="' + rt.id + '"' + (rt.id === S.route ? ' checked' : '') + '>' +
          '<span><strong>' + esc(rt.name) + '</strong> to ' + esc(rt.extract) + ', ' + esc(rt.estimate) + '. ' + esc(rt.tradeoff) + '</span></label></li>';
      }).join('') + '</ul></fieldset>' + modelFacts(r.model) + '</section>' +
      '<section class="panel" aria-labelledby="cap-h"><h2 id="cap-h">Latest capture</h2>' + lastCapture + '</section></div></div>';
    return page('Raid', 'What you know now, what is uncertain, and what to do next.', body, 'raid');
  }

  function viewLoot() {
    var L = C.loot, I = C.items;
    var shot = S.capture.lootTime || L.screenshot;
    var rows = L.decisions.map(function (d, i) {
      var it = I[d.item];
      var name = it.name;
      var reasons = d.reasons.slice();
      var decision = d.decision;
      if (d.item === 'military-cable' && S.correction) {
        name = S.correction; decision = 'LEAVE';
        reasons = ['Corrected by you to ' + S.correction, 'Below your value band'];
      }
      var extra = d.swapOut ? '<br>Swap out ' + esc(I[d.swapOut].name) + ' (carried, ' + esc(I[d.swapOut].size) + ', ' + rub(I[d.swapOut].net) + ' flea net est.). Gain ' + rub(d.gain) + ' flea net est.' : '';
      var review = decision === 'REVIEW' ? '<button type="button" data-action="correct" data-item="' + d.item + '">Correct match<span class="visually-hidden"> for ' + esc(name) + '</span></button>' : '';
      return '<tr><th scope="row">' + esc(name) + '</th><td><span class="decision ' + decision + '">' + decision + '</span></td>' +
        '<td><ol>' + reasons.map(function (x) { return '<li>' + esc(x) + '</li>'; }).join('') + '</ol>' + extra +
        '<details><summary>What would change this</summary><p>' + esc(d.change) + '</p></details></td>' +
        '<td>' + esc(it.size) + '</td><td class="num">' + rub(it.net) + '</td><td class="num">' + rub(it.net / it.squares) + '</td><td>' + esc(it.confidence) + '</td>' +
        '<td><div class="actions"><a class="button" href="' + intelHref(d.item) + '" data-origin="loot-' + i + '">Details<span class="visually-hidden"> for ' + esc(name) + '</span></a>' + review + '</div></td></tr>';
    }).join('');
    var body = '<section class="panel" aria-labelledby="loot-sum-h"><h2 id="loot-sum-h">6 of 6 container items: TAKE 3 · SWAP 1 · LEAVE 1 · REVIEW 1</h2>' +
      '<dl class="facts"><dt>Detected</dt><dd>' + esc(L.detected) + ' (confidence ' + esc(L.detectConfidence) + ')</dd>' +
      '<dt>Screenshot</dt><dd>Taken ' + esc(shot) + '. The file stays in your EFT folder; the decoded image was discarded after analysis.</dd>' +
      '<dt>Space</dt><dd>Backpack ' + L.backpack.cols + '×' + L.backpack.rows + ': ' + L.backpack.used + ' used, ' + L.backpack.free + ' free (' + esc(L.backpack.freeShape) + '). After the TAKE and SWAP moves: 16 used, 0 free.</dd>' +
      '<dt>Container</dt><dd>' + L.container.cols + '×' + L.container.rows + ': 6 items using ' + L.container.used + ' squares.</dd>' +
      '<dt>Values</dt><dd>Flea net estimate = sample flea price minus estimated fee, 12 min old. Not guaranteed proceeds. Gross is on each item’s details.</dd>' +
      '<dt>Your band</dt><dd>' + esc(L.valueBand) + '</dd></dl>' +
      '<p class="note">Advice only. You move items in the game yourself; the companion sends no input to it.</p></section>' +
      '<div class="table-wrap"><table><caption>Decisions, strongest reason first</caption><thead><tr><th scope="col">Item</th><th scope="col">Decision</th><th scope="col">Why</th>' +
      '<th scope="col">Size</th><th scope="col" class="num">Flea net est.</th><th scope="col" class="num">Per square</th><th scope="col">Match confidence</th><th scope="col">Actions</th></tr></thead><tbody>' +
      rows + '</tbody></table></div>' +
      '<section class="panel" aria-labelledby="carried-h"><h2 id="carried-h">Carried items considered for swaps</h2><ul>' +
      L.carried.map(function (c) { return '<li>' + esc(c.name) + ' (' + esc(c.size) + '): ' + esc(c.note) + '</li>'; }).join('') + '</ul></section>';
    return page('Loot decision', 'From your screenshot at ' + esc(shot) + '.', body, 'raid');
  }

  function itemDetail(id, headingLevel) {
    var it = C.items[id];
    if (!it) return '<p>Not in this storyboard.</p>';
    var h = headingLevel || 2;
    return '<h' + h + ' id="intel-h" tabindex="-1">' + esc(it.name) + '</h' + h + '>' +
      '<p class="muted">' + esc(it.type) + ' · ' + esc(it.size) + ' · match confidence when scanned ' + esc(it.confidence) + (it.alternative ? ' (could be ' + esc(it.alternative) + ')' : '') + '</p>' +
      (it.advice ? '<p><strong>' + esc(it.advice) + '</strong></p>' : '') +
      '<h' + (h + 1) + '>Price</h' + (h + 1) + '><dl class="facts">' +
      '<dt>Flea gross</dt><dd>' + rub(it.gross) + ' (sample, ' + esc(it.priceAge) + ' old)</dd>' +
      '<dt>Estimated fee</dt><dd>' + rub(it.fee) + '</dd><dt>Flea net est.</dt><dd>' + rub(it.net) + '</dd>' +
      '<dt>Best trader</dt><dd>' + rub(it.trader) + ' from ' + esc(it.traderName) + '</dd></dl>' +
      '<h' + (h + 1) + '>Needed for</h' + (h + 1) + '>' + (it.needs.length ? '<ul>' + it.needs.map(function (n) { return '<li>' + esc(n) + '</li>'; }).join('') + '</ul>' : '<p>No quest, hideout or pin needs it.</p>') +
      '<h' + (h + 1) + '>Owned</h' + (h + 1) + '><p>' + esc(it.owned) + '</p>' +
      '<div class="actions"><button type="button" data-action="stub" data-name="Add to plan">Add to plan</button>' +
      '<button type="button" data-action="copy-link">Copy link to this item</button></div>';
  }

  function viewIntel(route) {
    var p = route.parts;
    if (p[1] === 'stash') return viewStash();
    if (p[1] === 'item' && p[2]) {
      var back = S.intelOrigin ? '<p><a href="' + S.intelOrigin.href + '" data-action="close-intel">Back to ' + esc(S.intelOrigin.label) + '</a></p>' : '<p><a href="' + href('intel') + '">Back to Intel search</a></p>';
      return page('Intel', 'What it is, what it is worth, and whether it matters to you.', back + '<section class="panel" aria-labelledby="intel-h">' + itemDetail(p[2], 2) + '</section>', 'intel');
    }
    return page('Intel', 'Items, ammo, keys, crafts and barters.', searchPanel(href('intel/item/')) +
      '<section class="panel" aria-labelledby="stash-h"><h2 id="stash-h">Stash scan</h2><p>Scan your whole stash in one guided session.</p>' +
      '<a class="button" href="' + href('intel/stash') + '">Open stash scan</a></section>', 'intel');
  }

  function searchPanel(base) {
    var q = S.query.toLowerCase();
    var ids = Object.keys(C.items).filter(function (id) { return !q || C.items[id].name.toLowerCase().indexOf(q) >= 0; });
    var form = variantId === 'a' ? '<form class="search-form" role="search" id="intel-search"><label for="intel-q">Search Intel</label>' +
      '<input type="search" id="intel-q" name="q" value="' + esc(S.query) + '"><button type="submit">Search</button></form>' : '';
    return '<section class="panel" aria-labelledby="results-h">' + form + '<h2 id="results-h">' + (q ? ids.length + (ids.length === 1 ? ' result' : ' results') + ' for “' + esc(S.query) + '”' : 'Recent and planned items') + '</h2>' +
      (ids.length ? '<ul>' + ids.map(function (id) {
        var link = variantId === 'a' ? base + id : intelHref(id);
        return '<li><a href="' + link + '" data-origin="result-' + id + '">' + esc(C.items[id].name) + '</a> <span class="muted">' + esc(C.items[id].type) + ' · ' + rub(C.items[id].net) + ' flea net est.</span></li>';
      }).join('') + '</ul>' : '<p>No sample item matches. Try “drill” or “key”.</p>') + '</section>';
  }

  function viewSearch() {
    return page('Search', 'Results open beside the page you came from.', searchPanel(''), 'intel');
  }

  function viewStash() {
    var snap = C.stash.snapshots[S.stashStep];
    var steps = C.stash.steps.slice(0, snap.captures);
    var pct = Math.round(snap.rowsCovered / C.stash.rowsTotal * 100);
    var body = '<div class="split"><section class="panel" aria-labelledby="guide-h"><h2 id="guide-h">Guided capture: Full stash</h2>' +
      '<p><strong>Next:</strong> ' + esc(snap.next) + '</p><p class="note">Use the game’s own screenshot key. The companion never presses it for you.</p>' +
      '<h3>Screenshots in this session</h3><ol>' + steps.map(function (s) {
        return '<li>' + esc(s.rows) + ': ' + s.stacks + ' stacks' + (s.duplicates ? ', ' + s.duplicates + ' overlapping duplicates merged' : '') + ', ' + s.added + ' added.</li>';
      }).join('') + '<li>Waiting for your next screenshot.</li></ol>' +
      '<p>Coverage: <strong>' + snap.rowsCovered + ' of ' + C.stash.rowsTotal + ' rows (' + pct + '%)</strong>. Not complete.</p>' +
      '<p class="note">Each screenshot file stays in your EFT folder. Decoded images are discarded after analysis.</p>' +
      '<div class="actions"><button type="button" data-action="finish-stash">Finish with partial coverage</button></div></section>' +
      '<div class="grid"><section class="panel" aria-labelledby="groups-h"><h2 id="groups-h">' + snap.total + ' stacks seen so far</h2>' +
      '<div class="table-wrap"><table><caption>Suggested groups (advice)</caption><tbody>' +
      '<tr><th scope="row">Keep</th><td class="num">' + snap.keep + '</td></tr><tr><th scope="row">Sell</th><td class="num">' + snap.sell + '</td></tr>' +
      '<tr><th scope="row">Use soon</th><td class="num">' + snap.useSoon + '</td></tr><tr><th scope="row">Review</th><td class="num">' + snap.review + '</td></tr>' +
      '<tr><th scope="row">Total</th><td class="num">' + snap.total + '</td></tr></tbody></table></div>' +
      '<p>Review: ' + esc(snap.reviewParts) + '.</p><p>Keys: ' + esc(snap.keys) + '.</p><p>Sell group: ' + esc(snap.sellValue) + '.</p></section>' +
      '<section class="panel" aria-labelledby="org-h"><h2 id="org-h">Manual organisation plan</h2><p class="note">You move the items. The companion does not sort, click or drag anything in the game. Items are listed where they were seen, not rearranged.</p><ol>' +
      C.stash.organise.map(function (o) { return '<li>' + esc(o) + '</li>'; }).join('') + '</ol></section></div></div>';
    // Its own key in both variants, so an injected Intel or Plan state never shows different text here.
    return page('Stash scan', 'Observed from your screenshots; unknown stays unknown.', body, 'stash');
  }

  function viewPlan(route) {
    if (route.parts[1] === 'stash') return viewStash();
    var P = C.plan;
    var failed = S.stateBy.plan === 'failed';
    var mapOrList = S.mapView === 'map' && !failed ? svgMap('planmap', P.route.points, null, 'Customs schematic with plan route') :
      mapList('Route order', P.route.steps);
    var body = '<div class="split"><div class="grid"><section class="panel" aria-labelledby="bundles-h"><h2 id="bundles-h">Suggested bundles</h2><ul>' +
      P.bundles.map(function (b, i) { return '<li>' + (i === 0 ? '<strong aria-current="true">' : '') + esc(b.name) + (i === 0 ? '</strong> (open)' : '') + ' <span class="muted">' + esc(b.summary) + '</span></li>'; }).join('') + '</ul></section>' +
      '<section class="panel" aria-labelledby="obj-h"><h2 id="obj-h">Customs progression</h2><h3>Objectives</h3><ol>' +
      P.objectives.map(function (o) { return '<li>' + esc(o) + '</li>'; }).join('') + '</ol><p><strong>Extract:</strong> ' + esc(P.extract) + '</p>' +
      '<h3>Requirements: ' + esc(P.requirementSummary) + '</h3><ul>' + P.requirements.map(function (r) {
        return '<li><span class="status ' + (r.status === 'Confirmed' ? 'ok' : 'unknown') + '">' + esc(r.status) + '</span> ' + esc(r.name) + ' <span class="muted">(' + esc(r.source) + ')</span></li>';
      }).join('') + '</ul>' +
      '<div class="actions"><a class="button" href="' + intelHref('dorm-key') + '" data-origin="plan-dorm-key">Details for Dorm room 214 key</a>' +
      // Stash scan lives on Prepare in B. In A it lives under Intel only, so F-06 has one A answer.
      (variantId === 'b' ? '<a class="button" href="' + href(V.stashBase + '/stash') + '">Open stash scan</a>' : '') + '</div></section></div>' +
      '<section class="panel" aria-labelledby="proute-h"><div class="page-head"><h2 id="proute-h">Route estimate</h2>' + mapToggle('route') + '</div>' + mapOrList +
      (failed ? '<p><strong>Route estimate unavailable:</strong> the route model failed. The route order above is from your objectives, not the model.</p>'
        : '<p><strong>' + esc(P.route.estimate) + '</strong>, confidence ' + esc(P.route.confidence) + '. ' + esc(P.route.tradeoff) + '</p>' + modelFacts(C.raid.model)) +
      '<div class="actions"><a class="button primary" href="' + href('raid') + '">Open in Raid</a><button type="button" data-action="share-plan">Share…</button></div></section></div>';
    return page(V.planLabel, 'Bundle quests, check requirements and choose a route for the next raid.', body, 'plan');
  }

  function viewTeam() {
    var T = C.team;
    var body = '<div class="grid"><section class="panel" aria-labelledby="members-h"><h2 id="members-h">Members</h2><div class="table-wrap"><table><thead><tr><th scope="col">Member</th><th scope="col">Role</th><th scope="col">Device</th><th scope="col">Freshness</th></tr></thead><tbody>' +
      T.members.map(function (m) { return '<tr><th scope="row">' + esc(m.name) + '</th><td>' + esc(m.role) + '</td><td>' + esc(m.device) + '</td><td>' + (m.stale ? '<span class="status stale">' + esc(m.freshness) + '</span>' : esc(m.freshness)) + '</td></tr>'; }).join('') +
      '</tbody></table></div><h3>What each role can do</h3><dl class="facts">' + T.roles.map(function (r) { return '<dt>' + esc(r.role) + '</dt><dd>' + esc(r.can) + '</dd>'; }).join('') + '</dl></section>' +
      '<section class="panel" aria-labelledby="devices-h"><h2 id="devices-h">Your paired devices</h2><dl class="facts"><dt>Device</dt><dd>' + esc(T.device.name) + '</dd><dt>Relationship</dt><dd>' + esc(T.device.relation) + '</dd>' +
      '<dt>Can do</dt><dd>' + esc(T.device.scope) + '</dd><dt>Session</dt><dd>' + esc(T.device.expires) + '</dd></dl>' +
      '<div class="actions"><a class="button primary" href="' + href('tablet') + '">Open tablet preview</a><button type="button" data-action="pair">Pair another device</button><button type="button" data-action="stub" data-name="Revoke tablet">Revoke<span class="visually-hidden"> ' + esc(T.device.name) + '</span></button></div></section>' +
      '<section class="panel" aria-labelledby="marks-h"><h2 id="marks-h">Marks</h2>' + marksList() + '</section></div>';
    return page('Team', 'Who is with you, what you are doing, and what you have shared.', body, 'team');
  }

  function marksList() {
    return '<ul>' + S.marks.map(function (m) {
      return '<li><strong>' + esc(m.kind) + ':</strong> ' + esc(m.label) + ' <span class="muted">· ' + esc(m.author) + ' · shared with ' + esc(m.scope) + ' · ' + esc(m.age) + ' · ' + esc(m.ttl) + '</span></li>';
    }).join('') + '</ul>';
  }

  function viewTablet() {
    var t = S.tablet;
    var modes = [
      { id: 'follow', label: 'Follow desktop', owner: 'Desktop leads. This tablet shows what the desktop shows.' },
      { id: 'control', label: 'Control desktop', owner: 'This tablet leads. Your changes are applied on the desktop and confirmed.' },
      { id: 'independent', label: 'Independent view', owner: 'Browsing on this tablet only. The desktop is not changed.' }
    ];
    var mode = modes.filter(function (m) { return m.id === t.mode; })[0];
    var tabletNav = '<nav class="tablet-nav" aria-label="Tablet workspaces"><ul>' + V.nav.map(function (n) {
      var blocked = t.mode === 'follow';
      return '<li><button type="button" data-action="tablet-nav" data-label="' + esc(n.label) + '"' + (blocked ? ' aria-disabled="true" aria-describedby="follow-note"' : '') + '>' + esc(n.label) + '</button></li>';
    }).join('') + '</ul>' + (t.mode === 'follow' ? '<p id="follow-note" class="note">In Follow desktop, change mode to navigate from the tablet.</p>' : '') + '</nav>';
    var body = '<p><a class="button" href="' + href('team') + '">Leave tablet preview</a></p><div class="split">' +
      '<section class="tablet-frame" aria-labelledby="tablet-h"><h2 id="tablet-h">Paired tablet (simulated)</h2>' +
      '<p class="muted">' + esc(C.team.device.relation) + ' ' + esc(C.team.device.scope) + '</p>' +
      '<fieldset class="segmented"><legend>Tablet mode</legend>' + modes.map(function (m) {
        return '<label><input type="radio" name="tablet-mode" data-action="tablet-mode" value="' + m.id + '"' + (m.id === t.mode ? ' checked' : '') + '>' + esc(m.label) + '</label>';
      }).join('') + '</fieldset>' +
      '<p class="owner-indicator" id="owner-indicator">' + esc(mode.owner) + '</p>' + tabletNav +
      '<p>Tablet shows: <strong>' + esc(t.mode === 'independent' ? t.tabletView : t.desktopView) + '</strong></p>' +
      (t.mode === 'independent' ? '<div class="actions"><button type="button" class="primary" data-action="show-on-desktop">Show this view on desktop</button></div>' : '') +
      '<h3>Marks</h3><div class="actions mark-tools"><button type="button" data-action="add-mark" data-kind="Ping">Ping</button><button type="button" data-action="add-mark" data-kind="Waypoint">Waypoint</button><button type="button" data-action="add-mark" data-kind="Note">Note</button></div>' +
      '<p class="note">Marks are placed by choosing a named place, so they work without dragging on the map.</p>' + marksList() +
      '<h3>Capture from the tablet</h3>' + captureTray('tablet') + '</section>' +
      '<section class="panel" aria-labelledby="desk-h"><h2 id="desk-h">Desktop companion (simulated)</h2><dl class="facts"><dt>Showing</dt><dd>' + esc(t.desktopView) + '</dd>' +
      '<dt>Revision</dt><dd>' + t.desktopRev + '</dd><dt>Last confirmation</dt><dd>' + esc(t.lastAck) + '</dd>' +
      '<dt>Navigation owner</dt><dd>' + (t.mode === 'control' ? 'Your tablet' : 'Desktop') + '</dd></dl>' +
      '<p class="note">Pairing controls companion state only. It never sends input to the game.</p></section></div>';
    return page('Tablet preview', 'For the paired-tablet journey. Open this address on a real tablet for touch sessions.', body, 'team');
  }

  function viewDebrief() {
    var D = C.debrief;
    var cable = S.correction ? 'Corrected by you to ' + S.correction + ' at 18:55.'
      : S.draft ? 'Military cable, match confidence 0.58. Your correction to ' + S.draft + ' is a draft: not saved yet.'
      : 'Military cable, match confidence 0.58. Could be Power cord.';
    var body = '<div class="split"><section class="panel" aria-labelledby="raids-h"><h2 id="raids-h">Raids</h2><ul>' +
      D.raids.map(function (r, i) { return '<li>' + (i === 0 ? '<strong aria-current="true">' : '') + esc(r.when) + ' · ' + esc(r.map) + ' · ' + esc(r.side) + ' · ' + esc(r.outcome) + ' (' + esc(r.outcomeSource) + ')' + (i === 0 ? '</strong> (open)' : '') + '</li>'; }).join('') +
      '</ul></section><div class="grid"><section class="panel" aria-labelledby="tl-h"><h2 id="tl-h">Customs, 2026-09-14 18:30</h2><div class="table-wrap"><table><caption>Timeline. Each row says how it is known.</caption><thead><tr><th scope="col">Time</th><th scope="col">What</th><th scope="col">How known</th><th scope="col">Source</th></tr></thead><tbody>' +
      D.timeline.map(function (e) { return '<tr><td>' + esc(e.time) + '</td><td>' + esc(e.text) + '</td><td>' + esc(e.kind) + '</td><td>' + esc(e.source) + '</td></tr>'; }).join('') +
      '</tbody></table></div></section>' +
      '<section class="panel" aria-labelledby="corr-h"><h2 id="corr-h">Needs your review</h2><p id="cable-state">' + esc(cable) + '</p><div class="actions">' +
      (S.correction ? '<button type="button" data-action="undo-correct">Undo correction</button>' : '<button type="button" data-action="correct" data-item="military-cable" aria-describedby="cable-state">Correct match</button>') + '</div>' +
      '<p class="note">Corrections improve local evaluation only after you confirm them. Nothing is learned silently.</p></section>' +
      '<section class="panel" aria-labelledby="pred-h"><h2 id="pred-h">Traffic prediction for this raid</h2><p>' + esc(D.prediction) + '</p>' +
      modelFacts({ label: 'Modelled traffic · not live · as shown at raid time', source: C.raid.model.source, dataThrough: C.raid.model.dataThrough,
        generated: C.raid.model.generated, coverage: C.raid.model.coverage, confidence: C.raid.model.confidence, version: C.raid.model.version, why: C.raid.model.why }) +
      '<fieldset><legend>Compared with what you saw</legend><ul class="radio-list">' + ['Higher traffic than shown', 'About as shown', 'Lower traffic than shown', 'I did not notice'].map(function (o) {
        return '<li><label><input type="radio" name="traffic-feedback" data-action="feedback" value="' + esc(o) + '"> ' + esc(o) + '</label></li>';
      }).join('') + '</ul></fieldset><div class="actions"><button type="button" data-action="stub" data-name="Export this raid">Export this raid</button></div></section></div></div>';
    return page(V.historyLabel, 'What happened, how it is known, and what to correct.', body, 'debrief');
  }

  // ---------------------------------------------------------------- render

  var lastWs = null;
  function render(fromUser) {
    var route = parse(location.hash);
    if (variantId === 'a' && ['home', 'prepare', 'history', 'search'].indexOf(route.ws) >= 0) route = parse('#/' + V.first);
    if (variantId === 'b' && ['plan', 'debrief', 'intel'].indexOf(route.ws) >= 0) route = parse('#/' + V.first);
    renderChrome(route);
    var ws = route.ws, html;
    if (ws === 'setup') html = viewSetup();
    else if (ws === 'home') html = viewHome();
    else if (ws === 'raid') html = route.parts[1] === 'loot' ? viewLoot() : viewRaid();
    else if (ws === 'intel') html = viewIntel(route);
    else if (ws === 'search') html = viewSearch();
    else if (ws === 'plan' || ws === 'prepare') html = viewPlan(route);
    else if (ws === 'team') html = viewTeam();
    else if (ws === 'tablet') html = viewTablet();
    else if (ws === 'debrief' || ws === 'history') html = viewDebrief();
    else html = viewSetup();

    if (route.intel) {
      html = '<div class="split"><div>' + html + '</div><section class="panel intel-panel" aria-labelledby="intel-h">' +
        '<p class="muted">Intel, opened beside this page. Its address can be copied and opened directly.</p>' + itemDetail(route.intel, 2) +
        '<div class="actions"><a class="button" href="' + href(route.parts.join('/')) + '" data-action="close-intel">Close Intel</a></div></section></div>';
    }
    var main = $('#main');
    var keep = fromUser ? null : focusSignature(document.activeElement);
    // A background re-render keeps what the participant was in the middle of: the capture progress
    // or result box, and search text typed but not yet submitted.
    var stage = !fromUser && $('#stage-box') ? $('#stage-box').outerHTML : null;
    var typed = {};
    if (!fromUser) ['global-search', 'intel-q'].forEach(function (id) { var input = document.getElementById(id); if (input) typed[id] = input.value; });
    main.innerHTML = html;
    if (stage) placeStage(stage);
    Object.keys(typed).forEach(function (id) { var input = document.getElementById(id); if (input) input.value = typed[id]; });
    renderModerator(route);
    if (keep) restoreFocus(keep);
    document.title = ($('#main h1') ? $('#main h1').textContent : 'Storyboard') + ' · ' + V.name;

    if (!fromUser) return;
    if (route.intel) { var ih = $('#intel-h'); if (ih) ih.focus(); announce('Intel opened: ' + (C.items[route.intel] || { name: route.intel }).name); return; }
    if (pendingFocus) {
      var target = document.querySelector('[data-origin="' + pendingFocus + '"]') || document.querySelector('[data-action="' + pendingFocus + '"]');
      pendingFocus = null;
      if (target) { target.focus(); return; }
    }
    var h1 = $('#main h1');
    if (h1) h1.focus();
    if (ws !== lastWs) announce(h1 ? h1.textContent : '');
    lastWs = ws;
  }
  var pendingFocus = null;

  // A re-render replaces the DOM under the participant's focus. Find the same control again, or
  // fall back to the page heading, so a keyboard or screen-reader user is never dropped to <body>.
  function focusSignature(el) {
    if (!el || el === document.body) return null;
    if (!['#main', '#context-bar', '#rail'].some(function (s) { return $(s).contains(el); })) return null;
    if (el.id) return { sel: '#' + el.id, index: 0 };
    var sel = el.tagName.toLowerCase();
    ['data-action', 'data-label', 'data-kind', 'data-view', 'data-id', 'data-item', 'data-origin', 'name', 'value', 'href'].forEach(function (attr) {
      if (el.hasAttribute(attr)) sel += '[' + attr + '="' + String(el.getAttribute(attr)).replace(/"/g, '\\"') + '"]';
    });
    // Several controls can match (every "What would change this" summary), so remember which one.
    var index = Array.prototype.indexOf.call(document.querySelectorAll(sel), el);
    return { sel: sel, index: Math.max(index, 0) };
  }
  function restoreFocus(sig) {
    var target = null;
    try { var all = document.querySelectorAll(sig.sel); target = all[sig.index] || all[0] || null; } catch (err) { target = null; }
    if (!target) target = $('#main h1');
    if (target) target.focus();
  }

  // The progress box goes after the page heading, so the h1 stays the first heading in main.
  function placeStage(markup) {
    var old = $('#stage-box'); if (old) old.remove();
    var head = $('#main .page-head');
    if (head) head.insertAdjacentHTML('afterend', markup); else $('#main').insertAdjacentHTML('afterbegin', markup);
    return $('#stage-box');
  }

  // ---------------------------------------------------------------- capture flows

  function openCaptureDialog(device) {
    var c = S.capture;
    var list = c.history.slice(-5).reverse().map(function (h) {
      return '<li>Capture ' + h.n + ' at ' + esc(h.time) + ' (' + esc(intentLabel(h.intent)) + '): ' + esc(h.outcome) + '</li>';
    }).join('');
    var last = c.history[c.history.length - 1];
    var lastDup = last && last.duplicate ? last : null;
    var pending = c.pending.map(function (p) {
      return '<li>Capture ' + p.n + ' at ' + esc(p.time) + ': ' + esc(p.reason) + '</li>';
    }).join('');
    openDialog({
      title: 'Capture',
      body: '<p>Arm what your next screenshot is for, then take it with the game’s own screenshot key. The companion never presses keys in the game.</p>' +
        '<fieldset><legend>Next screenshot is for</legend><ul class="radio-list">' + C.intents.map(function (it) {
          var checked = (c.armed || 'auto') === it.id;
          return '<li><label><input type="radio" name="intent" value="' + it.id + '"' + (checked ? ' checked' : '') + '><span><strong>' + esc(it.label) + '</strong><br><span class="muted">' + esc(it.hint) + '</span></span></label></li>';
        }).join('') + '</ul></fieldset>' +
        (pending ? '<h3>Needs a decision</h3><ul>' + pending + '</ul><p class="note">Choose “Decide now” to go through them.</p>' : '') +
        '<h3>Recent captures</h3><ul>' + list + '</ul>',
      actions: (pending ? [{ label: 'Decide now', value: 'decide' }] : [])
        .concat(lastDup ? [{ label: 'Analyse capture ' + lastDup.n + ' again', value: 'again' }] : [])
        .concat([{ label: 'Cancel', value: 'cancel' }, { label: 'Arm', value: 'arm', primary: true }]),
      onClose: function (value, data) {
        if (value === 'decide') { setTimeout(function () { resolvePending(); }, 0); return null; }
        if (value === 'again') { lastDup.duplicate = false; runStages(lastDup, lastDup.intent === 'auto' ? 'loot' : lastDup.intent, false); return null; }
        if (value !== 'arm') return null;
        var id = data.get('intent') || 'auto';
        c.armed = id === 'auto' ? null : id;
        c.rev += 1; c.origin = device;
        render(false);
        announce(id === 'auto' ? 'Capture set to Auto-detect.' : 'Armed: ' + intentLabel(id) + '. Take the screenshot in the game.');
        return document.querySelector('[data-device="' + device + '"] [data-action="open-capture"]');
      }
    });
  }

  // Simulated captures follow the 18:41:07 loot screenshot and stay before the 18:42 header clock.
  function now() {
    var s = Math.min(59, 8 + (S.capture.seq - 2) * 3);
    return '18:41:' + (s < 10 ? '0' : '') + s;
  }

  function simulateScreenshot(kind) {
    var c = S.capture;
    c.seq += 1;
    var cap = { n: c.seq, time: now(), intent: c.armed || 'auto', rev: c.rev };
    processCapture(cap, kind);
  }

  function processCapture(cap, kind) {
    var c = S.capture;
    if (c.running && (kind === 'match' || kind === 'writing')) {
      c.queue.push({ cap: cap, kind: kind });
      announce('Capture ' + cap.n + ' waits until capture ' + c.running.n + ' finishes.');
      return;
    }
    if (kind === 'duplicate') {
      var prev = c.history[c.history.length - 1];
      c.history.push({ n: cap.n, time: cap.time, intent: cap.intent, duplicate: true, outcome: 'Same file as capture ' + prev.n + '. Not analysed again.' });
      render(false);
      announce('Screenshot already analysed as capture ' + prev.n + '. No new result.');
      return;
    }
    if (kind === 'race') { raceIntent(cap); return; }
    // A screenshot arrives while the player is in the game, so a disagreement is queued and
    // announced politely. A modal here would steal focus, and on Windows could pull the companion
    // in front of the game. The dialog opens only when the player chooses Decide.
    if (kind === 'unknown') { queueDecision(cap, 'Context unknown', null); return; }
    if (kind === 'mismatch') {
      if (cap.intent === 'auto') { announce('Nothing is armed, so Auto-detect cannot disagree. Arm Loot decision first to rehearse a mismatch.'); if (c.seq === cap.n) c.seq -= 1; return; }
      queueDecision(cap, 'Detected ' + intentLabel(cap.intent === 'stash' ? 'loot' : 'stash') + ' but ' + intentLabel(cap.intent) + ' was armed', cap.intent === 'stash' ? 'loot' : 'stash');
      return;
    }
    runStages(cap, cap.intent === 'auto' ? (S.stashStep === 0 && location.hash.indexOf('stash') >= 0 ? 'stash' : 'loot') : cap.intent, kind === 'writing');
  }

  function runStages(cap, asIntent, stillWriting) {
    var c = S.capture;
    c.running = cap;
    var i = 0, checks = 0;
    placeStage('<section class="panel" id="stage-box" aria-labelledby="stage-h"></section>');
    // A re-render replaces the box, so always write to whichever one is in the page now.
    function box() { return $('#stage-box') || placeStage('<section class="panel" id="stage-box" aria-labelledby="stage-h"></section>'); }
    announce('Screenshot seen. Analysing as ' + intentLabel(asIntent) + '.');
    function draw() {
      var writing = stillWriting && checks < 3 && i === 1;
      box().innerHTML = '<h2 id="stage-h">Capture ' + cap.n + ': ' + esc(intentLabel(asIntent)) + '</h2>' +
        (writing ? '<p>Screenshot is still being written by the game. Waiting (check ' + (checks + 1) + ' of 3). It will not be skipped.</p>' : '') +
        '<ol class="stages">' + C.captureStages.map(function (s, k) {
          return '<li class="' + (k < i ? 'done' : '') + '"' + (k === i ? ' aria-current="step"' : '') + '>' + esc(s) + '</li>';
        }).join('') + '</ol>';
    }
    function step() {
      draw();
      // Waiting is timing, not motion: reduced motion must not make the "still being written" step vanish.
      if (stillWriting && i === 1 && checks < 3) {
        if (checks === 0) announce('Screenshot still being written. Waiting; it will not be skipped.');
        checks += 1; setTimeout(step, 600); return;
      }
      if (i >= C.captureStages.length - 1) { finish(); return; }
      i += 1;
      setTimeout(step, 350);
    }
    function finish() {
      c.running = null;
      var label = 'Analysed as ' + intentLabel(asIntent);
      if (asIntent === 'stash' && S.stashStep === 0) { S.stashStep = 1; label += '. Rows 37 to 60 added.'; }
      if (asIntent === 'loot') { c.lootReady = true; c.lootTime = cap.time; }
      c.history.push({ n: cap.n, time: cap.time, intent: asIntent, outcome: label + (stillWriting ? ' (waited for the file to finish writing)' : '') });
      var where = asIntent === 'stash' ? href(V.stashBase + '/stash') : asIntent === 'loot' ? href('raid/loot') : null;
      box().innerHTML = '<h2 id="stage-h">Capture ' + cap.n + ' ready: ' + esc(intentLabel(asIntent)) + '</h2><p>' + esc(label) + '.</p>' +
        (where ? '<a class="button primary" href="' + where + '">Open result</a>' : '<p>This intent has no result page in the storyboard.</p>');
      // Re-render wherever the participant is, so Raid's Latest capture and Home update too; render
      // keeps the result box.
      render(false);
      announce('Capture ' + cap.n + ' result ready: ' + intentLabel(asIntent) + '.');
      if (c.queue.length) { var next = c.queue.shift(); setTimeout(function () { processCapture(next.cap, next.kind); }, 0); }
    }
    step();
  }

  function mismatch(cap) {
    var detected = cap.intent === 'stash' ? 'loot' : 'stash';
    var noun = { loot: 'a loot screen', stash: 'a stash', ammo: 'ammo', keys: 'keys', quest: 'quest items', extracts: 'an extract list', health: 'a health screen', flea: 'flea listings' };
    openDialog({
      title: 'This looks like ' + noun[detected] + ', not ' + noun[cap.intent],
      describedBy: 'mm-desc',
      body: '<p id="mm-desc">Nothing has been changed yet. Choose how to analyse capture ' + cap.n + '.</p><dl class="facts">' +
        '<dt>You armed</dt><dd>' + esc(intentLabel(cap.intent)) + ' (rev ' + cap.rev + ', set on ' + esc(S.capture.origin) + ')</dd>' +
        '<dt>Detected</dt><dd>' + esc(intentLabel(detected)) + ', a strong match</dd><dt>Screenshot</dt><dd>' + esc(cap.time) + '. File stays in your EFT folder.</dd></dl>' +
        '<p class="note">Closing this without choosing keeps the capture under “Needs a decision”.</p>',
      actions: [
        { label: 'Skip this screenshot', value: 'skip' },
        { label: 'Analyse as ' + intentLabel(cap.intent) + ' anyway', value: 'armed' },
        { label: 'Analyse as ' + intentLabel(detected), value: 'detected', primary: true }
      ],
      onClose: function (value) { settle(cap, value, detected, 'Detected ' + intentLabel(detected) + ' but ' + intentLabel(cap.intent) + ' was armed'); return null; }
    });
  }

  function unknownContext(cap) {
    openDialog({
      title: 'Couldn’t tell what capture ' + cap.n + ' shows',
      describedBy: 'uk-desc',
      body: '<p id="uk-desc">Nothing in your raid, stash or plan was changed.</p><fieldset><legend>It shows</legend><ul class="radio-list">' +
        C.intents.filter(function (it) { return it.id !== 'auto'; })
          .sort(function (x, y) { return (y.id === cap.intent) - (x.id === cap.intent); })
          .map(function (it, k) {
          // Nothing pre-selected: J3 scores the participant's own choice.
          return '<li><label><input type="radio" name="context" value="' + it.id + '"' + (k === 0 ? ' required' : '') + '> ' + esc(it.label) + '</label></li>';
        }).join('') + '</ul></fieldset><p class="note">Closing this without choosing keeps the capture under “Needs a decision”.</p>',
      actions: [{ label: 'Skip this screenshot', value: 'skip' }, { label: 'Analyse as chosen', value: 'chosen', primary: true }],
      onClose: function (value, data) { settle(cap, value, data.get('context'), 'Context unknown'); return null; }
    });
  }

  function settle(cap, value, as, reason) {
    var c = S.capture;
    c.pending = c.pending.filter(function (p) { return p.n !== cap.n; });
    if (value === 'skip') {
      c.history.push({ n: cap.n, time: cap.time, intent: cap.intent, outcome: 'Skipped by you. Nothing changed.' });
      render(false); announce('Capture ' + cap.n + ' skipped. Nothing changed.');
    } else if (value === 'armed') {
      render(false); runStages(cap, cap.intent, false);
    } else if (value === 'detected' || value === 'chosen') {
      render(false); runStages(cap, as, false);
    } else {
      cap.reason = reason; cap.as = as;
      c.pending.push(cap);
      render(false); announce('Capture ' + cap.n + ' needs a decision. It is kept, not skipped.');
    }
  }

  function queueDecision(cap, reason, as) {
    cap.reason = reason; cap.as = as;
    S.capture.pending.push(cap);
    render(false);
    announce('Capture ' + cap.n + ' needs a decision: ' + reason + '. Nothing was changed.');
  }

  function resolvePending() {
    var cap = S.capture.pending[0];
    if (!cap) return;
    if (cap.reason === 'Context unknown') unknownContext(cap); else mismatch(cap);
  }

  function raceIntent(cap) {
    var c = S.capture;
    // The tablet's change reached the hub first; the desktop's change was based on the older revision.
    var base = c.rev;
    c.rev = base + 1; c.armed = 'ammo'; c.origin = 'tablet';
    c.history.push({ n: cap.n, time: cap.time, intent: 'ammo', outcome: 'Bound to Ammo (rev ' + c.rev + ', set on tablet), the intent in force when the file appeared' });
    render(false);
    openDialog({
      title: 'Your capture change was not applied',
      describedBy: 'race-desc',
      body: '<p id="race-desc">The tablet changed capture to Ammo (rev ' + c.rev + ') before your change to Full stash arrived. Your change was based on rev ' + base + ', so nothing was overwritten.</p>' +
        '<p>Capture ' + cap.n + ' at ' + esc(cap.time) + ' is analysed as Ammo, because that was armed when the file appeared.</p>',
      actions: [{ label: 'Keep Ammo', value: 'keep' }, { label: 'Arm Full stash now', value: 'apply', primary: true }],
      onClose: function (value) {
        if (value === 'apply') { c.rev += 1; c.armed = 'stash'; c.origin = 'desktop'; render(false); announce('Armed: Full stash (rev ' + c.rev + '). Applies to the next screenshot.'); }
        else { announce('Kept Ammo from the tablet.'); }
        return null;
      }
    });
    announce('Capture change conflict. Your change was not applied.', true);
  }

  // ---------------------------------------------------------------- tablet flows

  function tabletNav(label) {
    var t = S.tablet;
    if (t.mode === 'follow') { announce('Follow desktop is on. Change mode to navigate from the tablet.'); return; }
    if (t.mode === 'independent') { t.tabletView = label + ' · Customs'; render(false); announce('Tablet only: ' + label + '. Desktop unchanged.'); return; }
    if (t.raceArmed) {
      t.raceArmed = false;
      var base = t.desktopRev - 1;
      openDialog({
        title: 'Desktop changed first',
        describedBy: 'trace-desc',
        body: '<p id="trace-desc">Someone at the desktop switched to ' + esc(t.desktopView) + ' (rev ' + t.desktopRev + '). Your change to ' + esc(label) + ' was based on rev ' + base + ' and was not applied.</p>',
        actions: [{ label: 'Keep desktop view', value: 'keep' }, { label: 'Show ' + label + ' on desktop', value: 'apply', primary: true }],
        onClose: function (value) {
          if (value === 'apply') applyControl(label);
          else announce('Kept desktop view: ' + t.desktopView + '.');
          return document.querySelector('[data-action="tablet-nav"][data-label="' + label + '"]');
        }
      });
      announce('Change not applied: desktop changed first.', true);
      return;
    }
    applyControl(label);
  }

  function applyControl(label) {
    var t = S.tablet;
    t.desktopRev += 1;
    t.desktopView = label + ' · Customs';
    t.tabletView = t.desktopView;
    t.lastAck = 'Rev ' + t.desktopRev + ' shown on desktop';
    render(false);
    announce('Desktop now shows ' + label + '. Confirmed at rev ' + t.desktopRev + '.');
  }

  function addMark(kind, opener) {
    var member = true;
    openDialog({
      title: 'Add ' + kind.toLowerCase(),
      returnFocus: opener,
      body: '<p><label for="mark-place">Place</label><br><select id="mark-place" name="place">' + C.team.places.map(function (p) { return '<option>' + esc(p) + '</option>'; }).join('') + '</select></p>' +
        (kind === 'Note' ? '<p><label for="mark-text">Note</label><br><input type="text" id="mark-text" name="text" value="Check the stairs"></p>' : '') +
        '<fieldset><legend>Share with</legend><ul class="radio-list"><li><label><input type="radio" name="scope" value="Only me" checked> Only me</label></li>' +
        '<li><label><input type="radio" name="scope" value="My paired devices"> My paired devices</label></li>' +
        '<li><label><input type="radio" name="scope" value="Team"' + (member ? '' : ' disabled') + '> Team</label></li></ul></fieldset>' +
        (kind === 'Ping' ? '<p class="note">Pings expire after 45 seconds and are never saved.</p>' : ''),
      actions: [{ label: 'Cancel', value: 'cancel' }, { label: 'Add', value: 'add', primary: true }],
      onClose: function (value, data) {
        if (value !== 'add') return null;
        var label = kind === 'Note' ? (data.get('text') || 'Note') + ' at ' + data.get('place') : kind === 'Ping' ? 'Look here: ' + data.get('place') : data.get('place');
        S.marks.push({ kind: kind, label: label, author: 'You · tablet', scope: data.get('scope'), age: 'just now', ttl: kind === 'Ping' ? 'Expires in 45 s' : 'Until cleared' });
        render(false);
        announce(kind + ' added at ' + data.get('place') + ', shared with ' + data.get('scope') + '. Shown on desktop.');
        return document.querySelector('[data-action="add-mark"][data-kind="' + kind + '"]');
      }
    });
  }

  // ---------------------------------------------------------------- moderator

  var JOURNEYS = [
    ['J1 First launch', function () { return V.first; }],
    ['J2 Loot decision', function () { return 'raid'; }],
    ['J3 Stash scan', function () { return V.stashBase + '/stash'; }],
    ['J4 Next-raid plan', function () { return V.planId; }],
    ['J5 Paired tablet', function () { return 'tablet'; }],
    ['J6 Debrief and correction', function () { return V.historyId; }]
  ];

  function renderModerator(route) {
    var key = route.parts[1] === 'stash' ? 'stash' : STATE_KEY[route.ws] || 'setup';
    var box = $('#moderator-content');
    var focusedId = document.activeElement && box.contains(document.activeElement) ? document.activeElement.id : null;
    box.innerHTML = '<div class="grid">' +
      '<section aria-labelledby="mod-j"><h2 id="mod-j">Journey start points</h2><ul>' + JOURNEYS.map(function (j) {
        return '<li><a href="' + href(j[1]()) + '" data-action="journey" data-journey="' + j[0].split(' ')[0] + '">' + esc(j[0]) + '</a></li>';
      }).join('') + '</ul><button type="button" id="mod-reset" data-action="reset">Reset session</button></section>' +
      '<section aria-labelledby="mod-c"><h2 id="mod-c">Simulate a screenshot</h2><p><label for="mod-shot">Outcome</label><br><select id="mod-shot">' +
      [['match', 'Matches what is armed'], ['mismatch', 'Detected context disagrees'], ['unknown', 'Context unknown'], ['writing', 'File still being written'],
        ['duplicate', 'Duplicate of the last file'], ['race', 'Tablet changes intent at the same time']].map(function (o) { return '<option value="' + o[0] + '">' + esc(o[1]) + '</option>'; }).join('') +
      '</select></p><button type="button" id="mod-shoot" data-action="shoot">Screenshot arrives</button></section>' +
      '<section aria-labelledby="mod-s"><h2 id="mod-s">Workspace state</h2>' + (C.states[key] ? '<p><label for="mod-state">State for ' + esc(key) + '</label><br><select id="mod-state" data-key="' + key + '">' +
      Object.keys(STATE_NAMES).map(function (s) { return '<option value="' + s + '"' + (S.stateBy[key] === s ? ' selected' : '') + '>' + esc(STATE_NAMES[s]) + '</option>'; }).join('') +
      '</select></p><button type="button" id="mod-apply-state" data-action="apply-state">Apply state</button>'
        : '<p>Stash scan has no injected workspace states in either variant; see state-matrix.md.</p>') +
      '<p><button type="button" id="mod-desk" data-action="desktop-race">Desktop user switches view (for J5)</button></p>' +
      '<p><label><input type="checkbox" id="mod-failsave" data-action="toggle-failsave"' + (S.failNextSave ? ' checked' : '') + '> Next correction save fails (for J6)</label></p></section>' +
      '<section aria-labelledby="mod-a"><h2 id="mod-a">Display (prefer the real OS setting)</h2>' +
      '<p><label><input type="checkbox" id="mod-hc" data-action="toggle-hc"' + (document.body.classList.contains('hc') ? ' checked' : '') + '> High contrast theme</label></p>' +
      '<p><label><input type="checkbox" id="mod-rm" data-action="toggle-rm"' + (document.body.classList.contains('rm') ? ' checked' : '') + '> Reduce motion</label></p>' +
      '<p><label for="mod-text">Text size</label><br><select id="mod-text" data-action="text-size">' + ['100', '150', '200'].map(function (v) {
        return '<option value="' + v + '"' + (document.documentElement.style.fontSize === v + '%' || (!document.documentElement.style.fontSize && v === '100') ? ' selected' : '') + '>' + v + '%</option>';
      }).join('') + '</select></p>' +
      '<p><label><input type="checkbox" id="mod-keys" data-action="toggle-keys"' + (shortcutsOn ? ' checked' : '') + '> Shortcut Alt+Shift+C opens Capture</label></p></section></div>';
    if (focusedId && document.getElementById(focusedId)) document.getElementById(focusedId).focus();
  }

  // ---------------------------------------------------------------- events

  function onClick(e) {
    var el = e.target.closest('[data-action]');
    if (!el) return;
    var a = el.getAttribute('data-action');
    if (el.getAttribute('aria-disabled') === 'true' && a !== 'tablet-nav') { e.preventDefault(); return; }
    switch (a) {
      case 'open-capture': openCaptureDialog(el.closest('[data-device]').getAttribute('data-device')); break;
      case 'decide': resolvePending(); break;
      case 'readiness':
        if (el.getAttribute('data-id') === 'profile') chooseProfile(el);
        else announce('Not part of this storyboard: ' + el.textContent.trim() + '.');
        break;
      case 'sample': announce('Sample data is already loaded in this storyboard. Every screen is labelled Sample.'); break;
      case 'stub': e.preventDefault(); announce('Not part of this storyboard: ' + (el.getAttribute('data-name') || el.textContent.trim()) + '.'); break;
      case 'map-view': S.mapView = el.getAttribute('data-view'); render(false); focusAction('map-view', '[data-view="' + S.mapView + '"]'); announce(S.mapView === 'map' ? 'Map view' : 'List view'); break;
      case 'correct': correct(el); break;
      case 'undo-correct': S.correction = null; render(false); focusAction('correct'); announce('Correction undone. Military cable is back under review.'); break;
      case 'copy-link': announce('Link copied: ' + location.href); break;
      case 'finish-stash': finishStash(el); break;
      case 'share-plan': sharePlan(el); break;
      case 'pair': pair(el); break;
      case 'tablet-nav': tabletNav(el.getAttribute('data-label')); break;
      case 'show-on-desktop': applyControl(S.tablet.tabletView.split(' · ')[0]); break;
      case 'add-mark': addMark(el.getAttribute('data-kind'), el); break;
      case 'recover': recover(el.getAttribute('data-key')); break;
      case 'reset':
        resetSession();
        if (location.hash === href(V.first)) render(true); else go(V.first);
        announce('Session reset.');
        break;
      case 'shoot': simulateScreenshot($('#mod-shot').value); moderatorDone(null, e); break;
      case 'skip': e.preventDefault(); $('#main').focus(); break;
      case 'journey': S.postRaid = el.getAttribute('data-journey') === 'J6'; break;
      case 'apply-state': {
        var sel = $('#mod-state'); S.stateBy[sel.getAttribute('data-key')] = sel.value; render(false);
        moderatorDone($('#mod-apply-state'), e);
        var cell = C.states[sel.getAttribute('data-key')][sel.value];
        announce(cell ? cell[2] : 'Back to normal.');
        break;
      }
      case 'desktop-race':
        // History, not Plan: in A the participant is about to tap Plan, so switching the desktop there
        // would make J5 step 2 a different puzzle in each variant.
        S.tablet.desktopRev += 1; S.tablet.desktopView = V.historyLabel + ' · Woods'; S.tablet.raceArmed = S.tablet.mode === 'control';
        if (S.tablet.mode !== 'control') { S.tablet.tabletView = S.tablet.mode === 'follow' ? S.tablet.desktopView : S.tablet.tabletView; S.tablet.lastAck = 'Rev ' + S.tablet.desktopRev + ' changed at desktop'; }
        render(false); moderatorDone($('#mod-desk'), e);
        announce('Desktop changed to ' + V.historyLabel + ', Woods.' + (S.tablet.mode === 'follow' ? ' Tablet follows.' : ''));
        break;
      case 'close-intel': pendingFocus = S.intelOrigin ? S.intelOrigin.focus : null; break;
      default: break;
    }
  }

  // A moderator clicking the panel must not take the participant's place. Pointer clicks return focus
  // to where the participant was; a keyboard moderator keeps focus on the control they used.
  var participantFocus = null;
  function moderatorDone(control, e) {
    if (e && e.detail > 0 && participantFocus) { restoreFocus(participantFocus); return; }
    if (control) control.focus();
  }

  function focusAction(action, extra) {
    var t = document.querySelector('[data-action="' + action + '"]' + (extra || ''));
    if (t) t.focus();
  }

  function recover(key) {
    S.stateBy[key] = 'success';
    if (key === 'debrief' && S.draft) {
      S.correction = S.draft; S.draft = null;
      render(false);
      focusAction('undo-correct');
      announce('Saved: corrected to ' + S.correction + '. Undo is available.');
      return;
    }
    render(false);
    var h1 = $('#main h1'); if (h1) h1.focus();
    announce('Recovered. Showing the normal state.');
  }

  function chooseProfile(opener) {
    openDialog({
      title: 'Choose profile and wipe',
      body: '<fieldset><legend>Profile</legend><ul class="radio-list"><li><label><input type="radio" name="profile" value="pvp" checked> Sample profile · PvP · wipe started 2026-06-17</label></li>' +
        '<li><label><input type="radio" name="profile" value="pve"> Sample profile · PvE · wipe started 2026-06-17</label></li></ul></fieldset>',
      actions: [{ label: 'Cancel', value: 'cancel' }, { label: 'Use this profile', value: 'ok', primary: true }],
      onClose: function (value) {
        if (value !== 'ok') return null;
        S.profileChosen = true; render(false);
        announce('Profile chosen. Nothing in setup needs action.');
        return document.querySelector('[data-action="readiness"][data-id="profile"]');
      },
      returnFocus: opener
    });
  }

  function correct(opener) {
    openDialog({
      title: 'Correct this match',
      returnFocus: opener,
      body: '<p>The screenshot region was read as Military cable with confidence 0.58.</p><fieldset><legend>It is</legend><ul class="radio-list">' +
        ['Military cable', 'Power cord', 'Something else', 'Not an item'].map(function (o, k) {
          // The current reading is selected, never the answer J2 and J6 score.
          return '<li><label><input type="radio" name="fix" value="' + o + '"' + (k === 0 ? ' checked' : '') + '> ' + o + '</label></li>';
        }).join('') + '</ul></fieldset><p class="note">Saved as your correction, with the time. You can undo it.</p>',
      actions: [{ label: 'Cancel', value: 'cancel' }, { label: 'Save correction', value: 'save', primary: true }],
      onClose: function (value, data) {
        if (value !== 'save') return null;
        if ((S.failNextSave || S.stateBy.debrief === 'failed') && parse(location.hash).ws === V.historyId) {
          S.failNextSave = false;
          S.stateBy.debrief = 'failed';
          S.draft = data.get('fix');
          render(false);
          announce('Correction not saved. Your choice is kept as a draft. Retry save.', true);
          return document.querySelector('[data-action="recover"]');
        }
        S.correction = data.get('fix');
        S.draft = null;
        render(false);
        announce('Saved: corrected to ' + S.correction + '. Undo is available.');
        return document.querySelector('[data-action="undo-correct"]') || document.querySelector('a[href*="military-cable"]') || $('#main h1');
      }
    });
  }

  function finishStash(opener) {
    var snap = C.stash.snapshots[S.stashStep];
    openDialog({
      title: 'Finish with partial coverage?',
      returnFocus: opener,
      describedBy: 'fs-desc',
      body: '<p id="fs-desc">' + snap.rowsCovered + ' of ' + C.stash.rowsTotal + ' rows are covered. The snapshot will be saved as partial, and items in uncovered rows stay unknown rather than counted as zero.</p>',
      actions: [{ label: 'Keep scanning', value: 'cancel' }, { label: 'Save partial snapshot', value: 'save', primary: true }],
      onClose: function (value) {
        if (value === 'save') announce('Partial snapshot saved: ' + snap.total + ' stacks, ' + snap.rowsCovered + ' of ' + C.stash.rowsTotal + ' rows.');
        return null;
      }
    });
  }

  function sharePlan(opener) {
    openDialog({
      title: 'Share Customs progression',
      returnFocus: opener,
      body: '<fieldset><legend>Share with</legend><ul class="radio-list"><li><label><input type="radio" name="scope" value="Only me" checked> Only me</label></li>' +
        '<li><label><input type="radio" name="scope" value="My paired devices"> My paired devices</label></li><li><label><input type="radio" name="scope" value="Team"> Team (Birch and Moth)</label></li></ul></fieldset>',
      actions: [{ label: 'Cancel', value: 'cancel' }, { label: 'Share', value: 'share', primary: true }],
      onClose: function (value, data) {
        if (value !== 'share') return null;
        if (S.stateBy.plan === 'denied' && data.get('scope') === 'Team') { announce(C.states.plan.denied[2], true); return null; }
        announce('Plan shared with ' + data.get('scope') + '.');
        return null;
      }
    });
  }

  function pair(opener) {
    openDialog({
      title: 'Pair a device',
      returnFocus: opener,
      body: '<p>On the tablet, open the companion address and enter the code below. You approve the device here before it can do anything.</p>' +
        '<p><strong>SAMPLE-CODE</strong> <span class="muted">(not a working code; expires in 09:42 in the real design)</span></p>' +
        '<p class="note">A paired device belongs to you. It never appears as a squad member and never sends input to the game.</p>',
      actions: [{ label: 'Close', value: 'cancel', primary: true }]
    });
  }

  function onChange(e) {
    var el = e.target;
    var a = el.getAttribute('data-action');
    if (a === 'route') { S.route = el.value; render(false); focusRadio('route', el.value); announce('Route: ' + el.parentNode.textContent.trim().split('.')[0] + '.'); }
    else if (a === 'tablet-mode') {
      S.tablet.mode = el.value;
      if (el.value === 'follow') S.tablet.tabletView = S.tablet.desktopView;
      render(false); focusRadio('tablet-mode', el.value);
      announce($('#owner-indicator').textContent);
    }
    else if (a === 'feedback') announce('Recorded: ' + el.value + '.');
    else if (a === 'toggle-hc') { document.body.classList.toggle('hc', el.checked); }
    else if (a === 'toggle-rm') { document.body.classList.toggle('rm', el.checked); }
    else if (a === 'toggle-keys') { shortcutsOn = el.checked; }
    else if (a === 'toggle-failsave') { S.failNextSave = el.checked; }
    else if (a === 'text-size') { document.documentElement.style.fontSize = el.value === '100' ? '' : el.value + '%'; }
  }

  function focusRadio(name, value) {
    var r = document.querySelector('input[name="' + name + '"][value="' + value + '"]');
    if (r) r.focus();
  }

  function onSubmit(e) {
    var form = e.target;
    if (form.id === 'search-form' || form.id === 'intel-search') {
      e.preventDefault();
      S.query = new FormData(form).get('q') || '';
      var target = variantId === 'a' ? 'intel' : 'search';
      if (location.hash === href(target)) render(true); else go(target);
      announce('Search results for ' + (S.query || 'everything') + '.');
    }
  }

  function onKey(e) {
    if (!shortcutsOn || !e.altKey || !e.shiftKey || e.ctrlKey || e.metaKey) return;
    if (e.key === 'C' || e.key === 'c') {
      if ($('#dialog').open) return;
      e.preventDefault();
      openCaptureDialog(parse(location.hash).ws === 'tablet' ? 'tablet' : 'desktop');
    }
  }

  function onLinkIntent(e) {
    var link = e.target.closest('a[href]');
    if (!link) return;
    var target = link.getAttribute('href');
    if (target.indexOf('/intel/') >= 0 && link.getAttribute('data-action') !== 'close-intel') {
      var h1 = $('#main h1');
      S.intelOrigin = { href: location.hash || href(V.first), label: h1 ? h1.textContent : 'previous page', focus: link.getAttribute('data-origin') };
    }
  }

  resetSession();
  initDialog();
  document.addEventListener('click', onLinkIntent, true);
  document.addEventListener('click', onClick);
  document.addEventListener('change', onChange);
  document.addEventListener('submit', onSubmit);
  document.addEventListener('keydown', onKey);
  document.addEventListener('focusin', function (e) {
    if (!e.target.closest('.moderator, dialog')) participantFocus = focusSignature(e.target) || participantFocus;
  });
  window.addEventListener('hashchange', function () {
    // Only "#/..." addresses are pages. Anything else (a fragment link) must not route to Setup.
    if (location.hash && !/^#\//.test(location.hash)) return;
    render(true);
  });
  if (!location.hash) history.replaceState(null, '', href(V.first));
  render(false);
})();
