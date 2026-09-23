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

## 核心指标

1. 完整消息读取率：单击后无需修改即可取得完整消息的次数 / 全部消息点击。
2. 气泡 OCR 修正率：修改气泡 OCR 后重试的次数 / 全部气泡 OCR 次数；不能和无障碍消息混算。
3. 完整耗时：从点击“解释这段”到整段解释可读，不只统计模型请求。
4. 术语克制程度：每段返回术语数，以及零术语段落占比；不追求数量越多越好。
5. 主动查词与实验高亮的点击率、明确无用率和解释错误率单独统计。
6. 持续使用：第二周非测试提醒下主动单击解释的消息数。
7. 有效查询成本：模型费用 / 被确认解决理解障碍的整段解释。

首次出现窗口不算理解任务完成；只有用户取得解释并给出反馈或继续使用，才构成产品证据。
