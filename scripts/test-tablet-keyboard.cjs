// #290: real-browser keyboard control for the paired tablet map.

const path = require("node:path");
const os = require("node:os");
const fs = require("node:fs");

function resolvePlaywright() {
  try { return require("playwright"); } catch {
    const candidates = [];
    try {
      for (const entry of fs.readdirSync(path.join(os.homedir(), ".npm", "_npx"))) {
        const modules = path.join(os.homedir(), ".npm", "_npx", entry, "node_modules");
        if (fs.existsSync(path.join(modules, "playwright"))) candidates.push(modules);
      }
    } catch { /* require.resolve below reports the useful error. */ }
    return require(require.resolve("playwright", { paths: candidates }));
  }
}

let failures = 0;
function check(name, condition, detail = "") {
  console.log(`CHECK:${condition ? "PASS" : "FAIL"}:${name}${condition ? "" : `:${detail}`}`);
  if (!condition) failures += 1;
}

const state = (page) => page.evaluate(() => window.__tabletTestState());
async function until(page, predicate, timeoutMs = 15000) {
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
    console.error("usage: node test-tablet-keyboard.cjs <relayOrigin> <pairingCode> <deviceName>");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const page = await browser.newPage({ viewport: { width: 1024, height: 768 } });
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    // Full UnitTests runs every browser harness on a busy shared host; pairing normally takes a
    // few seconds; the harness gives that first ceremony 45 seconds.
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 45000 });
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });
    let current = await until(page, (value) => value.hasSurface && value.hasLive);
    check("the keyboard legend is visible", await page.locator("#keyboardHelp").isVisible());

    await page.click('#deviceModes button[data-mode="Independent"]');
    current = await until(page, (value) => value.mode === "Independent" && value.camera &&
      value.pendingCommands === 0 && value.lastAcknowledgement?.disposition === "Applied");
    const beforePan = current.camera;
    await page.keyboard.press("ArrowRight");
    current = await state(page);
    check("ArrowRight pans the local map", current.camera.x > beforePan.x, JSON.stringify(current));

    const beforeZoom = current.camera.zoom;
    await page.keyboard.press("+");
    current = await state(page);
    check("+ zooms in", current.camera.zoom > beforeZoom, JSON.stringify(current));
    const zoomed = current.camera.zoom;
    await page.keyboard.press("-");
    current = await state(page);
    check("- zooms out", current.camera.zoom < zoomed, JSON.stringify(current));

    await page.locator("#markLabel").fill("F13+-");
    const beforeTextArrow = (await state(page)).camera;
    await page.keyboard.press("ArrowLeft");
    current = await state(page);
    check("map shortcuts leave focused inputs alone", current.camera.x === beforeTextArrow.x && current.camera.zoom === beforeTextArrow.zoom);
    check("typing shortcut characters stays in the label", await page.locator("#markLabel").inputValue() === "F13+-");

    await page.evaluate(() => document.activeElement?.blur());
    await page.keyboard.press("f");
    current = await until(page, (value) => value.mode === "Follow" && value.pendingCommands === 0 &&
      value.lastAcknowledgement?.disposition === "Applied");
    check("F returns to Follow", current.mode === "Follow", JSON.stringify(current));

    await page.click('#deviceModes button[data-mode="Control"]');
    await page.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    await page.evaluate(() => document.activeElement?.blur());
    await page.keyboard.press("3");
    current = await until(page, (value) => value.desktopWorkspace === "Plan" && value.pendingCommands === 0);
    check("3 switches Control to Plan", current.desktopWorkspace === "Plan", JSON.stringify(current));

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
    const pairingPage = await page.evaluate(() => ({
      error: document.getElementById("pairingError")?.textContent || null,
      shown: ["pairingIdle", "pairingWaiting", "pairingVerify", "pairingDone"]
        .filter((id) => document.getElementById(id) && document.getElementById(id).style.display !== "none"),
    })).catch(() => null);
    console.error(`PAIRING_PAGE: ${JSON.stringify(pairingPage)}`);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
  process.exitCode = 1;
});
