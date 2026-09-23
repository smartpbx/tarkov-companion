// #290: real-browser proof that a tablet can create a waypoint, label it, and move the same
// canonical mark rather than creating a duplicate. Spawned by TabletMarkEditingBrowserTests.

const path = require("node:path");
const os = require("node:os");
const fs = require("node:fs");

function resolvePlaywright() {
  try {
    return require("playwright");
  } catch {
    const candidates = [];
    try {
      for (const entry of fs.readdirSync(path.join(os.homedir(), ".npm", "_npx"))) {
        const modules = path.join(os.homedir(), ".npm", "_npx", entry, "node_modules");
        if (fs.existsSync(path.join(modules, "playwright"))) candidates.push(modules);
      }
    } catch {
      // require.resolve below reports the useful error.
    }

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
    console.error("usage: node test-tablet-mark-editing.cjs <relayOrigin> <pairingCode> <deviceName>");
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
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 15000 });
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 30000 });
    let current = await until(page, (value) => value.hasSurface && value.hasLive);
    check("the tablet has the desktop map", current.hasSurface, JSON.stringify(current));

    const canvas = page.locator("#map");
    const box = await canvas.boundingBox();
    if (!box) throw new Error("The map canvas has no layout box.");
    const first = { x: box.x + box.width * 0.45, y: box.y + box.height * 0.55 };
    await page.mouse.move(first.x, first.y);
    await page.mouse.down();
    await page.waitForTimeout(600);
    await page.mouse.up();
    current = await until(page, (value) => value.marks.length === 1 && value.pendingCommands === 0);
    check("a long press creates one waypoint", current.marks.length === 1, JSON.stringify(current));
    const markId = current.marks[0]?.id;
    const beforeMove = current.marks[0];

    const row = page.locator(`.markRow[data-mark-id="${markId}"]`);
    await row.locator(".markEditor input").fill("Roadblock");
    await row.getByRole("button", { name: "Save label" }).click();
    current = await until(page, (value) => value.marks[0]?.label === "Roadblock" && value.pendingCommands === 0);
    check("Save label updates the canonical mark", current.marks[0]?.label === "Roadblock", JSON.stringify(current));
    check("label editing keeps the mark id", current.marks[0]?.id === markId, JSON.stringify(current));

    await row.getByRole("button", { name: "Move", exact: true }).click();
    current = await state(page);
    check("Move arms the selected mark", current.movingMarkId === markId, JSON.stringify(current));
    const movedTo = { x: box.x + box.width * 0.72, y: box.y + box.height * 0.35 };
    await page.mouse.click(movedTo.x, movedTo.y);
    current = await until(page, (value) => value.marks[0]?.revision >= 3 && value.pendingCommands === 0);
    const moved = current.marks[0];
    check("the move changes coordinates", moved.x !== beforeMove.x || moved.y !== beforeMove.y, JSON.stringify(current));
    check("the move keeps one mark with the same id", current.marks.length === 1 && moved.id === markId, JSON.stringify(current));
    check("the move keeps the edited label", moved.label === "Roadblock", JSON.stringify(current));

    const screenshotDir = process.env.TABLET_MARK_SCREENSHOT_DIR;
    if (screenshotDir) {
      fs.mkdirSync(screenshotDir, { recursive: true });
      for (const [name, width, height] of [["1024x768", 1024, 768], ["390x844", 390, 844]]) {
        await page.setViewportSize({ width, height });
        await page.locator(`.markRow[data-mark-id="${markId}"]`).scrollIntoViewIfNeeded();
        await page.waitForTimeout(150);
        await page.screenshot({ path: path.join(screenshotDir, `tablet-mark-editing-${name}.png`) });
      }
    }

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
