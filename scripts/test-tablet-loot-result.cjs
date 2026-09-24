// #572: a Loot Scan result on the paired tablet. Drives the real tablet page against a real
// in-process relay and desktop. The C# side publishes a surface carrying a loot result; the sheet
// must open over the map with the desktop's rows, close on "Back to map", stay closed when the
// same result is published again, and reopen from "Last loot scan".
//
// Usage: node test-tablet-loot-result.cjs <relayOrigin> <pairingCode> <deviceName>

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

async function until(page, predicate, timeoutMs) {
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
    console.error("usage: node test-tablet-loot-result.cjs <relayOrigin> <pairingCode> <deviceName>");
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
    await page.waitForFunction(() => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(":pairingVerify")), null, { timeout: 45000 }); // [#840] shown, not still shown: an instant approval leaves the code step up for one frame
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });

    let current = await until(page, (s) => s.hasSurface && s.hasLive, 15000);
    check("the tablet has the desktop's map", current.hasSurface, JSON.stringify(current));
    check("no loot sheet before a scan", !current.lootOpen, JSON.stringify(current));
    console.log("MAP_READY");

    current = await until(page, (s) => s.lootOpen, 15000);
    check("a new loot result opens over the map", current.lootOpen, JSON.stringify(current));
    check(
      "it lists the desktop's rows in the desktop's order",
      JSON.stringify(current.lootRows) === JSON.stringify(["Graphics card", "Bolts", "Unknown item"]),
      JSON.stringify(current.lootRows));
    const title = await page.evaluate(() => document.getElementById("lootTitle").textContent);
    check("it is headed as a loot scan", title.startsWith("Loot scan"), title);
    const shotDir = process.env.TABLET_SCREENSHOT_DIR;
    if (shotDir) {
      await page.screenshot({ path: path.join(shotDir, "07-loot-result--tablet-landscape-1280x800.png") });
      await page.setViewportSize({ width: 390, height: 844 });
      await page.waitForTimeout(200);
      await page.screenshot({ path: path.join(shotDir, "07-loot-result--phone-390x844.png") });
      await page.setViewportSize({ width: 1280, height: 800 });
    }

    await page.click("#lootClose");
    current = await state(page);
    check("Back to map closes it", !current.lootOpen, JSON.stringify(current));
    console.log("CLOSED");

    // The C# side publishes the same result again with the map moved: it must stay closed.
    current = await until(page, (s) => s.surfaceView && s.surfaceView.centerX === 300, 15000);
    await page.waitForTimeout(300);
    current = await state(page);
    check("the same result published again stays closed", !current.lootOpen, JSON.stringify(current));

    await page.click("#lootOpen");
    current = await state(page);
    check("Last loot scan opens it again", current.lootOpen && current.lootRows.length === 3, JSON.stringify(current));

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

main().catch((error) => {
  console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
  process.exitCode = 1;
});
