"""Measure real Piper HTTP latency/playback gaps, without calling Ollama.
Requires the configured Piper environment/model, API build and Chrome.
"""
import http.client
import json
import re
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
with tempfile.TemporaryDirectory(prefix='lumi-voice-pauses-') as temp, tempfile.TemporaryFile(mode='w+') as log:
    server = subprocess.Popen(['dotnet', str(root / 'Lumi.Api/bin/Debug/net10.0/Lumi.Api.dll'),
                               '--urls', f'http://127.0.0.1:{port}',
                               '--contentRoot', str(root / 'Lumi.Api')], cwd=temp,
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
        subprocess.run(['node', str(root / 'tests/browser_smoke.cjs'), f'http://127.0.0.1:{port}/chat.html',
                        '--pauses'], cwd=root, check=True, timeout=240)
    finally:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait()

        log.seek(0)
        pids = re.findall(r'Worker Piper pronto, PID (\d+)', log.read())
        assert len(pids) == 1, f'Expected one loaded model, observed workers: {pids}'
        try:
            os.kill(int(pids[0]), 0)
        except ProcessLookupError:
            pass
        else:
            raise AssertionError('Worker survived API shutdown')
        print(json.dumps({'workerStarts':len(pids),'workerPids':pids,'workerReapedOnShutdown':True}),flush=True)
