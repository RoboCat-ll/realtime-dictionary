// Real MV3 smoke: Edge loads the unpacked extension, its service worker obtains
// the local token, and the content script acknowledges a real browser trigger.
const { chromium } = require(process.argv[2]);
const http = require('http');
const os = require('os');
const path = require('path');
const assert = require('assert/strict');

(async () => {
  const extensionPath = path.resolve(__dirname, '..', '..', 'browser-extension');
  const profilePath = path.join(os.tmpdir(), 'RealtimeDictionary-MV3-' + Date.now());
  const pageServer = http.createServer((_request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    response.end('<!doctype html><p style="margin:40px;font:24px sans-serif">'
      + '我们计划使用 RAG 和 OCR，9月3号下午14:00开 bootcamp 讨论会。</p>');
  });
  await new Promise((resolve) => pageServer.listen(0, '127.0.0.1', resolve));
  const pagePort = pageServer.address().port;
  let context;
  try {
    context = await chromium.launchPersistentContext(profilePath, {
      channel: 'msedge',
      headless: false,
      viewport: { width: 1000, height: 760 },
      args: [
        '--disable-extensions-except=' + extensionPath,
        '--load-extension=' + extensionPath,
      ],
    });
    let analyzeRequests = 0;
    context.on('request', (request) => {
      if (request.url() === 'http://127.0.0.1:8877/analyze') analyzeRequests++;
    });
    const page = context.pages()[0] || await context.newPage();
    await page.goto('http://127.0.0.1:' + pagePort, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#semantic-overlay-browser-root', { timeout: 10000 });

    const sessionResponse = await fetch('http://127.0.0.1:8877/session');
    assert.equal(sessionResponse.ok, true, 'local service session unavailable');
    const session = await sessionResponse.json();
    const triggerResponse = await fetch('http://127.0.0.1:8877/browser/trigger', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-RealtimeDictionary-Token': session.token,
      },
      body: '{}',
    });
    assert.equal(triggerResponse.ok, true, 'browser trigger failed');
    const command = await triggerResponse.json();
    await page.waitForSelector('.semantic-overlay-browser-term', { timeout: 15000 });

    let ack = { acked: false, focused: false };
    for (let attempt = 0; attempt < 24 && !(ack.acked && ack.focused); attempt++) {
      const ackResponse = await fetch(
        'http://127.0.0.1:8877/browser/ack-status?generation=' + command.generation,
        { headers: { 'X-RealtimeDictionary-Token': session.token } });
      ack = await ackResponse.json();
      if (!(ack.acked && ack.focused)) await page.waitForTimeout(150);
    }
    assert.equal(ack.acked, true, 'extension did not acknowledge trigger');
    const terms = await page.locator('.semantic-overlay-browser-term').allTextContents();
    assert.ok(terms.length > 0, 'extension rendered no terms');
    const rag = page.locator('[title="点击查看解释：RAG"]');
    await rag.click();
    const popup = page.locator('.semantic-overlay-browser-popup');
    await popup.locator('.semantic-overlay-browser-body').filter({ hasText: '检索增强生成' }).waitFor();
    await popup.locator('.semantic-overlay-browser-body a', { hasText: '模型' }).first().click();
    await popup.locator('[data-action="term"]').filter({ hasText: '模型' }).waitFor();
    await popup.locator('[data-action="back"]').click();
    await popup.locator('[data-action="term"]').filter({ hasText: 'RAG' }).waitFor();
    const serviceWorker = context.serviceWorkers()[0]
      || await context.waitForEvent('serviceworker', { timeout: 5000 });
    await serviceWorker.evaluate(() => chrome.storage.local.set({ autoFollow: false }));
    const beforeMutation = analyzeRequests;
    await page.evaluate(() => document.body.append(document.createTextNode(' 新消息 OCR')));
    await page.waitForTimeout(1600);
    assert.equal(analyzeRequests, beforeMutation,
      'DOM mutation scanned while auto follow was disabled');
    await serviceWorker.evaluate(() => chrome.storage.local.set({ autoFollow: true }));
    await page.evaluate(() => document.body.append(document.createTextNode(' 更新消息 FAISS')));
    await page.waitForTimeout(8500);
    assert.ok(analyzeRequests > beforeMutation,
      'auto follow did not rescan after the eight-second cooldown');
    process.stdout.write('PASS: installed-style MV3 bridge rendered ' + terms.length
      + ' highlights; recursive lookup and opt-in auto follow worked\n');
  } finally {
    if (context) await context.close();
    await new Promise((resolve) => pageServer.close(resolve));
  }
})().catch((error) => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
