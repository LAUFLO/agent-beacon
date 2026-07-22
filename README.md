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

可以在设置页切换模式，也可以使用 `--taskbar` 参数临时启动任务栏紧凑模式。

## Agent 适配

- TRAE：仅通过本地 MCP 接收显式生命周期事件，不读取 TRAE 日志、Work 会话、窗口控件或截图。等待事件显示黄色，用户回复后的 `running` 事件恢复绿色，完成、失败或取消显示红色。
- Codex：主要读取本地 session JSONL，识别任务开始、确认请求、完成与中断。对于 JSONL 未记录的隐式命令确认，仅在存在尚未结束的 `exec` 时按需检查当前可见审批卡。
- Claude Code：读取本地会话 JSONL，并支持可选 Hooks。工具执行与普通处理显示绿色，权限或交互请求显示黄色，最终回复显示红色。
- OpenCode：使用官方全局插件目录接收任务、权限和问题事件。权限与问题请求显示黄色，匹配的回复解除黄色，任务结束显示红色。

设置页会显示 TRAE MCP、Claude Code Hooks 和 OpenCode 插件的安装或连接状态。集成文件只会在用户点击对应配置按钮后生成。

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
- 发布包为单个桌面 EXE；只有启用 TRAE MCP 时，程序才会释放内嵌的轻量 Helper

各版本的独立更新说明保存在 [`releases`](releases) 目录。
