/* Tarkov Companion V3 concept mockups: shared chrome, icons and a stylised Customs plan.
   The map below is drawn from scratch for these concepts (no tarkov.dev / Shebuka artwork is
   copied or bundled; see docs/LICENSING.md). Positions are approximate and only illustrate layout. */

const ICONS = {
  raid: '<path d="M3 6l6-2 6 2 6-2v14l-6 2-6-2-6 2z"/><path d="M9 4v14M15 6v14"/>',
  intel: '<path d="M6 3h9l4 4v14H6z"/><path d="M14 3v5h5M9 12h7M9 16h7"/>',
  plan: '<rect x="5" y="4" width="14" height="17" rx="2"/><path d="M9 4V2.8h6V4M9 10h6M9 14h6M9 18h4"/>',
  team: '<circle cx="9" cy="8" r="3.2"/><path d="M3 20c0-3.3 2.7-6 6-6s6 2.7 6 6"/><circle cx="17" cy="9" r="2.4"/><path d="M16 14.2c2.9.2 5 2.6 5 5.8"/>',
  debrief: '<path d="M5 20V10M11 20V4M17 20v-7M3 20h18"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M12 2.5v3M12 18.5v3M21.5 12h-3M5.5 12h-3M18.7 5.3l-2.1 2.1M7.4 16.6l-2.1 2.1M18.7 18.7l-2.1-2.1M7.4 7.4L5.3 5.3"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l5 5"/>',
  camera: '<path d="M4 8h3.5L9 5.5h6L16.5 8H20v11H4z"/><circle cx="12" cy="13.2" r="3.3"/>',
  chev: '<path d="M6 9l6 6 6-6"/>',
  chevr: '<path d="M9 6l6 6-6 6"/>',
  min: '<path d="M6 12h12"/>', max: '<rect x="6" y="6" width="12" height="12" rx="1"/>', close: '<path d="M6 6l12 12M18 6L6 18"/>',
  log: '<path d="M5 4h14v16H5z"/><path d="M8 8h8M8 12h8M8 16h5"/>',
  speaker: '<path d="M4 9.5h4l5-4v13l-5-4H4z"/><path d="M16.5 9a4 4 0 010 6"/>',
  wave: '<path d="M4 9.5h4l5-4v13l-5-4H4z"/><path d="M16.5 9a4 4 0 010 6M19 6.5a7.5 7.5 0 010 11"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  cross: '<path d="M6.5 6.5l11 11M17.5 6.5l-11 11"/>',
  layers: '<path d="M12 4l9 5-9 5-9-5z"/><path d="M3 14l9 5 9-5"/>',
  follow: '<circle cx="12" cy="12" r="3"/><path d="M12 2.5v4M12 17.5v4M2.5 12h4M17.5 12h4"/>',
  plus: '<path d="M12 5v14M5 12h14"/>', minus: '<path d="M5 12h14"/>',
  fit: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
  floor: '<path d="M4 17l8 4 8-4M4 12.5l8 4 8-4M12 4l8 4-8 4-8-4z"/>',
  key: '<circle cx="8" cy="12" r="4"/><path d="M12 12h9M18 12v3M21 12v2"/>',
  marker: '<path d="M8 3h8v5l-2 2v11h-4V10L8 8z"/>',
  ping: '<circle cx="12" cy="12" r="2.5"/><path d="M7.5 7.5a6.4 6.4 0 000 9M16.5 7.5a6.4 6.4 0 010 9M4.6 4.6a10.5 10.5 0 000 14.8M19.4 4.6a10.5 10.5 0 010 14.8"/>',
  arrow: '<path d="M12 20V5M6 11l6-6 6 6"/>',
  link: '<path d="M10 14a4 4 0 005.7 0l3-3a4 4 0 00-5.7-5.7l-1 1M14 10a4 4 0 00-5.7 0l-3 3a4 4 0 005.7 5.7l1-1"/>',
  relay: '<circle cx="12" cy="12" r="2"/><path d="M8 8a5.7 5.7 0 000 8M16 8a5.7 5.7 0 010 8"/><path d="M12 14v7"/>',
  lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V8a4 4 0 018 0v3"/>',
  shield: '<path d="M12 3l8 3v6c0 4.5-3.4 8-8 9-4.6-1-8-4.5-8-9V6z"/>',
  clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
  route: '<circle cx="6" cy="18" r="2.2"/><circle cx="18" cy="6" r="2.2"/><path d="M8 18h6a3 3 0 000-6h-4a3 3 0 010-6h6"/>',
  play: '<path d="M8 5l11 7-11 7z"/>',
  flag: '<path d="M5 21V4h11l-2 4 2 4H5"/>',
  info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5M12 8h.01"/>',
  tablet: '<rect x="5" y="3" width="14" height="18" rx="2"/><path d="M11 18h2"/>',
  bag: '<path d="M6 8h12l1 13H5z"/><path d="M9 8V6a3 3 0 016 0v2"/>',
  scan: '<path d="M4 8V4h4M20 8V4h-4M4 16v4h4M20 16v4h-4"/><path d="M7 12h10"/>',
  eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/>',
  trader: '<circle cx="12" cy="8" r="3.5"/><path d="M5 20c.6-3.8 3.4-6 7-6s6.4 2.2 7 6"/>',
  tasks: '<path d="M9 6h11M9 12h11M9 18h11"/><path d="M4 6l1 1 2-2M4 12l1 1 2-2M4 18h2"/>',
  grid: '<rect x="4" y="4" width="16" height="16" rx="1.5"/><path d="M4 9.3h16M4 14.6h16M9.3 4v16M14.6 4v16"/>',
  question: '<circle cx="12" cy="12" r="8.5"/><path d="M9.6 9.5a2.5 2.5 0 114 2c-.9.6-1.6 1.2-1.6 2.3M12 17h.01"/>',
  undo: '<path d="M9 14L4 9l5-5"/><path d="M4 9h10a6 6 0 010 12h-3"/>',
  wifi: '<path d="M2.5 9a14 14 0 0119 0M5.5 12.5a9.5 9.5 0 0113 0M8.7 15.8a5 5 0 016.6 0M12 19h.01"/>',
  car: '<path d="M4 16v-4l2-5h12l2 5v4z"/><circle cx="7.5" cy="16.5" r="1.8"/><circle cx="16.5" cy="16.5" r="1.8"/>'
};
function icon(name, cls = '', style = '') {
  return `<svg class="i ${cls}" viewBox="0 0 24 24" style="${style}">${ICONS[name] || ''}</svg>`;
}

function chrome(o) {
  const nav = [['raid', 'Raid'], ['intel', 'Intel'], ['plan', 'Plan'], ['team', 'Team'], ['debrief', 'Debrief']];
  const top = `
  <div class="topbar">
    <span class="brand">Tarkov Companion</span><span class="ver">v3</span>
    <div class="seg picker">${o.map || 'Customs'} ${icon('chev', 's16', 'color:var(--text2)')}</div>
    <div class="seg sec">PMC · PvP · Permanent</div>
    <div class="seg state">${o.state}</div>
    ${o.chip ? `<div class="seg">${o.chip}</div>` : ''}
    <div class="spacer"></div>
    <span class="iconbtn">${icon('search')}</span>
    <span class="iconbtn">${icon('camera')}</span>
    <span class="pill-ready"><span class="dot" style="background:var(--success)"></span>Ready</span>
    <span class="winctl"><span>${icon('min', 's16')}</span><span>${icon('max', 's16')}</span><span>${icon('close', 's16')}</span></span>
  </div>`;
  const rail = `<nav class="rail">${nav.map(([k, t]) =>
    `<a class="${o.nav === k ? 'on' : ''}">${icon(k, 's24')}<span>${t}</span></a>`).join('')}
    <div class="grow"></div><div class="foot"><a>${icon('gear', 's24')}<span>Settings</span></a></div></nav>`;
  return top + rail;
}

/* ---------- stylised Customs plan (map units 1600 x 1000) ---------- */
const P = {
  crossroads: [104, 380], zb1011: [212, 262], trailer: [168, 842], ogs: [468, 772], zb1012: [648, 836],
  wh4: [520, 440], fortress: [705, 470], construction: [790, 300], crackhouse: [1012, 250],
  dorms3: [1004, 560], dorms2: [1086, 668], vex: [1168, 600], ngs: [1072, 842], boat: [1224, 150],
  bigred: [1340, 300], ruaf: [1496, 560], stronghold: [724, 668]
};

function mapDefs() {
  return `<defs>
    <pattern id="hatch" width="14" height="14" patternUnits="userSpaceOnUse" patternTransform="rotate(40)">
      <rect width="14" height="14" fill="rgba(184,164,245,.10)"/><line x1="0" y1="0" x2="0" y2="14" stroke="rgba(184,164,245,.55)" stroke-width="3"/></pattern>
    <pattern id="trees" width="18" height="18" patternUnits="userSpaceOnUse">
      <rect width="18" height="18" fill="#132a2a"/><circle cx="5" cy="6" r="4" fill="#173331"/><circle cx="13" cy="13" r="4.5" fill="#15302e"/></pattern>
    <pattern id="field" width="10" height="10" patternUnits="userSpaceOnUse" patternTransform="rotate(-8)">
      <rect width="10" height="10" fill="#15222c"/><line x1="0" y1="0" x2="10" y2="0" stroke="#192834" stroke-width="2"/></pattern>
    <radialGradient id="vig" cx="50%" cy="50%" r="75%"><stop offset="60%" stop-color="#000" stop-opacity="0"/><stop offset="100%" stop-color="#000" stop-opacity=".45"/></radialGradient>
    <filter id="glow" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="4"/></filter>
    <linearGradient id="edgeW" x1="0" x2="1"><stop offset="0" stop-color="#E69F00" stop-opacity=".75"/><stop offset="1" stop-color="#E69F00" stop-opacity="0"/></linearGradient>
  </defs>`;
}

function bld(x, y, w, h, extra = '') {
  return `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="2" fill="#22333f" stroke="#3e5566" stroke-width="1.4" ${extra}/>`;
}

function baseMap() {
  let s = `<rect x="-600" y="-500" width="2800" height="2000" fill="#111e27"/>`;
  // concrete lots (industrial zones)
  const lot = d => `<path d="${d}" fill="#16252f" stroke="#1b2c38" stroke-width="2"/>`;
  s += lot('M150 200 L300 196 L310 300 L160 312 Z') + lot('M70 300 L240 310 L250 460 L80 450 Z');
  s += lot('M360 380 L840 372 L850 520 L370 516 Z') + lot('M620 230 L880 236 L870 350 L630 356 Z');
  s += lot('M920 520 L1180 516 L1190 700 L940 710 Z') + lot('M1000 790 L1200 796 L1196 880 L1004 884 Z');
  s += lot('M1280 230 L1440 226 L1450 360 L1290 366 Z') + lot('M400 720 L580 716 L590 810 L404 812 Z');
  s += lot('M90 780 L300 776 L306 880 L96 884 Z') + lot('M960 200 L1110 190 L1116 290 L966 294 Z');
  // fields
  s += `<path d="M300 560 L520 580 L560 700 L330 690 Z" fill="url(#field)"/>`;
  s += `<path d="M760 700 L900 690 L905 790 L780 810 Z" fill="url(#field)"/>`;
  s += `<path d="M1380 640 L1590 620 L1600 820 L1420 840 Z" fill="url(#field)"/>`;
  // forests
  s += `<path d="M-600 -500 H1160 L1150 20 L1120 70 C900 120 700 70 520 120 C380 160 200 130 -20 160 L-600 160 Z" fill="url(#trees)"/>`;
  s += `<path d="M-600 940 L-20 930 C200 900 380 950 560 915 C760 880 900 940 1100 910 C1250 890 1400 930 1700 900 L2200 900 V1500 H-600 Z" fill="url(#trees)"/>`;
  s += `<path d="M1420 -500 H2200 V420 C1560 400 1470 330 1440 240 C1420 160 1440 80 1420 0 Z" fill="url(#trees)"/>`;
  s += `<path d="M-600 160 L-20 160 C10 300 -10 320 20 470 C40 520 60 600 40 680 L-20 940 L-600 940 Z" fill="url(#trees)"/>`;
  s += `<path d="M1640 420 L2200 420 V900 L1700 900 C1650 800 1680 600 1640 420 Z" fill="url(#trees)"/>`;
  // river
  const rv = 'M1150 -500 C1160 -200 1175 -60 1180 0 C1200 160 1250 290 1262 420 C1275 560 1300 720 1352 1000 C1370 1100 1380 1300 1390 1500';
  s += `<path d="${rv}" fill="none" stroke="#1b3b55" stroke-width="84"/><path d="${rv}" fill="none" stroke="#21476a" stroke-width="60"/>`;
  // roads
  const road = (d, w = 16) => `<path d="${d}" fill="none" stroke="#1f2d38" stroke-width="${w + 6}" stroke-linecap="round"/><path d="${d}" fill="none" stroke="#2f4250" stroke-width="${w}" stroke-linecap="round"/>`;
  s += road('M-600 516 L0 520 C200 505 380 520 600 522 C820 524 1000 512 1180 522 C1300 530 1420 540 1600 548 L2200 552', 18);
  s += road('M-600 738 L0 736 C220 740 420 790 640 776 C860 762 1000 790 1200 792 C1380 794 1500 770 1600 772 L2200 770', 14);
  s += road('M560 150 C560 300 572 420 566 520 C560 640 600 740 640 900', 12);
  s += road('M905 140 C900 300 918 420 920 520 C922 640 930 760 960 920', 12);
  s += road('M212 262 C260 360 250 440 260 520', 10);
  s += road('M1040 520 C1060 400 1030 300 1012 250', 10);
  s += road('M1300 530 C1330 450 1340 380 1340 300', 10);
  // bridge
  s += `<rect x="1232" y="506" width="74" height="42" fill="#3a4e5c" stroke="#50677a" stroke-width="1.5" transform="rotate(4 1269 527)"/>`;
  // railway
  s += `<path d="M-600 612 L0 612 C300 604 600 616 900 606 C1100 600 1250 624 1600 640 L2200 650" fill="none" stroke="#3d4f5c" stroke-width="7"/><path d="M-600 612 L0 612 C300 604 600 616 900 606 C1100 600 1250 624 1600 640 L2200 650" fill="none" stroke="#111e27" stroke-width="3" stroke-dasharray="3 9"/>`;
  // buildings: west (ZB, crossroads, trailer)
  s += bld(180, 228, 64, 40) + bld(96, 330, 34, 60) + bld(140, 402, 70, 30) + bld(120, 800, 90, 22) + bld(130, 832, 90, 22) + bld(214, 810, 40, 58);
  // warehouses and fortress
  s += bld(470, 400, 104, 64) + bld(380, 400, 70, 48) + bld(620, 410, 40, 90) + bld(672, 430, 70, 74, 'fill="#2a4155"') + bld(760, 440, 46, 48);
  s += bld(740, 260, 110, 56) + bld(760, 330, 60, 26) + bld(640, 250, 70, 40);
  s += bld(426, 745, 80, 44) + bld(520, 760, 30, 30) + bld(690, 640, 72, 50) + bld(612, 820, 40, 30);
  // dorms area
  s += bld(950, 540, 116, 38, 'fill="#2b465b"') + bld(1050, 650, 86, 34) + bld(1116, 580, 40, 26) + bld(990, 610, 26, 34);
  s += bld(1030, 820, 90, 40) + bld(1140, 830, 36, 30);
  // north east
  s += bld(980, 228, 60, 40) + bld(1060, 200, 30, 30) + bld(1300, 250, 120, 36, 'fill="#3b2f33" stroke="#6a4a4f"') + bld(1310, 320, 90, 30) + bld(1460, 520, 60, 24);
  s += bld(1470, 420, 40, 60) + bld(1180, 140, 30, 18, 'fill="#3b4c56"');
    return s;
}

function mlabel(x, y, text, o = {}) {
  const size = (o.size || 15) * (window.LS || 1), w = text.length * size * 0.56 + 16;
  const anchor = o.anchor || 'middle';
  const bx = anchor === 'middle' ? x - w / 2 : anchor === 'start' ? x : x - w;
  return `<g opacity="${o.op || 1}"><rect x="${bx}" y="${y - size * 0.9}" width="${w}" height="${size * 1.55}" rx="4" fill="rgba(13,20,27,.78)"/>
    <text x="${anchor === 'middle' ? x : anchor === 'start' ? x + 8 : x - 8}" y="${y + size * 0.28}" text-anchor="${anchor}" font-size="${size}" font-weight="${o.weight || 500}" fill="${o.color || '#C8D5DC'}" font-family="Inter">${text}</text></g>`;
}

function extractIcon(x, y, name, o = {}) {
  const off = o.offered, c = off ? '#71D19A' : '#8FA7B4';
  let s = '';
  if (off) s += `<circle cx="${x}" cy="${y}" r="26" fill="rgba(113,209,154,.13)" stroke="#71D19A" stroke-width="2.5"/>`;
  s += `<rect x="${x - 15}" y="${y - 15}" width="30" height="30" rx="7" fill="#0f2a22" stroke="${c}" stroke-width="2"/>`;
  if (o.car) s += `<g transform="translate(${x - 10} ${y - 10}) scale(.84)" stroke="${c}" fill="none" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">${ICONS.car}</g>`;
  else s += `<g transform="translate(${x - 10} ${y - 10}) scale(.84)" stroke="${c}" fill="none" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M13 4H6v16h7"/><path d="M11 12h10M17 8l4 4-4 4"/></g>`;
  if (name) {
    const side = o.side || 'right';
    const lx = side === 'right' ? x + 24 : side === 'left' ? x - 24 : x;
    const ly = side === 'below' ? y + 40 : side === 'above' ? y - 32 : y;
    s += mlabel(lx, ly, name, { anchor: side === 'right' ? 'start' : side === 'left' ? 'end' : 'middle', color: off ? '#BFF0D2' : '#C8D5DC', weight: 600, size: o.size || 15 });
  }
  return s;
}

function pin(x, y, letter, o = {}) {
  const c = o.color || '#F1C75B';
  return `<g opacity="${o.op || 1}"><path d="M${x} ${y} c-4 -10 -16 -16 -16 -30 a16 16 0 1 1 32 0 c0 14 -12 20 -16 30z" fill="${c}" stroke="#101820" stroke-width="2"/>
    <text x="${x}" y="${y - 24}" text-anchor="middle" font-size="17" font-weight="800" fill="#1d1606" font-family="Inter">${letter}</text></g>`;
}

function cone(x, y, deg, color, len = 70, spread = 34, op = .28) {
  const a1 = (deg - spread / 2 - 90) * Math.PI / 180, a2 = (deg + spread / 2 - 90) * Math.PI / 180;
  return `<path d="M${x} ${y} L${x + len * Math.cos(a1)} ${y + len * Math.sin(a1)} A${len} ${len} 0 0 1 ${x + len * Math.cos(a2)} ${y + len * Math.sin(a2)} Z" fill="${color}" opacity="${op}"/>`;
}

function member(x, y, color, name, age, o = {}) {
  let s = '';
  if (o.heading !== undefined) s += cone(x, y, o.heading, color, 58, 40, .30);
  s += `<circle cx="${x}" cy="${y}" r="11" fill="${color}" stroke="#F4F8FA" stroke-width="2.5"/>`;
  s += `<text x="${x}" y="${y + 4.5}" text-anchor="middle" font-size="12" font-weight="800" fill="#0b1218" font-family="Inter">${name[0]}</text>`;
  if (name) s += mlabel(x + (o.lx ?? 0), y + (o.ly ?? 34), `${name}${age ? ' · ' + age : ''}`, { color: '#F4F8FA', weight: 600, size: 14 });
  return s;
}

function you(x, y, deg, o = {}) {
  let s = cone(x, y, deg, '#63D4DD', 96, 50, .26);
  s += `<circle cx="${x}" cy="${y}" r="20" fill="#63D4DD" opacity=".18"/>`;
  s += `<g transform="translate(${x} ${y}) rotate(${deg})"><path d="M0 -15 L11 11 L0 5 L-11 11 Z" fill="#63D4DD" stroke="#F4F8FA" stroke-width="2.2" stroke-linejoin="round"/></g>`;
  if (o.label !== false) s += mlabel(x + (o.lx ?? -18), y + (o.ly ?? 42), o.label || 'You · 12 s ago', { color: '#F4F8FA', weight: 600, size: 14, anchor: o.anchor || 'end' });
  return s;
}

function landmarks(skip = [], o = {}) {
  const L = [
    ['wh4', 'Warehouse 4', 0, -52], ['fortress', 'Fortress', 0, 58], ['construction', 'Construction', 0, -44],
    ['crackhouse', 'Crackhouse', 0, -40], ['dorms3', 'Dorms 3-story', -120, -30], ['dorms2', 'Dorms 2-story', 0, 42],
    ['bigred', 'Big Red', 0, -50], ['ngs', 'New Gas Station', 0, 44], ['stronghold', 'Stronghold', 0, 50],
    ['ogs', 'Old Gas Station', -10, -46]
  ];
  return L.filter(l => !skip.includes(l[0])).map(([k, t, dx, dy]) => mlabel(P[k][0] + dx, P[k][1] + dy, t, { size: o.size || 14, color: '#AFC0CA' })).join('');
}

function extracts(offered = [], o = {}) {
  const E = [
    ['zb1011', 'ZB-1011', 'right'], ['crossroads', 'Crossroads', 'below'], ['trailer', 'Trailer Park', 'right'],
    ['ogs', 'Old Gas Station', null], ['zb1012', 'ZB-1012', 'right'], ['vex', 'Dorms V-Ex', 'right', true],
    ['boat', "Smugglers' Boat", 'right'], ['ruaf', 'RUAF Roadblock', 'above']
  ];
  return E.filter(e => !(o.skip || []).includes(e[0])).map(([k, t, side, car]) => {
    const [x, y] = P[k];
    const ex = k === 'ogs' ? x + 76 : x, ey = k === 'ogs' ? y + 14 : y;
    return extractIcon(ex, ey, side ? t + (car && o.vexPrice ? ' · ₽7,000' : '') : '', { offered: offered.includes(k), side, car, size: o.size });
  }).join('');
}

function path(pts, stroke, o = {}) {
  const d = 'M' + pts.map(p => p.join(' ')).join(' L');
  let s = '';
  if (o.halo !== false) s += `<path d="${d}" fill="none" stroke="#101820" stroke-width="${(o.w || 5) + 5}" stroke-linejoin="round" stroke-linecap="round" opacity="${o.op ?? 1}"/>`;
  s += `<path d="${d}" fill="none" stroke="${stroke}" stroke-width="${o.w || 5}" stroke-linejoin="round" stroke-linecap="round" ${o.dash ? `stroke-dasharray="${o.dash}"` : ''} opacity="${o.op ?? 1}"/>`;
  return s;
}

function compass(x, y) {
  return `<g transform="translate(${x} ${y})"><circle r="20" fill="rgba(13,20,27,.8)" stroke="rgba(130,151,164,.4)"/><path d="M0 -13 L5 2 L0 -1 L-5 2Z" fill="#F4F8FA"/><text y="14" text-anchor="middle" font-size="10" font-weight="700" fill="#C8D5DC" font-family="Inter">N</text></g>`;
}

function mapSvg(viewBox, layers, o = {}) {
  const base = o.dim ? `<rect x="-600" y="-500" width="2800" height="2000" fill="#0c141b"/><g opacity="${o.dim}">${baseMap()}</g>` : baseMap();
  return `<svg class="map" viewBox="${viewBox}" preserveAspectRatio="xMidYMid meet" xmlns="http://www.w3.org/2000/svg">${mapDefs()}${base}${layers}</svg>`;
}

function mount(id, html) { document.getElementById(id).insertAdjacentHTML('afterbegin', html); }
