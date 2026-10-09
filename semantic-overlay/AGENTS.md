# Semantic Overlay Project Rules

## Model billing policy (2026-09-28)

- Speech defaults to `FunAudioLLM/SenseVoiceSmall`; the speech interface must
  still pass the live zero-price guard. Never substitute an unverified model.
  Saving a SiliconFlow speech credential must preserve the separately bound
  official DeepSeek text credential and endpoint.

- Follow `C:\Users\17863\AGENTS.md`, section `硅基流动费用约束`, before any
  API-backed action or test. SiliconFlow permits only currently verified free
  models; all DeepSeek variants and other paid models are forbidden. No paid
  fallback, including speech/vision helpers. Unknown pricing blocks the request.
- Official DeepSeek text requests remain within existing user authorization.
  Inspect actual endpoint/model/overrides before launching old configurations.
  Documentation updates alone do not implement a runtime network guard.

## Accuracy refinement contract (2026-09-30)

- OCR correction must not insert actors or date/time qualifiers absent from the
  captured source. Preserve uncertainty rather than inventing an appointment.
- Initial float visibility repair must respect intentional hiding on foreground
  loss/minimize and obsolete OpenText generations. It must not revive old cards.
- Installer verification must accept the current browser-free package, reject
  missing production dependencies and version mismatch. Package verification is
  not proof of clean-machine installation or update rollback.

- Local OCR repairs must not consume a recognized whole technical token together
  with its neighboring English words. Join only bounded whitespace/dot OCR gaps,
  preserve identifier boundaries and numeric suffixes, and do not fuzzy-correct
  short ordinary words. URLs, addresses and paths are not repair targets.
  Model OCR edits must also preserve negation/cancellation markers; a small edit
  distance is not permission to change whether an event will happen.
- Prefer complete passage concepts over nested-only substring terms. Keep a short
  term if it also occurs independently elsewhere in the same message. The final
  highlighted list remains source-grounded, deduplicated and limited to five;
  explicit selected-word lookup stays available for omissions.
- Verify these changes on synthetic positive and negative cases without provider
  requests. Offline correctness is not a measured real-client accuracy increase.

## Jev retirement (2026-09-30)

- Jev/TypeSafe is retired. Do not read its saved/environment credentials, offer its configuration UI, invoke its endpoint, or build/run its dedicated diagnostics. Shared OCR, candidate extraction, local fallback and generative analysis remain supported. Existing user credential files and historical evidence are not rewritten or deleted.
- Analysis uses the configured generative provider, otherwise explicitly labeled local results. Current documentation and UI previews must reflect this routing.

## Caption endpoint refinement (2026-10-06)

- After at least three seconds of buffered speech/context, a quarter-second
  local quiet pause may end the segment before its hard eight-second cap.
  Shorter buffers retain the existing seven-tenths-second endpoint, avoiding
  fragmentation on brief hesitation. This changes segmentation, not vocabulary.
- Compare against the existing one-second-overlap version using the same
  video and reviewed subtitle reference. Record insertions as well as omissions;
  do not claim a universal accuracy or network-speed improvement from one video.

## Caption boundary accuracy contract (2026-10-05)

- A forced duration cut during continuous speech retains bounded audio context
  and stays active; do not reacquire a voice attack or discard the retained tail.
- Overlap metadata travels with the original chunk through retries. Trim only
  exact complete-word suffix/prefix matches for adjacent successful chunks that
  actually share audio; never trim across a dropped/failed chunk or a new session.
  Preserve deliberate repetition inside a chunk; do not force reference-video
  vocabulary into a general-purpose transcript.
- Accuracy comparisons must use the same video/reference and actual native
  segmentation before and after. Fixed non-overlapping clips are a separate
  benchmark, not proof of the old live segmenter's quality. Reference subtitles
  are not human-listened audio ground truth. Keep decoded audio in memory only.

## Product Contract

- Contextual understanding refinement (2026-10-08): sentence explanations
  translate essential jargon into concrete meaning while preserving speaker,
  recipient, negation and conditions; do not invent instructions for the reader.
  Explicit word lookup retains up to the complete 1000-character selected
  message, never reads neighboring messages, and treats quoted instructions as
  data. Uncertain names remain uncertain. Calendar candidates require definite
  arrangements; cancelled, postponed-without-new-time and tentative clauses
  must not become actionable suggestions. Keep separate definite sentences.
  Validate semantic output with bounded synthetic live text probes separately
  from offline parsing tests; do not claim a measured client-wide accuracy gain.

- Core lookup refinement (2026-10-06): active word lookup uses one brief
  request after deterministic shortcut resolution, without a preliminary
  server glossary request. Refresh failure preserves the same query's prior
  model definition, with an explicit failed-refresh notice and retry action;
  edited/new queries invalidate that protection. Failed refreshes are not
  recorded as successful model results. Captions are outside this refinement.
  Sentence reanalysis follows the same rule: keep its successful model meaning,
  corrected source, term links and pending calendar candidates while refreshing
  the identical source. A failed refresh cannot replace them with a fallback.
  Editing or opening another message invalidates this protection. Status and
  operation metrics must disclose the unsuccessful refresh.
  The selected-word action row must fit its button's actual preferred height
  and margins at the current DPI; it must not acquire a scrollbar for one
  button. Keep its recovery hint short and non-wrapping.

- Five-area repair (2026-10-06): preserve accessibility source paragraph breaks
  and reject oversized sources rather than truncate. When bubble geometry is
  available, do not accept an accessibility parent spanning neighboring messages.
  Brief refresh keeps the same concise detail policy; provenance belongs in
  metadata, not appended definition prose. Enabling continuous lookup enables
  its message-click prerequisite; disabling that prerequisite disables continuous
  mode. Speech reconnection retries the identical WAV at most once within a
  shared 25-second deadline, without retrying auth/payment/policy errors or
  replacing the free provider. Calendar supplementation must not erase known
  fields; an explicitly chosen duration follows start edits until the user
  manually edits the end. Supplementation parses the current visible start/zone
  rather than reverting user edits to the source date. Calendar parse/check/export
  completions update controls on the UI thread even without a synchronization
  context. Verification must identify isolated versus live results.
  Real calendar continuation found relative dates/Chinese clock numbers missing:
  deterministic clarification must resolve explicit today/tomorrow/weekday dates
  against local current time, and Chinese numeric hours without inventing a time
  for vague deadlines. Timezone and duration still require explicit information.
  VideoMeetingTarget is a local playback fixture for the authorized video's audio,
  not a mock transcript. It exposes a window for the installed caption app to
  capture, closes its decoder/output on completion, and creates no audio files.

- Five-point refinement (2026-10-05): OCR-derived source must remain visibly
  labeled for review even after model correction. User-edited source is
  authoritative: do not run OCR correction again on it. Brief definitions lead
  with the context-specific meaning; unresolved abbreviations disclose ambiguity.
  Capture failures show the existing selected-word shortcut as a recovery path.
  Caption telemetry contains categorical outcomes/queue-to-result timing only,
  no audio or transcript. Measure retries as one chunk, never as multiple speech
  successes. If a calendar draft only lacks its end, prefer explicit duration
  selection over duplicate freeform fields; do not silently assume a duration.
  For mixed-language OCR, an installed English recognizer may re-read the same
  bounded bubble bitmap. Repair only a short uppercase token with an anomalous
  degree glyph when a unique English token occupies the same box and preserves
  its first/last letters. Never replace legitimate ACC, numeric temperatures,
  Chinese words or neighboring tokens; absence of English OCR is not an error
  and must not install a language pack or call a model.
  A user-provided video may be used by an isolated local audio diagnostic with
  the existing decoder and process-specific capture. Keep only aggregate timing
  and chunk counts; do not persist captured audio or claim this proves ASR.
  Such a diagnostic must stop its playback/capture on completion or failure.

- Unified audit repairs (2026-10-04): follow PROJECT_AUDIT_2026-10-04.md IDs.
  Repair existing chat, caption and reminder reliability; this authorization
  does not add browser capability or change credentials or reminder schemas.
  Failed reminder reads must preserve the original file and block writes;
  mutations become visible only after persistence succeeds. Failed preference
  saves may retain explicitly disclosed session-only values and must not escape
  UI callbacks. Clean up only temporary files created by the failing operation.
  The legacy hourly analysis quota is for compatibility scanning, not a promise
  about all model requests; label it accordingly until a separate budget contract
  is adopted. Preserve original captions and bounded memory during history repair.
  Compatibility scan text is capped at 20000 characters and lookup terms at 200;
  reject wrong field types and oversize values with 400, never silent truncation.
  Runtime logs retain at most a 2 MB current journal and one previous journal;
  anonymous interaction metrics use 5 MB journals with one retained predecessor.
  New captions append to a visible matching date/session without disk reload;
  retained history must disclose skipped files/lines and display truncation.
  Date-history reads run off the UI thread and apply only to their current
  request generation. Large archives use bounded forward pages (2000 entries,
  8 MB input work plus at most one bounded record per page) with explicit
  navigation, never an unbounded load.
  Separate the continuous-lookup toggle leaf from its trigger settings submenu.

- Browser integration is retired by user decision (2026-10-01). Remove the DOM
  extension, browser-specific desktop routing, /browser endpoints, extension CORS,
  browser-only diagnostics and package payload. Keep QQ/WeChat lookup and shared
  native OCR intact. No browser profile/settings or installed external files are
  changed; do not restore this capability as a fallback.

- Progressive UI refinement: default message cards show meaning and source; term
  details expand only on explicit lookup, and empty schedule sections take no
  space. Source highlighter is the primary term navigation; do not duplicate
  every highlighted term as a button. Manual selected-word lookup stays available.
- Manual-sized reading cards use remaining client height only as a content
  ceiling, never force short text to fill it. Meaning, source and fixed actions
  stay grouped at the top; long text may use more space before scrolling.
  Source may grow when meaning is short and source overflows. Automatic-size caps remain unchanged;
  short text does not acquire a scrollbar merely because a user resized. Keep
  fixed actions visible, preserve user size and never request analysis on resize.
- One-shot arming uses a separate nonactivating, mouse-transparent compact hint,
  never a routine tray balloon. It expires with the 10-second arm, clears when
  consumed/cancelled and hides if its source window loses foreground. Reading
  shows in that hint; results continue using existing model progress/retry UI.
  Compatibility scanning/caption status windows must not be reused or disturbed.
- The message explanation panel is a lightweight borderless float attached near
  the clicked message bubble (v2, 2026-10-01). It shows without stealing chat
  focus (ShowWithoutActivation + topmost via SetWindowPos), but it never uses
  WS_EX_NOACTIVATE: clicking, selecting text and editing must focus it normally.
  Escape closes it only while the float itself has focus (KeyPreview); no global
  Escape hook. Placement derives from the bubble rectangle and the per-monitor
  working area (right side first, flip left, clear the input zone, never cover
  the target bubble). While open, a 400 ms heartbeat: target move follows unless
  the user dragged the float; target resize preserves the reading card but
  invalidates its bubble anchor (no new OCR/model request); minimize/foreground-loss
  hides and restores it; target close closes it; observed foreground chat wheel
  scrolling detaches the bubble anchor and labels the retained message, without
  re-probing text. Scrollbar dragging and keyboard scrolling are not detected
  (the float has no pointer and its content is self-contained).
- Reopening the desktop shortcut must signal the existing instance, restore its
  explanation if available, otherwise show concise trigger instructions. Never
  launch a second host or make a model request merely to acknowledge startup.
- Term lookup and word detail render inside the same float instance as a word
  view with an explicit back link, never as stacked popups; returning restores
  the sentence view with source, meaning and reading position intact. Unmarked
  words stay explainable via selection (nearby action button or Ctrl+Enter).
  Schedule candidates keep their compact row and explicit confirmation entry in
  the sentence view; rendering candidates switches back from the word view.
- Async result application is always marshalled to the UI thread and re-checks
  the generation guard there (InvokeRequired path included), because an await
  continuation may land on a thread pool thread when no synchronization context
  exists. A user edit dims the stale explanation in place instead of clearing it,
  and late results must never overwrite edited content. Failure states keep an
  explicit retry action.
- Calendar confirmation summarizes known fields and presents missing fields;
  an explicit edit toggle reveals all fields. Preserve complete visible dates,
  inference notices, manual overrides and explicit creation confirmation.
- Caption-history translation uses the same independent text provider and credential as word
  lookup, matching the text-send consent; speech credentials are not a fallback
  for an explicitly invalid text configuration.
- A socket timeout during an armed response-header budget is classified as a
  header-budget timeout while the total deadline is still open, even if the OS
  timer returns just before the nominal budget. Preserve the global deadline
  and attempt cap; body timeouts do not acquire this retry classification.
- Caption-history query details stay collapsed until requested. Switching archive
  dates or explicitly switching to the current session invalidates old pending
  lookups/translations/tasks; incoming lines must still preserve reading position
  and selection. These local UI checks are not live sustained ASR acceptance.

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
- Bubble fallback must search nearby connected background components when the
  clicked pixel lies inside a closed glyph. Only a component enclosing the click
  may become the message crop; nearby messages must not be merged into it.
- Bounded message OCR may repair a verified split glyph only when adjacent OCR
  boxes share the baseline and together occupy one character width. Keep it
  classified as editable OCR, never accessibility-exact text. Do not guess
  missing numeric punctuation or globally replace source words.
- Model OCR correction must preserve numeric values and their decimal/time/date
  separators. Equivalent fullwidth punctuation and OCR whitespace may normalize;
  the existing isolated incomplete trailing-date exclusion remains bounded.
- One passage-analysis request returns the passage explanation and zero to five useful
  terms. A term must occur verbatim in the submitted passage; do not force a term count
  and do not render invented or fragmentary terms.
- Keep the clicked-message model response limited to fields the service actually uses.
  Schedule candidates remain source-grounded local extraction; do not ask the model to
  generate duplicate schedule fields. For OCR input, request corrected full text only
  when a small repair is needed; unchanged or accessibility-exact text must not be
  echoed back in the model response. Reuse only successful exact-message explanations
  in a short-lived, bounded process-memory cache; never persist chat text or cache a
  failure, and a user edit must use the edited text as its cache identity. The
  explicit `重新解释` action bypasses the cache and asks the model again.
- The explanation describes the message's meaning, not the OCR or correction workflow.
  A short ordinary message may need only one sentence; do not pad it with speculation.
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
  excessive interruption. Meeting captions and schedules/reminders are frozen experimental capabilities, not first-run or release promises.
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
- Starting live captions must disclose that captured speech is sent to the
  configured speech provider. The user may explicitly remember approval and
  suppress repeat prompts, with a tray option to restore the prompt. A visible
  compact nonactivating status near the bound window must distinguish startup,
  listening, transcription and failure; it must hide when the target is not in
  the foreground and must not imply a transcript has arrived before one does.
- Repeating `Ctrl+Alt+K` on the same active caption target must reveal the
  current status instead of restarting capture or repeating consent. To restart
  that target, the user explicitly stops with `Ctrl+Alt+G` before starting again.
- Silence is filtered locally and is never uploaded or turned into caption text.
  Speech is segmented into bounded WAV chunks. Segmentation keeps a short
  pre-roll so the first syllable is not lost, adapts its gate to the observed
  noise floor, and rechecks WAV energy in the local service before upload.
  A stopped or replaced session
  cancels queued work, ignores late responses and clears the current lyric.
- Speech chunks may queue only within a small fixed bound. Preserve arrival
  order inside that bound; if it overflows, discard the oldest not-yet-started
  chunk rather than allowing unbounded latency or memory growth. Retryable
  provider cooldowns must retain and retry the failed chunk itself before later
  queued speech, with a fixed per-chunk attempt limit. Newly captured chunks
  may queue only within the same bound. If a chunk is finally lost by retry
  exhaustion or queue overflow, append an explicit source-time gap marker to
  the local session archive and show it in dated history; never imply a
  complete transcript. Audio bytes remain in memory and are never archived.
- Active caption status must report the actual audio source, whether capture
  packets and speech-level input are arriving, and the age of the last
  successful transcript. An isolated process with no audio must be described
  as silent/missing input, not as a model timeout. Keep the small HUD concise
  and provide the fuller state in the tray.
- Keep the draggable audio-caption card inside the visible part of the bound
  window and its monitor working area, including when that window extends
  beyond a screen edge.
- Empty, punctuation-only, tag-only, exact-repeat, and formatting-only ASR
  results never create lyric/history entries or trigger DeepSeek analysis.
- Caption terminology repair may canonicalize only explicit, tested ASR
  mishearings when nearby speech context supports the intended term. It must
  not use open-ended LLM rewriting or fuzzy replacement of arbitrary words.
  Preserve the original ASR text with a changed caption in dated local history
  so users can audit the repair; leave uncertain phrases untouched.
- Every active caption status must name the bound meeting window separately
  from the real audio source (`仅会议进程` or `全系统声音`);
  never imply that binding the overlay window isolates its audio.
- Speech recognition is a distinct operation from text analysis.
  The current beta reuses an explicitly configured SiliconFlow endpoint/key for
  transcription and explanations by default. A separately configured text
  provider may serve clicked-message explanations and term lookups without
  changing the SiliconFlow speech endpoint/key or background analysis route;
  its endpoint, model, and credential must be saved as one validated, provider-
  scoped set and must never silently fall back to another provider's key.
  Choose the beta speech default from bounded
  English-speech latency and accuracy checks, allow an explicit speech-model
  override, and report the active model in health output. A file transcription
  API is not a streaming latency guarantee. Audio is sent
  only during an explicitly active meeting session, never saved automatically,
  and never written to logs.
- API credentials are provider scoped: a DeepSeek or generic OpenAI environment
  key must never override a saved SiliconFlow key for a SiliconFlow endpoint.
  Explicit per-user settings saved through the app take precedence over ambient
  endpoint, model and key environment variables. A saved key is usable only for
  its normalized full endpoint identity; matching provider-specific environment
  credentials fill missing keys only. A generic `OPENAI_API_KEY` may go to a
  custom endpoint only when that endpoint is explicitly supplied by the same
  environment configuration, never merely because it appears in a saved user
  or project file.
  Resolve the effective endpoint before
  selecting a fallback credential. Health may name the credential source but
  must never expose credential values, fingerprints or provider error bodies.
- Starting the tray host or reading `/health` must never perform a billable
  model inference. A real validation call is allowed only when the user
  explicitly tests a new key or launches an explicitly marked live-model test.
- Automated regression and soak tests must use local fixtures by default.
  Access to a paid endpoint requires an explicit live-billing switch and a
  bounded call budget; stress duration alone never authorizes paid traffic.
- With a generative API key configured, full analysis reads the original sentence
  context and discovers/selects concepts in one request, without local candidate
  hints or a candidate-presence gate. Without a generative key, use only labeled
  local rules. Health must report this routing.
- Sentence discovery requests only verbatim concept strings; local code locates
  spans. Use the configured model unless explicitly overridden; SiliconFlow
  requests must pass the free-only admission check. Generative background analysis has a
  bounded 10-second default deadline (not a first-visible latency promise).
  Existing local previews stay visible. Local action extraction remains available.
- Provider HTTP connections may be reused only after a complete, bounded response,
  scoped to origin, proxy/tunnel route and credential identity. Discard partial,
  expired or failed connections; retain certificate validation and redirect denial.
  By default text calls retry transient connection failures or HTTP 502/503/504 at most
  twice for official DeepSeek and once for other providers,
  within the original total deadline, without changing provider/model/credentials.
  Explicit diagnostic attempt overrides are also capped at three.
  For official DeepSeek text calls with at least a 4s total budget, reserve retry
  time by bounding non-final-attempt response headers to 2.5s; this does not shorten
  successful body generation. A header-budget timeout may retry with the
  remaining total budget and the same attempt cap. Discard that socket and record unknown usage;
  do not claim the original POST was unbilled or automatically retry elsewhere.
  Never retry authentication, billing-policy, malformed-output or exhausted-deadline
  failures. Record each physical attempt separately; interrupted usage is unknown.
  Log connection, submission, header/body timing and attempt count without content.
  Deadline workers must be bounded; expired workers must not send later attempts.
  Official api.deepseek.com and api.siliconflow.cn HTTPS requests default to
  direct routing when no explicit proxy environment variable is present; do not
  silently inherit the Windows system proxy for these domestic API hosts. Honor
  explicit proxy settings, match exact hostnames only and keep other routes intact.
  The <=1% user-visible request-failure target requires uncached live evidence and
  latency statistics; local injected-fault tests are not provider acceptance.
- Model analysis uses a bounded in-memory exact-text cache, skips only a closed
  set of clearly mundane greetings/acknowledgements, coalesces duplicate work, and enforces a
  per-hour request ceiling. Reaching the ceiling must preserve local highlights
  and report `local_budget`, never go blank or silently reset the limit.
- Existing screen-caption OCR is compatibility-only diagnostics and must not be
  exposed as the live-caption input. Rapid transcription updates may refresh
  local terms immediately, but text-model refinement must be throttled.
- Live audio captions present up to the two latest completed speech sentences
  in a compact rolling box over the bound window. The box must be readable,
  nonactivating, and confined to a bounded part of the window. Its header can
  be dragged and its corner resized without taking foreground focus; keep the
  position and size within the meeting window, persist explicit user layout
  changes separately from model credentials, and restore them next session.
  The initial position must clear common bottom playback controls. Existing
  OCR lyric overlays remain mouse pass-through. The audio box must not cover
  the full meeting picture or invent text while ASR
  is pending or retrying. Existing screen-caption OCR retains the transparent
  lyric presentation. Retain source-exact text in history and term lookup.
- Explicitly opening subtitle history from the tray or caption box must make
  its window visibly accessible above a topmost meeting player, including when
  the history is empty; closing history must not clear the session transcript.
- The lyric layer anchors above the recognized source caption where space permits,
  not at a fixed percentage that overlaps captions in resized windows. Never show
  the same text as both the previous and current line during partial revisions.
- The user has explicitly requested dated, locally retained caption history.
  Store committed transcript lines in per-session append-only UTF-8 records
  under `%APPDATA%/RealtimeDictionary/caption-history/YYYY-MM-DD/`; the folder
  date is the session start date. Never store audio, credentials, or model
  responses there, and never upload archived lines without an explicit lookup,
  translation or task action. The history window defaults to the current date,
  offers a date picker for previous sessions, and keeps session boundaries and
  line timestamps visible. Current in-memory history remains bounded to 5000
  entries; archive browsing must not be replaced by incoming live updates.
  A corrupt archive line must not prevent reading later valid lines. Do not
  delete archived files when clearing the current on-screen session.
  Partial ASR updates replace the pending line; a line is committed after a
  short stability delay or replacement, so incremental fragments do not flood
  history or the model.
- While live captions append to history, preserve the reader's selected text and
  scroll position when they are reviewing earlier lines. Follow new captions
  only when the reader is already at the bottom and has no active selection.
  Merely receiving or selecting captions must never trigger a translation call.
- Users can explicitly export retained captions as UTF-8 text using a save dialog.
  Export shows full dates and times, reports failures, and never writes automatically.
- Retained captions support explicit selected-text lookup with bounded sentence
  context, Chinese explanations, nested term links and back navigation even after
  the capture session ends. Hidden history windows must discard pending replies.
  Do not automatically send the whole transcript or query on every selection change.
- Caption history may translate one explicitly selected passage or the current
  caption line into Chinese on demand. Preserve the original transcript, reject
  overlong selections instead of silently truncating them, and show translation
  in a separate result area. Never bulk-send all retained captions implicitly.
- Historical captions can explicitly request task extraction for the selected
  line and open the same confirmation editor. These actions work after capture
  stops. Clearing records invalidates pending lookups and task extractions.
- A transient speech transport reset is retryable and keeps the active audio
  capture running with bounded cooldown/queue behavior. Permanent provider
  authentication or balance failures may stop capture and must say why.
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
  native OCR path; no adapter may override an explicit frame.
- The scope menu must show the effective manual selection, mutually exclusive
  with automatic/full scope. Log only selection dimensions, not screen contents.
- While an enabled foreground conversation has trackable highlights, probe local
  movement independently of wheel notifications. Before rendering delayed model
  coordinates, verify the captured frame is still current; never anchor stale
  coordinates to a freshly captured tracking baseline.
- A highlighted term can be locally ignored from its context action. Ignored
  terms never render, are stored only in the current user's profile, and can be
  restored from the tray settings. Do not send feedback lists to a
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
- Short dictionary explanations respect the explicitly configured text model;
  never silently substitute a paid preset. The model name must be visible in
  health/status output and overridable without changing the speech or structured
  highlight engines. Other providers continue to use their configured model.
- Whole-message explanation respects the configured text model. It may be independently overridable
  and reported by `/health`; changing it must not alter dictionary lookup,
  sentence concept discovery or speech. A successful model
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
  and native caption surfaces. Case and whitespace variants share a color; do not strip
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
  Candidate years stay empty until the editor's explicit local prefill step;
  its inferred year follows the next-occurrence contract below. Missing end
  times remain empty. Identical confirmed content uses a
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
  or numeric UTC offset). Unqualified month/day may prefill the next occurrence
  year as specified below, never a default duration.
  Missing or ambiguous date/time/duration fields stay empty; clarification never
  exports an event. An unspecified timezone defaults to Beijing (+08:00).
  Calendar confirmation must not ask users to enter or edit a timezone; retain
  the numeric offset internally for correct reminder/export instants. Explicit
  source offsets remain authoritative rather than silently changing an instant.
- Local mode may extract only high-confidence schedule language containing both
  a time expression and an action cue. A bare date or time must not become a task.
- The primary task outcome is a local reminder, not a cloud calendar write.
  After the user completes title, start and end, they can choose a
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
  the native host hardcodes it, so `config.json`
  must not offer a port option (the server ignores and warns about one).
- The native host accepts the analysis service only when `/health` and `/session`
  identify `realtime-dictionary` with the exact supported protocol version. A stale
  build or unrelated process on port 8877 must produce an explicit error instead of
  being treated as a healthy backend. Health/status responses may expose provider
  and model names but never tokens or credentials.
- The server generates a random token at startup. `/selection/analyze`, `/analyze`,
  and `/lookup` require the `X-RealtimeDictionary-Token` header.
- The token is handed out only by `GET /session`. Requests with a non-loopback
  `Host` header are rejected (DNS-rebinding defense). No CORS is provided and
  every request carrying Origin is rejected. Native callers fetch `/session`
  and retry once on token expiry.
- API-key setup must validate the candidate key and configured model before
  saving. Applying a validated key gracefully restarts only the local analysis
  service; the tray host and global hotkeys remain active.
- Provider presets must pass the project's terminology, false-positive,
  calendar-candidate, Chinese-explanation and JSON-output checks within the
  current billing policy. The historical SiliconFlow DeepSeek preset is no
  longer authorized. Use only verified free SiliconFlow models; quality loss
  never authorizes a paid fallback. Minimize repeated requests and recheck
  availability and pricing before API-backed verification or release.
- Scanned text is sent to the local service; when an API key is configured it is
  forwarded to the model provider. User-facing docs must state this.
- Conversation screenshots needed by the Windows OCR bridge are temporary files
  below the current user's temporary directory and are deleted immediately after
  OCR completes. They must never be retained in the project or installation tree.
- Live-caption mode must disclose that foreground captions can be sent repeatedly
  while the mode is active. It never scans a background window.

## Development closeout contract (2026-09-30)

- Keep `Program.cs` as the GUI entry point. Put context coordination in
  `OverlayContext.cs` and responsibility-based `OverlayContext.*.cs` partials;
  shared windows, tracking, familiarity and native interop have named root C# modules.
  `ServiceManager.*.cs` partials separate OCR, local HTTP and runtime lifecycle.
  Mechanical moves retain existing methods, fields and class identities.
- `RequestFeedback.cs` owns content-free error text and elapsed wait feedback.
  Editing a pending input invalidates its result and restores explicit submission;
  brief lookup failures have an explicit retry. Never start an extra model request
  just to update UI status, and never display exception bodies or credentials.
- `PreferenceStore.cs` serializes preference access and atomically replaces the
  same JSON format. It does not migrate credentials or change consent semantics.
- `request_validation.py` validates bounded UTF-8 JSON object request bodies before
  dispatch. Invalid length, malformed encoding and non-object JSON return 400/413.
- Launcher ownership requires an exact absolute backend script argument, ignoring
  case; substring matches must not stop backup scripts or test processes.
- `DEVELOPMENT_CLOSEOUT.md` records this bounded development milestone and its
  checks. It must not claim measured completion percentage or real-client acceptance.

## Structure

- `.local-asr/`: ignored, project-local Whisper feasibility environment. Keep
  its virtual environment in `venv/`, public model weights in `models/`, and
  private diagnostic results/logs in `results/`. Never include these in Git or
  distributable packages. Install dependencies only in this virtual environment;
  do not alter global Python, system CUDA, drivers, credentials or cloud routing.
  Local transcription uses only the user-designated video. Compare warm chunk
  latency separately from model loading and full-file throughput; without a
  reference transcript, do not report a word-error rate. Integrate into the app
  only after an explicit measurable feasibility result.

- `native-host/`: Windows tray host, hotkeys, tracking, small highlight windows,
  adaptive audio segmentation, and the process-loopback activation bridge.
- `native-host/windows_ocr.ps1`: thin bridge to the built-in Windows OCR engine.
- `native-host/diagnostics/`: standalone visual targets used to verify follow behavior.
- `media/`: public-facing demo media. Prefer synthetic test windows. A real
  chat capture requires the user's explicit authorization; retain only the
  selected message and application overlay, and mask contacts, adjacent chat,
  desktop background, credentials, and unrelated content before publication.
  Label screenshot composites and experimental behavior honestly. Keep GIFs small.
- `ocr_service.py`: fallback OCR service started only when Windows OCR fails.
- `server.py`: term analysis and lookup service.
- `outlook_calendar.py`: delegated Microsoft calendar login/check/create; tokens
  and review tickets stay in memory. `OUTLOOK_SETUP.md` documents registration.
- `installer/`: source and build script for the single-file per-user Windows setup executable.
- `tests/`: offline regression tests for term ranking, offsets, and Chinese-only fallbacks; tests never call a model provider.
- Runtime logs use `_native_host.log`, `_ocr_service.log`, and `_server.log`.

Desktop and installed shortcuts launch `native-host/bin/SemanticOverlay.exe`
directly so routine startup shows no console. A development-directory shortcut
uses the most recently compiled executable and does not build newer sources.
`start.cmd` remains the explicit development/portable build-and-start entry;
`start.ps1` implements source freshness checks and startup diagnostics behind it.
- First launch may show one concise tray notification. The tray menu must always
  provide an in-app Chinese help item; routine launches remain background-only.

## Engineering Rules

- Tray navigation: top level contains status, message explanation, manual lookup,
  caption history, explanation-model settings, Settings, Experiments, Help and
  Exit. Usage/privacy/caption persistence/familiarity belong under Settings;
  whole-window scan presentation/density/scope belong under Experiments.
  Reading archived captions remains available when experiments are disabled.
  Menu regrouping must preserve existing callbacks, preference values and hotkeys.
- Native source modules stay directly under `native-host/`: `TrayMenu.cs` owns
  tray construction; `MessageExplanationForms.cs` owns message/selection lookup
  windows; `MessageTextReader.cs` owns accessibility/bubble reading;
  `ServiceManager.cs` owns configuration and API coordination; its `Http`, `Ocr`
  and `Runtime` partials own transport, OCR and process lifecycle;
  `ServiceContracts.cs` owns response DTOs; `CaptionForms.cs` owns caption data
  and windows; `CalendarForms.cs` owns calendar confirmation windows.
  Preserve existing class names and namespace while extracting these units.
  The build compiles all root-level native C# files; freshness checks must include
  them. Diagnostics remain below `native-host/diagnostics/`, not in root sources.

- Employee-pilot hardening: every SiliconFlow inference must pass a runtime
  free-price check against the official pricing page before network submission.
  All DeepSeek variants are forbidden there; unknown, nonzero, stale or unreadable
  pricing fails closed. Never reroute a blocked request to a paid provider.
- Provider usage records contain only local request ID, provider/model/operation,
  timestamps, outcome, elapsed time and reported token counts. Missing usage is
  unknown, never zero. Do not log query text, provider bodies or credentials.
- New saved credentials use Windows CurrentUser DPAPI with endpoint/slot binding.
  An unreadable protected credential disables credential fallback. Legacy plaintext
  remains read-compatible with a warning; converting live saved secrets requires
  separate user approval. No automatic migration, plaintext backup or key rotation.
- Text uploads require an explicit first-use disclosure naming the effective
  provider. Caption saving is user-controllable and archive deletion requires an
  explicit date-specific confirmation. Do not delete existing archives in tests.
- Keep version.txt authoritative for server, native host and installer builds.
  Distributable installer payloads require the embedded Python runtime and must
  be checked for version consistency and exclusion of user settings/logs/secrets.
- New security and provider-policy modules live at the project root; regression
  tests stay in tests/ and native diagnostics in native-host/diagnostics/.

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
- The clicked-message panel's displayed sentence must remain selectable even
  when analysis proposes no terms. Dragging a word or short phrase there offers
  an explicit explain action using the current sentence as context; selecting
  text alone never calls a model. Previously recognized terms retain their
  visible emphasis and direct click-to-explain behavior. A missing highlight
  must not strand the user or require copying the whole message.
- A deliberate selection in the clicked-message panel offers a small nearby
  explanation button without sending a request. Mouse and keyboard selections
  retain the bottom action as a fallback; scrolling, moving, editing or leaving
  the panel dismisses the nearby action. Lookup uses the selected phrase and
  current message only, with no neighboring chat or clipboard read.
- Term annotations in this panel are brief and context-first. Existing passage
  annotations are reused; unmarked terms explicitly request `/lookup` with
  `detail=brief`. The default lookup detail remains `full` for existing clients.
  Only clicking `展开解释` requests additional detail with `detail=expanded`; collapsing/reopening an
  already successful expansion does not send another request. A late expansion
  cannot replace another term or edited message, and a failed expansion retains
  the readable brief explanation with an explicit retry action.
- Render accepted corrected text as the visible sentence and make each returned
  exact sentence term clickable in place. A term annotation must not hide the
  whole-message explanation. Keep raw OCR editable behind an explicit correction
  control; editing invalidates every pending result and requires a new request.
- The clicked-message analysis returns knowledge terms and actionable schedule
  candidates as separate collections from the same bounded request. A task is an
  intended action tied to an explicit time expression, not the word `任务` or a
  concept name by itself. Every candidate must retain an exact time span from the
  accepted visible sentence; uncertain or example-only wording must not be shown
  as a confirmed appointment. Show schedule candidates separately after the
  whole-message meaning and term controls. Clicking `添加日程` opens the existing
  editable reminder/calendar confirmation flow; never create anything on analysis
  alone. Calendar confirmation may prefill an unstated year using the next valid
  occurrence of the supplied month/day, including today even if the clock has
  passed. Show the complete inferred date and its basis for editing; explicit
  numeric/relative years override it, and historical framing must not silently
  roll into a future appointment. Missing end time and UTC offset remain empty.
  Opening an incomplete draft runs local clarification without a provider call
  or creating a reminder. Do not overwrite existing fields during initial prefill.
  Reject example framing such as `我打一个比方` even if the quoted sentence contains
  a valid time and meeting verb. Preserve dotted month/day input such as `9.30
  14:30` as the candidate's exact source phrase; `参加` plus a discussion event is
  actionable. Never transfer a time from an example or neighboring message into
  a real task. Calendar clarification may parse the dotted month/day, but may not
  invent an end time or UTC offset. Year defaults follow the above rule; invalid
  dates fail, and February 29 uses the next actual leap-day occurrence.
  When a timed example is excluded, the clicked-message panel must say it is an
  example and direct the user to a message containing the actual arrangement;
  do not show the generic no-task state as if task recognition failed. This
  reason is display-only and never creates a calendar candidate.
  A date and clock may be separated within one message clause by bounded event
  wording such as `10月8号导员要开班会，到时候下午5点记得到`. Treat them as one
  candidate only when the connector and actionable event are explicit and no
  second date intervenes. Keep the entire date-to-clock span source-exact for
  review, use a concise source-grounded title, and leave year/end/zone empty.
  Do not join times across sentence boundaries, examples, or neighboring bubbles.
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

- Caption transcription may have at most two in-flight requests. Buffer completed
  results by capture sequence and deliver in source order; a bounded same-chunk
  retry must hold its position. Dropped chunks advance the delivery sequence with
  an explicit gap, and stopping a session cancels buffered results. Keep the
  existing provider, consent, price guards and bounded pending queue unchanged.
- Speech transport has an explicit eight-second connection budget within its
  existing 25-second total budget. Report the observed timeout phase and elapsed
  time; an early connect/header timeout must not be mislabeled as 25 seconds.
  Permit one immediate retry only for a timeout before request submission, sharing
  the original deadline. Never retry authentication/payment/policy errors.

- Offline regression: `python tests/run_offline.py`. The runner must isolate
  `%APPDATA%` and provider credential environment variables before importing
  application modules so tests cannot bill a live account or print a saved key
  through a failing mock assertion.
- Compile: `pwsh -File native-host/build.ps1`
- Python syntax: `D:\Dev\anaconda\python.exe -m py_compile ocr_service.py server.py`
- Service test: POST a passage to `/selection/analyze`, sample text to `/analyze`, and a term to `/lookup` with the token from `GET /session`; validate source-exact terms, entity offsets, and explanation output. Without the token all three endpoints must return 403, and a request with a foreign `Host` header must return 403.
- Retired-browser regression: old `/browser/*` routes return 404 and web/extension
  Origin requests return 403 without CORS headers; native `/session` stays usable.
- Runtime: confirm `Ctrl+Alt+K` and `Ctrl+Alt+G` register successfully.
- Visual: verify no window larger than an individual term rectangle is created for highlighting.
- Caption stress: `CaptionTarget.exe` accepts `CAPTION_TEST_DURATION_MS` and
  `CAPTION_TEST_STRESS=1`; use them to keep one caption session active while the
  target repeatedly moves and resizes. Record the physical monitor count and DPI
  separately—one monitor cannot count as multi-monitor acceptance.
