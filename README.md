# 实时字典 · Realtime Dictionary

面向聊天与外语会议的 Windows 实时字典。识别专业术语，点击查看中文解释，在解释中继续查词，并将识别出的安排经用户确认后保存为本地提醒。

**当前为开发中的 Beta 源码快照。** 真实聊天软件兼容性、滚动跟随、多显示器和长时间会议仍需验证；不保证所有软件和布局都能准确识别。本仓库首次上传不包含安装包，历史本地安装包也不代表当前源码。

## 功能

- Windows OCR 获取聊天区域文字，词语小窗口高亮与点击解释。
- 本地强候选优先展示，后台语义筛选补充高亮。
- TypeSafe Jev 批量判断候选；生成式模型提供中文释义。
- 递归查词、返回、主动选词查询与可编辑输入兜底。
- 捕获会议播放声音并转写，透明桌面字幕和会话历史；不采集麦克风。
- 日程候选确认、本地提醒、可选 ICS 和 Microsoft 日历流程。
- 可选 Chrome/Edge 扩展，使用网页文字位置提供浏览器适配。

## 从源码运行

需要 Windows x64、系统 .NET Framework C# 编译器、Windows PowerShell（Windows OCR 桥接）及 Python 3.10+。Windows 需安装所用语言的 OCR 组件。会议进程音频隔离能力取决于 Windows 版本和目标应用。

1. 克隆本仓库，确保 Python 可从 PATH 找到。
2. 双击 `semantic-overlay/start.cmd`。首次启动会构建原生宿主。
3. 从系统托盘配置解释服务地址、模型名和个人 API Key。配置验证会调用服务商，可能产生费用。
4. 在目标聊天窗口按 `Ctrl+Alt+K` 开始识别。

| 操作 | 快捷键 |
| --- | --- |
| 启用或刷新当前窗口高亮 | Ctrl+Alt+K |
| 清除高亮、结束当前会话 | Ctrl+Alt+G |
| 主动查询选中文字 | Ctrl+Alt+D |

程序没有主窗口；完全退出请使用系统托盘菜单。未暴露选中文字的应用会显示可编辑输入框。本地提醒需要托盘程序运行才能准时触发。

主路径的 Python 后端使用标准库。可选 PaddleOCR 备用路径另需 Pillow、PaddleOCR 和与机器匹配的 PaddlePaddle；该备用路径存在下述已知问题，当前不建议依赖它。

## 模型配置

- Jev 从启动环境变量 `TYPESAFE_API_KEY` 读取凭据，可通过 `TYPESAFE_MODEL` 指定型号（当前默认 `jev-latest`）。在启动程序前配置到自己的进程环境，勿提交密钥。
- 解释服务使用托盘设置；也支持 `OPENAI_BASE_URL`、`OPENAI_MODEL` 及 `SILICONFLOW_API_KEY`、`DEEPSEEK_API_KEY`、`OPENAI_API_KEY`。实际优先级见 `server.py`。
- 当前 Jev 的独立持久化配置界面尚未完成；没有 Jev 凭据时可能使用已配置的生成模型进行筛选。`/health` 的 `analysis_provider`、`analysis_mode` 可用于核对运行状态，但不是服务商调用成功证明。
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

主流程：获取文字 → 本地候选与坐标 → 语义判断 → 高亮 → 用户点击 → 中文解释。模型不控制鼠标或直接执行提醒；创建提醒需要用户确认。

## 隐私和已知限制

- 配置云端模型后，识别的文字、候选及必要语境会发送至相应服务商；会议转写会发送音频片段。使用前需确认参与者和内容适合该处理方式。
- 不要上传个人配置、密钥、日志、聊天截图或音频。公开源码采用 `.gitignore` 白名单。
- 当前密钥配置以明文 JSON 存在用户目录，Windows 凭据保护尚待实现。运行日志可能包含查询词语，不应直接公开。
- 备用 OCR 的本地接口鉴权、截图文件清理及主服务令牌传递尚需修复。不要将本地端口暴露到网络，不建议在敏感内容场景启用备用 OCR。
- 当前主服务绑定 127.0.0.1:8877。健康检查和版本握手仍需加强；旧安装版可能与源码版冲突，运行源码前请退出旧托盘实例。
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
