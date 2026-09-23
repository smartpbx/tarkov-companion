// #708: the real tablet page can leave a saved desktop and a fresh QR wins over that saved
// pairing. Driven by TabletPairAgainBrowserTests against a real in-process relay and desktop.

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

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

const inputLines = [];
let inputWaiter = null;
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  for (const line of chunk.split("\n")) {
    if (!line.trim()) continue;
    if (inputWaiter) {
      const resolve = inputWaiter;
      inputWaiter = null;
      resolve(line.trim());
    } else {
      inputLines.push(line.trim());
    }
  }
});

function nextInput() {
  if (inputLines.length > 0) return Promise.resolve(inputLines.shift());
  return new Promise((resolve) => { inputWaiter = resolve; });
}

async function deleteStoredSession(page) {
  await page.evaluate(() => new Promise((resolve, reject) => {
    const open = indexedDB.open("tarkov-companion-device", 1);
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const transaction = open.result.transaction("identity", "readwrite");
      transaction.objectStore("identity").delete("session");
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
    };
  }));
}

async function storedPairingKeys(page) {
  return page.evaluate(() => new Promise((resolve, reject) => {
    const open = indexedDB.open("tarkov-companion-device", 1);
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const transaction = open.result.transaction("identity", "readonly");
      const store = transaction.objectStore("identity");
      const paired = store.get("paired");
      const session = store.get("session");
      transaction.oncomplete = () => resolve({ paired: !!paired.result, session: !!session.result });
      transaction.onerror = () => reject(transaction.error);
    };
  }));
}

async function storeOldPairing(page) {
  await page.evaluate(() => new Promise((resolve, reject) => {
    const open = indexedDB.open("tarkov-companion-device", 1);
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const transaction = open.result.transaction("identity", "readwrite");
      transaction.objectStore("identity").put(
        { desktopKeyId: "remembered-desktop", deviceName: "Old tablet" },
        "paired",
      );
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
    };
  }));
}

async function screenshot(page, outDir, state, width, height) {
  if (!outDir || outDir === "-") return;
  fs.mkdirSync(outDir, { recursive: true });
  await page.setViewportSize({ width, height });
  await page.waitForTimeout(100);
  await page.screenshot({ path: path.join(outDir, `${state}--${width}x${height}.png`) });
}

async function main() {
  const [, , relayOrigin, firstCode, deviceName, outDir = "-"] = process.argv;
  if (!relayOrigin || !firstCode || !deviceName) {
    console.error("usage: node test-tablet-pair-again.cjs <relayOrigin> <firstCode> <deviceName> [outDir]");
    process.exitCode = 2;
    return;
  }

  const { chromium } = resolvePlaywright();
  const browser = await chromium.launch({ args: ["--ignore-certificate-errors"] });
  const page = await browser.newPage({ viewport: { width: 1024, height: 768 } });
  const browserLog = [];
  page.on("console", (message) => browserLog.push(`[console:${message.type()}] ${message.text()}`));
  page.on("pageerror", (error) => browserLog.push(`[pageerror] ${error}`));

  try {
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.fill("#pairingCode", firstCode);
    await page.fill("#pairingName", deviceName);
    await page.click("#pairingGo");
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 15000 });
    console.log("FIRST_PAIRING_ASKED");
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 30000 });
    console.log("INITIAL_PAIRED");

    // Leave the old remembered desktop in place but remove its live session. With no desktop
    // bridge poll answering the resume ticket, this is the player's stale reconnect state.
    await deleteStoredSession(page);
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.locator("#pairingWaiting").waitFor({ state: "visible", timeout: 15000 });
    await page.locator("#pairAgain").waitFor({ state: "visible", timeout: 15000 });
    if (await page.locator("#unpairHeader").textContent() !== "Forget this desktop") {
      throw new Error("the remembered desktop action is missing while reconnecting");
    }
    await screenshot(page, outDir, "reconnecting", 1024, 768);
    await screenshot(page, outDir, "reconnecting", 390, 844);
    console.log("RECONNECTING_HAS_PAIR_AGAIN");

    page.once("dialog", (dialog) => dialog.accept());
    await page.click("#pairAgain");
    await page.locator("#pairingIdle").waitFor({ state: "visible", timeout: 5000 });
    await page.locator("#pairingCode").waitFor({ state: "visible", timeout: 5000 });
    const stored = await storedPairingKeys(page);
    if (stored.paired || stored.session) {
      throw new Error(`pair again left saved state behind: ${JSON.stringify(stored)}`);
    }
    if (!await page.locator("#unpairHeader").isHidden()) {
      throw new Error("forget action stayed visible after the desktop was forgotten");
    }
    await screenshot(page, outDir, "pair-again", 1024, 768);
    await screenshot(page, outDir, "pair-again", 390, 844);
    console.log("PAIR_AGAIN_SHOWS_CODE_ENTRY");

    // A fresh QR is explicit intent. Put an obsolete remembered desktop back, then prove that
    // opening the new pairing URL starts its code rather than trying this stored value.
    await storeOldPairing(page);
    console.log("READY_FOR_FRESH_CODE");
    const freshQrUrl = await nextInput();
    await page.goto(freshQrUrl, { waitUntil: "load" });
    await page.locator("#pairingVerify").waitFor({ state: "visible", timeout: 15000 });
    if (await page.locator("#pairingCode").isVisible()) {
      throw new Error("the fresh QR fell back to code entry instead of starting pairing");
    }
    console.log("FRESH_CODE_STARTED");
    console.log("ALL_DONE");
  } catch (error) {
    console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
    console.error(browserLog.join("\n"));
    process.exitCode = 1;
  } finally {
    process.stdin.pause();
    await browser.close();
  }
}

main().catch((error) => {
  console.error(`FAILURE: ${error && error.stack ? error.stack : error}`);
  process.exitCode = 1;
});
