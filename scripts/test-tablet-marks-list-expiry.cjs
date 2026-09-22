// #584 remainder: a ping placed from the tablet sits on the tablet's own Marks list, which is
// fed by the paired protocol's MarkAggregate, not by the map surface. That aggregate never expired
// anything, so the ping left the map after 45 s and stayed on the list for good. This drives the
// real tablet page against a real in-process relay and desktop: tap the map (a ping), see it on
// the list, tell the C# side (PING_LISTED), which moves the desktop's clock past the ping's
// lifetime, then wait for the list to empty with nothing else done on this page.
//
// Usage: node test-tablet-marks-list-expiry.cjs <relayOrigin> <pairingCode> <deviceName>

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

const listText = (page) => page.evaluate(() => document.getElementById("markList").textContent);

async function until(page, predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let text = await listText(page);
  while (!predicate(text) && Date.now() < deadline) {
    await page.waitForTimeout(100);
    text = await listText(page);
  }

  return text;
}

async function main() {
  const [, , relayOrigin, pairingCode, deviceName] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-tablet-marks-list-expiry.cjs <relayOrigin> <pairingCode> <deviceName>");
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

    const surfaceDeadline = Date.now() + 15000;
    let state = await page.evaluate(() => window.__tabletTestState());
    while (!(state.hasSurface && state.hasLive) && Date.now() < surfaceDeadline) {
      await page.waitForTimeout(100);
      state = await page.evaluate(() => window.__tabletTestState());
    }

    check("the tablet has the desktop's map", state.hasSurface, JSON.stringify(state));

    // A tap on bare map is a ping, after the double-tap window closes.
    const box = await page.locator("#map").boundingBox();
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    const listed = await until(page, (text) => text.includes("Ping"), 15000);
    check("the ping is on the Marks list", listed.includes("Ping"), JSON.stringify(listed));
    console.log("PING_LISTED");

    // The C# side now moves the desktop's clock past the ping's lifetime. Nothing else happens on
    // this page: the list has to empty because the desktop said so.
    const emptied = await until(page, (text) => text.includes("Nothing marked yet."), 20000);
    check("the ping leaves the Marks list once it has expired", emptied.includes("Nothing marked yet."), JSON.stringify(emptied));

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
