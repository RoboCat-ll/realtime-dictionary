# Realtime Dictionary distribution

Current source: **0.20.4-beta**, authoritative value in `version.txt`.
This is a local beta development package, not evidence of company deployment or
clean-machine acceptance. Older packages do not contain the current source changes.

## Build locally

From this directory, after running the isolated regressions:

```powershell
pwsh -NoProfile -File native-host/build.ps1
pwsh -NoProfile -File package.ps1
pwsh -NoProfile -File installer/build-installer.ps1
```

Default outputs are `RealtimeDictionary-portable-v0.20.4-beta.zip` and
`RealtimeDictionary-Setup-v0.20.4-beta.exe`. Existing output paths are deliberately
not overwritten; choose an unused versioned path for another build.

`package.ps1` defaults to the project's `runtime/` directory and includes that
self-contained Python runtime. Only explicitly passing `-RuntimePath ""` omits it;
recipients then need Python on PATH. The default package includes native GUI host,
Windows OCR helpers, backend modules and NAudio/license. The browser adapter is retired and excluded.
PaddleOCR fallback dependencies are optional and are not certified by this package.
The runtime must be reviewed before packaging; configuration, logs, captures,
credentials and developer diagnostics must not be embedded.

The installer embeds the ZIP and the same `version.txt`. It installs per user to
LocalAppData and creates shortcuts to the GUI executable. Routine shortcut startup
shows no command window. `start.cmd` is the explicit developer/portable launcher;
it can rebuild and therefore can show a console. A development shortcut starts the
last compiled executable, so rebuild before judging a source change.

## Recipient workflow

1. Install using Setup, or extract the full portable ZIP. Preserve its directory
   layout and runtime. Use the desktop shortcut or `native-host/bin/SemanticOverlay.exe`.
2. Open the tray's **模型设置** to configure and test your own authorized provider.
   New saved credentials use Windows CurrentUser DPAPI, bound to endpoint/slot.
   No usable credentials are bundled. Legacy plaintext credentials, if present,
   are reported separately and are not silently migrated.
3. In WeChat/QQ, press **Ctrl+Alt+K**, then click one message within 10 seconds.
   Its explanation appears first. Click a highlighted term, or select any unmarked
   word and click **解释**. Correct the source if OCR is inaccurate.
4. Pending requests show elapsed waiting. Editing invalidates previous results and
   enables a new explicit submission. Failed brief lookups offer **重试解释**;
   expanded definitions retain the brief when expansion fails.
5. A schedule candidate is **待确认**. Supply/check year, start/end, timezone and
   reminder lead time before creating a local reminder. It is not an automatic
   calendar write. Outlook/ICS actions remain explicit experimental alternatives.

## Billing and privacy

SiliconFlow permits only currently verified zero-price models. All of its DeepSeek
models and other paid models are blocked; unknown prices fail closed. Official
DeepSeek/other paid endpoints require their own authorization and bill their own
accounts. This package does not imply paid-model or speech-service authorization.

Only the requested message/term and its necessary context go to the configured
text provider after consent. Logs record status/latency, not request text or keys.
Consent and other preferences retain their existing JSON format; saves are now
serialized and atomically replaced. Audio captures playback, not the microphone.

Caption history is saved by date in `%APPDATA%\RealtimeDictionary\caption-history`
when enabled. Settings can disable future archiving; the history window offers
explicit confirmed deletion of a selected day's history. Message explanations are
not persisted as chat archives. Capture/transcription, whole-window scanning and
calendar integration remain experiments; their presence is not maturity evidence.

## Verification boundary

See `DEVELOPMENT_CLOSEOUT.md` for this build's evidence. Build/package inspection,
isolated diagnostics and offline HTTP tests do not replace real QQ/WeChat operation,
clean installation, meeting transcription or long-duration reliability checks.
