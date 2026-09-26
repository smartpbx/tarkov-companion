// #925: "I changed the map on my tablet to another map. It updated the desktop app to the right
// map, but the tablet stayed on the old map." A real headless tablet against a real in-process
// relay and desktop bridge (TabletMapArtworkSwitchBrowserTests). The desk posts a new map's
// surface first and its picture a moment later, as a real desktop uploading megabytes does. After
// every switch, in Control (the tablet picks), Follow and Independent (the desk picks), the picture
// the tablet DRAWS is hashed and must be the one the new surface names, within the deadline.
//
// Prints CHECK lines, one SWITCH <mode> <mapId> <ms> line per switch, WANT_DESK <mapId> when it
// needs the desk to switch by itself, and ALL_DONE.
// Usage: node test-tablet-map-artwork-switch.cjs <relayOrigin> <pairingCode> <deviceName> <mapId,...>

const path = require("node:path");
const os = require("node:os");
const fs = require("node:fs");

function resolvePlaywright() {
  try {
    return require("playwright");
  } catch {
    const npxCache = path.join(os.homedir(), ".npm", "_npx");
    const candidates = [];
    try {
      for (const entry of fs.readdirSync(npxCache)) {
        const modules = path.join(npxCache, entry, "node_modules");
        if (fs.existsSync(path.join(modules, "playwright"))) candidates.push(modules);
      }
    } catch {
      // No npx cache at all; require.resolve below reports the real reason.
    }

    return require(require.resolve("playwright", { paths: candidates }));
  }
}

let failures = 0;
function check(name, condition, detail) {
  if (condition) {
    console.log(`CHECK:PASS:${name}`);
  } else {
    failures += 1;
    console.log(`CHECK:FAIL:${name}:${detail}`);
  }
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const DEADLINE_MS = 2500;

async function main() {
  const [, , relayOrigin, pairingCode, deviceName, mapList] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName || !mapList) {
    console.error("usage: node test-tablet-map-artwork-switch.cjs <relayOrigin> <pairingCode> <deviceName> <mapId,...>");
    process.exitCode = 2;
    return;
  }

  const maps = mapList.split(",");
  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    hasTouch: true,
    isMobile: true,
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  // What the page shows: its map, the picture its surface names, and the hash of the picture it
  // is actually drawing (read back from the image itself, not from the page's own bookkeeping).
  const shown = () => page.evaluate(async () => {
    const state = window.__tabletTestState();
    const { src, named } = window.__tabletTestArtwork();
    let drawn = null;
    if (src) {
      const bytes = await (await fetch(src)).arrayBuffer();
      drawn = [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))]
        .map((value) => value.toString(16).padStart(2, "0")).join("");
    }

    return { mode: state.mode, mapId: state.mapId, drawn, named };
  });
  const describe = (s) => `mode=${s.mode} map=${s.mapId} named=${s.named?.slice(0, 12)} drawn=${s.drawn?.slice(0, 12)}`;
  const lands = async (mapId) => {
    const started = Date.now();
    let current = await shown();
    while (!(current.mapId === mapId && current.named && current.drawn === current.named) &&
      Date.now() - started < DEADLINE_MS) {
      await sleep(40);
      current = await shown();
    }

    return { current, elapsed: Date.now() - started };
  };
  const expectSwitch = async (label, mapId) => {
    const { current, elapsed } = await lands(mapId);
    check(`${label}-map:${mapId}`, current.mapId === mapId, describe(current));
    check(`${label}-draws-the-new-picture:${mapId}`, current.mapId === mapId && !!current.named && current.drawn === current.named,
      describe(current));
    console.log(`SWITCH ${label} ${mapId} ${elapsed}`);
    // And it stays that way: nothing later puts the old picture back.
    await sleep(600);
    const settled = await shown();
    check(`${label}-stays:${mapId}`, settled.mapId === mapId && settled.drawn === settled.named, describe(settled));
  };
  const setMode = async (next) => {
    await page.click(`#deviceModes button[data-mode="${next}"]`);
    await page.waitForFunction((wanted) => window.__tabletTestState().mode === wanted, next, { timeout: 20000 });
  };

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    await page.waitForFunction(() => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(":pairingVerify")), null, { timeout: 45000 });
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });
    await page.waitForFunction(() => window.__tabletTestState().hasSurface, null, { timeout: 15000 });
    await expectSwitch("start", maps[0]);

    // Control: the tablet picks the map, the desk goes there, and so must the tablet's picture.
    await setMode("Control");
    await page.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    for (const mapId of [maps[1], maps[2], maps[0]]) {
      await page.selectOption("#mapSwitch", mapId);
      await expectSwitch("Control", mapId);
    }

    // Follow and Independent: the desk picks, and the tablet shows it.
    for (const [next, mapId] of [["Follow", maps[1]], ["Independent", maps[2]]]) {
      await setMode(next);
      console.log(`WANT_DESK ${mapId}`);
      await expectSwitch(next, mapId);
    }

    console.log(failures === 0 ? "ALL_DONE" : `FAILED ${failures}`);
  } catch (error) {
    console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
    console.error(consoleLog.join("\n"));
    await browser.close();
    process.exitCode = 1;
    return;
  }

  await browser.close();
}

main().catch((error) => {
  console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
  process.exitCode = 1;
});
