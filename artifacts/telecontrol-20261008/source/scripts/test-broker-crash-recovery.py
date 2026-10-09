#!/usr/bin/env python3
"""Kill an owned synthetic BrokerHost, optionally crash its owned PG16 cluster.
Never accepts an existing database, remote vendor or production configuration.
Run after building tests/support/BatteryEms.BrokerRecovery.TestHost in Release.
"""
import argparse
import concurrent.futures
import contextlib
import http.client
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / 'tests/support/BatteryEms.BrokerRecovery.TestHost/bin/Release/net10.0/BatteryEms.BrokerRecovery.TestHost.dll'
TOKEN = 'synthetic-legacy-token-not-real-12345678'


def run(*args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, text=True, timeout=40, **kwargs).stdout


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def wait_for(check, label):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(.1)
    raise AssertionError('Timed out: ' + label)


class Vendor(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        with self.server.count_lock:
            self.server.updates += 1
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'{}')

    def log_message(self, *_):
        pass


def scenario(evidence, phase, crash_database):
    evidence.mkdir()
    child = None
    cluster = Path(tempfile.mkdtemp(prefix='bess-recovery-owned-'))
    # Never delete cluster state if shutdown fails.
    try:
        data = cluster / 'data'
        db_port, web_port = free_port(), free_port()
        run('initdb', '-D', str(data), '-U', 'bess_recovery', '--auth=trust', '--encoding=UTF8', '--locale=C')
        def pg_start():
            run('pg_ctl', '-D', str(data), '-l', str(cluster / 'postgres.log'), '-o',
                f'-h 127.0.0.1 -p {db_port} -k {cluster}', '-w', 'start')
        vendor = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Vendor)
        vendor.updates = 0
        vendor.count_lock = threading.Lock()
        vendor_thread = threading.Thread(target=vendor.serve_forever, daemon=True)
        vendor_thread.start()
        try:
            pg_start()
            run('createdb', '-h', '127.0.0.1', '-p', str(db_port), '-U', 'bess_recovery', 'bess_recovery_owned')
            cert, key, pfx = (cluster / name for name in ('cert.pem', 'key.pem', 'cert.pfx'))
            run('openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj',
                '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1', '-keyout', str(key), '-out', str(cert))
            run('openssl', 'pkcs12', '-export', '-inkey', str(key), '-in', str(cert), '-out', str(pfx), '-passout', 'pass:synthetic-test-only')
            tls = ssl.create_default_context(cafile=str(cert))
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=tls))
            base = f'https://127.0.0.1:{web_port}'
            env = {name: os.environ[name] for name in ('PATH', 'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'LANG', 'LD_LIBRARY_PATH') if name in os.environ}
            env.update({
                'RECOVERY_CONNECTION': f'Host=127.0.0.1;Port={db_port};Database=bess_recovery_owned;Username=bess_recovery;Password=synthetic;',
                'RECOVERY_LISTEN': base,
                'RECOVERY_VENDOR': f'http://127.0.0.1:{vendor.server_port}/update',
                'RECOVERY_EVIDENCE': str(evidence), 'RECOVERY_PHASE': phase,
                'ASPNETCORE_Kestrel__Certificates__Default__Path': str(pfx),
                'ASPNETCORE_Kestrel__Certificates__Default__Password': 'synthetic-test-only',
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
            })
            def start(label):
                with (evidence / (label + '.log')).open('wb') as log:
                    process = subprocess.Popen(['dotnet', str(HOST)], cwd=cluster, env=env, stdout=log, stderr=subprocess.STDOUT)
                return process
            def live():
                if child.poll() is not None:
                    raise AssertionError('Test host exited; inspect evidence log')
                try:
                    with opener.open(base + '/livez', timeout=1) as response:
                        return response.status == 200
                except (OSError, urllib.error.URLError):
                    return False
            def write(body):
                request = urllib.request.Request(base + '/v1/device-writes', data=json.dumps(body).encode(),
                    headers={'Authorization': 'Bearer ' + TOKEN, 'Content-Type': 'application/json'})
                with opener.open(request, timeout=20) as response:
                    return json.load(response)
            def state():
                return run('psql', '-h', '127.0.0.1', '-p', str(db_port), '-U', 'bess_recovery', '-d',
                    'bess_recovery_owned', '-tAc', "SELECT state FROM device_write_broker_attempts;").strip()
            child = start('before-crash')
            wait_for(live, 'TLS host startup')
            body = json.loads((evidence / 'request.json').read_text())
            with concurrent.futures.ThreadPoolExecutor(max_workers=1) as worker:
                in_flight = worker.submit(write, body)
                wait_for(lambda: (evidence / 'checkpoint').exists(), 'durable initiation checkpoint')
                assert state() == 'Initiated'
                child.kill()  # SIGKILL: no managed shutdown or observation.
                child.wait(timeout=10)
                child = None
                with contextlib.suppress(OSError, urllib.error.URLError, http.client.HTTPException):
                    in_flight.result(timeout=10)
            expected = 1 if phase == 'after-send' else 0
            assert vendor.updates == expected
            if crash_database:
                run('pg_ctl', '-D', str(data), '-m', 'immediate', '-w', 'stop')
                pg_start()
            assert state() == 'Initiated'
            child = start('after-crash')
            wait_for(live, 'replacement TLS host startup')
            replay = write(body)
            assert not replay['driverInvoked'], replay
            import uuid
            competing = write({**body, 'AttemptId': str(uuid.uuid4())})
            assert not competing['admitted'], competing
            assert competing['outcomeCode'] == 'device-write-broker-unresolved-attempt', competing
            assert state() == 'Initiated'
            assert vendor.updates == expected
            result = {'phase': phase, 'postgresCrash': crash_database, 'mutationCount': vendor.updates,
                'persistedState': state(), 'replayDriverInvoked': replay['driverInvoked'],
                'replacementAdmitted': competing['admitted'], 'tlsVerified': True, 'passed': True}
            (evidence / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
            return result
        finally:
            if child is not None and child.poll() is None:
                child.kill()
                child.wait(timeout=10)
            vendor.shutdown()
            vendor.server_close()
            vendor_thread.join(timeout=5)
            if (data / 'postmaster.pid').exists():
                run('pg_ctl', '-D', str(data), '-m', 'immediate', '-w', 'stop')
            if (cluster / 'postgres.log').exists():
                shutil.copyfile(cluster / 'postgres.log', evidence / 'postgres.log')
    finally:
        if not (cluster / 'data/postmaster.pid').exists():
            shutil.rmtree(cluster)
        else:
            print(f'Cluster shutdown unconfirmed; preserved at {cluster}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--results', type=Path, required=True)
    args = parser.parse_args()
    if not HOST.is_file():
        raise SystemExit('Build the synthetic test host first')
    if not run('postgres', '--version').startswith('postgres (PostgreSQL) 16.'):
        raise SystemExit('Requires PostgreSQL 16')
    args.results.mkdir(parents=True, exist_ok=False)
    results = [scenario(args.results.resolve() / f'{phase}-pg{int(crash)}', phase, crash)
        for phase in ('before-send', 'after-send') for crash in (False, True)]
    (args.results / 'summary.json').write_text(json.dumps(results, indent=2) + '\n')
    print(json.dumps({'passed': len(results), 'failed': 0, 'results': str(args.results.resolve())}))


if __name__ == '__main__':
    main()
