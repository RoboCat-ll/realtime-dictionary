'use strict';

// 本地服务的唯一出口：content script 不直接发请求，全部经这里代理。
// 好处：令牌只存在于 service worker，网页脚本拿不到（服务端 CORS 只放行
// chrome-extension:// 来源，网页连 /session 的响应都读不到）。

const API = 'http://127.0.0.1:8877';
const TOKEN_HEADER = 'X-RealtimeDictionary-Token';
const EXPECTED_PRODUCT_ID = 'realtime-dictionary';
const SUPPORTED_PROTOCOL_VERSION = 2;
let serviceToken = null;

async function ensureToken() {
  if (serviceToken) return serviceToken;
  const response = await fetch(API + '/session');
  if (!response.ok) throw new Error('session HTTP ' + response.status);
  const data = await response.json();
  if (!data || data.product_id !== EXPECTED_PRODUCT_ID ||
      data.protocol_version !== SUPPORTED_PROTOCOL_VERSION) {
    throw new Error('本地实时字典版本不兼容，请退出旧版本后启动当前版本');
  }
  serviceToken = data && data.token ? data.token : null;
  if (!serviceToken) throw new Error('session response has no token');
  return serviceToken;
}

async function request(path, options) {
  const init = { method: options.method || 'GET' };
  if (options.body !== undefined) {
    init.headers = { 'Content-Type': 'application/json' };
    init.body = JSON.stringify(options.body);
  }
  let response = null;
  for (let attempt = 0; attempt < 2; attempt++) {
    const headers = Object.assign({}, init.headers);
    headers[TOKEN_HEADER] = await ensureToken();
    response = await fetch(API + path, Object.assign({}, init, { headers }));
    if (response.status !== 403) break;
    // 403 说明服务重启过、令牌已轮换；丢弃缓存重新获取，再试一次。
    serviceToken = null;
  }
  if (!response.ok) throw new Error('HTTP ' + response.status);
  return response.json();
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== 'api') return;
  request(message.path, message.options || {})
    .then((data) => sendResponse({ ok: true, data }))
    .catch((error) => sendResponse({ ok: false, error: String((error && error.message) || error) }));
  return true;
});
