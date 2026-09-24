# DshShell

把 [`dsh web`](https://www.npmjs.com/package/@deepseek-ai/dsh) 包装成一个可双击的 Windows 桌面程序。

启动时在后台拉起 DSH 引擎，前端先播开屏动画并显示环境自检结果，检测到引擎就绪后自动进入界面。
基于**系统自带 WebView2**（不是 Electron），最终产物是**单个自包含 exe**，约 53 MB。

```
双击 DshShell.exe
   ├─ 开屏页：Node / 引擎版本 / WebView2 版本 + 加载动画
   ├─ 后台：解析引擎 → 检查更新 → dsh web --port 0 --no-open
   └─ 就绪后自动进入 DSH 界面（关窗时回收整个引擎进程树）
```

## 它解决了什么

`dsh web` 本身已经很好用，但作为日常工具还差一层外壳：

| 直接跑 `npx dsh web` | 本壳 |
| --- | --- |
| 要开终端，一个黑窗口一直挂着 | 双击即用，无控制台窗口 |
| 引擎端口/地址要自己看 | 自动分配空闲端口，自动导航 |
| Node 没装时报错难懂 | 开屏页直接显示环境自检，指出缺什么 |
| 引擎装坏了要手动清目录 | 界面提供「重新安装引擎」 |
| 首次安装 223 MB 全程无反馈 | 按包计数的下载进度条 |
| 关窗后 Node 进程可能残留 | 关闭时回收整个进程树 |

## 功能

**启动与生命周期**
- 单实例（每 Windows 会话），二次启动唤起已有窗口
- `--port 0` 由系统分配空闲端口，不与已有实例冲突
- 关窗时回收整个进程树，不留孤儿进程
- 引擎中途崩溃或界面加载失败时回到开屏页并给出「重试」，而不是把用户留在死页面上

**引擎管理**
- 三级解析：专用目录 → PATH 上的 `dsh` → npm npx 缓存
- 都没有时用 npm 自动安装到 `%LOCALAPPDATA%\DshShell\engine`
- 只对**自己安装的**引擎做静默更新（每 24 小时最多一次），不动用户在 PATH / npx 缓存里的安装
- 更新前取跨进程互斥锁，避免两个会话同时往同一目录写

**开屏页状态面板**
- 底部环境自检：Node 版本、已装引擎版本、WebView2 版本（缺失项标红）
- 失败时给「重试」「打开日志」；引擎类故障另有「重新安装引擎」（二次确认）
- 安装/更新进度条

**安全边界**
- 只接受回环地址（`127.0.0.1` / `localhost` / `::1`）+ `http` + 合法 token 的就绪 URL，其余一律拒绝
- 日志脱敏：`token=`、JSON 形式的 `"token":"..."`、`Authorization` 头、`api_key` / `password` 等键名一律打码

## 运行环境

- **Windows 10 1809 (17763)** 或更高，x64
- **WebView2 Runtime** —— Win10/11 一般已预装；缺失时程序会提示并给出官方下载链接
- **Node.js** —— 引擎是 Node 程序，必需（[nodejs.org](https://nodejs.org)，建议 LTS）
- 首次运行需联网（自动安装 `@deepseek-ai/dsh`）

## 使用

从 [Releases](https://github.com/hjphh11/dsh-shell/releases/latest) 下载 `DshShell.exe`，双击即可。
无需安装，也无需 .NET 运行时（已内嵌）。

校验下载（可选）：

```powershell
Get-FileHash .\DshShell.exe -Algorithm SHA256
# v0.2.0: 34C6E23A8291A02597BD3BE67B07A7576F4020962C97A3B9C6022DFD1D4752CA
```

> 尚未做代码签名，首次运行可能触发 SmartScreen 提示，选择「更多信息 → 仍要运行」。

## 从源码构建

```powershell
pwsh .\build.ps1                 # 构建 + 发布到 .\publish\DshShell.exe
pwsh .\build.ps1 -RebuildIcon    # 顺带从 SVG 重新生成 app.ico（需要 tools/ 下的 sharp）
```

需要 .NET SDK 10。产物是单个自包含 exe，分发时只需要这一个文件。

> `publish\DshShell.exe` 若正在运行会被文件锁占用，`dotnet publish` 会以 `MSB4018` 失败——先关闭再发布。

## 测试

两层互补，**都不需要联网，也不改动 PATH**：

```powershell
dotnet test .\tests\DshShell.Tests\DshShell.Tests.csproj   # 单元测试，约 2 秒
pwsh .\test.ps1                                          # 端到端冒烟测试，约 10 秒
```

单元测试通过注入的接缝驱动启动失败路径，每种故障都是确定的：

| 场景 | 断言 |
| --- | --- |
| Node.js 不存在 | 报「需要安装 Node.js」，且**不启动任何进程** |
| npm 不存在 / 安装失败 | 报出原因，不继续去启动引擎 |
| 引擎始终不打印 URL | 报启动超时 |
| 引擎就绪前退出 | 报退出码，且**不**当成崩溃 |
| 引擎就绪后崩溃 | 触发崩溃事件，且不报成启动失败 |
| 引擎输出非回环 URL | 绝不作为导航目标 |
| 就绪 URL 策略 | 6 个接受用例 + 12 个拒绝用例（远程主机、LAN 地址、`0.0.0.0`、回环伪装域名、https/file/javascript/ftp、无 token、控制字符、越界端口） |
| 下载进度解析 | 只计 `.tgz`、失败请求不计、同 URL 去重、节流、卡住判定 |
| 重新安装引擎 | 删除所属引擎目录；且**只在引擎类故障时**才提供该按钮 |

冒烟测试启动一个**隔离的真实实例**（独立的日志、单实例对象、WebView2 目录与 harness 主目录），
端到端验证：引擎解析 → 就绪 → URL 校验 → 开屏握手 → 导航 → 干净退出 → 无孤儿进程。

## 安装与更新进度条

首次运行要下载约 **584 个包、223 MB**，更新也可能拉取若干包，这段过程必须有反馈。

`npm install` 在默认日志级别下**直到结束才输出任何内容**，且没有聚合百分比可读。因此：

- npm 以 `--loglevel=http` 运行，每下载一个包产出一行；
- 进度按「已抓取的 tgz 包数 / 584」计算，只计 `.tgz`、失败请求不计、同 URL 去重；
- **持续 2.5 秒没有新包**时切换为不确定态「正在更新引擎…」——因为常规 `@latest` 更新常常只有几个包
  （其余命中 npm 缓存），"3 / 584" 会看起来像卡死。

**两点如实说明**：

1. 分母是估算值（来自实际安装的 lockfile 条目数），进度条**可能停在 100% 之前就完成**。
   对 223 MB 的下载来说，「数字在动」比「精确」更有价值。
2. npm 把 http 日志写到 **stderr 而不是 stdout**，两个流都会解析。

## 排障

| 项 | 位置 |
| --- | --- |
| 日志 | `%TEMP%\dsh-shell.log`（超 2 MB 轮转为 `.old`；凭据已打码） |
| 引擎 | `%LOCALAPPDATA%\DshShell\engine`（删掉即强制重装） |
| WebView2 数据 | `%LOCALAPPDATA%\DshShell\WebView2` |
| 更新节流标记 | `%LOCALAPPDATA%\DshShell\last-update-check` |
| 整体重置 | 删除 `%LOCALAPPDATA%\DshShell` 即可 |

> 日志是**跨会话累积**的。要判断「本次启动是否正常」，请从最后一次 `DshShell starting` 开始看。

### 环境变量（可选，仅供自动化/测试）

正常使用**不需要**设置；不设置时行为与本文档其余部分一致。

| 变量 | 作用 |
| --- | --- |
| `DSHSHELL_LOG` | 覆盖日志路径 |
| `DSHSHELL_INSTANCE` | 使用独立的单实例互斥体/事件名 |
| `DSHSHELL_WEBVIEW2_DIR` | 独立的浏览器用户数据目录 |
| `DSH_HOME` | 独立的 harness 主目录（引擎识别的变量） |

`test.ps1` 用它们启动一个与用户实例互不干扰的隔离实例。

## 已知限制

- 依赖目标机已安装 Node.js（.NET 运行时已内嵌，Node 没有）
- 单实例是**每 Windows 会话**（`Local\`）粒度，多用户会话可各开一个
- 每个 harness 主目录同一时刻只允许一个引擎（引擎插件层持单属主锁），因此同一用户下无法并行运行两个引擎
- 引擎更新到 `@latest`，未固定版本
- 未做代码签名
- 仅 Windows

## 许可

[MIT](LICENSE)。图标源自 DeepSeek 品牌资源，仅用于标识本工具。
