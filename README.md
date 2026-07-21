# Agent Beacon v1.3.0

Agent Beacon 是 Windows 原生单文件桌面状态灯，用于监控 TRAE、Codex、Claude Code 和 OpenCode。没有携带 Chromium/Electron，适合常驻后台。

v1.3.0 为 TRAE 增加本地 MCP 主动上报通道。Agent Beacon 主程序内嵌一个轻量控制台 MCP Helper；在设置页复制配置后，TRAE 可通过标准输入输出上报 `running / waiting / completed / failed / cancelled`。MCP 事件优先于可变日志和后台心跳，等待状态立即黄灯闪烁，完成、失败或取消立即红灯；只有新的任务 ID 或明确恢复事件才能变回绿色。未安装 MCP 时仍保留原有日志和界面识别作为降级方案。

v1.2.3 适配 TRAE Work 将确认卡和最终回复都包装为 `progressMessage` 的新结构：等待用户回复的语义优先标黄，明确结束且没有后续动作的语义进入候选完成。视觉确认检测不再要求 TRAE 必须是前台窗口；后台可见窗口仍可建立黄色锁，但只有 TRAE 在前台且确认标记确实消失时才能清除该锁，避免切换窗口或截图时黄灯丢失。新版会通知 v1.2.2 及更早版本的托盘进程退出。

v1.2.2 修复了 TRAE Work 最终回答已经生成、但 `modelState.value` 仍残留为 `0` 时一直显示绿色的问题。TRAE 的有序响应尾部优先于可被后台回写的摘要状态：最后一项是结构化最终响应且没有继续执行证据时，会形成候选完成；候选状态连续稳定 5 秒后才转红，如果期间追加工具或进度事件则继续保持绿色，避免执行途中短暂红灯。

v1.2.1 重构了 Codex 桌面确认检测，以适配 Codex 集成到 ChatGPT Windows 桌面应用后的多进程窗口结构。状态灯同时映射 `ChatGPT.exe` 窗口宿主和 `codex.exe` 后端，但仍只在检测到 Codex 任务时显示；只有当前可见的审批标题与可点击“允许”按钮同时存在才显示黄色，已经进入聊天历史的审批文字不会误报。审批卡关闭后恢复 JSONL 会话状态。

v1.2.0 完整重构了 TRAE 监控：新增独立 `TraeStateEngine.cs`，将可变会话快照归一化为请求级事件，并用 TRAE 专用单向显示状态机取代旧关键词堆叠和通用终态门控。新版本按真实请求时间戳选择最新任务，完成状态不再被后台心跳、数组乱序、响应清空或 `modelState` 回写覆盖；只有真正新增的运行/确认响应或时间戳更晚的新请求才能解除终态。

## 状态规则

- 绿色：任务进行中
- 黄色闪烁：需要用户确认、授权或输入
- 红色：任务已完成、失败或中断
- Agent 关闭后，对应红绿灯自动消失
- 没有检测到 Agent 时，桌面模式显示 `LOGIN...`

多个 Agent 固定从左到右排列为：TRAE、Codex、Claude Code、OpenCode。每个 Agent 只显示最新任务或当前活动任务，不显示历史列表。

## 两种显示模式

- 桌面灯杆：极简扁平像素红绿灯；多灯使用直角横梁，不使用斜向分叉；设置和关闭位于灯杆上。
- 任务栏紧凑模式：每个运行中的 Agent 显示一个 32×32 像素红绿灯和短灯杆，主窗口、设置和关闭按钮不显示；右键红绿灯可打开设置、刷新、切换模式或退出。

可在设置页切换任务栏紧凑模式，也可使用 `--taskbar` 参数临时启动该模式。

设置窗口同样采用无圆角的扁平像素风：方块开关、分段刷新频率按钮、像素边框和三色状态标识，与桌面灯杆保持一致。

桌面灯杆支持小（当前默认）、中（1.5×）、大（2×）三档等比例缩放；Windows 任务栏图标尺寸由系统固定，不参与缩放。

## Agent 适配

- TRAE：优先读取本地 MCP 的显式生命周期事件。MCP 的完成、失败和取消状态不经过日志方案的 5 秒终态候选窗口，立即显示红灯，并拒绝被后台日志心跳覆盖；等待事件立即黄灯，收到用户回复后的 `running` 事件解除黄色。可见确认弹窗仍作为漏报黄色的兜底。未配置 MCP 时，继续使用结构化 Work 会话状态机和时间戳日志降级检测。
- Codex：读取本地 session JSONL，识别任务开始、明确的权限/输入请求、任务完成与中断；桌面确认适配器同时检查 ChatGPT 窗口宿主与 Codex 后端进程。仅当当前窗口同时存在可见审批标题和可点击允许按钮时标黄，审批卡关闭后立即回到日志状态。普通 `apply_patch`、Shell 和工具调用始终按执行中处理，不再因工具耗时产生绿→黄→绿的闪动。`task_complete` 明确结束事件固定显示红色。
- Claude Code：同时读取本地会话 JSONL、可选 Hooks 和 Claude 子进程状态。Shell/工具执行、工具结果和普通处理为绿色；已经批准且正在运行的长命令会覆盖旧黄色事件；只有权限请求、`AskUserQuestion`、明确的空闲等待或交互请求标黄；最终回复标红。启动时会自动更新已经安装的 Hook 脚本。
- OpenCode：使用官方全局插件目录 `~/.config/opencode/plugins/`。只接收带有效会话 ID 的任务、权限与问题事件；`permission.asked` 和 `question.asked` 显示黄灯，匹配请求 ID 的回复或忽略事件解除黄灯。等待期间的 busy/idle/工具事件不会覆盖黄灯；无关事件和占位会话 ID 被拒绝。

设置页会显示 TRAE MCP 是否已准备/连接，以及 Claude Code Hooks 和 OpenCode 插件是否已安装。点击对应按钮后才会生成或写入集成文件；Claude 配置在覆盖前保存备份。

## 配置 TRAE 本地 MCP

1. 打开 Agent Beacon 设置，点击“复制 TRAE MCP 配置”。
2. 在 TRAE Work 中进入“设置 > MCP > 本地 > 创建 > 手动配置”，粘贴配置并确认。
3. 在“设置 > 对话流”中开启“自动运行 MCP”，避免状态上报本身触发确认。
4. 回到 Agent Beacon 设置，点击“复制 TRAE 状态规则”。
5. 在 TRAE Work 的“设置 > 规则”中创建本地全局规则并粘贴。
6. 新建一个 TRAE 任务，依次验证执行中绿灯、等待确认黄灯、确认后绿灯和完成后红灯。设置页出现“TRAE MCP 已连接”表示已收到真实事件。

发布包仍只有一个桌面 EXE。只有启用 TRAE MCP 时，Agent Beacon 才会将内嵌的轻量 Helper 解压到 `%LOCALAPPDATA%\AgentTrafficLight\integrations`；它不包含 Node.js 或浏览器内核。

## 性能

- 原生 WinForms 单 EXE，不启动浏览器内核，也不使用 WMI
- Codex JSONL 首次只读取末尾 512 KB，此后从上次字节位置增量解析；TRAE/Claude 候选日志读取 256–512 KB
- 日志和 TRAE 会话内容按长度及修改时间缓存，未变化文件不重复解析
- `FileSystemWatcher` 在会话文件变化后约 900 ms 合并唤醒；普通内容变化不会触发目录重扫，完整目录发现降为 30 秒兜底
- 每轮只生成一份 Agent 进程快照，TRAE 可访问性扫描失败后自动退避 30 秒
- Codex 桌面确认仅扫描 ChatGPT/Codex 窗口中的文本与按钮控件，并使用 1 秒结果缓存，不遍历聊天正文数据文件
- 进程启动时间通过轻量 Win32 句柄读取，只查询匹配到的 Agent；不再逐个读取系统全部进程的 `StartTime`
- 自动排除 Electron/Chromium 缓存、Crashpad、Service Worker 和 `node_modules`
- 每个日志根目录最多遍历 600 个目录，长期缓存会自动清理
- 托盘和任务栏图标预生成缓存，黄灯闪烁不再反复创建 GDI 图标
- 不再强制调用 `GC.Collect` 或清空 Working Set，避免换页和周期性卡顿
- 默认刷新频率为 1 秒，设置页仍可手动选择其他频率

## 状态诊断

设置页显示 TRAE、Codex、Claude Code、OpenCode 当前颜色、判定理由、事件来源和时间，并支持复制脱敏诊断。诊断不包含聊天正文；扫描失败时保留上一份有效状态，不会把全部灯清空。

如果 TRAE 以管理员身份运行，普通权限的 Agent Beacon 可能无法读取其窗口控件，诊断来源会显示权限限制。优先让两个程序使用相同权限级别。

## 状态回放测试

`tests/Replay-State-Tests.ps1` 分别对 TRAE MCP、TRAE 旧日志、新旧 Work 会话格式、Codex、Claude Code、OpenCode 验证生命周期。TRAE MCP 额外覆盖显式完成立即红灯，以及后台日志不能覆盖 MCP 终态。

`tests/Mcp-Protocol-Tests.ps1` 会编译并启动 Helper，验证 MCP 初始化、工具发现以及绿 → 黄 → 绿 → 红的真实 stdio JSON-RPC 回放。

`tests/integration-event-tests.mjs` 验证 Claude Code/OpenCode 事件过滤，以及 OpenCode 问题/权限黄灯锁、匹配回复解除、无效会话 ID 和无关事件拒绝规则。

## 构建

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /optimize+ /platform:anycpu /out:integrations\Agent-Beacon-TRAE-MCP-1.3.0.exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll TraeMcpHost.cs

& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /optimize+ /platform:x64 /out:Agent-Beacon-1.3.0.exe /win32icon:assets\Agent-Beacon.ico /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll' /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll' /resource:integrations\claude-hook.cjs,claude-hook.cjs /resource:integrations\opencode-plugin.js,opencode-plugin.js /resource:integrations\Agent-Beacon-TRAE-MCP-1.3.0.exe,trae-mcp-host.exe AgentTrafficLight.cs TraeStateEngine.cs
```

目标环境：Windows x64 + .NET Framework 4.8（Windows 10/11 通常已包含）。
