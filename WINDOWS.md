# 龙掌柜的账本 · Windows 桌面版

适用于 Windows 10 / 11 x64，需 .NET Framework 4.8。将下载的程序放在固定目录，双击即可在桌面打开龙掌柜的账房。千问官方组件随程序提供。[下载 Windows x64 桌面版 v1.2.2](https://github.com/Darkericy/DeepSeek-Balance-Whale-Widget/releases/download/windows-v1.2.2/DragonLedger-Windows-x64.exe)

## 首次连接

1. 在 DeepSeek 卡片点 **填写密钥**，粘贴该平台的 API Key，再点 **保存设置**。设置窗口内的 **获取密钥** 可以打开 DeepSeek 的密钥管理页。[DeepSeek 查询余额文档](https://api-docs.deepseek.com/zh-cn/api/get-user-balance/)
2. 在千问 AI 卡片点 **网页登录**，在浏览器中用充值的账号完成授权。看板会复用有效的官方 CLI 登录。千问将 `sk-`、`sk-ws-` 和 `sk-sp-` 模型 Key 用于模型服务，查询充值账户余额需使用管理登录。[千问 CLI 的登录与余额查询](https://platform.qianwenai.com/docs/api-reference/preparation/cli) · [千问可用额度说明](https://platform.qianwenai.com/docs/resources/billing-overview)

如果关闭或更换本机代理后出现千问连接错误，可以在 **设置 → 账户连接** 中勾选 **直连千问** 并保存。看板随后会使用现有登录立即刷新。此选项只作用于千问组件的授权和查询请求。

## 按 API Key 查看用量

从 v1.3.0 起，点击 DeepSeek 卡片上的 **Key 用量**，在账本窗口内登录 DeepSeek 开放平台。在 **时间维度** 中选择统计范围，再从 **API Key** 菜单选择要查看的密钥。点击窗口上方的 **刷新用量** 可重新读取官方页面。余额卡片显示设置中 API Key 所属账户的余额，用量页显示当前网页登录账户的数据。[DeepSeek 官方用量页](https://platform.deepseek.com/usage) · [DeepSeek 官方常见问题](https://static.deepseek.com/faq/index.html?lang=zh)

用量窗口通过 Microsoft Edge WebView2 显示。登录状态保存在当前 Windows 用户的 `%LOCALAPPDATA%\BalanceWatcher\DeepSeekPlatform`，下次打开可继续使用。要切换账户，请从官方页面退出后再登录。如果窗口提示缺少运行时，点击 **获取 Microsoft Edge WebView2 运行时**，安装 Evergreen 运行时后点击 **重试**。[微软运行时下载](https://developer.microsoft.com/microsoft-edge/webview2/#download)

## 桌面使用

- 账本分行显示两个账户。点掌柜或 **刷新** 可立即查询，点 **明细** 可查看 DeepSeek 各币种的充值余额、赠金及两个账户各自的更新时间。千问显示账单账户的可用额度。查询失败时，账本会显示原因和上次成功时间。
- 拖动掌柜或标题可以移动挂件。靠近屏幕边缘松手即可贴边，账页排在掌柜靠近屏幕中央的一侧。看板记住窗口位置，默认置顶，点图钉可切换。
- 点击右上角的减号收起到托盘。单击托盘图标可恢复窗口，右键菜单提供刷新、设置和退出。看板收起后继续监控。
- 打开齿轮 **设置 → 桌面与提醒**，勾选 **登录 Windows 时打开看板** 并保存即可为当前用户配置自启。刷新间隔可选 1、3、5 或 15 分钟，默认每分钟一次。人民币和美元提醒线默认为 ¥10 和 $2，可分别修改或关闭。

DeepSeek 密钥由 Windows DPAPI 按当前用户加密，随设置保存在 `%LOCALAPPDATA%\BalanceWatcher\settings.json`。千问授权凭据由官方 CLI 管理。看板只在内存中保留本次运行收到的余额。

## 从源码构建

在项目目录用 PowerShell 运行 `./build.ps1`。脚本使用 Windows 自带的 .NET Framework 编译器，把界面、图标、千问组件和 WebView2 SDK 的托管库、x64 加载器及许可文本编入同一个可执行文件。首次构建会下载千问 CLI v1.12.0 和 WebView2 SDK，并核对各自的 SHA-256。[千问官方发布](https://github.com/QianWen-AI/qianwen-cli/releases/tag/v1.12.0) · [微软 WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3537.50)

运行 `./build.ps1 -Check` 可验证余额解析、鉴权错误、刷新竞态、提醒、密钥保存和窗口布局。测试使用示例数据以及隔离的空千问配置。第三方许可证位于 `third_party/qianwen-cli-LICENSE.txt`。

## 与 DSH 插件的关系

本版将 MeteorNOX 挂件的气泡、贴边翻转与按压回弹移植到 WPF，并接入两个充值平台。[原项目](https://github.com/MeteorNOX/DeepSeek-Balance-Whale-Widget)

掌柜头像参考 ZipZipPipe 的 GPT 白龙娘立绘及小比例版本。半身比例、无嘴表情和淡紫粗描边参考 T7moris 公开的制作说明。Q 版账簿、袖口小手和算盘另行绘制，再与头像合成。DSH 用户可以在自定义角色中导入带道具的透明图。[作者三视图](https://www.bilibili.com/opus/1238613841710940181) · [半身头像生成说明](https://github.com/T7moris/GPT-Niang/blob/main/gpt-niang-usage/assets/%E7%94%9F%E6%88%90%E8%AF%B4%E6%98%8E.md) · [素材来源与使用条件](PROVENANCE.md) · [掌柜与账本 PNG](ui/shopkeeper-ledger.png)
