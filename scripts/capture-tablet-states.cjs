// LOOK tool, not a test: drives the real tablet page (Tablet/index.html) in a real headless
// Chromium through every state a player reaches, and screenshots each one at three viewports —
// tablet landscape (1280x800), portrait (800x1280) and phone (390x844) — so a person (or an
// agent) can actually open the PNGs and see what the page looks like, rather than trust a
// string-contains test that the markup exists.
//
// See tests/TarkovCompanion.UnitTests/RelayLink/TabletScreenshotHarness.cs, which spawns this as
// a subprocess against an in-process relay and desktop coordinator: it approves the pairing
// request and a control request as they arrive, and answers one stdout/stdin handshake for the
// "desktop offline" state (it fast-forwards a fake clock rather than waiting fifteen real
// seconds). Built the same way scripts/test-relay-browser-pairing.cjs is (#289): CommonJS,
// because Playwright is not a project dependency here either.
//
// Usage: node capture-tablet-states.cjs <relayOrigin> <pairingCode> <deviceName> <outDir>

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

const SIZES = [
  ["tablet-landscape-1280x800", 1280, 800],
  ["portrait-800x1280", 800, 1280],
  ["phone-390x844", 390, 844],
];

async function shootAll(page, outDir, stateName) {
  for (const [label, width, height] of SIZES) {
    await page.setViewportSize({ width, height });
    await page.waitForTimeout(150); // canvas resize + one redraw
    await page.screenshot({ path: path.join(outDir, `${stateName}--${label}.png`) });
  }
}

async function main() {
  const [, , relayOrigin, pairingCode, deviceName, outDir] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName || !outDir) {
    console.error("usage: node capture-tablet-states.cjs <relayOrigin> <pairingCode> <deviceName> <outDir>");
    process.exitCode = 2;
    return;
  }

  fs.mkdirSync(outDir, { recursive: true });
  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const page = await browser.newPage();
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  try {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await shootAll(page, outDir, "01-before-pairing");

    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 15000 });
    await shootAll(page, outDir, "02-pairing-verify");

    // Not #pairingDone: the moment this page goes live it collapses the whole pairing card
    // (including #pairingDone) and shows the header's Unpair button instead (#407) — the same
    // fix this run exists to prove out.
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 30000 });
    // The map surface was already published before pairing began; give the first poll a moment
    // to land so "Follow" is not screenshotted mid-spinner.
    await page.waitForTimeout(800);
    await shootAll(page, outDir, "03-paired-follow");
    // Full-page, once: the marks list and "Look something up" are below the fold in every
    // viewport this captures at the ordinary height, and a clipped screenshot alone cannot say
    // whether they are laid out sanely once scrolled to.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(outDir, "03b-paired-follow-fullpage--phone-390x844.png"), fullPage: true });

    // Control: the desktop grants it the moment it sees the request (TabletScreenshotHarness).
    // The Control button's own aria-pressed goes true the instant it is clicked — ControlPending
    // sets it too, optimistically — so it is not what to wait on; the mode note's wording is the
    // only visible difference between "asked for it" and "the desktop said yes".
    await page.click('#deviceModes button[data-mode="Control"]');
    try {
      await page.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    } catch (error) {
      const noteText = await page.locator("#modeNote").textContent().catch(() => "<unreadable>");
      const liveText = await page.locator("#liveStatus").textContent().catch(() => "<unreadable>");
      const noticeText = await page.locator("#commandNotice").textContent().catch(() => "<unreadable>");
      const noticeHidden = await page.locator("#commandNotice").getAttribute("hidden").catch(() => "<unreadable>");
      console.error(`DEBUG modeNote="${noteText}" liveStatus="${liveText}" notice="${noticeText}" noticeHidden=${noticeHidden}`);
      console.error(`DEBUG console=${JSON.stringify(consoleLog.slice(-20))}`);
      throw error;
    }

    await page.waitForTimeout(250);
    await shootAll(page, outDir, "04-control");

    // #290 bonus: a rejected command's notice — asking for Control again while already holding
    // it is refused by the reducer (control-request-not-available) with no desktop involvement,
    // so this needs no further coordination.
    await page.click('#deviceModes button[data-mode="Control"]');
    await page.locator("#commandNotice:not([hidden])").waitFor({ timeout: 15000 });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(outDir, "04b-control-rejected-notice--phone-390x844.png") });

    // #407: switch the desktop to a different map from here. TabletScreenshotHarness asserts the
    // canonical desktop workspace afterward.
    await page.selectOption("#mapSwitch", "woods");
    await page.waitForTimeout(1200);

    // Independent
    await page.click('#deviceModes button[data-mode="Independent"]');
    await page.locator('#deviceModes button[data-mode="Independent"][aria-pressed="true"]').waitFor({ timeout: 10000 });
    await page.waitForTimeout(250);
    await shootAll(page, outDir, "05-independent");

    // #290 bonus: "Show this view on desktop" (ShowOnDesktopCommand, which had no caller before).
    await page.click("#showOnDesktop");
    await page.waitForTimeout(400);
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(outDir, "05b-show-on-desktop--tablet-landscape-1280x800.png") });

    // "Desktop offline": handed to the C# side, which fast-forwards its fake clock past the
    // 15-second silence threshold and stops answering, then tells this to reload and look again.
    console.log("READY_FOR_OFFLINE");
    await new Promise((resolve) => {
      process.stdin.resume();
      process.stdin.once("data", () => resolve());
    });
    await page.reload({ waitUntil: "load" });
    await page.locator("#liveStatus").filter({ hasText: /offline/i }).waitFor({ timeout: 15000 });
    await shootAll(page, outDir, "06-offline");

    console.log("DONE");
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
