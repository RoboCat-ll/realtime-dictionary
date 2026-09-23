# 实时字典 · Realtime Dictionary

面向微信与 QQ 桌面聊天的 Windows 阅读助手。默认流程是先按 `Ctrl+Alt+K`，再在 10 秒内点击一条消息，先理解整条消息，再按需查看原文中的术语。整窗 OCR 高亮保留为显式兼容实验。

**当前为开发中的 Beta 源码快照。** 真实聊天软件兼容性、滚动跟随、多显示器和长时间会议仍需验证；不保证所有软件和布局都能准确识别。本仓库首次上传不包含安装包，历史本地安装包也不代表当前源码。

## 当前验证范围

- 微信与 QQ 共用的单次消息解释：按 `Ctrl+Alt+K` 后点击一次目标消息；优先读取完整消息控件，读不到时自动定位整个消息气泡并 OCR。
- 每次只发送当前消息，最多 1000 个字符；一次返回整段中文解释和 0-5 个原文术语。原文在面板中始终可见、可修改。
- 查词卡片立即打开；除含义确定的程序快捷键外，每次主动查词都会自动请求
  在线 AI 解释。本地内容只用于等待期间的即时预览，不需要再点一次 AI 查询。
- 整窗 OCR、悬浮助手、原文高亮与 TypeSafe Jev 候选判断移入兼容实验。
- 递归查词、返回、主动选词查询与可编辑输入兜底。
- 会议字幕、日程提醒和浏览器扩展保留为冻结的实验功能，不属于当前首次使用或产品验证主路径。

产品范围、指标和真实验收分别见 [PRODUCT_SCOPE.md](semantic-overlay/PRODUCT_SCOPE.md)、[METRICS.md](semantic-overlay/METRICS.md) 与 [REAL_WORLD_TEST_PROTOCOL.md](semantic-overlay/REAL_WORLD_TEST_PROTOCOL.md)。

## 从源码运行

需要 Windows x64、系统 .NET Framework C# 编译器、Windows PowerShell（Windows OCR 桥接）及 Python 3.10+。Windows 需安装所用语言的 OCR 组件。会议进程音频隔离能力取决于 Windows 版本和目标应用。

1. 克隆本仓库，确保 Python 可从 PATH 找到。
2. 双击 `semantic-overlay/start.cmd`。首次启动会构建原生宿主。
3. 从系统托盘分别配置解释模型与 Jev 高亮判断。连接测试会调用服务商，可能产生少量费用。
4. 按 `Ctrl+Alt+K`，在 10 秒内单击一条需要理解的消息。点击后待选状态自动结束。

| 操作 | 快捷键 |
| --- | --- |
| 进入一次消息待选状态（10 秒） | Ctrl+Alt+K |
| 清除实验高亮、结束实验会话 | Ctrl+Alt+G |
| 主动查询选中文字 | Ctrl+Alt+D |

程序没有主窗口；完全退出请使用系统托盘菜单。未暴露选中文字的应用会显示可编辑输入框。本地提醒需要托盘程序运行才能准时触发。

主路径的 Python 后端使用标准库。可选 PaddleOCR 备用路径另需 Pillow、PaddleOCR 和与机器匹配的 PaddlePaddle；该备用路径存在下述已知问题，当前不建议依赖它。

## 模型配置

- 托盘中的“配置 Jev 高亮判断”独立保存 TypeSafe 地址、型号与个人 Key；也支持 `TYPESAFE_API_KEY`、`TYPESAFE_BASE_URL`、`TYPESAFE_MODEL` 环境变量，环境变量优先。
- 托盘中的“配置解释模型”保存 OpenAI 兼容服务；也支持 `OPENAI_BASE_URL`、`OPENAI_MODEL` 及 `SILICONFLOW_API_KEY`、`DEEPSEEK_API_KEY`、`OPENAI_API_KEY`。实际优先级见 `server.py`。
- 托盘状态会分别显示当前高亮判断与解释引擎。没有 Jev 凭据时，高亮会使用已配置的生成模型或本地规则降级；状态显示仅证明进程加载了配置，不等于最近一次服务商调用成功。
- 使用硅基流动时，短词解释默认使用快速非思考模型，避免让旗舰模型承担一句话查词的延迟；可通过 `REALTIME_DICTIONARY_LOOKUP_MODEL` 单独覆盖，不影响语音或 Jev 高亮判断。
- 使用硅基流动时，整条消息解释默认使用 `Qwen/Qwen2.5-7B-Instruct`，可通过 `REALTIME_DICTIONARY_SELECTION_MODEL` 单独覆盖。这个选择只影响消息解释，不改变短词解释、整窗高亮判断或语音转写。
- 转写目前复用受支持的 SiliconFlow 配置。模型可用性、价格和账户额度以服务商为准。每位用户使用自己的 Key，仓库不提供免费模型额度。

## 架构与目录

```text
semantic-overlay/
  native-host/   Windows 托盘、OCR、位置跟随、解释窗口和音频采集
  server.py      候选分析、模型适配、中文解释和本地 HTTP 服务
  ocr_service.py 可选备用 OCR 服务
  tests/         离线回归与测试工具
  installer/     安装器源码
  vendor/NAudio/ 音频依赖及原许可
browser-extension/ 可选浏览器适配器
```

主流程：按 `Ctrl+Alt+K` → 10 秒内单击一次消息 → 自动退出待选状态 → 读取完整控件文字或定位整个气泡 OCR → 整段中文解释 → 按需查看原文术语。未进入待选状态的普通点击不触发分析。拖选和手工修改只是失败兜底。模型不控制鼠标或直接执行提醒；创建提醒需要用户确认。

## 隐私和已知限制

- 配置云端模型后，识别的文字、候选及必要语境会发送至相应服务商；会议转写会发送音频片段。使用前需确认参与者和内容适合该处理方式。
- 当前 Windows 用户目录会保存一份有上限的本地体验事件，用于统计取词来源、完整耗时、是否修正、反馈和高亮数量；不记录聊天正文、查询词、解释正文、截图、音频、联系人或密钥，可从托盘直接查看所在目录。
- 不要上传个人配置、密钥、日志、聊天截图或音频。公开源码采用 `.gitignore` 白名单。
- 当前密钥配置以明文 JSON 存在用户目录，Windows 凭据保护尚待实现。运行日志可能包含查询词语，不应直接公开。
- 备用 OCR 的本地接口鉴权、截图文件清理及主服务令牌传递尚需修复。不要将本地端口暴露到网络，不建议在敏感内容场景启用备用 OCR。
- 当前主服务绑定 127.0.0.1:8877，并通过产品标识与协议版本握手。旧安装版或其他程序占用该端口时，当前宿主会明确拒绝连接；运行源码前仍应退出旧托盘实例。
- 滚动高亮依靠独立窗口和画面位移测量，无法保证与第三方文字逐帧同步。OCR 可能漏词或识别错误；模型解释也需要核对。
- 打包脚本依赖本地嵌入式 Python 运行环境等构建材料，这些不随源码上传。首次克隆优先按源码运行步骤使用。

## 开发验证

在 `semantic-overlay` 目录运行：

```powershell
python -m unittest discover -s tests -v
python -m py_compile ocr_service.py server.py
powershell -File native-host/build.ps1
```

原生诊断需要交互式 Windows 桌面。离线测试通过不等于真实会议或所有聊天软件通过验收。真实模型测试需明确限制调用预算。

## 许可

项目原创代码采用 [MIT License](LICENSE)。第三方组件保留各自许可，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
