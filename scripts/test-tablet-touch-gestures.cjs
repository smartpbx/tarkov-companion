// #570: drives the real tablet page in a real, touch-emulated headless Chromium (a browser
// context with hasTouch/isMobile, CDP Input.dispatchTouchEvent for drag/pinch, page.mouse.wheel
// for the trackpad/mouse path, page.touchscreen for a plain tap) through the + / - / fit buttons,
// wheel, one-finger drag, two-finger pinch and double-tap, in each of Follow, Control and
// Independent, and checks what actually moved: the tablet's own camera
// (window.__tabletTestState(), a test-only hook index.html exposes for exactly this) or nothing.
//
// Spawned by tests/TarkovCompanion.UnitTests/RelayLink/TabletTouchGestureTests.cs against an
// in-process relay and desktop coordinator, the same shape as capture-tablet-states.cjs and
// scripts/test-relay-browser-pairing.cjs (#289): CommonJS, because Playwright is not a project
// dependency here.
//
// Two places hand control back to the .NET side, which is the only thing that can see the
// desktop's own canonical state: LOCAL_MODES_START/_END bracket Follow+Independent (nothing
// should reach the desktop across that whole span) and CONTROL_START/CONTROL_STEP:<name> bracket
// each Control gesture (the desktop's canonical viewport must change after every one). Each
// marker blocks on a line of stdin ("NEXT") before continuing, so the snapshot the .NET side
// takes the instant it reads a marker is provably the state at that marker, not some later state
// a fast script already ran past.
//
// Usage: node test-tablet-touch-gestures.cjs <relayOrigin> <pairingCode> <deviceName>

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

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/// Blocks until the .NET side writes one line to this process's stdin. Used only around the
/// markers above; everything else in this script paces itself with waitForTimeout.
function waitForLine() {
  return new Promise((resolve) => {
    process.stdin.resume();
    process.stdin.once("data", () => resolve());
  });
}

async function main() {
  const [, , relayOrigin, pairingCode, deviceName] = process.argv;
  if (!relayOrigin || !pairingCode || !deviceName) {
    console.error("usage: node test-tablet-touch-gestures.cjs <relayOrigin> <pairingCode> <deviceName>");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  // hasTouch + isMobile: a real tablet's browser context, not a desktop one that merely accepts
  // synthetic touch events. touchscreen/dispatchTouchEvent both need this to produce the same
  // pointerType:"touch" PointerEvents index.html's own listeners branch on.
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    hasTouch: true,
    isMobile: true,
    ignoreHTTPSErrors: true,
  });
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);
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
    await page.waitForTimeout(500); // first surface read landing

    const box = await page.locator("#map").boundingBox();
    if (!box) throw new Error("The map canvas has no layout box.");
    const center = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
    // Off-centre on purpose: a pinch anchored only at the canvas centre (the pre-fix behaviour)
    // would leave the camera's x/y untouched by a pinch here, where a midpoint-anchored one would
    // have to move the camera to keep this point under the fingers.
    const offCentre = { x: box.x + box.width * 0.32, y: box.y + box.height * 0.3 };

    const state = () => page.evaluate(() => window.__tabletTestState());

    async function touch(type, points) {
      await cdp.send("Input.dispatchTouchEvent", {
        type,
        touchPoints: points.map((point, index) => ({ x: point.x, y: point.y, id: point.id ?? index })),
      });
    }

    async function drag(from, to, steps = 6) {
      await touch("touchStart", [{ x: from.x, y: from.y, id: 0 }]);
      for (let step = 1; step <= steps; step += 1) {
        const x = from.x + ((to.x - from.x) * step) / steps;
        const y = from.y + ((to.y - from.y) * step) / steps;
        await touch("touchMove", [{ x, y, id: 0 }]);
        await sleep(16);
      }
      await touch("touchEnd", []);
    }

    async function pinch(around, startHalf, endHalf, steps = 6) {
      await touch("touchStart", [
        { x: around.x - startHalf, y: around.y, id: 0 },
        { x: around.x + startHalf, y: around.y, id: 1 },
      ]);
      for (let step = 1; step <= steps; step += 1) {
        const half = startHalf + ((endHalf - startHalf) * step) / steps;
        await touch("touchMove", [
          { x: around.x - half, y: around.y, id: 0 },
          { x: around.x + half, y: around.y, id: 1 },
        ]);
        await sleep(16);
      }
      await touch("touchEnd", []);
    }

    async function tap(at) {
      await touch("touchStart", [{ x: at.x, y: at.y, id: 0 }]);
      await sleep(30);
      await touch("touchEnd", []);
    }

    async function doubleTap(at) {
      await tap(at);
      await sleep(100);
      await tap(at);
    }

    // --- Follow: buttons, wheel, drag, pinch and double-tap all explain themselves rather than
    // moving anything, and the one-tap switch on the notice actually leaves Follow. ------------
    let before = await state();
    check("Follow starts as the paired default", before.mode === "Follow");

    await page.click("#zoomIn");
    await page.waitForTimeout(150);
    let notice = await page.locator("#commandNotice").isVisible();
    let noticeText = notice ? await page.locator("#commandNotice").textContent() : "";
    check(
      "Follow: the + button explains itself instead of doing nothing",
      notice && /switch to Independent/i.test(noticeText ?? ""),
      noticeText ?? "(notice hidden)",
    );
    let after = await state();
    check("Follow: the + button did not create a local camera", after.mode === "Follow" && after.camera === null);

    await page.mouse.move(center.x, center.y);
    await page.mouse.wheel(0, -120);
    await page.waitForTimeout(150);
    after = await state();
    check("Follow: the wheel did not create a local camera", after.mode === "Follow" && after.camera === null);

    await drag(center, { x: center.x + 80, y: center.y + 40 });
    await page.waitForTimeout(150);
    after = await state();
    check("Follow: a one-finger drag did not create a local camera", after.mode === "Follow" && after.camera === null);

    await pinch(center, 40, 90);
    await page.waitForTimeout(150);
    after = await state();
    check("Follow: a two-finger pinch did not create a local camera", after.mode === "Follow" && after.camera === null);

    await doubleTap(offCentre);
    await page.waitForTimeout(150);
    after = await state();
    check("Follow: a double-tap did not create a local camera", after.mode === "Follow" && after.camera === null);

    // The one-tap switch on the notice itself.
    await page.click("#commandNotice");
    await page.locator('#deviceModes button[data-mode="Independent"][aria-pressed="true"]').waitFor({ timeout: 10000 });
    after = await state();
    check("Follow: tapping the notice switches to Independent", after.mode === "Independent");

    // Back to Follow for a clean baseline before Independent's own block below.
    await page.click('#deviceModes button[data-mode="Follow"]');
    await page.locator('#deviceModes button[data-mode="Follow"][aria-pressed="true"]').waitFor({ timeout: 10000 });

    // --- Handoff: nothing above should have reached the desktop's canonical workspace, and
    // nothing Independent does below should either. --------------------------------------------
    console.log("LOCAL_MODES_START");
    await waitForLine();

    await page.click('#deviceModes button[data-mode="Independent"]');
    await page.locator('#deviceModes button[data-mode="Independent"][aria-pressed="true"]').waitFor({ timeout: 10000 });
    await page.waitForTimeout(200);

    before = await state();
    check("Independent starts with a local camera", before.mode === "Independent" && before.camera !== null);

    await page.click("#zoomIn");
    await page.waitForTimeout(150);
    after = await state();
    check("Independent: + zooms the tablet's own view in", after.camera.zoom > before.camera.zoom);
    before = after;

    await page.click("#zoomOut");
    await page.waitForTimeout(150);
    after = await state();
    check("Independent: - zooms the tablet's own view out", after.camera.zoom < before.camera.zoom);
    before = after;

    await page.click("#zoomIn"); // move off wherever fit would land, so its own effect is visible
    await page.waitForTimeout(150);
    before = await state();
    await page.click("#fit");
    await page.waitForTimeout(150);
    after = await state();
    check("Independent: fit changes the tablet's own zoom", after.camera.zoom !== before.camera.zoom);
    before = after;

    await page.mouse.move(center.x, center.y);
    await page.mouse.wheel(0, -120);
    await page.waitForTimeout(150);
    after = await state();
    check("Independent: the wheel zooms the tablet's own view", after.camera.zoom > before.camera.zoom);
    before = after;

    await drag(center, { x: center.x - 90, y: center.y - 50 });
    await page.waitForTimeout(150);
    after = await state();
    check(
      "Independent: a one-finger drag pans the tablet's own view",
      after.camera.x !== before.camera.x || after.camera.y !== before.camera.y,
    );
    before = after;

    await pinch(offCentre, 40, 100);
    await page.waitForTimeout(150);
    after = await state();
    check("Independent: a two-finger pinch zooms in", after.camera.zoom > before.camera.zoom);
    check(
      "Independent: the pinch is anchored to the fingers' midpoint, not the canvas centre",
      after.camera.x !== before.camera.x || after.camera.y !== before.camera.y,
    );
    before = after;

    await doubleTap(offCentre);
    await page.waitForTimeout(150);
    after = await state();
    check("Independent: a double-tap zooms in", after.camera.zoom > before.camera.zoom);

    console.log("LOCAL_MODES_END");
    await waitForLine();

    // --- Control: every gesture moves the desktop, which the .NET side confirms at each step. --
    await page.click('#deviceModes button[data-mode="Control"]');
    await page.locator("#modeNote").filter({ hasText: "You are driving" }).waitFor({ timeout: 20000 });
    await page.waitForTimeout(300);
    console.log("CONTROL_START");
    await waitForLine();

    async function controlStep(name, action) {
      await action();
      // The tablet's own frame poll (pollLiveFrames) only checks every 1.5s, not on a held read
      // the way the map surface is — so the echo of this command (which is what advances
      // live.workspaceRevision for the *next* command) can still be in flight if the next step
      // starts too soon, and the next command is then rejected as a stale revision rather than
      // applied. 2s clears that with margin.
      await page.waitForTimeout(2000);
      console.log(`CONTROL_STEP:${name}`);
      await waitForLine();
    }

    await controlStep("zoom-in", () => page.click("#zoomIn"));
    await controlStep("zoom-out", () => page.click("#zoomOut"));
    // Fit after the wheel: Control starts from the desk's view (#800), which here is the fitted
    // one, and + then - lands back on it, so a fit straight after would change nothing.
    await controlStep("wheel", async () => {
      await page.mouse.move(center.x, center.y);
      await page.mouse.wheel(0, -120);
    });
    await controlStep("fit", () => page.click("#fit"));
    await controlStep("drag", () => drag(center, { x: center.x + 70, y: center.y + 30 }));
    await controlStep("pinch", () => pinch(offCentre, 40, 95));
    await controlStep("double-tap", () => doubleTap(offCentre));

    process.stdin.pause();

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
