// #846: a returning tablet its desktop no longer recognises goes to the code form at once. Before,
// the desktop gave up only on its own side and the page sat on "Reconnecting" until its own
// timeouts ran out (a minute on an unanswered ticket, 30 s on a handshake step). Driven by
// TabletResumeRefusedBrowserTests against a real in-process relay and desktop.

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

const REASON = "Desktop didn't recognise this tablet · enter the code";

async function main() {
  const [, , relayOrigin, firstCode, deviceName] = process.argv;
  if (!relayOrigin || !firstCode || !deviceName) {
    console.error("usage: node test-tablet-resume-refused.cjs <relayOrigin> <firstCode> <deviceName>");
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
    await page.waitForFunction(() => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(":pairingVerify")), null, { timeout: 45000 });
    console.log("FIRST_PAIRING_ASKED");
    await page.locator("#unpairHeader:not([hidden])").waitFor({ state: "visible", timeout: 45000 });
    console.log("INITIAL_PAIRED");

    // The test revokes this tablet on the desktop only, then says go.
    if (await nextInput() !== "GO") throw new Error("expected GO");

    // The session is gone and the pairing is remembered: the page comes back by key.
    await deleteStoredSession(page);
    await page.goto(`${relayOrigin}/tablet`, { waitUntil: "load" });
    await page.waitForFunction(() => window.__tabletTestState().pairingStages.some((stage) => stage.includes(":pairingWaiting")), null, { timeout: 15000 });
    console.log("RECONNECTING");

    // 45 s so the page from before #846 fails by showing nothing, not by a tight limit: it
    // waits a minute on the ticket. The bound that matters is the elapsed time checked below.
    await page.waitForFunction(
      (reason) => window.__tabletTestState().pairingStages.some((stage) => stage.endsWith(`:pairingIdle:${reason}`)),
      REASON,
      { timeout: 45000 },
    );
    const stages = await page.evaluate(() => window.__tabletTestState().pairingStages);
    const at = (entry) => Number(entry.split(":", 1)[0]);
    const reconnecting = stages.find((stage) => stage.includes(":pairingWaiting"));
    const refused = stages.find((stage) => stage.endsWith(`:pairingIdle:${REASON}`));
    const elapsed = at(refused) - at(reconnecting);
    console.log(`REFUSAL_SHOWN_AFTER_MS ${elapsed}`);
    if (elapsed > 4000) throw new Error(`the refusal took ${elapsed} ms to reach the page: ${stages.join(" | ")}`);
    if (!await page.locator("#pairingCode").isVisible()) throw new Error("the code form is hidden after the refusal");
    if ((await page.locator("#pairingError").textContent()) !== REASON) throw new Error("the reason is not on screen");
    if (await page.locator("#pairingWaiting").isVisible()) throw new Error("still says reconnecting after the refusal");
    const stored = await storedPairingKeys(page);
    if (stored.paired) throw new Error("the refused pairing is still remembered, so the page would knock again");
    console.log("CODE_STEP_SHOWN");
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
