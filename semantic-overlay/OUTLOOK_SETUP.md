# 微软日历首次连接

本版通过 Microsoft Graph 读取默认日历、检查冲突并创建经用户确认的事件。使用微软全球云；无需安装 Outlook 桌面版。模型 API Key 与微软登录相互独立。

## 开发者只需注册一次

1. 打开 https://entra.microsoft.com ，进入“Entra ID → 应用注册 → 新注册”。账户需要有应用注册权限；没有组织目录或被学校/公司限制时，需要目录管理员协助。
2. 名称填“实时字典”。为支持同学各自的账户，账户类型选“任何组织目录中的账户和个人 Microsoft 账户”。本版使用 common 登录入口，请勿注册为仅单租户应用。
3. 注册后复制“应用程序（客户端）ID”，是 GUID 格式的公开标识。
4. 进入“身份验证 → 高级设置”，将“允许公共客户端流”设为“是”并保存。设备代码登录不需要重定向 URI，也不需要创建客户端密钥。
5. “API 权限”中添加 Microsoft Graph 的“委托权限” `Calendars.ReadWrite`。使用时由每个人登录并授权；组织政策可能要求管理员同意。
6. 将自己的应用 ID 填入实时字典的微软日历连接窗口。可以将同一个公开应用 ID 提供给同学；不要分享访问令牌、登录代码或任何账户密码。

## 每位使用者登录

在日程编辑器补全时间，点击“写入 Outlook / Microsoft 365 日历…”，填应用 ID 并连接。只在自己刚发起登录时，把窗口中的代码输入微软登录页面；核对应用名称、自己的账户及日历权限。回到程序后先检查日历，核对显示的日历账户，再明确确认创建。

默认日历之外的日历、共享日历、邀请参会者、会议室预订和中国世纪互联云尚未接入。登录仅保留在后台内存中；重启后台后需再次登录。这个版本不会自动创建或修改任何演示日程。

## 验收顺序

先只读检查一个已知有安排的时段，确认能显示冲突。然后由用户确认创建一个明确命名的测试日程，在 Outlook 中打开核对起止时间。重复同一操作应提示已有相同事件。测试日程的清理由用户在日历中操作。

截至本次交付，离线模拟通过；没有获得应用 ID 和账户登录，不能声称真实账户创建已验收。

参考：

- https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app
- https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration
- https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code
- https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/user-post-events?view=graph-rest-1.0
