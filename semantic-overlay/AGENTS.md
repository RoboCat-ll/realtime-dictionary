# Semantic Overlay Project Rules

## Product Contract

- The default workflow is one-shot message explanation: `Ctrl+Alt+K` arms one
  target gesture for 10 seconds, and the next click on a WeChat or QQ message
  reads the complete message and opens its passage explanation. The click is
  observed but never intercepted, so the source application keeps its normal
  click behavior. Ordinary clicks while unarmed do nothing.
- Prefer the complete UI Automation text and bounds for the clicked message. If the
  client does not expose them, locate the whole visual message bubble around the click
  locally and OCR that bounded bubble. Never infer a wrapped message from only the two
  endpoints of a text drag.
- Accept 1-1000 message characters. Never silently truncate. Accessibility text may
  be analyzed after the armed message click. Whole-bubble OCR starts the same
  one-shot analysis, remains visible/editable in the panel, and a user edit invalidates
  the pending result before a corrected retry.
- Analyze only the clicked message. Do not include adjacent chat messages, window
  titles, contacts, or other screen text in the request.
- One passage-analysis request returns the passage explanation and zero to five useful
  terms. A term must occur verbatim in the submitted passage; do not force a term count
  and do not render invented or fragmentary terms.
- Full-window OCR/highlighting is an explicit experimental compatibility feature. It
  must not be the default tray action, hotkey action, or product promise.
- The user experience is a background Windows utility, not a visible desktop app.
- Conversation results support two presentation modes. The default floating-assistant
  mode shows one non-activating draggable bubble attached to the target chat window;
  expanding it lists the current terms and opens the existing definition flow. The
  compatibility mode keeps the existing text-attached highlight rectangles. Switching
  presentation mode must not rescan, call a model, or discard the current result.
- The floating assistant must hide when the target window is minimized or leaves the
  foreground, reappear without re-analysis when the target returns, and never become
  the foreground window merely because the user clicks its bubble or term buttons.
- The validated product target is Windows desktop chat in WeChat and QQ. Both
  clients share one conversation workflow and acceptance standard; do not add
  client-specific product behavior unless a real compatibility failure requires it.
- Active selected-text lookup is the reliability baseline. Automatic highlighting
  is an optional experiment that must prove it restores understanding faster without
  excessive interruption. Meeting captions, schedules/reminders, and the browser
  extension are frozen experimental capabilities, not first-run or release promises.
- The persisted work mode is either conversation (default) or experimental live captions.
  Conversation mode remains event-driven and OCR-backed. Live-caption mode is
  explicit opt-in and transcribes captured meeting audio; it must never infer
  speech from screen OCR. `Ctrl+Alt+G` ends either mode.
- Live captions prefer Windows process-loopback capture for the selected meeting
  process and its children on supported systems. Full-system output is an
  explicit compatibility source and an activation-failure fallback; the tray
  must always show which source is actually active. Known-incompatible clients
  may start in compatibility mode rather than presenting a false isolated state.
  Microphone capture remains out of scope and default-off.
- Silence is filtered locally and is never uploaded or turned into caption text.
  Speech is segmented into bounded WAV chunks. Segmentation keeps a short
  pre-roll so the first syllable is not lost, adapts its gate to the observed
  noise floor, and rechecks WAV energy in the local service before upload.
  A stopped or replaced session
  cancels queued work, ignores late responses and clears the current lyric.
- Speech chunks may queue only within a small fixed bound. Preserve arrival
  order inside that bound; if it overflows, discard the oldest not-yet-started
  chunk rather than allowing unbounded latency or memory growth.
- Empty, punctuation-only, tag-only, exact-repeat, and formatting-only ASR
  results never create lyric/history entries or trigger DeepSeek analysis.
- Every active caption status must name the bound meeting window separately
  from the real audio source (`仅会议进程` or `全系统声音`);
  never imply that binding the overlay window isolates its audio.
- Speech recognition is a distinct operation from text analysis.
  The current beta reuses an explicitly configured SiliconFlow endpoint/key for
  transcription and explanations. TypeSafe Jev has its own validated configuration
  entry and is used only for structured highlight judgments. Audio is sent
  only during an explicitly active meeting session, never saved automatically,
  and never written to logs.
- Starting the tray host or reading `/health` must never perform a billable
  model inference. A real validation call is allowed only when the user
  explicitly tests a new key or launches an explicitly marked live-model test.
- Automated regression and soak tests must use local fixtures by default.
  Access to a paid endpoint requires an explicit live-billing switch and a
  bounded call budget; stress duration alone never authorizes paid traffic.
- The bounded Jev desktop diagnostic must preflight `/health` and trigger no
  hotkeys unless the active service reports `analysis_provider=typesafe` and
  `analysis_mode=jev`; a missing or different provider aborts the diagnostic.
- With a generative API key configured, full analysis reads the original sentence
  context and discovers/selects concepts in one request, without local candidate
  hints or a candidate-presence gate. Jev is compatibility-only when no generative
  key exists; never serially call both providers. Health must report this routing.
- Sentence discovery requests only verbatim concept strings; local code locates
  spans. On SiliconFlow, default to the validated fast lookup-model preset for
  analysis unless explicitly overridden. Generative background analysis has a
  bounded 10-second default deadline (not a first-visible latency promise).
  Existing local previews stay visible. Local action extraction remains available.
- Model analysis uses a bounded in-memory exact-text cache, skips only a closed
  set of clearly mundane greetings/acknowledgements, coalesces duplicate work, and enforces a
  per-hour request ceiling. Reaching the ceiling must preserve local highlights
  and report `local_budget`, never go blank or silently reset the limit.
- Existing screen-caption OCR is compatibility-only diagnostics and must not be
  exposed as the live-caption input. Rapid transcription updates may refresh
  local terms immediately, but text-model refinement must be throttled.
- Live-caption mode presents the recognized current line and previous committed
  line as a desktop-lyric overlay: transparent background, no activation, and
  mouse pass-through. It must never introduce an opaque caption rectangle.
- The lyric layer anchors above the recognized source caption where space permits,
  not at a fixed percentage that overlaps captions in resized windows. Never show
  the same text as both the previous and current line during partial revisions.
- Caption history is session-memory only by default, bounded to the latest 5000
  committed lines, and never written to disk automatically. Partial ASR updates
  replace the pending line; a line is committed after a short stability delay or
  replacement, so incremental fragments do not flood history or the model.
- Users can explicitly export retained captions as UTF-8 text using a save dialog.
  Export shows full dates and times, reports failures, and never writes automatically.
- Retained captions support explicit selected-text lookup with bounded sentence
  context, Chinese explanations, nested term links and back navigation even after
  the capture session ends. Hidden history windows must discard pending replies.
  Do not automatically send the whole transcript or query on every selection change.
- Historical captions can explicitly request task extraction for the selected
  line and open the same confirmation editor. These actions work after capture
  stops. Clearing records invalidates pending lookups and task extractions.
- History explanations must be scrollable, retain bounded context centered on
  the selection and ignore duplicate transcript refreshes while reading.
- After a model lookup times out, return the local Chinese fallback immediately;
  do not append additional public-network requests to an exhausted lookup deadline.
- Local term highlighting may update from each OCR result. Model refinement runs
  only for a committed stable caption and never waits until the meeting ends.
- Live-caption model refinement sends only the selected caption line and its OCR
  boxes, never every word from the surrounding scan region. If a refinement is
  already running, retain only the newest committed line for the next throttled
  request; never resend the same committed generation.
- In conversation mode, `Ctrl+Alt+K` arms exactly one message selection for 10
  seconds. The next click or deliberate drag inside a foreground WeChat or QQ
  window consumes that armed state; ordinary clicks while unarmed do nothing.
  Expired or cancelled armed states must not start OCR or a model request. The
  same shortcut may start captions only in the explicitly enabled caption
  experiment. Full-window highlighting starts only from the explicit
  experimental tray command.
- `Ctrl+Alt+G` disables the current highlight session.
- Moving a target window must move existing highlights without OCR.
- Resizing, scrolling, or changing target content must trigger a debounced refresh.
- Before repeating desktop OCR, compare a low-resolution visual fingerprint of
  the selected scan region. Ignore tiny animation/caret differences, reuse the
  current highlights when visually unchanged, and rescan when text materially changes.
- Top-level window movement must follow through native location events; polling is fallback only.
- Scrolling may retain highlights only while local pixel tracking confidently
  measures their content displacement. Clip each term to the selected viewport;
  uncertain tracking and resize invalidate stale coordinates. Never infer pixel
  displacement from wheel notches. Tracking frames stay in memory, no model calls.
- Generic accessibility/name/value notifications only schedule a visual probe;
  they are not proof that text moved. Probe unchanged displayed content without
  hiding highlight windows. Explicit resize invalidates immediately; scrolling
  first attempts bounded local tracking, then falls back to a debounced rescan.
- Only the final scan captured after content settles may render highlights.
- An OCR frame whose reconstructed text is empty or whitespace-only is a normal
  transition frame. Return an empty successful local result without calling
  `/analyze`; it must not become an HTTP 400 or trigger the fallback OCR engine.
- In conversation mode, a settled OCR frame may render a deterministic strong
  subset immediately. This subset must use the same strict mundane-term and span
  filters as the final result; it must never be the broader local candidate set.
  While model selection is pending, keep those stable terms visible and keep the
  compact scanning indicator. A valid model result may only append newly approved
  terms for that same generation; it must not remove, reorder, or reposition the
  already visible stable terms. Repeated scans of visually unchanged content reuse
  the completed progressive set.
- Conversation refinement is single-flight with one latest-frame slot. Content
  changes while a refinement is running replace the queued frame; completion of
  the older request must immediately start the latest queued frame. Never leave
  changed content permanently at a provisional or stale result.
- Each conversation request retains its own OCR fallback, including coordinates.
  A timeout, null/invalid response, or disconnected backend must end the pending
  state using that fallback. Content invalidation advances the generation for
  both OCR and model work and invalidates the visual cache. Empty frames clear
  any queued work. Stale completion must never restore previous text.
- Highlight density is user-selectable and persisted: concise (8), standard
  (15, default), or detailed (22). Every level ranks by likely reading
  difficulty; detailed mode still must not mark ordinary English or daily words.
- Selection must be deterministic after model output. A closed mundane-term
  filter removes standalone common units, time quantities, generic UI words and
  ordinary operation words in every density. Compounds such as `毫秒级延迟` may
  remain when the whole phrase is a genuine concept. During one backend process,
  remember accepted normalized concepts for 30 minutes and reapply them when the
  exact term reappears in later OCR frames at the same density. Never persist or
  upload this decision memory, and never stabilize rejected schedule candidates.
- Scroll tracking samples a broad conversation-content strip and evaluates its
  left, center, and right lanes so alternating chat bubbles are not missed. Pixel
  capture and displacement matching run off the UI thread, with at most one probe
  in flight. The UI applies only a confident consensus displacement; isolated
  ambiguous frames retain the current clipped highlights briefly, while repeated
  uncertainty triggers a settled OCR refresh. Probe cadence is not a guaranteed
  display frame rate and verification must measure the complete capture, matching,
  window-positioning and compositor path rather than capture time alone.
- During scrolling, each term remains clipped to the selected conversation
  viewport. Terms crossing an edge disappear by their own visible intersection;
  one uncertain sample must never clear the whole highlight set.
- Large WeChat, QQ, DingTalk, Feishu/Lark, WeCom, and ChatGPT/Codex windows scan the central
  conversation region by default using a proportional heuristic, not actual UI
  element boundaries. It can miss text or include chrome with unusual layouts;
  retain the explicit full-window override and disclose this limitation.
  Highlight coordinates must be translated back to full-window coordinates.
- The user can persistently override scan scope between automatic chat-content
  priority and full-window OCR; changing it invalidates the visual fingerprint
  and refreshes the active target.
- A user may explicitly frame a conversation region in a normal screenshot
  preview dialog. Keep this selection in host-session memory for that window;
  map normalized coordinates on moves/resizes, offer reset, and never present
  a full-screen opaque selector. Reflow may require adjusting the selection.
- A manual framed region is authoritative for the current window. It uses the
  native OCR path even when that window is browser-based; the automatic browser
  adapter must not silently override an explicit frame.
- The scope menu must show the effective manual selection, mutually exclusive
  with automatic/full scope. Log only selection dimensions, not screen contents.
- While an enabled foreground conversation has trackable highlights, probe local
  movement independently of wheel notifications. Before rendering delayed model
  coordinates, verify the captured frame is still current; never anchor stale
  coordinates to a freshly captured tracking baseline.
- A highlighted term can be locally ignored from its context action. Ignored
  terms never render, are stored only in the current user's profile, and can be
  restored from the tray/extension settings. Do not send feedback lists to a
  remote model or write ignored terms to diagnostics logs.
- Windows OCR conversation highlights learn a conservative local familiarity signal. A
  concept counts as an unclicked exposure at most once per explicit highlight
  session and only after it remained visibly available for several seconds.
  Four completed unclicked sessions suppress that normalized term. Clicking a
  concept immediately clears its unclicked score and protects the current
  session from counting it. Calendar candidates never participate. The feature
  is user-toggleable, has a one-click reset, stores only normalized terms and
  counters in the current user's profile, and never sends feedback to a model.
- Familiarity normalization is case-insensitive and ignores whitespace so
  variants such as `WorkBuddy` and `work Buddy` share one decision while
  punctuation remains significant for `C`, `C++`, and `C#`.
- Every conversation highlight session has an ephemeral analysis-context ID.
  Accepted-term memory and exact-text caches are scoped to that ID and density,
  so a decision can remain stable through overlapping OCR frames without leaking
  into another chat or a later session. Previously accepted terms take priority
  at the density limit. Model, local-preview, no-key, budget and failure paths all
  apply the same exact-span validation, mundane-term filter and overlap rules.
- No implementation may create an opaque full-window overlay.
- A highlighted term is clickable and opens one nearby, non-activating definition popup.
- Definition lookup is progressive. The card must open immediately with a local
  glossary/structural result or an honest context-first placeholder; it must not
  remain a blank spinner while a provider request is pending. The local stage
  never calls a model or public network. Deterministic app-shortcut results are
  final. Every ordinary explicit lookup automatically starts the configured
  online explanation request; bundled-glossary and structural text are preview
  only, never a reason to require a second AI button click. A late response may
  replace only the preview for the same lookup generation.
- Short dictionary explanations use a dedicated fast non-thinking lookup model
  when the configured provider is SiliconFlow; do not spend the flagship model's
  latency on one- or two-sentence definitions. The model name must be visible in
  health/status output and overridable without changing the speech or structured
  highlight engines. Other providers continue to use their configured model.
- Whole-message explanation uses its own fast non-thinking selection model when
  the configured provider is SiliconFlow. It must be independently overridable
  and reported by `/health`; changing it must not alter dictionary lookup,
  sentence concept discovery, TypeSafe judgment, or speech. A successful model
  response retains source-exact deterministic local terms that the model omitted,
  capped by the same five-term limit.
- Definition lookup carries a bounded local sentence context around the term.
  Cache keys include both normalized term and context so meanings from unrelated
  conversations cannot contaminate each other. Logs record the term only, never
  the surrounding sentence.
- Definition text may contain up to three levels of clickable related terms; the popup provides a back action.
- Lookup results are cached for the lifetime of the host process.
- A configured model timing out or rejecting a lookup must fall back to the
  Chinese local explanation without additional network waiting. The fallback remains
  retryable and a transient failure must not produce an empty definition card.
- Lookup failures disclose a sanitized category (timeout, authentication, rate
  limit, network, invalid response, or provider error), never raw provider text.
  Recognize full keyboard chords and their constrained OCR confusions locally;
  do not label arbitrary unknown terms as products or invent app-specific actions.
- Model-backed definition cards provide one explicit "换个解释" action. A retry
  bypasses both client and server caches, replaces the current card without
  consuming a recursive-history level, and uses the bounded context plus the
  previous explanation only as untrusted reference material. The action is
  hidden when no model is configured, and logs must not record either context
  or the previous explanation.
- Clicking outside the interactive overlay, switching targets, or clearing the session closes the popup.
- `/analyze` returns two independent collections: `entities` for knowledge
  concepts and `actions` for actionable candidates. A calendar candidate must
  include an exact source span, the original time phrase, a concise draft title,
  confidence, and `needs_confirmation=true`; ambiguous dates stay unresolved
  instead of inventing a year or timezone.
- Knowledge terms use a deterministic normalized-term color, shared across native
  and browser surfaces. Case and whitespace variants share a color; do not strip
  punctuation (C, C++ and C# must remain distinct). Blue is reserved for schedules.
- Microsoft calendar access uses the user's registered public-client app and
  delegated Calendars.ReadWrite via device login. Tokens stay in process memory;
  no client secret or shared third-party client ID. Read only the default calendar
  time window; expand recurrence through calendarView and follow bounded pages.
  Creating an event requires explicit confirmation and a fresh conflict check;
  changed conflicts require another review. Use transactionId for safe retries,
  do not send invitations, and report success only with a returned event ID.
- Calendar candidates use a blue highlight distinct from knowledge term colors.
  Clicking one opens the reminder confirmation editor directly. The editor
  carries forward every reliable title/time/offset field from analysis, lists
  all unresolved fields together, and requires a title, full start/end dates
  and times, and a confirmed UTC offset.
  Only explicit confirmation may export an ICS file; export does not mean the
  event has been imported into a calendar. Microsoft writes use the separate
  explicitly confirmed workflow above; no invitations.
  Missing years and end times remain empty. Identical confirmed content uses a
  stable UID; importing software ultimately controls duplicate handling.
- `/calendar/export` is token-protected, validates every field and a strict
  boolean `confirmed`, and returns an RFC 5545 file in JSON without storing or
  logging event contents. `calendar_export.py` owns validation and serialization.
- Local calendar workflow: users explicitly import an ICS snapshot and check
  a completed draft before confirming export. `/calendar/check` is authenticated,
  read-only, and never calls a model or saves imported calendar contents.
  Report overlap, duplicate events, and unsupported entries separately. Never
  claim availability when recurrence, floating time, or unsupported timezones
  prevent a complete check. Edits invalidate the UI check and require rechecking.
  Imported calendars are limited to 1 MiB; this checks a snapshot, not live accounts.
- `/calendar/clarify` accepts the original time phrase and explicit supplemental
  text to build an editable proposal (Chinese date/time, duration, Beijing time
  or numeric UTC offset). Never infer the current year or a default duration.
  Missing or ambiguous fields stay empty; clarification never exports an event.
- Local mode may extract only high-confidence schedule language containing both
  a time expression and an action cue. A bare date or time must not become a task.
- The primary task outcome is a local reminder, not a cloud calendar write.
  After the user completes title, start, end and UTC offset, they can choose a
  lead time and explicitly create one reminder. Persist reminders only below the
  current user's RealtimeDictionary profile, never in the install directory.
- The tray host checks reminders locally. At the due time show one compact,
  topmost, non-full-screen reminder card with an audible cue and explicit
  `知道了` / `10分钟后提醒` actions. Never create an opaque screen overlay.
  Dismissed reminders are removed; snoozed reminders are persisted. A restart
  may surface reminders overdue by at most 24 hours, but never old stale items.
- Creation must detect identical title/start reminders and validate full dates,
  end-after-start, UTC offset and lead time without a provider call. The UI must
  state that the tray utility has to be running for an on-time notification.
- A successful creation must become an explicit completed state: disable repeat
  creation, show the exact reminder time, and offer a direct way to open the
  local reminder list. Outlook and ICS controls remain visually secondary so
  they do not interrupt the main identify-understand-confirm-remind path.
- Microsoft Graph and ICS remain optional calendar paths. Neither is required
  to use local reminders; do not make Azure registration part of first-run UX.

## Security & Privacy Contract

- Requests to the exact HTTPS SiliconFlow mainland API host use a direct
  connection when no explicit HTTPS_PROXY/ALL_PROXY environment setting exists.
  This avoids silently inheriting a broken Windows proxy. Other providers retain
  system routing. Never disable TLS verification or change system proxy settings.

- The local service binds only to `127.0.0.1`, and the port is fixed at `8877`:
  the native host and the browser extension both hardcode it, so `config.json`
  must not offer a port option (the server ignores and warns about one).
- The native host accepts the analysis service only when `/health` and `/session`
  identify `realtime-dictionary` with the exact supported protocol version. A stale
  build or unrelated process on port 8877 must produce an explicit error instead of
  being treated as a healthy backend. Health/status responses may expose provider
  and model names but never tokens or credentials.
- The server generates a random token at startup. `/selection/analyze`, `/analyze`, `/lookup`, and
  every `/browser/*` endpoint require the `X-RealtimeDictionary-Token` header.
- The token is handed out only by `GET /session`. Requests with a non-loopback
  `Host` header are rejected (DNS-rebinding defense), and CORS responses echo
  only `chrome-extension://` origins, so ordinary web pages can never read the
  token or any endpoint response. Callers fetch `/session` and retry once on 403.
- API-key setup must validate the candidate key and configured model before
  saving. Applying a validated key gracefully restarts only the local analysis
  service; the tray host and global hotkeys remain active.
- Provider presets must favor the user's chosen quality baseline after it passes
  the project's terminology, false-positive, calendar-candidate,
  Chinese-explanation, and JSON-output checks. As of 2026-09-11 the SiliconFlow
  preset is `deepseek-ai/DeepSeek-V4-Flash`. Cost control comes from sending only
  the selected stable caption line, suppressing duplicate generations, and
  keeping at most the latest queued line; do not silently replace the selected
  model with a cheaper model. Availability and pricing must be rechecked before
  a later release rather than assumed permanent.
- Scanned text is sent to the local service; when an API key is configured it is
  forwarded to the model provider. User-facing docs must state this.
- Conversation screenshots needed by the Windows OCR bridge are temporary files
  below the current user's temporary directory and are deleted immediately after
  OCR completes. They must never be retained in the project or installation tree.
- Live-caption mode must disclose that foreground captions can be sent repeatedly
  while the mode is active. It never scans a background window.
- The browser adapter scans only from the explicit experimental scan command by default.
  Automatic rescans require the user to enable "auto follow" in the extension
  popup, and then send only viewport text with at least 8 seconds between calls.

## Structure

- `native-host/`: Windows tray host, hotkeys, tracking, small highlight windows,
  adaptive audio segmentation, and the process-loopback activation bridge.
- `native-host/windows_ocr.ps1`: thin bridge to the built-in Windows OCR engine.
- `native-host/diagnostics/`: standalone visual targets used to verify follow behavior.
- `ocr_service.py`: fallback OCR service started only when Windows OCR fails.
- `server.py`: term analysis and lookup service.
- `outlook_calendar.py`: delegated Microsoft calendar login/check/create; tokens
  and review tickets stay in memory. `OUTLOOK_SETUP.md` documents registration.
- `../browser-extension/`: optional browser DOM adapter that polls the local service command channel.
- `installer/`: source and build script for the single-file per-user Windows setup executable.
- `tests/`: offline regression tests for term ranking, offsets, and Chinese-only fallbacks; tests never call a model provider.
- Runtime logs use `_native_host.log`, `_ocr_service.log`, and `_server.log`.

The only user-facing launcher is `start.cmd`; it selects PowerShell, builds the
native host when needed, removes only stale host/backend processes whose paths
belong to this project, and starts the tray utility. `start.ps1` is the
PowerShell implementation behind it.
- First launch may show one concise tray notification. The tray menu must always
  provide an in-app Chinese help item; routine launches remain background-only.

## Engineering Rules

- Highlight windows must be limited to term rectangles; only those rectangles may intercept clicks.
- Definition lookup must be asynchronous and must never block target tracking.
- Python code-point offsets must be converted to UTF-16 before native rendering,
  then validated against the exact source substring. Reject mismatched spans.
- Ctrl+Alt+D explicitly looks up selected text using Windows accessibility, with
  an editable text fallback when the application does not expose its selection.
  Never replace clipboard contents or intercept ordinary unhighlighted clicks.
- Clipboard text may be read only after the user explicitly clicks `粘贴并解释`
  in the direct-lookup window. The clicked-message flow never substitutes
  clipboard text for an unreadable selection; it shows editable region OCR instead.
  Never poll the clipboard, and never replace its contents.
- The clicked-message panel is meaning-first: show the whole-message explanation
  before the sentence and term controls. For `bubble_ocr` or bounded region OCR,
  the same single model request may propose a corrected sentence, but the backend
  accepts it only when a conservative punctuation-insensitive similarity check
  proves it is a small OCR repair. It may restore at most a tiny, context-unique
  Chinese omission when the same message makes the missing one-to-two-character
  word unambiguous; it must preserve every readable source fragment and never
  paraphrase. Accessibility-exact text is never rewritten.
- Render accepted corrected text as the visible sentence and make each returned
  exact sentence term clickable in place. A term annotation must not hide the
  whole-message explanation. Keep raw OCR editable behind an explicit correction
  control; editing invalidates every pending result and requires a new request.
- A model-proposed canonical lookup term may replace the editable query only
  after the local service verifies that it is a small OCR-like correction of
  the submitted text. Unrelated model renaming must be discarded.
- A user-enabled selection toolbar may appear after a deliberate text drag,
  independently of a highlight session. It is small, non-activating and dismisses
  on another click, scrolling, target changes or timeout. Reading a selection is
  local; only clicking the explanation action sends text to the provider.
- If accessibility cannot read a selection, the toolbar offers local OCR of the
  bounded drag region. Show editable recognized text before a provider request;
  never pretend that a drag rectangle exactly equals an application's selection.
- Clicking a term presents feedback before familiarity persistence; local
  learning writes must not synchronously delay the lookup card.
- Keyboard chords accept `+`, ASCII/full-width hyphens and common OCR modifier
  confusions. Correct a trailing `O/0` only when the remaining structure is an
  otherwise valid chord key (for example `Ctrl-Alt-DO` -> `Ctrl+Alt+D`). Show
  the canonical chord in the definition and resolve known product shortcuts
  locally instead of sending malformed OCR text to a model.
- ASCII knowledge-term spans must start and end on token boundaries. Model or
  OCR output must never highlight a suffix such as `PT` inside `GPT`. Standalone
  two-letter uppercase candidates are rejected unless they belong to a small
  explicit technical allowlist such as `AI`, `UI` or `VR`; arbitrary OCR
  fragments such as `PT` and `DO` never become highlights. A fragment inside a
  structurally valid keyboard chord may expand only to the complete source
  chord and must not remain as an independently highlighted component.
- Keep screen coordinates physical-pixel aware across DPI and multiple displays.
- Never kill unrelated Python processes. Stop only PIDs started by this project.
- Do not store API keys in source control or logs.
- Prefer local term rules when no API key is configured.
- Candidate extraction may suggest terms, but only high-confidence candidates render without a model; ordinary words must remain untouched.
- The installer is per-user and may write only below `%LOCALAPPDATA%\RealtimeDictionary`
  plus the current user's Desktop/Start Menu shortcuts. It must never bundle or
  copy `config.json`, API keys, or logs.
- Upgrade may stop only `SemanticOverlay.exe` and bundled Python processes whose
  executable paths resolve inside the exact install directory.
- The native tray host must hold a per-user named mutex so shortcuts and the
  launcher cannot create duplicate tray instances or duplicate hotkey owners.
- Uninstall requires an explicit confirmation dialog and validates the exact
  default install directory before recursive deletion. `%APPDATA%` user settings
  remain unless the user separately requests their removal.

## Verification

- Offline regression: `python -m unittest discover -s tests -v`
- Compile: `pwsh -File native-host/build.ps1`
- Python syntax: `D:\Dev\anaconda\python.exe -m py_compile ocr_service.py server.py`
- Service test: POST a passage to `/selection/analyze`, sample text to `/analyze`, and a term to `/lookup` with the token from `GET /session`; validate source-exact terms, entity offsets, and explanation output. Without the token all three endpoints must return 403, and a request with a foreign `Host` header must return 403.
- Browser bridge test: POST `/browser/trigger` (with token), then verify an installed extension acknowledges the generation.
- Runtime: confirm `Ctrl+Alt+K` and `Ctrl+Alt+G` register successfully.
- Visual: verify no window larger than an individual term rectangle is created for highlighting.
- Caption stress: `CaptionTarget.exe` accepts `CAPTION_TEST_DURATION_MS` and
  `CAPTION_TEST_STRESS=1`; use them to keep one caption session active while the
  target repeatedly moves and resizes. Record the physical monitor count and DPI
  separately—one monitor cannot count as multi-monitor acceptance.
