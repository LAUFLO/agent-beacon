import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const packaged = path.join(here, '..', 'integrations');
const integrations = fs.existsSync(packaged) ? packaged : path.resolve(here, '..', '..', 'agent-traffic-light', 'integrations');
const { normalizeClaudeEvent } = require(path.join(integrations, 'claude-hook.cjs'));

assert.equal(normalizeClaudeEvent({ hook_event_name: 'PermissionRequest', session_id: 'c1' }, 1).status, 'attention');
assert.equal(normalizeClaudeEvent({ hook_event_name: 'Notification', session_id: 'c1', message: 'build complete' }, 2), null);
assert.equal(normalizeClaudeEvent({ hook_event_name: 'Notification', session_id: 'c1', message: 'please wait while the build runs' }, 2), null);
assert.equal(normalizeClaudeEvent({ hook_event_name: 'Notification', session_id: 'c1', notification_type: 'idle_prompt' }, 3).status, 'attention');
assert.equal(normalizeClaudeEvent({ hook_event_name: 'PreToolUse', session_id: 'c1', tool_name: 'Bash' }, 4).status, 'running');
assert.equal(normalizeClaudeEvent({ hook_event_name: 'PostToolUse', session_id: 'c1', tool_name: 'Bash' }, 5).status, 'running');
assert.equal(normalizeClaudeEvent({ hook_event_name: 'StopFailure', session_id: 'c1' }, 4).status, 'complete');

console.log('PASS Claude event filtering');
