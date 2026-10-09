#!/usr/bin/env python3
"""Read-only HTTPS/DB health probe with atomic JSON and Prometheus evidence.
Does not follow redirects, send credentials, or invoke device/agent endpoints.
"""
import argparse
from datetime import datetime, timezone
import http.client
import json
import os
from pathlib import Path
import ssl
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def probe(url, ca_file=None, timeout=10):
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError('Expected a credential-free HTTPS origin')
    if parsed.path not in ('', '/'):
        raise ValueError('Expected an origin, without an endpoint path')
    context = ssl.create_default_context(cafile=ca_file)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
        urllib.request.HTTPSHandler(context=context), NoRedirect())
    start = time.monotonic()
    result = {'checkedAtUtc': datetime.now(timezone.utc).isoformat(), 'healthy': False,
        'https': False, 'database': False, 'ui': False, 'code': 'probe-failed'}
    try:
        with opener.open(url.rstrip('/') + '/health', timeout=timeout) as response:
            if response.status != 200:
                raise ValueError('Health is not ready')
            raw = response.read(65537)
            if len(raw) > 65536:
                raise ValueError('Oversized health response')
            payload = json.loads(raw)
            if not isinstance(payload, dict) or not isinstance(payload.get('components'), dict):
                raise ValueError('Invalid health response')
            observed = datetime.fromisoformat(payload.get('at', '').replace('Z', '+00:00'))
            age = (datetime.now(timezone.utc) - observed).total_seconds()
            if age < -5 or age > 180:
                raise ValueError('Stale health response')
            result['https'] = True
            result['database'] = payload.get('components', {}).get('database') == 'ok'
            result['healthy'] = payload.get('status') == 'ok' and result['database']
        with opener.open(url.rstrip('/') + '/operator/', timeout=timeout) as response:
            result['ui'] = response.status == 200 and b'BESS EMS' in response.read(65536)
        result['healthy'] = result['healthy'] and result['ui']
        result['code'] = 'ok' if result['healthy'] else 'application-unready'
    except urllib.error.HTTPError as error:
        result['healthy'] = False
        result['code'] = 'redirect-rejected' if 300 <= error.code < 400 else f'http-{error.code}'
    except (OSError, urllib.error.URLError, ValueError, TypeError, http.client.HTTPException):
        # Fixed codes keep remote error text and identifiers out of evidence.
        result['healthy'] = False
        result['code'] = 'connection-or-response-invalid'
    result['durationSeconds'] = round(time.monotonic() - start, 4)
    return result


def atomic_write(path, content):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix='.staging-probe-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'w') as stream:
            stream.write(content)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def write_metrics(result, path):
    lines = ['# Staging application health; this does not indicate hardware/write readiness.']
    for name in ('healthy', 'https', 'database', 'ui'):
        lines.append(f'bess_staging_{name} {int(result[name])}')
    lines.extend([f'bess_staging_probe_duration_seconds {result["durationSeconds"]}',
        f'bess_staging_probe_timestamp_seconds {datetime.fromisoformat(result["checkedAtUtc"]).timestamp()}'])
    atomic_write(path, '\n'.join(lines) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', required=True)
    parser.add_argument('--ca-file')
    parser.add_argument('--json', required=True)
    parser.add_argument('--prometheus', required=True)
    args = parser.parse_args()
    result = probe(args.url, args.ca_file)
    atomic_write(args.json, json.dumps(result, indent=2) + '\n')
    write_metrics(result, args.prometheus)
    print(json.dumps(result))
    raise SystemExit(0 if result['healthy'] else 1)


if __name__ == '__main__':
    main()
