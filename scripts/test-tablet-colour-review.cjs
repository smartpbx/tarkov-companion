// #290: mark colours and Stash/Flea review cards on the paired tablet. Drives the real tablet page
// against a real in-process relay and desktop (TabletColourReviewBrowserTests):
//   1. the colour picker offers Auto and six named colours, keeps the choice on the device, and a
//      new ping and waypoint carry the chosen colours to the desktop;
//   2. the C# side then publishes a surface with those marks coloured plus a Stash scan and a flea
//      screen; the new review opens over the map as cards, closes, and each reopens from its button.
// With TABLET_SCREENSHOT_DIR set it also writes what it saw there, for a person to look at.
//
// Usage: node test-tablet-colour-review.cjs <relayOrigin> <pairingCode> <deviceName>

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
    console.error("usage: node test-tablet-colour-review.cjs <relayOrigin> <pairingCode> <deviceName>");
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
    let current = await until(page, (value) => value.hasSurface && value.hasLive);
    check("the tablet has the desktop map", current.hasSurface, JSON.stringify(current));

    // 1. The picker.
    const swatches = page.locator("#markColour button");
    check("the picker offers Auto and six colours", (await swatches.count()) === 7, String(await swatches.count()));
    const names = await swatches.evaluateAll((items) => items.map((item) => item.getAttribute("aria-label")));
    check("every colour has a name a screen reader says", names.every((name) => name && name.length > 2) &&
      names.includes("Orange") && names.includes("Sky blue"), JSON.stringify(names));
    check("Auto is chosen until something else is", current.markColour === null &&
      (await page.locator('#markColour button[aria-checked="true"]').getAttribute("aria-label")) === "Auto colour", JSON.stringify(current.markColour));

    await page.locator('#markColour button[aria-label="Orange"]').click();
    current = await state(page);
    check("Orange becomes the colour of new marks", current.markColour === "#E69F00", JSON.stringify(current.markColour));
    check("the choice is kept on this device",
      (await page.evaluate(() => localStorage.getItem("tarkov-companion-tablet-mark-colour"))) === "#E69F00");
    // Arrow keys move the choice inside the group, and do not pan the map.
    await page.locator('#markColour button[aria-checked="true"]').focus();
    const cameraBefore = (await state(page)).camera;
    await page.keyboard.press("ArrowRight");
    current = await state(page);
    check("an arrow key moves to the next colour", current.markColour === "#009E73", JSON.stringify(current.markColour));
    check("and leaves the map where it was", JSON.stringify(current.camera) === JSON.stringify(cameraBefore),
      `${JSON.stringify(cameraBefore)} -> ${JSON.stringify(current.camera)}`);
    await page.locator('#markColour button[aria-label="Orange"]').click();

    const canvas = page.locator("#map");
    const box = await canvas.boundingBox();
    if (!box) throw new Error("The map canvas has no layout box.");
    const at = (fx, fy) => ({ x: box.x + box.width * fx, y: box.y + box.height * fy });

    // A tap is an orange ping.
    const ping = at(0.40, 0.45);
    await page.mouse.click(ping.x, ping.y);
    current = await until(page, (value) => value.marks.length === 1 && value.pendingCommands === 0);
    check("a tap sends an orange ping", current.marks[0]?.kind === "Ping" && current.markColours[0] === "#E69F00",
      JSON.stringify(current.marks) + JSON.stringify(current.markColours));

    // A hold, in pink, is a pink waypoint.
    await page.locator('#markColour button[aria-label="Pink"]').click();
    const hold = at(0.60, 0.55);
    await page.mouse.move(hold.x, hold.y);
    await page.mouse.down();
    await page.waitForTimeout(600);
    await page.mouse.up();
    current = await until(page, (value) => value.marks.length === 2 && value.pendingCommands === 0);
    const waypointIndex = current.marks.findIndex((mark) => mark.kind === "Waypoint");
    check("a hold sends a pink waypoint", waypointIndex >= 0 && current.markColours[waypointIndex] === "#CC79A7",
      JSON.stringify(current.marks) + JSON.stringify(current.markColours));
    const swatchLabels = await page.locator("#markList .markSwatch").evaluateAll((items) => items.map((item) => item.getAttribute("aria-label")));
    check("each row names its colour", swatchLabels.includes("Orange") && swatchLabels.includes("Pink"), JSON.stringify(swatchLabels));

    await page.locator("#markColour").scrollIntoViewIfNeeded();
    await shoot(page, "01-colour-picker--tablet-landscape-1280x800.png");
    console.log("MARKS_DONE");

    // 2. The desktop publishes the coloured marks and both reviews.
    current = await until(page, (value) => value.objectColours.length >= 2 && value.reviewOpen !== null, 30000);
    check("the desktop's surface carries both marks' colours",
      current.objectColours.some((item) => item.color === "#E69F00") && current.objectColours.some((item) => item.color === "#CC79A7"),
      JSON.stringify(current.objectColours));
    check("a new flea screen opens over the map", current.reviewOpen === "flea", JSON.stringify(current.reviewOpen));
    check("it is headed with when it was seen", current.reviewTitle.startsWith("Offers for Graphics card · as seen at"), current.reviewTitle);
    check("its cards are the desktop's rows, in order",
      JSON.stringify(current.reviewRows.map((row) => row.tag)) === JSON.stringify(["Good buy", "Over average"]), JSON.stringify(current.reviewRows));
    check("each card says how sure the read was", current.reviewRows.every((row) => row.meta.includes("sure")), JSON.stringify(current.reviewRows));
    await shoot(page, "02-flea-review--tablet-landscape-1280x800.png");

    await page.click("#reviewClose");
    current = await state(page);
    check("Back to map closes it", current.reviewOpen === null, JSON.stringify(current.reviewOpen));
    await page.waitForTimeout(200);
    await shoot(page, "03-coloured-marks--tablet-landscape-1280x800.png");

    await page.click("#stashOpen");
    current = await state(page);
    check("Last stash scan opens the stash cards", current.reviewOpen === "stash" && current.reviewTitle.startsWith("Stash scan · as seen at"),
      current.reviewTitle);
    check("with each item's group, count and confidence",
      current.reviewRows.length === 3 && current.reviewRows[0].tag === "Keep" && current.reviewRows[0].meta.includes("93%"),
      JSON.stringify(current.reviewRows));
    await shoot(page, "04-stash-review--tablet-landscape-1280x800.png");
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(200);
    await shoot(page, "05-stash-review--phone-390x844.png");
    await page.click("#reviewClose");
    await page.click("#fleaOpen");
    current = await state(page);
    check("Last flea scan opens the flea cards again", current.reviewOpen === "flea", JSON.stringify(current.reviewOpen));
    await shoot(page, "06-flea-review--phone-390x844.png");
    await page.click("#reviewClose");
    await page.locator("#markColour").scrollIntoViewIfNeeded();
    await shoot(page, "07-colour-picker--phone-390x844.png");

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
