// #800: "when I change maps on the tablet the desktop changes too, but not to the correct map,
// and the zoom does not sync up." A real headless tablet in Control against a real in-process relay
// and desktop bridge (TabletControlMapSyncBrowserTests). The page switches through every map its
// picker offers and, after each switch, pans and zooms, checking that the desktop's published view
// (the surface it sends back) lands on the chosen map and matches the tablet's own camera. Every
// other map is dragged straight after choosing it, before the desktop has answered.
//
// Prints OPTIONS <json>, one STEP line per map, FINAL <mapId> <x> <y> <zoom>, CHECK lines, ALL_DONE.
// Usage: node test-tablet-control-map-sync.cjs <relayOrigin> <pairingCode> <deviceName>

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

// The desk applies the tablet's numbers as they are, so these only absorb JSON round-tripping.
const aligned = (s) => !!s.camera && !!s.surfaceView &&
  Math.abs(s.camera.x - s.surfaceView.centerX) < 0.01 &&
  Math.abs(s.camera.y - s.surfaceView.centerY) < 0.01 &&
  Math.abs(s.camera.zoom - s.surfaceView.zoom) < 0.001;
const describe = (s) => `map=${s.mapId} tablet=${s.camera ? `${s.camera.x},${s.camera.y},${s.camera.zoom}` : "none"} ` +
  `desk=${s.surfaceView ? `${s.surfaceView.centerX},${s.surfaceView.centerY},${s.surfaceView.zoom}` : "none"}`;

async function main() {
  const [, , relayOrigin, pairingCode, deviceName] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-tablet-control-map-sync.cjs <relayOrigin> <pairingCode> <deviceName>");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    hasTouch: true,
    isMobile: true,
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));
  const state = () => page.evaluate(() => window.__tabletTestState());
  const until = async (predicate, timeoutMs) => {
    const deadline = Date.now() + timeoutMs;
    let current = await state();
    while (!predicate(current) && Date.now() < deadline) {
      await sleep(50);
      current = await state();
    }

    return current;
  };
  const touch = (type, points) => cdp.send("Input.dispatchTouchEvent", {
    type,
    touchPoints: points.map((point) => ({ x: point.x, y: point.y, id: 0 })),
  });

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    await page.waitForFunction(() => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(":pairingVerify")), null, { timeout: 45000 }); // [#840] shown, not still shown: an instant approval leaves the code step up for one frame
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });
    await until((s) => s.hasSurface, 15000);

    await page.click('#deviceModes button[data-mode="Control"]');
    await page.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    const options = await page.$$eval("#mapSwitch option", (items) => items.map((item) => [item.value, item.textContent]));
    console.log(`OPTIONS ${JSON.stringify(options)}`);

    // Taking control starts from where the desk is, not from a view the desk never showed.
    let current = await until(aligned, 3000);
    check("control-starts-on-desk-view", aligned(current), describe(current));

    const box = await page.locator("#map").boundingBox();
    const drag = async (moves = 8) => {
      const start = { x: box.x + box.width * 0.4, y: box.y + box.height * 0.5 };
      await touch("touchStart", [start]);
      for (let index = 1; index <= moves; index += 1) {
        await touch("touchMove", [{ x: start.x + index * 9, y: start.y + index * 4 }]);
        await sleep(25);
      }

      await touch("touchEnd", []);
    };

    for (let index = 0; index < options.length; index += 1) {
      const [mapId] = options[index];
      const before = await state();
      if (before.mapId !== mapId) {
        await page.selectOption("#mapSwitch", mapId);
        // Every other map: the finger is already moving before the desk has answered the switch.
        if (index % 2 === 0) await drag(16);
      }

      current = await until((s) => s.mapId === mapId && aligned(s), 2500);
      check(`switch-lands:${mapId}`, current.mapId === mapId, describe(current));
      check(`switch-view-matches:${mapId}`, current.mapId === mapId && aligned(current), describe(current));
      // A new map opens on its own plan, not on a corner of the world outside it.
      const plan = await page.evaluate(() => window.__tabletTestPlan());
      const view = current.surfaceView;
      check(`switch-view-on-plan:${mapId}`, !!plan && !!view &&
        view.centerX >= plan.minimumX && view.centerX <= plan.maximumX &&
        view.centerY >= plan.minimumY && view.centerY <= plan.maximumY,
        `${describe(current)} plan=${JSON.stringify(plan)}`);

      await drag();
      await page.click("#zoomIn");
      await sleep(50);
      const moved = await state();
      current = await until((s) => aligned(s) && s.mapId === mapId &&
        s.surfaceView.zoom === moved.camera?.zoom, 1500);
      check(`pan-zoom-follows:${mapId}`, current.mapId === mapId && aligned(current), describe(current));
      console.log(`STEP ${mapId} ${describe(current)}`);
    }

    // Settled: nothing in flight. The desk's own state is compared with this line by the test.
    await sleep(500);
    current = await state();
    console.log(`FINAL ${current.mapId} ${current.camera?.x} ${current.camera?.y} ${current.camera?.zoom}`);
    console.log(failures === 0 ? "ALL_DONE" : `FAILED ${failures}`);
  } catch (error) {
    console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
    console.error(consoleLog.join("\n"));
    const pairingPage = await page.evaluate(() => ({
      error: document.getElementById("pairingError")?.textContent || null,
      shown: ["pairingIdle", "pairingWaiting", "pairingVerify", "pairingDone"]
        .filter((id) => document.getElementById(id) && document.getElementById(id).style.display !== "none"),
      cardHidden: document.getElementById("pairing")?.hidden ?? null,
      live: window.__tabletTestState?.().hasLive ?? null,
      stages: window.__tabletTestState?.().pairingStages ?? null,
    })).catch(() => null);
    console.error(`PAIRING_PAGE: ${JSON.stringify(pairingPage)}`);
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
