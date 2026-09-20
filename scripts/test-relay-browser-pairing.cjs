// Drives the real tablet page (Tablet/index.html) in a real, headless browser against an
// in-process relay a .NET test hosts — the one thing PR #506/#514's own reports flagged as
// untested ("the tablet page's JavaScript in a real browser (syntax-checked with node only)").
// See tests/TarkovCompanion.UnitTests/RelayLink/RelayLinkRealBrowserTests.cs, which spawns this
// as a subprocess, approves the pairing request from the desktop side while this is running, and
// reads PAIRED / STILL_CONNECTED_AFTER_RELOAD off stdout.
//
// CommonJS rather than this repo's usual .mjs node scripts: Playwright is not a project
// dependency (nothing here installs a browser — see the brief), only whatever `npx playwright`
// already cached on this machine, and only `require()` honours NODE_PATH-style resolution against
// an arbitrary cache directory found at runtime.
//
// Usage: node test-relay-browser-pairing.cjs <relayOrigin> <pairingCode> <deviceName> [hostAlias]
//   relayOrigin  e.g. https://tarkov-relay-browser-test.invalid:54321 (or plain http://127.0.0.1:port)
//   pairingCode  the code DesktopRun.Panel.PairingCode is showing right now
//   deviceName   what to type as the tablet's name
//   hostAlias    when relayOrigin's host is not a loopback IP, the DNS name to remap to 127.0.0.1
//                via Chromium's --host-resolver-rules (WebAuthnDeviceKeyProofVerifier, like
//                production, refuses an IP-address origin)

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

function resolvePlaywright() {
  try {
    return require("playwright");
  } catch {
    // Not a project dependency: found instead wherever `npx playwright` last cached it.
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

async function main() {
  const [, , relayOrigin, pairingCode, deviceName, hostAlias] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-relay-browser-pairing.cjs <relayOrigin> <pairingCode> <deviceName> [hostAlias]");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const args = ["--ignore-certificate-errors"];
  if (hostAlias) {
    args.push(`--host-resolver-rules=MAP ${hostAlias} 127.0.0.1`);
  }

  const browser = await chromium.launch({ args });
  const page = await browser.newPage();
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");

    // Waiting on the desktop to press Approve, which the .NET side does once it sees this
    // request arrive — nothing here signals it directly; both sides only ever talk through the
    // relay, the same as production. Not #pairingDone's own visibility: once this page is live it
    // collapses the whole pairing card (#407, so the map is not pushed off the bottom of the
    // screen), #pairingDone included, and shows the header's Unpair button instead. #pairedName's
    // text is still set underneath and is read below regardless of the card's visibility.
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 30000 });
    const pairedName = await page.textContent("#pairedName");
    if (pairedName !== deviceName) {
      throw new Error(`expected the paired name "${deviceName}", the page showed "${pairedName}"`);
    }

    console.log(`PAIRED:${pairedName}`);

    // The one-time code and admin approval are spent now; a reload has nothing to fall back on
    // but the session (or paired key) this page is supposed to have already saved to IndexedDB.
    await page.reload({ waitUntil: "load" });
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 15000 });
    const pairedNameAfterReload = await page.textContent("#pairedName");
    if (pairedNameAfterReload !== deviceName) {
      throw new Error(`lost pairing after reload: the page showed "${pairedNameAfterReload}"`);
    }

    console.log(`STILL_CONNECTED_AFTER_RELOAD:${pairedNameAfterReload}`);
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
