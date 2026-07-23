import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const safe = (value) => String(value).replace(/[^a-z0-9_.-]+/gi, '_').slice(0, 120);
const pick = (properties, names) => {
  for (const name of names) if (properties?.[name] !== undefined && properties?.[name] !== null) return properties[name];
  return undefined;
};

function writeEvent(event, directory, projectDirectory) {
  const properties = event?.properties || {};
  const session = properties.session || properties.info || {};
  const rawSessionId = pick(properties, ['sessionID', 'sessionId']) ?? session.id;
  if (rawSessionId === undefined || rawSessionId === null) return false;
  const sessionId = String(rawSessionId).trim();
  if (!/^[a-z0-9][a-z0-9_.:-]{2,127}$/i.test(sessionId)) return false;

  fs.mkdirSync(directory, { recursive: true });
  const target = path.join(directory, `opencode-${safe(sessionId)}.json`);
  let previous = {};
  try { previous = JSON.parse(fs.readFileSync(target, 'utf8')); } catch { /* first event */ }

  const eventType = event?.type;
  let status;
  let detail;
  let phase;
  let pendingInteraction = previous.pendingInteraction || null;
  const requestId = String(pick(properties, ['requestID', 'requestId']) ?? '').trim();
  const askedKind = eventType === 'question.asked' ? 'question' : eventType === 'permission.asked' ? 'permission' : '';
  const resolvedKind = eventType === 'question.replied' || eventType === 'question.rejected' ? 'question' : eventType === 'permission.replied' ? 'permission' : '';

  if (askedKind) {
    status = 'attention';
    detail = askedKind === 'question' ? '等待你回答问题或选择选项' : '等待你的权限确认';
    phase = askedKind === 'question' ? '等待输入' : '等待确认';
    pendingInteraction = { kind: askedKind, requestID: requestId };
  } else if (resolvedKind) {
    if (pendingInteraction?.kind && pendingInteraction.kind !== resolvedKind) return false;
    if (pendingInteraction?.requestID && requestId && pendingInteraction.requestID !== requestId) return false;
    status = 'running';
    detail = eventType === 'question.rejected' ? '问题已忽略，继续执行' : '已确认，继续执行';
    phase = '继续处理';
    pendingInteraction = null;
  } else if (eventType === 'session.idle') {
    if (pendingInteraction) return false;
    status = 'complete'; detail = '任务已完成'; phase = '已完成'; pendingInteraction = null;
  } else if (eventType === 'session.error') {
    status = 'complete'; detail = '任务失败并结束'; phase = '已失败'; pendingInteraction = null;
  } else if (eventType === 'tool.execute.before') {
    if (pendingInteraction) return false;
    status = 'running'; detail = '正在执行工具'; phase = '执行工具';
  } else if (eventType === 'session.status') {
    const kind = properties.status?.type || properties.status;
    if (pendingInteraction) return false;
    if (kind === 'idle') { status = 'complete'; detail = '任务已完成'; phase = '已完成'; pendingInteraction = null; }
    else if (kind === 'busy' || kind === 'running' || kind === 'retry') { status = 'running'; detail = '正在执行'; phase = kind === 'retry' ? '正在重试' : '处理中'; pendingInteraction = null; }
    else return false;
  } else {
    return false;
  }

  const now = Date.now();
  const reportedProgress = Number(properties.status?.progress ?? properties.progress);
  const progress = Number.isFinite(reportedProgress) && reportedProgress >= 0 && reportedProgress <= 100 ? Math.round(reportedProgress) : undefined;
  const row = {
    ...previous,
    version: 2,
    source: 'OpenCode',
    id: `opencode:${sessionId}`,
    sessionId,
    title: session.title || properties.title || previous.title || `OpenCode ${sessionId.slice(-6)}`,
    status,
    detail,
    phase,
    progress,
    cwd: projectDirectory || previous.cwd,
    eventType,
    pendingInteraction,
    startedAt: previous.startedAt || now,
    lastActivityAt: now,
    updatedAt: now
  };
  const temporary = `${target}.${process.pid}.tmp`;
  fs.writeFileSync(temporary, JSON.stringify(row), 'utf8');
  fs.renameSync(temporary, target);
  return true;
}

export { writeEvent };

export const AgentBeaconPlugin = async ({ directory: projectDirectory, worktree } = {}) => {
  const directory = path.join(os.homedir(), '.agent-traffic-light', 'events');
  return { event: async ({ event }) => writeEvent(event, directory, worktree || projectDirectory) };
};
