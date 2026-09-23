# Browser Adapter Rules

## Product contract

- This is an optional Chrome/Edge MV3 adapter for the single `semantic-overlay` tray host.
- The user still starts only `semantic-overlay\start.cmd`.
- The content script may read visible page text and draw only term-sized, fixed-position highlight buttons.
- It must never replace page content or create a full-page opaque layer.
- All analysis and lookup requests go to the local service at `127.0.0.1:8877` (port is fixed).
- Content scripts never talk to the service directly: every request goes through
  `background.js` (the MV3 service worker), which holds the token from `GET /session`
  and attaches the `X-RealtimeDictionary-Token` header. Web pages cannot reach the
  service themselves because the server only returns CORS headers for
  `chrome-extension://` origins.
- Browser scanning is a frozen compatibility experiment. A scan starts only from
  the tray's explicit experimental scan command after experimental features are
  enabled; the default `Ctrl+Alt+K` workflow is reserved for one clicked WeChat
  or QQ message and must not silently trigger a browser-wide scan.
- Automatic rescans on DOM mutations are opt-in (`chrome.storage.local.autoFollow`,
  toggled from the extension popup): they send only viewport text and keep at least
  8 seconds between calls. The popup page also discloses that scanned text is sent
  to the local service and, with an API key configured, to the model provider.
- The definition popup anchors to a live `Range` of the clicked term, so it follows
  scrolling instead of keeping a stale snapshot rect; nested term links keep the
  original anchor.
- Blue calendar candidates have an editable confirmation form. Full start/end
  dates and UTC offset are required. Explicit submit calls `/calendar/export`
  through the worker and offers an ICS download; never claim calendar import.

## Structure

- `manifest.json`: MV3 permissions (storage), background worker, action popup, content-script registration.
- `background.js`: service worker proxy; fetches and caches the service token, forwards `api` messages from content scripts, retries once on 403 after a server restart.
- `content.js`: polling bridge, DOM text extraction (full or viewport-only), term rectangles, and recursive definition popup.
- `popup.html` / `popup.js`: user toggle for automatic rescans plus the privacy disclosure.
- `styles.css`: term and popup presentation.

## Verification

- Validate `manifest.json` as JSON and run `node --check` on every JS file.
- Load the folder as an unpacked extension in Chrome/Edge (reload after updates).
- Enable experimental features, invoke the explicit compatibility scan from the
  tray, and confirm that it triggers one scan on a normal HTML page.
- Confirm page scroll/resize repositions term rectangles and the popup without reloading the page.
- With "auto follow" off (default), mutating the page must not cause any `/analyze` request.
- With "auto follow" on, chat-style page updates must produce viewport-only `/analyze` calls no more often than once per 8 seconds.
