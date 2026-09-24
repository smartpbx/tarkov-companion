// #604: "Control is jerky on the desktop." Measures how long a tablet gesture takes to reach the
// desktop, in a real headless tablet browser against a real in-process relay and desktop. The page
// drags the map with real touch input (CDP Input.dispatchTouchEvent) at ~30 moves a second and
// prints, for every move, the wall-clock instant and the camera it left the tablet on:
//   STEP <drag> <index> <epochMs> <x> <y> <zoom>
// TabletControlLatencyTests records when the desktop is handed each viewport and matches the two.
//
// Usage: node test-tablet-control-latency.cjs <relayOrigin> <pairingCode> <deviceName> [drags] [moves]

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
        if (fs.existsSync(path.join(modules, "playwright"))) {
          candidates.push(modules);
        }
      }
    } catch {
      // No npx cache at all; require.resolve below reports the real reason.
    }

    return require(require.resolve("playwright", { paths: candidates }));
  }
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function main() {
  const [, , relayOrigin, pairingCode, deviceName, dragsArg, movesArg] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-tablet-control-latency.cjs <relayOrigin> <pairingCode> <deviceName> [drags] [moves]");
    process.exitCode = 2;
    return;
  }

  const drags = Number(dragsArg) || 3;
  const moves = Number(movesArg) || 45;
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
    const surfaceDeadline = Date.now() + 15000;
    while (!(await state()).hasSurface && Date.now() < surfaceDeadline) await sleep(100);

    await page.click('#deviceModes button[data-mode="Control"]');
    await page.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    await sleep(2500); // the grant's own canonical update settles before anything is timed
    console.log("CONTROL_READY");

    const box = await page.locator("#map").boundingBox();
    for (let drag = 0; drag < drags; drag += 1) {
      const start = { x: box.x + box.width * 0.3, y: box.y + box.height * 0.5 };
      await touch("touchStart", [start]);
      for (let index = 1; index <= moves; index += 1) {
        await touch("touchMove", [{ x: start.x + index * 6, y: start.y + index * 2 }]);
        const at = Date.now();
        const camera = (await state()).camera;
        if (camera) console.log(`STEP ${drag} ${index} ${at} ${camera.x} ${camera.y} ${camera.zoom}`);
        await sleep(Math.max(0, 33 - (Date.now() - at)));
      }

      await touch("touchEnd", []);
      console.log(`DRAG_END ${drag} ${Date.now()}`);
      await sleep(3000); // whatever is still in flight lands before the next drag starts
    }

    console.log("ALL_DONE");
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
