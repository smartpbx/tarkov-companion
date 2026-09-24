// #289/#290: real-browser proof that a tablet chooses a new mark's scope and lifetime, changes a
// mark's scope afterwards, and draws a short route that lands as ordered stops of one route.
// Spawned by TabletMarkScopeRouteBrowserTests.

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
    console.error("usage: node test-tablet-mark-scope-route.cjs <relayOrigin> <pairingCode> <deviceName>");
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
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 45000 });
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });
    let current = await until(page, (value) => value.hasSurface && value.hasLive);
    check("the tablet has the desktop map", current.hasSurface, JSON.stringify(current));

    const canvas = page.locator("#map");
    const box = await canvas.boundingBox();
    if (!box) throw new Error("The map canvas has no layout box.");
    const at = (fx, fy) => ({ x: box.x + box.width * fx, y: box.y + box.height * fy });

    // Just me, five minutes: a long press makes a private waypoint that ends.
    await page.locator('#markScope button[data-scope="Private"]').click();
    await page.selectOption("#markLifetime", "FiveMinutes");
    const first = at(0.40, 0.50);
    await page.mouse.move(first.x, first.y);
    await page.mouse.down();
    await page.waitForTimeout(600);
    await page.mouse.up();
    current = await until(page, (value) => value.marks.length === 1 && value.pendingCommands === 0);
    const privateMark = current.marks[0];
    check("the new mark is Just me", privateMark?.scope === "Private", JSON.stringify(current));
    check("the new mark lasts five minutes", privateMark?.lifetime === "FiveMinutes" && privateMark?.kind === "Waypoint" &&
      !!privateMark?.expiresUtc && Math.abs(Date.parse(privateMark.expiresUtc) - Date.now() - 300000) < 30000, JSON.stringify(current));
    const row = page.locator(`.markRow[data-mark-id="${privateMark.id}"]`);
    check("the row says who sees it", (await row.locator(".meta").textContent()).includes("Just me"), await row.innerText());

    await row.locator(".markScopeToggle").click();
    current = await until(page, (value) => value.marks[0]?.scope === "PairedDevice" && value.pendingCommands === 0);
    check("Share with squad widens the same mark", current.marks.length === 1 && current.marks[0]?.scope === "PairedDevice" &&
      current.marks[0]?.id === privateMark.id, JSON.stringify(current));

    // Back to Squad for the route, which lasts until removed.
    await page.locator('#markScope button[data-scope="Squad"]').click();
    await page.selectOption("#markLifetime", "");
    await page.click("#routeStart");
    for (const [fx, fy] of [[0.25, 0.30], [0.50, 0.25], [0.70, 0.45]]) {
      const stop = at(fx, fy);
      await page.mouse.click(stop.x, stop.y);
      await page.waitForTimeout(350);
    }
    current = await state(page);
    check("three taps are three route stops, not pings", current.routeDraft === 3 && current.marks.length === 1, JSON.stringify(current));
    const screenshotDir = process.env.TABLET_MARK_SCREENSHOT_DIR;
    if (screenshotDir) {
      fs.mkdirSync(screenshotDir, { recursive: true });
      await page.screenshot({ path: path.join(screenshotDir, "tablet-route-draft-1024x768.png") });
    }

    await page.click("#routeDone");
    current = await until(page, (value) => value.marks.length === 4 && value.pendingCommands === 0, 20000);
    const stops = current.marks.filter((mark) => mark.routeId);
    check("Done sends every stop", stops.length === 3, JSON.stringify(current));
    check("the stops share one route", new Set(stops.map((mark) => mark.routeId)).size === 1, JSON.stringify(stops));
    check("the stops are numbered 1 to 3", stops.map((mark) => mark.routeStep).sort().join(",") === "1,2,3", JSON.stringify(stops));
    check("the stops are Squad waypoints", stops.every((mark) => mark.scope === "PairedDevice" && mark.kind === "Waypoint"), JSON.stringify(stops));

    if (screenshotDir) {
      for (const [name, width, height] of [["1024x768", 1024, 768], ["390x844", 390, 844]]) {
        await page.setViewportSize({ width, height });
        await page.waitForTimeout(250);
        await page.screenshot({ path: path.join(screenshotDir, `tablet-marks-map-${name}.png`) });
        await page.locator("#markScope").scrollIntoViewIfNeeded();
        await page.waitForTimeout(150);
        await page.screenshot({ path: path.join(screenshotDir, `tablet-marks-card-${name}.png`) });
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
