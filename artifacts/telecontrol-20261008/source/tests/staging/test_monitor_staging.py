import sys
sys.dont_write_bytecode = True
import importlib.util
from datetime import datetime, timezone
import http.server
import json
from pathlib import Path
import ssl
import subprocess
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('staging_monitor', ROOT / 'scripts/monitor-staging.py')
monitor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitor)


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        mode = self.server.mode
        if mode == 'redirect':
            self.send_response(301)
            self.send_header('Location', self.server.url + self.path)
            self.end_headers()
            return
        if self.path == '/operator/':
            self.send_response(503 if mode == 'ui-down' else 200)
            self.end_headers()
            self.wfile.write(b'BESS EMS')
            return
        self.send_response(200)
        self.end_headers()
        payload = {'status': 'ok', 'at': datetime.now(timezone.utc).isoformat(), 'components': {'database': 'ok'}}
        if mode == 'db-down': payload['components']['database'] = 'failed'
        if mode == 'stale': payload['at'] = '2020-01-01T00:00:00Z'
        if mode == 'invalid': payload = []
        self.wfile.write(json.dumps(payload).encode())

    def log_message(self, *_): pass


class StagingMonitorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='bess-monitor-owned-')
        cls.root = Path(cls.directory.name)
        cls.cert, key = cls.root / 'cert.pem', cls.root / 'key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
            '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1',
            '-keyout', str(key), '-out', str(cls.cert)], check=True, capture_output=True)
        cls.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(str(cls.cert), str(key))
        cls.server.socket = context.wrap_socket(cls.server.socket, server_side=True)
        cls.server.url = f'https://127.0.0.1:{cls.server.server_port}'
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()
        cls.directory.cleanup()

    def setUp(self): self.server.mode = 'ok'

    def test_trusted_tls_database_and_ui_with_atomic_evidence(self):
        result = monitor.probe(self.server.url, str(self.cert))
        self.assertTrue(result['healthy'])
        path = self.root / 'metrics/staging.prom'
        monitor.write_metrics(result, path)
        self.assertIn('bess_staging_healthy 1', path.read_text())
        self.assertIn('bess_staging_probe_timestamp_seconds', path.read_text())
        self.assertEqual(list(path.parent.glob('.staging-probe-*')), [])

    def test_untrusted_tls_is_rejected(self):
        self.assertFalse(monitor.probe(self.server.url)['healthy'])

    def test_self_redirect_is_not_followed(self):
        self.server.mode = 'redirect'
        result = monitor.probe(self.server.url, str(self.cert))
        self.assertFalse(result['healthy'])
        self.assertEqual(result['code'], 'redirect-rejected')

    def test_failed_db_ui_stale_and_invalid_responses_are_unhealthy(self):
        for mode in ('db-down', 'ui-down', 'stale', 'invalid'):
            with self.subTest(mode=mode):
                self.server.mode = mode
                self.assertFalse(monitor.probe(self.server.url, str(self.cert))['healthy'])

    def test_credentials_http_and_endpoint_urls_are_refused(self):
        for url in ('http://127.0.0.1', 'https://user:pass@127.0.0.1', 'https://127.0.0.1/agent', 'https://127.0.0.1?token=x'):
            with self.subTest(url=url), self.assertRaises(ValueError): monitor.probe(url)


if __name__ == '__main__': unittest.main()
