"""Replay one real Ollama stream in Chrome before/after, using the real Piper.
Requires local models, API build, Chrome and a baseline chat.js path.
"""
import http.client
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

root = Path(__file__).resolve().parents[1]
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
with tempfile.TemporaryFile(mode='w+') as log:
    server = subprocess.Popen(['dotnet', str(root / 'Lumi.Api/bin/Debug/net10.0/Lumi.Api.dll'),
                               '--urls', f'http://127.0.0.1:{port}'], cwd=root / 'Lumi.Api',
                              env=dict(os.environ, ASPNETCORE_ENVIRONMENT='Production'), stdout=log, stderr=log)
    try:
        for _ in range(100):
            connection = http.client.HTTPConnection('127.0.0.1', port, timeout=1)
            try:
                connection.request('GET', '/')
                if connection.getresponse().status == 200:
                    break
            except OSError:
                time.sleep(.1)
            finally:
                connection.close()
        else:
            log.seek(0)
            raise RuntimeError(log.read())
        subprocess.run(['node', 'tests/browser_smoke.cjs', f'http://127.0.0.1:{port}/chat.html',
                        '--latency', str(Path(sys.argv[1]).resolve())], cwd=root, check=True, timeout=240)
    finally:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait()
