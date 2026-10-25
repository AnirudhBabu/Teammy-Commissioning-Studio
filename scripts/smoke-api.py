#!/usr/bin/env python3
"""Real HTTP checks; --demo additionally verifies authorization and stale writes."""
import argparse
import json
import time
import urllib.error
import urllib.request
from urllib.parse import urlparse

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', default='http://127.0.0.1:5080')
parser.add_argument('--demo', action='store_true')
args = parser.parse_args()
base = args.base_url.rstrip('/')
if args.demo and urlparse(base).hostname not in {'localhost', '127.0.0.1', '::1'}:
    parser.error('--demo writes synthetic data and is restricted to loopback endpoints')

def request(path, role=None, payload=None):
    headers = {'Accept': 'application/json'}
    if role:
        headers['X-Teammy-Demo-Role'] = role
    if payload is not None:
        headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(base + path, headers=headers,
        data=json.dumps(payload).encode() if payload is not None else None)
    try:
        with urllib.request.urlopen(req, timeout=5) as response:
            body = response.read().decode()
            try:
                body = json.loads(body) if body else None
            except json.JSONDecodeError:
                pass
            return response.status, body
    except urllib.error.HTTPError as error:
        body = error.read().decode()
        try:
            body = json.loads(body)
        except json.JSONDecodeError:
            pass
        return error.code, body

for attempt in range(30):
    try:
        if request('/health/ready')[0] == 200:
            break
    except (OSError, ValueError):
        pass
    time.sleep(1)
else:
    raise SystemExit('Readiness did not become healthy within 30 attempts.')

assert request('/health/live')[0] == 200, 'Liveness failed'
assert request('/api/workspace')[0] == 401, 'Workspace must require authentication'
if args.demo:
    code, state = request('/api/workspace', 'Viewer')
    assert code == 200, f'Viewer read failed: {code}'
    payload = {
        'type': 'createWorkItem', 'expectedRevision': state['revision'],
        'projectId': 'project-conveyor', 'title': 'Deployment smoke verification',
        'description': 'Synthetic item created by the loopback-only HTTP smoke check.',
        'assigneeId': 'engineer-anirudh', 'priority': 'Low'
    }
    assert request('/api/commands', 'Viewer', payload)[0] == 403, 'Viewer write was not denied'
    code, updated = request('/api/commands', 'Engineer', payload)
    assert code == 200, f'Engineer write failed: {code}'
    assert updated['revision'] == state['revision'] + 1, 'Revision did not advance atomically'
    assert request('/api/commands', 'Engineer', payload)[0] == 409, 'Stale revision was not rejected'
    code, audit = request('/api/audit', 'Viewer')
    assert code == 200 and any(x['revision'] == updated['revision'] for x in audit), 'Audit evidence missing'
print('HTTP smoke checks passed' + (' including write, RBAC, concurrency, and audit.' if args.demo else '.'))
