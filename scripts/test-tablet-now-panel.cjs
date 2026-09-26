// #712 0-11: the desktop's Now panel on the paired tablet. Drives the real tablet page against a
// real in-process relay and desktop (TabletNowPanelBrowserTests):
//   A. a mid-raid Now panel shows beside the map at 1280x800 without scrolling, counts its clock
//      from the anchor and keeps its "counted" label, stacks under the map on a 390x844 phone, and
//      a squad row's Ping sends an ordinary tablet ping at that squadmate's shared spot;
//   B. a late raid with a fresh loot verdict still fits at 1280x800;
//   C. the map's ☰ swaps the panel for the old controls and Back to Now returns;
//   D. a surface without the panel (an older desktop, or its flag off) is the old layout again.
// With TABLET_SCREENSHOT_DIR set it also writes what it saw there, for a person to look at.
//
// Usage: node test-tablet-now-panel.cjs <relayOrigin> <pairingCode> <deviceName>

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

const state = (page) => page.evaluate(() => window.__tabletTestState());

async function until(page, predicate, timeoutMs = 20000) {
  const deadline = Date.now() + timeoutMs;
  let current = await state(page);
  while (!predicate(current) && Date.now() < deadline) {
    await page.waitForTimeout(100);
    current = await state(page);
  }

  return current;
}

async function main() {
  const [, , relayOrigin, pairingCode, deviceName] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-tablet-now-panel.cjs <relayOrigin> <pairingCode> <deviceName>");
    process.exitCode = 2;
    return;
  }

  const shotDir = process.env.TABLET_SCREENSHOT_DIR;
  if (shotDir) fs.mkdirSync(shotDir, { recursive: true });
  const shoot = async (page, name) => {
    if (shotDir) await page.screenshot({ path: path.join(shotDir, name) });
  };

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    await page.waitForFunction(() => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(":pairingVerify")), null, { timeout: 45000 }); // [#840]
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });

    // A. Mid-raid.
    let current = await until(page, (value) => value.hasSurface && value.now?.shown, 45000);
    check("the tablet shows the desktop's Now panel", !!current.now?.shown, JSON.stringify(current.now));
    check("NOW counts the clock from its anchor", /^\d+:\d\d left$/.test(current.now?.headline ?? ""), current.now?.headline);
    check("and keeps its label: counted, not the game's clock",
      (current.now?.detail ?? "").startsWith("counted from the raid start"), current.now?.detail);
    check("YOU says how old the screenshot is", /^\d+ s ago$/.test(current.now?.youAge ?? ""), current.now?.youAge);
    check("SQUAD has one row per squadmate, in the desk's order",
      JSON.stringify(current.now?.squad) === JSON.stringify(["Geo", "Riley", "Sam", "Kai"]), JSON.stringify(current.now?.squad));
    check("the panel does not scroll at 1280x800", current.now && !current.now.scrolls && !current.now.pageScrolls, JSON.stringify(current.now));
    const [x, , width] = current.now.panelBox;
    check("it sits beside the map, on the right", x > 640 && x + width <= 1280, JSON.stringify(current.now.panelBox));
    const headline1 = current.now.headline;
    await page.waitForTimeout(1100);
    current = await state(page);
    check("the clock ticks on the tablet", current.now.headline !== headline1, `${headline1} -> ${current.now.headline}`);
    check("a row that cannot be pinged has its Ping disabled",
      await page.locator('#nowSquadRows .nowSquadRow[data-name="Sam"] button').isDisabled());
    await shoot(page, "01-now-mid-raid--tablet-landscape-1280x800.png");

    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(250);
    const stacked = await page.evaluate(() => {
      const map = document.getElementById("mapWrap").getBoundingClientRect();
      const now = document.getElementById("nowPanel").getBoundingClientRect();
      return { mapBottom: map.bottom, nowTop: now.top, nowWidth: now.width, pageWidth: document.documentElement.scrollWidth };
    });
    check("on a phone the Now panel is under the map", stacked.nowTop >= stacked.mapBottom, JSON.stringify(stacked));
    check("and nothing runs off the side", stacked.pageWidth <= 390 && stacked.nowWidth <= 390, JSON.stringify(stacked));
    await shoot(page, "02-now-mid-raid--phone-390x844.png");
    await page.locator("#nowPanel").screenshot({ path: shotDir ? path.join(shotDir, "03-now-mid-raid-panel--phone-390x844.png") : path.join(os.tmpdir(), "now-phone-panel.png") });
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.waitForTimeout(250);

    await page.locator('#nowSquadRows .nowSquadRow[data-name="Riley"] button').click();
    current = await until(page, (value) => value.marks.some((mark) => mark.kind === "Ping" && mark.label === "Riley") && value.pendingCommands === 0);
    const ping = current.marks.find((mark) => mark.kind === "Ping" && mark.label === "Riley");
    check("Ping on Riley's row sends a ping named for Riley", !!ping, JSON.stringify(current.marks));
    check("at Riley's last shared spot", ping && ping.x === 620 && ping.y === 180, JSON.stringify(ping));
    console.log("PING_DONE");

    // B. Late raid with a fresh verdict.
    current = await until(page, (value) => (value.now?.scanRows ?? 0) > 0, 30000);
    check("a fresh verdict shows its top rows", current.now?.scanRows === 3, JSON.stringify(current.now));
    check("with the desk's counts", current.now?.scanLine === "Take 2 · Swap 1 · Leave 1", current.now?.scanLine);
    check("late raid still fits at 1280x800", current.now && !current.now.scrolls && !current.now.pageScrolls, JSON.stringify(current.now));
    check("late raid is amber", await page.locator("#nowPanel.late, #nowPanel.urgent").count() === 1);
    await shoot(page, "04-now-late-verdict--tablet-landscape-1280x800.png");
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(250);
    await page.locator("#nowPanel").screenshot({ path: shotDir ? path.join(shotDir, "05-now-late-verdict-panel--phone-390x844.png") : path.join(os.tmpdir(), "now-phone-late.png") });
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.waitForTimeout(250);

    // C. The controls are one tap away, and so is the way back.
    await page.click("#panelToggle");
    current = await state(page);
    check("the map's ☰ swaps the panel for the controls",
      !current.now.shown && await page.locator("#surfacePanel").isVisible(), JSON.stringify(current.now));
    await page.click("#nowBack");
    current = await state(page);
    check("Back to Now returns", current.now.shown && !(await page.locator("#surfacePanel").isVisible()), JSON.stringify(current.now));
    console.log("B_DONE");

    // D. No panel on the surface: the page as it was.
    current = await until(page, (value) => value.hasSurface && value.now === null, 30000);
    check("a surface without the panel has none", current.now === null, JSON.stringify(current.now));
    check("and the old controls column is back", await page.locator("#surfacePanel").isVisible() && !(await page.locator("#nowPanel").isVisible()));
    await shoot(page, "06-no-now-panel--tablet-landscape-1280x800.png");

    if (failures > 0) {
      console.error(`\n${failures} check(s) failed.`);
      console.error(consoleLog.join("\n"));
      process.exitCode = 1;
    } else {
      console.log("ALL_DONE");
    }
  } catch (error) {
    console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
    console.error(consoleLog.join("\n"));
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
  process.exitCode = 1;
});
