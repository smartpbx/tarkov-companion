// #290 "Send to tablet": the desktop's half of "Show on desktop". Drives the real tablet page
// against a real in-process relay and desktop. The tablet goes Independent (its own view); the
// C# side then publishes the desk's view moved WITHOUT a send, which must leave the tablet where
// it is, and then WITH a send, which must move the tablet there and keep it Independent.
//
// Usage: node test-tablet-send-to-tablet.cjs <relayOrigin> <pairingCode> <deviceName>

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
    console.error("usage: node test-tablet-send-to-tablet.cjs <relayOrigin> <pairingCode> <deviceName>");
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
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 15000 });
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 30000 });

    let current = await until(page, (s) => s.hasSurface && s.hasLive, 15000);
    check("the tablet has the desktop's map", current.hasSurface, JSON.stringify(current));

    await page.click('#deviceModes button[data-mode="Independent"]');
    current = await until(page, (s) => s.mode === "Independent" && s.camera, 15000);
    check("the tablet is Independent", current.mode === "Independent", JSON.stringify(current));
    // Its own view, somewhere the desk is not.
    await page.click("#zoomIn").catch(() => {});
    current = await state(page);
    const own = current.camera;
    console.log("INDEPENDENT");

    // The desk moves without sending: Independent does not follow.
    current = await until(page, (s) => s.surfaceView && s.surfaceView.centerX === 300, 15000);
    check("the desk's unsent move reached the page", current.surfaceView?.centerX === 300, JSON.stringify(current));
    await page.waitForTimeout(300);
    current = await state(page);
    check(
      "an unsent desk move leaves an Independent tablet's view alone",
      JSON.stringify(current.camera) === JSON.stringify(own),
      `${JSON.stringify(own)} -> ${JSON.stringify(current.camera)}`);
    console.log("UNSENT_CHECKED");

    // The desk sends: the tablet takes the desk's view and stays Independent.
    current = await until(page, (s) => s.camera && s.camera.x === 250, 15000);
    check(
      "a send moves the Independent tablet to the desk's view",
      current.camera?.x === 250 && current.camera?.y === 700 && current.camera?.zoom === 3,
      JSON.stringify(current));
    check("the tablet stays Independent", current.mode === "Independent", JSON.stringify(current));
    check(
      "the tablet says the desk sent it",
      (current.notice ?? "").includes("The desktop sent its view of Customs"),
      JSON.stringify(current.notice));

    // Moving on its own afterwards still works, and a later publish of the same send does not
    // pull it back.
    await page.click("#zoomOut").catch(() => {});
    const moved = (await state(page)).camera;
    console.log("SENT_CHECKED");
    current = await until(page, (s) => s.surfaceView && s.surfaceView.zoom === 4, 15000);
    await page.waitForTimeout(300);
    current = await state(page);
    check(
      "a republish carrying the same send does not move the tablet again",
      JSON.stringify(current.camera) === JSON.stringify(moved),
      `${JSON.stringify(moved)} -> ${JSON.stringify(current.camera)}`);

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
