# 本地体验指标

指标用于判断产品价值，不用于证明代码量或模型能力。默认只写入当前 Windows 用户的 RealtimeDictionary 配置目录，不记录聊天正文、查询词、解释正文、截图、音频、密钥或联系人。

## 事件字段

- `timestamp_utc`：UTC 时间。
- `session_id`：本次程序运行生成的随机标识。
- `event_name`：事件名称。
- `trigger_mode`：`selection_passage`、`active_lookup` 或 `auto_highlight`。
- `source_app`：仅记录 `wechat`、`qq` 或 `other`，不记录窗口标题、联系人或进程路径。
- `text_source`：`message_accessibility`、`bubble_ocr`、`accessibility`、`clipboard`、`ocr`、`typed` 或 `highlight`。
- `elapsed_ms`：从用户触发到当前结果的完整耗时。
- `lookup_mode`：模型、缓存、本地规则或降级路径。
- `manual_correction`：用户是否修改了自动取得的文字。
- `feedback`：`useful`、`unnecessary_highlight` 或 `wrong_explanation`。
- `count`：消息解释返回的术语数，或实验整窗扫描显示的高亮数。

## 消息点击完整链路（2026-10-04）

`message_operation_completed` 从点击消息开始计时，包含读取、OCR、等待与结果处理。每个操作用随机 `operation_id` 标识，只写一次终态；字段仅含 `source_app`、`outcome`、`elapsed_ms`、`text_source`、`cache_hit`，不记录消息或联系人。关闭、读取失败、忙碌跳过也计入，避免只统计成功打开的面板。修改原文与再次解释另起操作；直接选词等其他入口仍使用原有事件，不能并入消息点击分母。

`outcome` 区分模型结果、本地结果、本地不可用、降级、读取失败、忙碌、取消、修改原文、客户端异常和输入错误。成功得到结果不等于用户认为解释有用，用户反馈仍须单独统计。

指标日志上限为每份 5 MB，保留当前文件和一份前驱；写入失败计数在本次运行的用量视图显示。宿主及后端运行日志每份上限 2 MB，同样保留一份前驱。轮换后的历史统计必须合并两份，超出保留范围的数据不作为全生命周期样本。兼容整窗扫描的小时上限只覆盖该入口，不能当作全部模型调用的预算。

## 核心指标

字幕新增 `caption_chunk_completed`（2026-10-05）：每个音频块只记一次终态，
包含 `outcome`（transcript/empty/gap/dropped/cancelled）、`elapsed_ms` 和
`attempts`（0–2）。耗时从音频分段入队开始，到该块完成或被放弃为止，
包括排队和重试；不包括入队前的录音分段时间，不能当作从说话开始的字幕延迟。
停止时未处理块记为取消；标点或格式空结果不记为成功字幕。
不保存音频、字幕正文、联系人或模型请求内容，沿用现有有界日志。

1. 完整消息读取率：单击后无需修改即可取得完整消息的次数 / 全部消息点击。
2. 气泡 OCR 修正率：修改气泡 OCR 后重试的次数 / 全部气泡 OCR 次数；不能和无障碍消息混算。
3. 完整耗时：从点击“解释这段”到整段解释可读，不只统计模型请求。
4. 术语克制程度：每段返回术语数，以及零术语段落占比；不追求数量越多越好。
5. 主动查词与实验高亮的点击率、明确无用率和解释错误率单独统计。
6. 持续使用：第二周非测试提醒下主动单击解释的消息数。
7. 有效查询成本：模型费用 / 被确认解决理解障碍的整段解释。

首次出现窗口不算理解任务完成；只有用户取得解释并给出反馈或继续使用，才构成产品证据。

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
