// #407: real-browser proof that the Control-only workspace picker sends an authorised command
// and observes the desktop's acknowledgement.

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
    console.error("usage: node test-tablet-workspace-navigation.cjs <relayOrigin> <pairingCode> <deviceName>");
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
    let current = await until(page, (value) => value.hasSurface && value.hasLive, 15000);
    check("the tablet has the desktop map", current.hasSurface, JSON.stringify(current));
    check("Follow hides the workspace picker", await page.locator("#workspacePicker").isHidden());
    check("Follow hides capture arming", await page.locator("#capturePicker").isHidden());

    await page.click('#deviceModes button[data-mode="Control"]');
    await page.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    check("Control shows the workspace picker", await page.locator("#workspacePicker").isVisible());
    check("Control shows capture arming", await page.locator("#capturePicker").isVisible());
    const labels = await page.locator("#workspacePicker button").allTextContents();
    check(
      "the picker offers the five desktop workspaces",
      JSON.stringify(labels) === JSON.stringify(["Raid", "Intel", "Plan", "Team", "Debrief"]),
      JSON.stringify(labels));

    await page.click('#workspacePicker button[data-workspace="Plan"]');
    current = await until(
      page,
      (value) => value.desktopWorkspace === "Plan" && value.pendingCommands === 0 &&
        value.lastAcknowledgement?.disposition === "Applied",
      15000);
    check("the canonical desktop workspace became Plan", current.desktopWorkspace === "Plan", JSON.stringify(current));
    check("the tablet received an Applied acknowledgement", current.pendingCommands === 0 &&
      current.lastAcknowledgement?.disposition === "Applied", JSON.stringify(current));
    check("the Plan choice is selected", await page.locator('#workspacePicker button[data-workspace="Plan"]')
      .getAttribute("aria-pressed") === "true");

    const captureLabels = await page.locator("#capturePicker button").allTextContents();
    check(
      "capture arming offers Loot, Stash, and Flea",
      JSON.stringify(captureLabels) === JSON.stringify(["Loot", "Stash", "Flea"]),
      JSON.stringify(captureLabels));
    await page.click('#capturePicker button[data-intent="Flea"]');
    current = await until(
      page,
      (value) => value.pendingCommands === 0 && value.lastAcknowledgement?.disposition === "Applied" &&
        value.lastAcknowledgement?.label === "Arming Flea capture",
      15000);
    check("the Flea arm request was applied", current.lastAcknowledgement?.disposition === "Applied", JSON.stringify(current));

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
