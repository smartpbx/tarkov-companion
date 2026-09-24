// #290: real-browser proof that a mark placed without relay connectivity is visibly queued and
// reaches the desktop exactly once after reconnect.

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
    console.error("usage: node test-tablet-offline-mark-queue.cjs <relayOrigin> <pairingCode> <deviceName>");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const context = await browser.newContext({ viewport: { width: 1024, height: 768 }, ignoreHTTPSErrors: true });
  const page = await context.newPage();
  const consoleLog = [];
  page.on("console", (message) => consoleLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error}`));

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", pairingCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    await page.waitForFunction(() => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(":pairingVerify")), null, { timeout: 45000 }); // [#840] shown, not still shown: an instant approval leaves the code step up for one frame
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });
    let current = await until(page, (value) => value.hasSurface && value.hasLive);
    check("the tablet has the desktop map", current.hasSurface, JSON.stringify(current));

    await page.locator("#markLabel").fill("Offline flank");
    const canvas = page.locator("#map");
    const box = await canvas.boundingBox();
    if (!box) throw new Error("The map canvas has no layout box.");
    await context.setOffline(true);
    await page.mouse.click(box.x + box.width * 0.62, box.y + box.height * 0.42);
    current = await until(page, (value) => value.queuedMarks.length === 1 && value.pendingCommands === 0);
    check("the failed mark is queued", current.queuedMarks.length === 1, JSON.stringify(current));
    check("the queued mark keeps its label", current.queuedMarks[0]?.label === "Offline flank", JSON.stringify(current));
    const queuedRow = page.locator('.markRow.queued[data-queued-mark-id]');
    check("the queue is visible", await queuedRow.isVisible());
    check("the row says queued", /queued/i.test(await queuedRow.textContent()));

    const screenshotDir = process.env.TABLET_OFFLINE_SCREENSHOT_DIR;
    if (screenshotDir) {
      fs.mkdirSync(screenshotDir, { recursive: true });
      for (const [name, width, height] of [["1024x768", 1024, 768], ["390x844", 390, 844]]) {
        await page.setViewportSize({ width, height });
        await queuedRow.scrollIntoViewIfNeeded();
        await page.waitForTimeout(150);
        await page.screenshot({ path: path.join(screenshotDir, `tablet-offline-queue-${name}.png`) });
      }
    }

    await context.setOffline(false);
    current = await until(page, (value) => value.queuedMarks.length === 0 && value.marks.length === 1 &&
      value.pendingCommands === 0, 30000);
    check("reconnect sends and clears the queue", current.queuedMarks.length === 0 && current.marks.length === 1, JSON.stringify(current));
    const markId = current.marks[0]?.id;
    await page.waitForTimeout(2500);
    current = await state(page);
    check("the queued action is not replayed again", current.marks.length === 1 && current.marks[0]?.id === markId &&
      current.queuedMarks.length === 0, JSON.stringify(current));

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
