# Agent Beacon

Agent Beacon 是一款 Windows 原生桌面状态灯，用于集中显示 TRAE、Codex、Claude Code 和 OpenCode 的当前任务状态。程序采用 WinForms 构建，不携带 Chromium、Electron、Node.js 或浏览器内核，适合常驻后台。

![Agent Beacon](assets/Agent-Beacon.png)

## 状态含义

- 绿色：任务正在进行
- 黄色闪烁：需要用户确认、授权或输入
- 红色：任务已完成、失败或中断
- Agent 关闭后，对应状态灯自动隐藏

多个 Agent 固定从左到右排列为：TRAE、Codex、Claude Code、OpenCode。每个 Agent 只显示当前活动任务或最新任务，不展示历史任务列表。

## 显示模式

- 桌面灯杆：显示完整像素风红绿灯，可选择小、中、大三档尺寸。
- 任务栏紧凑模式：每个运行中的 Agent 显示一个紧凑状态灯；右键状态灯可打开设置、刷新、切换模式或退出。
- 设置、状态中心以及应用内确认弹窗统一使用白底重像素风格：直角粗描边、方形阴影按钮和红黄绿像素标识。

可以在设置页切换模式，也可以使用 `--taskbar` 参数临时启动任务栏紧凑模式。

## 实用功能

- TRAE MCP 状态会显示最近通信时间；超过 10 分钟没有有效通信时标记为“已失联”，不再把很久以前的事件当作当前连接。
- 启动时可自动检查 GitHub Release。发现新版本后，程序会下载 EXE、校验 SHA-256 和内部版本号，再退出旧进程并原子替换当前程序。
- 黄灯和红灯支持 Windows 通知；可以分别关闭确认通知、完成通知，按 Agent 单独启用，并提供 22:00–08:00 免打扰模式。
- 通知策略支持黄灯延迟提醒、同一次确认只提醒一次，以及可选的长任务运行提醒。
- 单击桌面灯头或双击任务栏状态灯，可以切换到对应 Agent 窗口。
- 状态中心将实时诊断与本机状态历史合并显示：实时诊断解释当前颜色和扫描异常，历史记录用于回看变化；两者都不保存聊天正文，历史文件会自动限制大小。
- 设置页显示当前版本和今日完成数、运行时长、等待时长；统计只保存匿名计数和时长。
- 扫描频率会根据空闲、运行和等待确认状态自动调节；设置页可一键检查并修复已经配置的 Agent 集成。

## Agent 适配

- TRAE：仅通过本地 MCP 接收显式生命周期事件，不读取 TRAE 日志、Work 会话、窗口控件或截图。等待事件显示黄色，用户回复后的 `running` 事件恢复绿色，完成、失败或取消显示红色。
- Codex：主要读取本地 session JSONL，通过集中兼容层同时识别旧版 `custom_tool_call / exec` 和新版 `function_call / exec_command`，并识别任务开始、确认请求、完成与中断。对于 JSONL 未记录的隐式命令确认，仅在存在尚未结束的命令时按需检查当前可见审批卡。
- Claude Code：读取本地会话 JSONL，并支持可选 Hooks。工具执行与普通处理显示绿色，权限或交互请求显示黄色，最终回复显示红色。
- OpenCode：使用官方全局插件目录接收任务、权限和问题事件。权限与问题请求显示黄色，匹配的回复解除黄色，任务结束显示红色。

设置页会显示 TRAE MCP 最近通信或失联时间，以及 Claude Code Hooks 和 OpenCode 插件的安装状态。集成文件只会在用户点击对应配置按钮后生成。

## 配置 TRAE MCP

1. 打开 Agent Beacon 设置，点击“复制 TRAE MCP 配置”。
2. 在 TRAE Work 中进入“设置 > MCP > 本地 > 创建 > 手动配置”，粘贴配置并确认。
3. 在“设置 > 对话流”中开启“自动运行 MCP”。
4. 回到 Agent Beacon 设置，点击“复制 TRAE 状态规则”。
5. 在 TRAE Work 的“设置 > 规则”中创建本地全局规则并粘贴。
6. 新建任务，依次验证执行中绿灯、等待确认黄灯、确认后绿灯和完成后红灯。

固定 MCP Helper 路径为：

```text
%LOCALAPPDATA%\AgentTrafficLight\integrations\Agent-Beacon-MCP.exe
```

Helper 由 Agent Beacon 按内容哈希自动更新。若设置页提示 Helper 正被占用，请完全退出 TRAE，重新启动 Agent Beacon 完成替换，再打开 TRAE。从 v1.3.0 升级的用户需要删除旧 MCP 配置并按以上步骤重新创建一次；之后升级无需修改命令路径。

## 运行环境

- Windows 10/11 x64
- .NET Framework 4.8
- 单文件 EXE：直接运行的便携版本
- Portable ZIP：包含 EXE、SHA-256 和应用说明
- Setup EXE：安装到当前用户目录，创建开始菜单入口并支持卸载
- 只有启用 TRAE MCP 时，程序才会释放内嵌的轻量 Helper

各版本的独立更新说明保存在 [`releases`](releases) 目录。
