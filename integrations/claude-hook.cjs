'use strict';

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

function safe(value, fallback = 'session') {
  return String(value || fallback).replace(/[^a-z0-9_.-]+/gi, '_').slice(0, 120);
}

function normalizeClaudeEvent(input, now = Date.now()) {
  const eventName = input.hook_event_name || input.event_name || 'Unknown';
  const sessionId = String(input.session_id || 'claude-session');
  const background = Array.isArray(input.background_tasks) ? input.background_tasks : [];
  const notificationType = String(input.notification_type || input.type || '').toLowerCase();
  const notificationText = String(input.message || input.title || input.body || '');
  const manualNotification = /^(idle_prompt|permission_prompt|elicitation_dialog|user_input_required|approval_required)$/.test(notificationType)
    || /(?:waiting for|needs?|requires?) (?:your |user )?(?:input|approval|confirmation|permission)/i.test(notificationText)
    || /(?:等待|需要).{0,10}(?:输入|确认|授权)/.test(notificationText);
  let status = 'running';
  let detail = '正在执行';
  if (eventName === 'PermissionRequest') {
    status = 'attention'; detail = '等待你的确认或输入';
  } else if (eventName === 'Notification') {
    if (!manualNotification) return null;
    status = 'attention'; detail = '空闲等待或请求交互';
  } else if (/^(PreToolUse|PostToolUse|PostToolUseFailure|SubagentStart|SubagentStop)$/.test(eventName)) {
    status = 'running'; detail = '正在执行';
  } else if (eventName === 'Stop') {
    status = background.length ? 'running' : 'complete';
    detail = background.length ? `主回复已结束，仍有 ${background.length} 个后台任务` : '任务已完成';
  } else if (eventName === 'StopFailure') {
    status = 'complete'; detail = '任务失败并结束';
  } else if (eventName === 'SessionEnd') {
    status = 'complete'; detail = '会话已结束';
  }
  return {
    version: 1,
    source: 'Claude Code',
    id: `claude:${sessionId}`,
    sessionId,
    title: input.prompt || input.session_title || input.title || `Claude Code ${sessionId.slice(-6)}`,
    status,
    detail,
    cwd: input.cwd,
    updatedAt: now
  };
}

function writeRecord(record) {
  const dir = path.join(os.homedir(), '.agent-traffic-light', 'events');
  fs.mkdirSync(dir, { recursive: true });
  const target = path.join(dir, `claude-${safe(record.sessionId)}.json`);
  let previous = {};
  try { previous = JSON.parse(fs.readFileSync(target, 'utf8')); } catch { /* first event */ }
  const merged = { ...previous, ...record, startedAt: previous.startedAt || record.updatedAt };
  const temporary = `${target}.${process.pid}.tmp`;
  fs.writeFileSync(temporary, JSON.stringify(merged), 'utf8');
  fs.renameSync(temporary, target);
}

if (require.main === module) {
  let body = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (chunk) => { body += chunk; });
  process.stdin.on('end', () => {
    try { const record = normalizeClaudeEvent(JSON.parse(body || '{}')); if (record) writeRecord(record); } catch { process.exitCode = 0; }
  });
}

module.exports = { normalizeClaudeEvent, writeRecord };
