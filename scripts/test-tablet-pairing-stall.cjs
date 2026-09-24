// [#840] The tablet page must not stall on a relay that answers a pairing poll badly once.
//
// The ceremony's approval poll used to stop at the first non-404 error and wait on any request
// that never settled, so the page sat on "Waiting for the desktop…" (or, for a returning tablet,
// "Reconnecting to the desktop…" with the code form hidden) until somebody reloaded it. This
// drives the real page against a real relay and spoils the tablet's first two approval polls on
// the way: the first comes back 503 and the second is never answered at all. The page has to ask
// again, show the verification code and finish pairing without a reload.
//
// See tests/TarkovCompanion.UnitTests/RelayLink/RelayLinkRealBrowserTests.cs, which runs this,
// approves the request from the desktop side and reads PAIRED / RECOVERED off stdout.
//
// Usage: node test-tablet-pairing-stall.cjs <relayOrigin> <pairingCode> <deviceName>

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

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

async function main() {
  const [, , relayOrigin, pairingCode, deviceName] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-tablet-pairing-stall.cjs <relayOrigin> <pairingCode> <deviceName>");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const page = await browser.newPage();
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  const revealPolls = [];
  await page.route("**/v2/companion/pairing/reveals/**", async (route) => {
    revealPolls.push(Date.now());
    if (revealPolls.length === 1) {
      await route.fulfill({ status: 503, body: "relay restarting" });
    } else if (revealPolls.length === 2) {
      // Never answered: a request a phone's network dropped.
    } else {
      await route.continue();
    }
  });

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");

    await page.waitForFunction(
      () => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(":pairingVerify")),
      null,
      { timeout: 45000 },
    );
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });
    if (revealPolls.length < 3) {
      throw new Error(`paired after ${revealPolls.length} reveal poll(s); the spoiled polls were never reached`);
    }

    console.log(`RECOVERED:${revealPolls.length} reveal polls`);
    console.log(`PAIRED:${await page.textContent("#pairedName")}`);
  } catch (error) {
    console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
    console.error(consoleLog.join("\n"));
    const pairingPage = await page.evaluate(() => ({
      error: document.getElementById("pairingError")?.textContent || null,
      stages: window.__tabletTestState?.().pairingStages ?? null,
    })).catch(() => null);
    console.error(`PAIRING_PAGE: ${JSON.stringify(pairingPage)} revealPolls=${revealPolls.length}`);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
  process.exitCode = 1;
});
