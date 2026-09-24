// #553: two desktops on one relay, each with its own tablet, in a real headless Chromium.
//
// Two browser contexts, so two separate IndexedDB stores and therefore two tablet identities,
// both loading the real tablet page from the same relay. Tablet A pairs by opening the QR link
// (the page URL with the payload in the fragment, #530); tablet B pairs by typing the code.
// Neither is told which desktop it belongs to: the one-time code resolves, on the relay, to the
// offer of the desktop that made it.
//
// Spawned by tests/TarkovCompanion.UnitTests/RelayLink/TwoDesktopBrowserTests.cs, which holds the
// relay and both desktop coordinators in-process and is the only side that can see a desktop's
// canonical state. Same shape as test-tablet-touch-gestures.cjs: a marker on stdout hands over to
// the .NET side, and one line on stdin ("NEXT") hands back.
//
// Usage: node test-two-desktop-tablets.cjs <relayOrigin> <qrFragmentA> <codeB>

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

const lines = [];
let waiter = null;
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  for (const line of chunk.split("\n")) {
    if (!line.trim()) continue;
    if (waiter) {
      const resolve = waiter;
      waiter = null;
      resolve();
    } else {
      lines.push(line);
    }
  }
});

/// Says where the script is and blocks until the .NET side answers with one line.
function handOver(marker) {
  console.log(marker);
  if (lines.length > 0) {
    lines.shift();
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    waiter = resolve;
  });
}

async function until(read, accept, timeoutMs = 25000) {
  const deadline = Date.now() + timeoutMs;
  let last = await read();
  while (!accept(last) && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 200));
    last = await read();
  }

  return last;
}

async function main() {
  const [, , relayOrigin, qrFragmentA, codeB] = process.argv;
  if (!relayOrigin || !qrFragmentA || !codeB) {
    console.error("usage: node test-two-desktop-tablets.cjs <relayOrigin> <qrFragmentA> <codeB>");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const consoleLog = [];
  async function openTablet(name) {
    const context = await browser.newContext({
      viewport: { width: 1280, height: 800 },
      hasTouch: true,
      isMobile: true,
      ignoreHTTPSErrors: true,
    });
    const page = await context.newPage();
    page.on("console", (message) => consoleLog.push(`[${name}:${message.type()}] ${message.text()}`));
    page.on("pageerror", (error) => consoleLog.push(`[${name}:pageerror] ${error}`));
    return page;
  }

  try {
    const tabletA = await openTablet("A");
    const tabletB = await openTablet("B");
    const stateOf = (page) => page.evaluate(() => window.__tabletTestState());
    const paired = (page) => page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });

    // A: the QR link, exactly what a phone camera opens. Nothing typed at all.
    await tabletA.goto(`${relayOrigin}/tablet#${qrFragmentA}`, { waitUntil: "load" });
    await tabletA.locator("#pairingVerify").waitFor({ state: "visible", timeout: 45000 });
    await handOver("A_ASKED");
    await paired(tabletA);

    // B: the typed code.
    await tabletB.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await tabletB.fill("#pairingCode", codeB);
    await tabletB.fill("#pairingName", "Bob's tablet");
    await tabletB.click("#pairingGo");
    await tabletB.locator("#pairingVerify").waitFor({ state: "visible", timeout: 45000 });
    await handOver("B_ASKED");
    await paired(tabletB);

    // Each desktop publishes a different map; each tablet must show its own and only its own.
    await handOver("BOTH_PAIRED");
    let a = await until(() => stateOf(tabletA), (state) => state.mapId === "customs");
    let b = await until(() => stateOf(tabletB), (state) => state.mapId === "woods");
    check("tablet A shows desktop A's map", a.mapId === "customs", JSON.stringify(a));
    check("tablet B shows desktop B's map", b.mapId === "woods", JSON.stringify(b));

    // Control from tablet A. The .NET side snapshots both desktops either side of the press.
    await tabletA.click('#deviceModes button[data-mode="Control"]');
    await tabletA.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    await tabletA.waitForTimeout(300);
    await handOver("CONTROL_READY");
    await tabletA.click("#zoomIn");
    await tabletA.waitForTimeout(2000);
    await handOver("CONTROL_PRESSED");

    // The relay goes down and comes back on the same address. Nothing is typed on either tablet;
    // each desktop then publishes a new map and each tablet must pick its own up.
    await handOver("RESTART_RELAY");
    a = await until(() => stateOf(tabletA), (state) => state.hasLive && state.mapId === "interchange", 45000);
    b = await until(() => stateOf(tabletB), (state) => state.hasLive && state.mapId === "reserve", 45000);
    check("after a relay restart tablet A is back on desktop A", a.hasLive && a.mapId === "interchange", JSON.stringify(a));
    check("after a relay restart tablet B is back on desktop B", b.hasLive && b.mapId === "reserve", JSON.stringify(b));
    check("nothing was asked of tablet A", !(await tabletA.locator("#pairingIdle").isVisible()));
    check("nothing was asked of tablet B", !(await tabletB.locator("#pairingIdle").isVisible()));

    // Desktop A revokes its tablet, then desktop B publishes once more.
    await handOver("REVOKE_A");
    a = await until(() => stateOf(tabletA), (state) => !state.hasLive, 30000);
    b = await until(() => stateOf(tabletB), (state) => state.mapId === "shoreline", 30000);
    check("revoking A's tablet ended tablet A", !a.hasLive, JSON.stringify(a));
    check("revoking A's tablet left tablet B connected", b.hasLive && b.mapId === "shoreline", JSON.stringify(b));
  } catch (error) {
    failures += 1;
    console.log(`CHECK:FAIL:script:${error && error.stack ? error.stack : error}`);
  } finally {
    for (const line of consoleLog.slice(-40)) console.log(line);
    await browser.close();
  }

  console.log(failures === 0 ? "RESULT:PASS" : `RESULT:FAIL:${failures}`);
  process.exitCode = failures === 0 ? 0 : 1;
  process.stdin.pause();
}

main();
