// Isolated page + real content script and real local service; Chrome APIs are
// shimmed here, so this does not certify an installed MV3 worker integration.
const { chromium } = require(process.argv[2]);
const path = require('path');
const assert = require('assert/strict');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ acceptDownloads: true, viewport: { width: 1000, height: 850 } });
    const session = await (await fetch('http://127.0.0.1:8877/session')).json();
    await page.exposeFunction('calendarTestApi', async (message) => {
      if (message.path.startsWith('/browser/poll')) {
        const client = new URLSearchParams(message.path.split('?')[1]).get('client');
        const response = { generation: 1, kind: 'scan', client };
        return { ok: true, data: response };
      }
      if (message.path === '/browser/ack') return { ok: true, data: { ok: true } };
      const response = await fetch('http://127.0.0.1:8877' + message.path, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-RealtimeDictionary-Token': session.token },
        body: JSON.stringify(message.options.body),
      });
      return { ok: response.ok, data: await response.json() };
    });
    await page.setContent('<html><body><p style="margin:40px;font:22px sans-serif">我们9月3号下午14:00和oneapi的同事约一个bootcamp讨论会啊。</p></body></html>');
    await page.bringToFront();
    await page.evaluate(() => {
      window.chrome = { runtime: { id: 'isolated-test', sendMessage: (message, callback) => {
        window.calendarTestApi(message).then(callback);
      } }, storage: { local: { get: (defaults, callback) => callback(defaults), set: () => {} },
        onChanged: { addListener: () => {} } } };
    });
    await page.addStyleTag({ path: path.resolve(__dirname, '../../browser-extension/styles.css') });
    await page.addScriptTag({ path: path.resolve(__dirname, '../../browser-extension/content.js') });
    const task = page.locator('.semantic-overlay-browser-task').first();
    await task.waitFor();
    await task.click();
    await page.getByRole('button', { name: '编辑日程…' }).click();
    const form = page.locator('.semantic-overlay-calendar-form');
    assert.equal(await form.locator('[name="start"]').inputValue(), '');
    await form.locator('[name="start"]').fill('2026-09-20T14:00');
    await form.locator('[name="end"]').fill('2026-09-20T13:00');
    await form.locator('[name="utc_offset"]').fill('+08:00');
    const submit = form.getByRole('button', { name: '确认并导出日历文件' });
    await submit.click();
    await form.getByRole('status').filter({ hasText: '结束时间必须晚于开始时间' }).waitFor();
    await form.locator('[name="end"]').fill('2026-09-20T15:00');
    await page.screenshot({ path: path.join(__dirname, 'browser-calendar-preview.png') });
    const downloadPromise = page.waitForEvent('download');
    await submit.click();
    const download = await downloadPromise;
    const stream = await download.createReadStream();
    const buffers = [];
    for await (const chunk of stream) buffers.push(chunk);
    const text = Buffer.concat(buffers).toString('utf8');
    assert.match(text, /DTSTART:20260920T060000Z/);
    assert.match(text, /DTEND:20260920T070000Z/);
    assert.equal(await submit.isDisabled(), true);
    console.log('PASS: blue highlight -> edit -> invalid end rejected -> confirmed ICS download -> repeat disabled');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
