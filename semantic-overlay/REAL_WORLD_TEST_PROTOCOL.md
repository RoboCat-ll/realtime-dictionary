# 微信与 QQ 真实使用验收

## 2026-10-03 真实桌面试跑

安装后的补验：用户重新启动新版宿主 PID 24148，已核对目标文件与候选一致。微信同一多行消息再次正确返回模型解释。通过真实边缘鼠标拖动，浮框从 480×328 变为 1180×328，再独立增高为 1180×528；读取实际偏好文件中的 float_size 确认为 1180×528。未观察到崩溃或保存失败提示。用户再次退出并打开，宿主 PID 变为 34052；重新点击消息后截图确认大尺寸保留，原生可见浮框边界实测 1180×528（独立阴影窗 1185×533），与保存值完全一致。单显示器真实拖动、保存、跨进程重启恢复通过；不计为新独立消息样本，不扩大为多 DPI/多显示器结论。

由执行 Agent 观察实际桌面，注入 `Ctrl+Alt+K` 后点击用户指定测试会话的已有消息，再逐条核对气泡与解释浮框。没有发送新消息，没有把合成窗口计为真实样本。桌面为单显示器 2560×1440，系统缩放 150%；截图仅临时用于观察，不发布联系人或聊天画面。

| 编号 | 客户端 | 类型 | 实际读取路径 | 核对结果 |
| --- | --- | --- | --- | --- |
| QQ-01 | QQ | RAG 技术表达 | message_accessibility，22 字符 | 原句完整，含义切题，术语可点 |
| QQ-02 | QQ | PMF 与暂停投放 | message_accessibility，16 字符 | 原句完整，含义切题 |
| QQ-03 | QQ | AGI/AIGC 区别 | message_accessibility，29 字符 | 原句完整，点击 AGI 后同框显示正确短释义 |
| WX-01 | 微信 | RREF 数学表达 | bubble_ocr，36 字符 | 与原消息核对完整，含义切题；原消息本身以未完短语结尾 |
| WX-02 | 微信 | 普通疲惫/晚安聊天 | bubble_ocr，20 字符 | 原句完整，无强行术语标注 |
| WX-03 | 微信 | oneAPI 中英混排与换行 | bubble_ocr，42 字符 | 两行均读取，OCR 空白/标点规范化，Intel 语境解释正确 |

运行版本为 0.20.3-beta；实际解释请求走官方 DeepSeek，界面显示模型结果。每次点击后的 4.5 秒截图检查点已看到最终结果；该检查点不是精确耗时测量，日志秒级时间也不能用来推算 P95。首轮 6/6 成功仅证明当前案例链路可用，不证明 1% 故障率，也不能替代每端 25 条正式验收。QQ 首轮均为技术表达，需补充普通聊天、多行及边界场景。日程、未标注词选取、连续双击、持续字幕和多显示器均不在本轮真实通过范围。

另发现并修复浮框保存尺寸的本地缺陷：允许拉宽但重启解析拒绝大于 1000 像素的宽度。1280×720 保存/新实例恢复回归在修复前失败、修复后通过；当前工作区宽度钳制、选词与迟到结果诊断通过。用户确认退出后，新宿主已替换到桌面快捷方式实际目标，候选与安装文件 SHA256 一致。普通用户重启后的真实尺寸恢复已按上文补验通过；上述六条独立消息试跑是在替换前的宿主上完成。

## 环境

- Windows 微信桌面版和 QQ 桌面版各至少 20 次单次消息解释，剩余 10 次可按真实使用分配。每次必须先按 `Ctrl+Alt+K`，并在 10 秒内只点击一次目标消息。
- 覆盖单行、多行、中英混合、不同缩放比例，以及可读/不可读无障碍消息。
- 离线回归、诊断窗口和合成文本不能计入 50 次真实操作。

## 单次记录

1. 软件与版本。
2. 是否为单次消息解释、主动查词或兼容实验扫描；单次消息解释记录是否在待选状态内点击。
3. 文字来源：完整消息无障碍或气泡 OCR；主动查词与实验扫描另记。
4. 是否一次取得完整且仅包含被点击消息的文字。
5. 是否人工修改。
6. 从单击到面板出现、原文可见和整段解释可读的耗时。
7. 整段解释是否解决理解障碍；术语是否遗漏、冗余或并非原文。
8. 是否误带相邻消息、静默截断、遮挡、重复提交或卡顿。

## 通过标准

- 完整消息无障碍读取至少 95% 无需人工修正；气泡 OCR 修正率单独报告，不得混入无障碍结果美化数据。
- 单击后原文面板出现的中位数不超过 500ms；模型解释耗时单独报告。
- 任何超过 1000 字的选区必须明确拒绝，不能截断后发送。
- 模型返回的每个术语都必须逐字存在于提交原文，单段不超过 5 个，也允许为 0 个。
- 两小时连续聊天使用无崩溃、无整屏遮罩、无后台无限请求；未按 `Ctrl+Alt+K` 的普通聊天点击不得产生识别或模型请求。
- 每个失败必须归类为选区读取、OCR、模型、术语过滤、面板或交互问题，不能只记录“测试失败”。

## 用户试用

- 5 名目标用户，各记录至少 10 次真实理解障碍。
- 所有人先只使用单次消息解释至少三天；主动查词仅作辅助，整窗扫描不进入首轮对照。
- 首轮不加入新功能，只记录用户是否自然形成单击解释习惯以及哪些内容根本不值得解释。
- 第二周至少 3 人主动继续使用，才进入下一个应用或会议场景。

## 2026-09-28：标准文本真实客户端复验（0.20.1-beta）

本轮使用用户发送到微信和 QQ 的同一套 25 条标准文本，实际执行快捷键、点击气泡、读取原文和打开解释面板。标准文本覆盖路径兼容性；它们不是自然聊天中的真实理解障碍，也不能替代上文连续使用及用户试用标准。未发送额外消息，未创建日程。

| 指标 | QQ | 微信 |
| --- | ---: | ---: |
| 实际客户端操作 | 25/25 | 25/25 |
| 解释面板打开 | 25/25 | 25/25 |
| 模型整句解释完成 | 24/25 | 23/25 |
| 原文面板出现中位数 | 0.384 秒 | 0.470 秒 |
| 完成/失败状态出现中位数 | 1.294 秒 | 1.446 秒 |
| 完成/失败状态出现 P95 | 1.708 秒 | 10.805 秒 |
| 包含缓存命中的样本 | 2 | 2 |

QQ 通过无障碍读取完整消息，25 条均与客户端文字一致（忽略换行空白）。微信通过本地气泡 OCR 读取，23 条在忽略空白、大小写及标点样式后未发现文字差异，2 条有实质错误：第 13 条将 1.28 读作 128，并把“别”拆成“另刂”；第 23 条同样拆错“别”。这种归一化不是逐字精确率，不能用来证明日期、小数点等标点准确。

时延由点击计时，到面板可读成功结果或明确失败状态为止，包含本地读取、界面调度及缓存；不是纯模型延迟。QQ 第 1 条、微信第 6/15 条等满 10 秒后失败，本地提示不计为模型解释成功。成功返回也不等于解释和术语语义全部正确，人工语义评分及未高亮词主动查询仍需补充。

已修复并复现的根因：QQ 的 UI Automation 反射误把 ControlViewWalker 字段当属性，且 GetParent 存在重载歧义；微信点击封闭字形内部时只搜索最近的孤立背景像素，导致完整气泡定位失败。已增加字段/重载、读取超时、单工作线程和字形孔洞隔离回归。修改后，上述微信漏点击样本能打开解释面板。

冻结日程功能另记：明确包含钟点的 4 条安排可生成待确认项；“周三之前把方案初稿发我”缺少钟点，当前本地提取没有生成待确认项。不是模型没有理解，而是本地规则的覆盖边界。假设示例未生成日程。未检验真实日历写入。

费用：当前文字请求使用已授权的官方 DeepSeek / deepseek-flash；没有通过硅基流动调用收费模型。13:04 后的本机当日用量账本累计 66 次官方请求、服务商已报告 41,260 tokens，3 次请求用量未知；包含准备和重测，不能把它当作只属于最终 50 个样本的账单。未知用量不记为零，最终费用以服务商账单为准。

离线回归 191 项通过，包含新的原生读取与气泡诊断；编译和 Python 语法检查通过。离线回归隔离真实凭据并阻止外网模型请求。

**本轮结论：不能宣称已通过公司员工使用验收。** 尚缺微信 OCR 精确性、模型超时处理效果、语义/主动查词评分、不同缩放和多屏、两小时连续实际聊天、干净环境安装及 5 人两周试用。新配置写入已支持用户绑定加密；旧用户保存的明文密钥未自动迁移，修改必须单独获批。
补救复验（原始 50 条统计不重写）：新增几何约束的“另 + 刂”合并后，用生产读取代码对微信第 13/23 条实际气泡做本地复验，两条均恢复“别”；第 13 条保留 1．28。另增加模型校正数字不变约束，离线验证禁止把版本、小数、日期和时间改值，允许全角标点转半角。未追加付费模型重测，不能据此把此前 23/25 模型成功率改为 25/25。

本地安装包：0.20.1-beta-r2，内嵌 ZIP 完整性检查通过，关键文件与当前源码/原生二进制完全一致；内置 Python 在空白配置、清除真实凭据且禁用外网的环境中成功导入主服务、费用策略和加密读取模块。旧 Paddle/OCR 兼容服务依赖 PIL，不包含在该精简运行时，仍为未验证的冻结实验路径。安装程序 `/verify` 执行被自动审批以 `blocked by policy` 拒绝，随后仅作不执行安装器的文件核对；未完成干净机器安装。

## 2026-09-29 请求可靠性修复与接口复验

用户要求故障率不高于 1%，同时保留消息解释的 10 秒总截止时间。新增 `provider_transport.py`，在原有 urllib 代理与错误处理入口下复用 HTTP/1.1 连接。连接按服务地址、代理/隧道路由和凭据身份隔离，只有完整读取的健康响应可归还；最多 8 个活跃、8 个闲置连接，20 秒闲置过期。套接字绝对截止计时器中止拖延的响应，工作线程上限为 4，超时后不再发送后续尝试。默认 TLS 验证和凭据重定向拦截保留。

官方 DeepSeek 默认最多 3 次尝试，其他服务默认 2 次；只有短暂连接故障、502/503/504，以及预留预算的响应头超时可重试。官方 DeepSeek 非最后一次尝试的响应头预算为 2.5 秒，成功收到响应头后，读取正文仍可使用剩余总预算。认证、费用策略、格式错误和耗尽的截止时间不重试，不更换模型或服务商。每次物理尝试单独记录；被中断请求可能已计费，用量未知不等于免费。

没有显式代理环境变量时，官方 `api.deepseek.com` 与 `api.siliconflow.cn` HTTPS 请求默认直连；其他地址与显式代理保持原有路由。仅修改本应用，不修改 Windows 代理设置。新增连接完成、提交完成、响应头、正文与尝试数日志，不记录原文、响应内容或密钥。

校准结果全部保留，不合并为最终成绩：

| 版本/条件 | 样本 | 最终失败 | 说明 |
| --- | ---: | ---: | --- |
| 复用连接、原系统代理 | 20 | 0 | 中位 1.180 秒，P95 1.489 秒 |
| 强制冷连接、原系统代理 | 20 | 5 | 连接/提交后等待响应头超时 |
| 强制冷连接、直连、无预留响应头重试 | 20 | 2 | 仅切换路由不足以解决 |
| 强制冷连接、直连、最多 2 次尝试 | 50 | 1 | 53 次物理尝试，P95 3.671 秒，仍为 2% |
| 最终候选：强制冷连接、直连、最多 3 次尝试 | 300 | 2 | 329 次物理尝试，24 次暂时失败被救回 |

最终 300 条为生成的标准测试文本，长度 21–722 字，包含普通聊天、技术术语、任务、长段落。直接调用生产模型请求与结果解析函数，每条不同编号、强制清空闲置连接，没有命中解释缓存；显式授权开关与预算上限为 300 轮、最多 900 次物理尝试，实际 329 次。生成文件只保留样本编号、长度、耗时与错误类型，不保留测试原文或模型响应。

最终观察故障率 **2/300 = 0.67%**，中位耗时 **1.013 秒**，P95 **3.870 秒**。首次尝试失败 26 条，重试救回 24 条；另外 29 次物理尝试计入用量。两次最终失败分别发生在多次连接/等待响应头之后，以及首次响应正文读取阶段，均约 10 秒后明确结束。没有用本地结果冒充成功，也没有延长总预算。

**这不是长期故障率或桌面全流程达标证明。** 这批精确二项式单侧 95% 故障率上界仍约 2.08%；一次短时串行批次也不能排除日间波动或相关故障。今天 QQ 未运行，未重新完成 QQ/微信点击、OCR、解释卡片和真实使用验收。此前 50 次真实客户端记录保留，不能改写为这次接口结果。

215 项隔离凭据、禁止外网的离线回归通过，包含实网回环连接复用、断连、半截正文、503、认证/重定向拦截、分阶段超时、连续恢复、工作线程/连接上限及代理隧道检查；Python 语法和差异空白检查通过。当前宿主健康信息报告 `bounded-http1-keepalive-v1`、官方 DeepSeek、直连与最多 3 次尝试。

截至本轮结束，当日本机账本为官方 DeepSeek 442 次尝试、已报告 272,721 tokens、42 次用量未知，包含上述所有校准批次和最终复验。没有硅基流动模型调用。最终费用以服务商账单为准。

本地交付更新为 **0.20.1-beta-r3**；安装包内 ZIP 完整性、当前源文件/原生程序与内嵌负载一致性检查通过，内置 Python 在无凭据、禁外网的隔离环境中可导入主服务与新传输模块。PIL 未打包，旧 Paddle 实验路径仍不能导入，明确单独记录；未执行安装器或做干净机器安装。R3 SHA256：`290997d8eb11d7cf2d73ccfaef916d5e75f1d7a53ce57010921cb7e69ffce3ca`。

## 2026-09-29 消息窗口选词与分层释义

消息解释窗口的原句支持选词后在附近显示“解释”按钮。选择本身不发送请求；点击后带上当前原句，查询简短语境释义。已有整句注释直接复用。“展开解释”显式请求必要背景和例子，失败时保留简短释义并允许重试；成功结果在当前词语卡片内收起、再次展开不重复查询。修改原句或切换词语后，旧请求不得覆盖新状态。旧客户端 `/lookup` 默认行为不变。

219 项隔离凭据、禁止外网的离线回归通过，新增 4 项覆盖简短/展开请求、上下文、非法参数和 HTTP 参数传递。原生编译、Python 语法及差异空白检查通过。新增 `SelectionLookupInteractionTest.exe` 使用注入的模拟查询结果，实际打开生产窗口并验证附近按钮、选择不请求、显式查询、收起复用、失败保留及过期请求隔离；在当前桌面检查了选区按钮和展开后的布局。该诊断不读取真实密钥或调用外网模型。

本轮仅更新开发目录源码和原生程序，不重打 R3 分发包。没有追加 QQ/微信真实消息点击或真实模型释义质量与时延测试；上述界面与离线结果不能替代这些验收。

## 2026-09-30 托盘菜单收敛与原生代码拆分

一级菜单保留状态、解释一条消息、主动查词、字幕记录、模型设置、设置、实验功能、帮助和退出。用量/隐私/字幕保存/熟悉度收进设置，显示方式/标注密度/识别范围 收进实验功能。字幕历史只保留一个主要入口，关闭实验功能仍可回看；原有事件、偏好与快捷键保留。“解释一条消息”复用原 10 秒待选操作。

从 Program.cs 提取 TrayMenu、MessageExplanationForms、MessageTextReader、ServiceManager、ServiceContracts、CaptionForms、CalendarForms 七个模块；六组类在提取时逐段保持原有内容，托盘模块只调整导航和摘要呈现。Program.cs 从 10,786 行降至约 5,430 行，主要剩宿主会话与通用窗口/系统辅助代码；总功能量没有减少，尚未完成全面架构解耦。构建与启动/打包的新鲜度检查包含所有根目录 C# 模块。

编译和脚本语法/差异空白检查通过，220 项隔离离线回归通过。新增 TrayMenuTest 使用无真实配置、无后端启动的菜单对象，验证主要入口、偏好勾选、实验开关禁用/恢复及历史记录独立可用；选词交互、字幕文本和本地提醒诊断通过。精简菜单的窗口截图已检查。

额外 RefinementTest 发现旧模拟健康对象缺少 app_version，已按现有严格版本握手修正，并增加版本不符的负例。完整桌面诊断两次停于模拟无障碍窗口无法获得前台焦点；定位后再跑确认同一原因。随后使用该诊断已有的 SKIP_LIVE_ACCESSIBILITY=1 开关，明确跳过前台无障碍/划词工具条子项，其余异步追加、滚动跟随、失败回退和过期结果隔离通过。完整诊断不得记为通过；不将此限定测试算作 QQ/微信实际操作。

仅更新本地源码和开发二进制，未重打安装包、上传仓库或请求外网模型。


## 2026-09-30 development closeout — v0.20.2-beta

This checkpoint is development verification, not a new QQ/WeChat or employee-pilot
sample. 227 isolated offline tests pass, including actual loopback request parsing,
exact launcher script matching and bundled Python imports without initial script
path. Native build, pending-edit/stale-result/explicit-retry/wait-feedback and
parallel preference diagnostic pass; menu, selected-word, caption/reminder and
isolated credential/message-reader diagnostics pass. Program member declarations
remain present after extraction into modules. With SKIP_LIVE_ACCESSIBILITY=1,
remaining refinement checks pass; two foreground accessibility subtests are still
explicitly skipped, never counted as full desktop acceptance.

The first generated candidate failed embedded-runtime startup due to new module
import placement; fixed, regression added, and final package rebuilt. Final ZIP
module/native/runtime hashes and installer embedded ZIP/version match. Current
source GUI is running with local health version 0.20.2-beta, product identity and
protocol 2; desktop shortcut targets that compiled GUI host. No live provider calls,
credentials changes, clean install execution or publication in this task. Detailed
scope/checklist: DEVELOPMENT_CLOSEOUT.md. Ignored machine-readable package hashes:
native-host/bin/development-closeout-v0.20.2-beta.json.


## 2026-09-30 accuracy refinement — v0.20.3-beta

Synthetic counterexamples reproduced 11 assertion failures before the fixes.
Local OCR repair no longer consumes ordinary words beside complete technical terms,
fuzzily renames short ordinary words, strips numeric suffixes or changes protected
links/addresses/paths. Exact whitespace splits and bounded dot-noise repairs remain
covered by positive cases. Model OCR correction rejects added/removed negation,
cancellation and rescheduling markers rather than relying only on edit distance.
Passage term filtering prefers complete concepts over nested-only terms while
retaining independent occurrences. Local merging cannot reintroduce nested-only
terms and fills only missing annotations, never overwriting an existing contextual
model definition. Explicit manual selected-word lookup remains available.

236 isolated offline tests pass, including nine new accuracy test methods; no live
model request. Native build and selected-word/message-reader/pending-edit diagnostics
pass. Source GUI PID 31652 reports 0.20.3-beta, product identity and protocol 2.
New local ZIP/Setup module/native/runtime hashes, embedded payload and version match;
root personal configuration/logs are excluded. Evidence metadata is retained in
native-host/bin/accuracy-v0.20.3-beta.json. These are reproducible logic regressions,
not a measured improvement percentage on actual QQ/WeChat OCR or model output.

## 2026-09-30 calendar year prefill

User authorized unstated-year month/day defaults. Local clarification chooses the
next valid date including today; same-day past clock warns instead of rolling a
year. Explicit numeric/relative years override the default; historical framing
without a year stays unresolved. Leap-day and invalid-date cases are covered.
Opening an incomplete confirmation form automatically calls only local clarification,
preserves existing draft fields and shows the inference basis; nothing is created.
241 isolated offline tests pass; native build, CalendarFlowTest and LocalReminderTest
pass. Local live clarification of the synthetic October 8 17:00 message returns
2026-10-08T17:00, with end/timezone still missing. Current source GUI restarted;
existing installer was not rebuilt. No model calls or new real QQ/WeChat samples.

## 2026-09-30 progressive workflow UI

Message cards collapse unused term/schedule rows and use source highlights without
duplicate term buttons; explicit selected-word explanations and correction remain.
Calendar confirmation summarizes known fields and exposes missing inputs, with an
edit-all toggle and user-selected 30/60/120-minute duration (no automatic selection).
Inferred-year and past-clock notices remain visible independently of missing fields.
Caption query details open only on request; changing date/current session invalidates
pending results, while append updates preserve selected words and reading position.
Date format and narrow-window wrapping are improved.

241 isolated offline tests and native build pass. Six native diagnostics pass:
CompactWorkflowTest, CalendarFlowTest, LocalReminderTest, SelectionLookupInteractionTest,
CaptionTextTest, CloseoutReliabilityTest. DPI-aware target-only synthetic form previews
were inspected; this is injected-data UI verification, not a fresh QQ/WeChat or long
live audio trial. Source GUI restarted; no provider calls or installer rebuild.


## 2026-10-03 reading area and trigger feedback — real desktop checks

The ordinary-user installed host was checked in the designated QQ and WeChat
test chats. Actual foreground handles remained unchanged when arming in both
clients. The compact hint cleared after message capture; QQ arm-only expiry
was observed after the 10-second window. Complete RAG source and relevant model
results were visually checked. No test messages were sent.

A real bottom-edge drag changed the float from 1180x528 to 1180x728, keeping
width unchanged. The sentence meaning control increased from 282 to 482 pixels
high; the same-size word view allocated 622 pixels to its body. Source and
fixed actions stayed visible. These measurements are physical pixels on the
current 2560x1440, 150-percent desktop, not mixed-DPI acceptance.

Real checking exposed clipping of the long armed-hint copy at this scale.
The copy is shortened and a text-fit regression passes. Latest candidate
compiles; that final copy fix awaits replacement of the running host. Six
related native diagnostic groups passed for this batch, and the hint group
was rerun after the copy fix. Python was not changed or retested this batch.
Repeated RAG operations are UI checks, not additional independent formal
accuracy samples, a measured latency percentile, or a 1-percent failure proof.


### User-visible compactness correction, pending installation

The user screenshot exposed that the earlier manual-height allocation stretched
a one-line meaning excessively. The candidate now caps short text by content
instead of assigning all leftover height; its minimum meaning row is 40 logical
pixels. Word layout uses a separate spacer so the final text row does not
stretch. Explicit outer dimensions remain unchanged. New short-manual regressions
failed against the prior implementation and pass after correction, alongside
long-content growth and fixed-action checks. Three affected native diagnostic
groups passed; production candidate compiled. This correction is not yet installed
or counted as real-client acceptance. Earlier growth measurements above describe
the prior installed behavior, not the corrected content-dependent allocation.


Final compact-layout candidate was installed after an actual tray-menu Exit,
with no forced termination. Host absence, source freshness, exact desktop shortcut
target and installed/candidate SHA256 equality were checked. Automatic shortcut
reopening through Explorer was rejected by execution policy before execution;
ordinary-user reopening and real compact-layout recheck remain pending.

## 2026-10-03 five-point installed-build recheck

This section supersedes the pending-installation statements above. The user
reopened the verified installed host; actual QQ and WeChat cards were checked.

| Check | Observed evidence | Remaining limits |
| --- | --- | --- |
| Compact reading | A short real-message meaning body is 34 physical pixels; source/actions stay grouped at the top. Word detail remains readable in the same float. | Measurements are on this 150-percent desktop. |
| Independent resize/persistence | Real right-edge and bottom-edge resizing reached 685x360 without a crash; sentence/word switching kept the size. Earlier real host restart restored the saved 1180x528 size. | The latest 685x360 size was not separately checked after another complete host restart. |
| Arming feedback | Real QQ and WeChat foreground stayed unchanged. Hint clears on consumption; QQ expiry was observed after ten seconds. | No mixed-DPI acceptance. |
| Message/word workflows | Fresh 25-per-client clicks all returned model-result status. QQ sources matched 25/25; WeChat punctuation-normalized sources matched 24/25. Highlighted RAG and an unmarked selected word were explained. | WeChat sample 18 repeatedly read AIGC as ACC. Not every explanation was independently graded for semantic correctness; cached and uncached timings are mixed. Double-click also exposed an extra text toolbar of unconfirmed origin; WeChat Alt-click avoided it. |
| Calendar/captions | Actual calendar confirmation preserved the fixture's explicit 2026-09-30 14:30 start and past-date notice, asking for missing fields; no reminder was created. Real system-loopback captions accepted four distinct synthetic TTS lines over about thirty seconds, stayed running beyond ten seconds, and were stopped explicitly. Actual dated history showed all four lines plus an earlier failed-session gap, date navigation, selected-word explanation and selected-text translation. | This is a short synthetic audio trial, not a sustained foreign-language meeting. ASR misheard some Chinese words and bootcamp as bookcam. |

Speech uses FunAudioLLM/SenseVoiceSmall on SiliconFlow's transcription endpoint.
Applicable official pricing was checked as zero on 2026-10-03 and the runtime
free-price guard remains enabled. The desktop settings UI saved/applied the
authorized encrypted speech credential; official DeepSeek text settings stayed
separate. Raw helper configuration writes initially disagreed with the
desktop-launched backend; that discrepancy is not root-caused and is not claimed
fixed by changing file permissions.

Two backend corrections were made and applied through the existing graceful
service restart path: caption translation now uses the independent text provider
and its key, matching consent; a socket timeout within an armed header budget is
retryable even when the OS timer fires slightly early. The absolute deadline,
attempt cap, and body-timeout handling remain intact. The transport regression
failed repeatedly before this correction; the full isolated suite subsequently
passed 235/235 with no model calls. Live history translation was checked after
restart; health confirmed both independent configurations remained available.

Local OCR comparison on 24 identifiable rendered fixture bubbles found 18 exact
normalized matches at each of 1.5x and 1.7x, with different failures. A separate
capture of the AIGC bubble succeeded at 1.5x but failed at 1.7x; the result did
not generalize across crops/positions. These are OCR-only experiments, not new
message-acceptance samples. Production scaling was therefore not changed and
the AIGC defect remains open. No fuzzy ACC-to-AIGC replacement was added.

No chat messages, reminders, or calendar entries were created in this recheck.
Private screenshots/audio, diagnostic helper outputs, credentials, and local
archives remain outside Git. This batch was not pushed or packaged. The five
checks have concrete evidence, but the open accuracy and long-session limits
prevent claiming complete acceptance or a measured one-percent failure rate.

## Five-point refinement checkpoint — 2026-10-05

The earlier AIGC defect was reproduced through the real WeChat message click:
the Chinese OCR yielded A/℃/C, and the model changed it to ACC. A fresh local
worker re-read the exact same detector/inflation/1.7x bounded bubble image with
the installed English recognizer. The narrow unique-box repair now yields AIGC;
ordinary ACC, numeric temperatures, shifted and ambiguous boxes stay unchanged.
This is exact failure-crop evidence, not a new overall OCR success-rate claim.
If English OCR is unavailable or the legacy one-shot bridge is used, no such
repair is promised. No language pack or fuzzy ACC-to-AIGC rule was added.

Real QQ RAG capture and explanation succeeded before the final OCR build.
The user exited the app and the actual desktop-shortcut executable was rebuilt.
Fresh live WeChat acceptance after reopening remains pending. Only the two
authorized official DeepSeek analyses above were called; no SiliconFlow model
request was made during these checks.

The five changes are: preserve visible OCR uncertainty and authoritative manual
edits; disclose ambiguous definitions in the lookup prompt; show the selected-word
recovery shortcut in a text-fitting hint; record anonymous once-per-chunk caption
outcomes and queue-to-result time; ask only duration when a calendar draft solely
lacks its end. No duration is guessed and no reminder/calendar was created.

Current-source build, five targeted native diagnostics, the exact failed-crop
probe, Python and OCR-worker syntax checks passed. Offline regression: 237/237
OK (25.665s). CompactWorkflowTest suppressed screenshots. Live ambiguity quality,
actual duration-confirmation interaction and sustained real English audio remain
unverified; source/isolated evidence does not close those acceptance items.

Live follow-up: after the user reopened the final executable, the actual WeChat
AIGC bubble click returned AGI and AIGC unchanged, with a matching concise
explanation. The OCR-review label remained visible. Verification used window
text, not new screenshots. This closes this specific reproduction only.

`LocalVideoCaptureTest.exe <video-path>` plays a user-designated video through
the existing decoder and production process-specific audio capture. It makes
zero provider requests and retains only numeric summaries. A completed local
capture does not establish ASR transcription, English accuracy or subtitle
latency. Chunk emission intervals can include silence and music and cannot be
treated as missing speech. Run it separately; it is compiled but not part of
the automatic offline suite because it plays real audio in real time.

Real designated-video run (2026-10-05): input duration 195.86s, played 197.55s
including drain; production process-isolated capture emitted 25 segments,
first at 8.03s and last at 196.60s. Maximum segment duration was 8.00s;
largest inter-emission interval was 22.07s. These intervals are segmentation
measurements, not model/subtitle latency or proven speech losses. Audio was not
persisted; only aggregate log values remain in ignored diagnostics output.
Playback and capture disposed successfully; exit 0. Provider requests: zero.

Live transcription was not attempted: local HTTPS pricing requests failed TLS
handshake using Python, Windows curl and Windows HTTP, including a direct probe.
The free-price runtime guard stayed closed. The official pricing search result
lists SenseVoiceSmall as free, but this does not bypass the program's own fresh
verification requirement. No proxy/system setting or credential was changed.

### 2026-10-05 caption boundary comparison
- Actual old/new native segmenters, same 195.86s video and 69 visually reviewed
  subtitle lines (467 words): word agreement 88.6510% -> 90.3640%.
- S/D/I: 14/33/6 -> 15/25/5; 27 -> 29 requests. Reference is embedded subtitles,
  not human-listened audio ground truth. File decoding/core pipeline benchmark,
  not complete live UI acceptance. Fixed non-overlap 8s prior results are separate.
- Preserve one-second context across forced cuts, keep active, attach overlap and
  sequence to chunks, trim exact overlap only across adjacent successful chunks.
  No reference-specific vocabulary substitutions; fee guard unchanged.
- Full native build + audio segmentation/text/reliability diagnostics pass;
  Python offline 237/237 passes. Runtime replacement hash verified.
- Single-video improvement does not establish cross-audio accuracy or faster
  network latency; extra context increased segment count by two in this sample.

### 2026-10-06 targeted natural-pause refinement
- Actual native segmentation/core pipeline, same 195.86s video + reviewed
  69 subtitle lines/467 words: 90.3640% -> 92.2912% agreement, S/D/I
  15/25/5 -> 11/11/14. More inserted words are disclosed; not audio-ground-truth
  WER, cross-meeting accuracy, or live meeting UI acceptance.
- Submit at >=3s buffer plus >=0.25s quiet; shorter buffers keep0.7s endpoint.
  Source phrase "your bags" released at89.1s vs92s, before network processing.
  Other missing expressions returned; no reference-specific vocabulary rewriting.
- Segments29 ->30. Full native build and AudioSegmentationTest/CaptionTextTest
  passed, installed target matches candidate hash. No unrelated broad re-audit.
