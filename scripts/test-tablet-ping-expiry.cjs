// Issue 584: "pings also arent dissappearing, at least one i set from my tablet." Drives the real
// tablet page in a real headless browser against a real in-process relay and desktop: publishes a
// map surface naming a ping with a near-future `expiresUtc` (exactly the field
// TabletMapSurfaceBuilder now carries from RaidMark.State.ExpiresUtc, or that
// PingExpiryBrowserTests writes by hand here to keep this test fast and independent of the
// desktop's own 45s constant), confirms the tablet draws it, then waits past that instant with
// nothing else happening — no new publish, no gesture, nothing — and confirms the tablet has
// dropped it on its own. This is the client-side half of the fix; the store's own expiry/pruning
// is covered by JsonFileRaidMarkStoreTests, and both places read a ping's placement (tablet or
// desktop) the same way.
//
// Same shape as scripts/test-tablet-touch-gestures.cjs (#570): CommonJS, Playwright resolved the
// same way, because it is not a project dependency here.
//
// The ping's lifetime starts once the tablet is paired, not before the browser is launched: the
// script prints PAIRED, the test publishes the surface with the ping and answers on stdin with
// `EXPIRES <iso-8601>`. With the expiry fixed before launch, a loaded machine could spend the whole
// lifetime starting Chromium and pairing, and the tablet never saw the ping it was meant to drop.
//
// Usage: node test-tablet-ping-expiry.cjs <relayOrigin> <pairingCode> <deviceName>

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

let failures = 0;
function check(name, condition, detail = "") {
  if (condition) {
    console.log(`CHECK:PASS:${name}`);
  } else {
    failures += 1;
    console.log(`CHECK:FAIL:${name}:${detail}`);
  }
}

// Lines from the test on stdin, queued from the start so none is missed while the page loads.
const readline = require("node:readline");
const input = readline.createInterface({ input: process.stdin });
const pendingLines = [];
let lineWaiter = null;
input.on("line", (line) => {
  if (lineWaiter) {
    const waiter = lineWaiter;
    lineWaiter = null;
    waiter(line);
  } else {
    pendingLines.push(line);
  }
});
input.on("close", () => {
  if (lineWaiter) {
    const waiter = lineWaiter;
    lineWaiter = null;
    waiter(null);
  }
});
const nextLine = () => pendingLines.length > 0
  ? Promise.resolve(pendingLines.shift())
  : new Promise((resolve) => { lineWaiter = resolve; });

async function main() {
  const [, , relayOrigin, pairingCode, deviceName] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-tablet-ping-expiry.cjs <relayOrigin> <pairingCode> <deviceName>");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const page = await browser.newPage();
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  try {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    // Liveness bounds, not measurements: a browser starting on a machine running the rest of the
    // suite has taken longer than fifteen seconds to get this far.
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 45000 });
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });

    console.log("PAIRED");
    const expires = await nextLine();
    if (!expires || !expires.startsWith("EXPIRES ")) {
      throw new Error(`expected EXPIRES <instant> on stdin, got ${JSON.stringify(expires)}`);
    }

    const expiresAtMs = Date.parse(expires.slice("EXPIRES ".length));

    // The surface with the soon-to-expire ping was published just before that line was sent.
    // Polled rather than a fixed sleep: the read has no fixed cost.
    let before = await page.evaluate(() => window.__tabletTestState());
    const setupDeadline = Date.now() + 15000;
    while (!before.pingIds.includes("ping-expiring") && Date.now() < setupDeadline) {
      await page.waitForTimeout(100);
      before = await page.evaluate(() => window.__tabletTestState());
    }

    if (!before.pingIds.includes("ping-expiring")) {
      const liveStatus = await page.evaluate(() => document.getElementById("liveStatus")?.textContent);
      console.error(`DIAGNOSTIC: state=${JSON.stringify(before)} liveStatus=${JSON.stringify(liveStatus)}`);
    }

    check("the ping is drawn once the tablet has the surface", before.pingIds.includes("ping-expiring"), JSON.stringify(before.pingIds));

    console.log("SURFACE_READ");

    // Nothing else happens here: no new publish, no gesture, no reload. Only real time passing
    // and the tablet's own timer (index.html's scheduleNextPingExpiry) should move it — waited
    // for precisely, against the actual expiry instant, plus a margin for the timer's own delay.
    const waitMs = Math.max(200, expiresAtMs - Date.now() + 700);
    await page.waitForTimeout(waitMs);

    const after = await page.evaluate(() => window.__tabletTestState());
    check("the ping is gone on its own once its lifetime has passed", !after.pingIds.includes("ping-expiring"), JSON.stringify(after.pingIds));

    if (failures > 0) {
      console.error(`\n${failures} check(s) failed.`);
      console.error(consoleLog.join("\n"));
      await browser.close();
      process.exitCode = 1;
      return;
    }

    console.log("ALL_DONE");
  } catch (error) {
    console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
    console.error(consoleLog.join("\n"));
    await browser.close();
    process.exitCode = 1;
    return;
  }

  await browser.close();
}

main()
  .catch((error) => {
    console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
    process.exitCode = 1;
  })
  .finally(() => input.close());
