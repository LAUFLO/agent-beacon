# Agent Beacon v1.1.0

Agent Beacon 是 Windows 原生单文件桌面状态灯，用于监控 TRAE、Codex 和 Claude Code。没有携带 Chromium/Electron，适合常驻后台。

## 状态规则

- 绿色：任务进行中
- 黄色闪烁：需要用户确认、授权或输入
- 红色：任务已完成、失败或中断
- Agent 关闭后，对应红绿灯自动消失
- 没有检测到 Agent 时，桌面模式显示 `LOGIN...`

多个 Agent 固定从左到右排列为：TRAE、Codex、Claude Code。每个 Agent 只显示最新任务或当前活动任务，不显示历史列表。

## 两种显示模式

- 桌面灯杆：极简扁平像素红绿灯；多灯使用直角横梁，不使用斜向分叉；设置和关闭位于灯杆上。
- 任务栏紧凑模式：每个运行中的 Agent 显示一个 32×32 像素红绿灯和短灯杆，主窗口、设置和关闭按钮不显示；右键红绿灯可打开设置、刷新、切换模式或退出。

可在设置页切换任务栏紧凑模式，也可使用 `--taskbar` 参数临时启动该模式。

设置窗口同样采用无圆角的扁平像素风：方块开关、分段刷新频率按钮、像素边框和三色状态标识，与桌面灯杆保持一致。

桌面灯杆支持小（当前默认）、中（1.5×）、大（2×）三档等比例缩放；Windows 任务栏图标尺寸由系统固定，不参与缩放。

## Agent 适配

- TRAE：使用独立请求级状态机，组合新版 `.jsonl`/旧版 `.json` Work 会话、真实时间戳日志、低频 Windows UI Automation 和前台窗口确认色标检测。TRAE 当前版本不向 Windows 可访问性树暴露弹窗正文，因此界面兜底识别橙色“等待你的回复”标记且不保存截图；识别后会暂存黄灯，直到出现更新的回复/执行事件。刚启动 Agent 时，旧“运行中”历史不会再直接点亮绿灯；等待确认优先黄灯，回复后的新事件恢复绿灯，完成状态不会被后台事件改写。
- Codex：读取本地 session JSONL，识别任务开始、权限/输入请求、任务完成与中断。`task_complete` 明确结束事件固定显示红色，不再因最终回复中提到“等待确认/输入”等说明文字而误判黄色。
- Claude Code：同时读取本地会话 JSONL、可选 Hooks 和 Claude 子进程状态。Shell/工具执行、工具结果和普通处理为绿色；已经批准且正在运行的长命令会覆盖旧黄色事件；只有权限请求、`AskUserQuestion`、明确的空闲等待或交互请求标黄；最终回复标红。启动时会自动更新已经安装的 Hook 脚本。
- OpenCode 不包含在当前版本中。

设置页会明确显示 Claude Code Hooks 是否已安装。点击“安装/更新 Claude Code Hooks”后才会修改 Claude 配置，并在覆盖前保存备份。

## 性能

- 原生 WinForms 单 EXE，不启动浏览器内核，也不使用 WMI
- Codex JSONL 首次只读取末尾 512 KB，此后从上次字节位置增量解析；TRAE/Claude 候选日志读取 256–512 KB
- 日志和 TRAE 会话内容按长度及修改时间缓存，未变化文件不重复解析
- `FileSystemWatcher` 在会话文件变化后约 900 ms 合并唤醒；普通内容变化不会触发目录重扫，完整目录发现降为 30 秒兜底
- 每轮只生成一份 Agent 进程快照，TRAE 可访问性扫描失败后自动退避 30 秒
- 进程启动时间通过轻量 Win32 句柄读取，只查询匹配到的 Agent；不再逐个读取系统全部进程的 `StartTime`
- 自动排除 Electron/Chromium 缓存、Crashpad、Service Worker 和 `node_modules`
- 每个日志根目录最多遍历 600 个目录，长期缓存会自动清理
- 托盘和任务栏图标预生成缓存，黄灯闪烁不再反复创建 GDI 图标
- 不再强制调用 `GC.Collect` 或清空 Working Set，避免换页和周期性卡顿
- 默认刷新频率为 1 秒，设置页仍可手动选择其他频率

## 状态诊断

设置页显示 TRAE、Codex、Claude Code 当前颜色、判定理由、事件来源和时间，并支持复制脱敏诊断。诊断不包含聊天正文；扫描失败时保留上一份有效状态，不会把全部灯清空。

如果 TRAE 以管理员身份运行，普通权限的 Agent Beacon 可能无法读取其窗口控件，诊断来源会显示权限限制。优先让两个程序使用相同权限级别。

## 状态回放测试

`tests/Replay-State-Tests.ps1` 分别对 TRAE 旧日志、新旧 Work 会话格式、Codex、Claude Code 验证：绿色进行中 → 黄色手动处理 → 红色结束 → 绿色新任务。TRAE 还覆盖刚启动不沿用旧绿灯、确认弹窗黄灯、回复后回绿、完成后变红、JSONL 增量补丁重建、重启后的终态保护、完成后的后台活动不回绿，以及三 Agent 并发状态隔离。

`tests/integration-event-tests.mjs` 验证 Claude Code 事件过滤规则。

## 构建

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /optimize+ /platform:x64 /out:Agent-Beacon-1.1.0.exe /win32icon:assets\Agent-Beacon.ico /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll' /reference:'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll' /resource:integrations\claude-hook.cjs,claude-hook.cjs AgentTrafficLight.cs
```

目标环境：Windows x64 + .NET Framework 4.8（Windows 10/11 通常已包含）。
