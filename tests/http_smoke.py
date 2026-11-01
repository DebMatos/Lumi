"""Linux HTTP contracts with fake voice engines/Ollama; no model downloads."""
from concurrent.futures import ThreadPoolExecutor
import http.client
import http.server
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
import wave

ROOT = Path(__file__).resolve().parents[1]

class Ollama(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            chunks = []
            while True:
                size = int(self.rfile.readline().split(b';')[0], 16)
                if not size:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            body = b''.join(chunks)
        else:
            body = self.rfile.read(int(self.headers['Content-Length']))
        payload = json.loads(body)
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        result = {'message': {'content': 'resposta de teste'}}
        self.wfile.write((json.dumps(result) + ('\n' if payload['stream'] else '')).encode())


def request(port, path, data=None, headers=None, method='POST'):
    conn = http.client.HTTPConnection('127.0.0.1', port, timeout=10)
    try:
        conn.request(method, path, body=data, headers=headers or {})
        response = conn.getresponse()
        return response.status, response.getheader('Content-Type', ''), response.read()
    finally:
        conn.close()


def check(condition, name):
    assert condition, name
    print('PASS', name, flush=True)


def json_post(port, path, payload):
    return request(port, path, json.dumps(payload), {'Content-Type': 'application/json'})


def upload(port, data, mime='audio/wav'):
    boundary = 'lumi-test-boundary'
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="audio"; filename="test.wav"\r\n'
            f'Content-Type: {mime}\r\n\r\n').encode() + data + f'\r\n--{boundary}--\r\n'.encode()
    return request(port, '/audio/transcribe', body, {'Content-Type': f'multipart/form-data; boundary={boundary}'})


with tempfile.TemporaryDirectory(prefix='lumi-http-') as temp:
    folder = Path(temp)
    fixture = folder / 'test.wav'
    with wave.open(str(fixture), 'wb') as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(16000)
        wav.writeframes(bytes(6400))
    fake_whisper = folder / 'whisper'
    fake_whisper.write_text('#!/bin/sh\necho "transcrição de teste"\n')
    fake_whisper.chmod(0o700)
    ollama = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Ollama)
    threading.Thread(target=ollama.serve_forever, daemon=True).start()
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Production',
               Speech__PythonExecutable=sys.executable, Speech__WorkerScript=str(ROOT / 'tests/fixtures/fake_piper_worker.py'),
               Speech__ModelPath=str(fixture), Speech__TimeoutSeconds='3',
               Whisper__Executable=str(fake_whisper), Whisper__ModelPath=str(fixture),
               Ollama__BaseUrl=f'http://127.0.0.1:{ollama.server_port}')
    with (folder / 'server.log').open('w+') as log:
        server = subprocess.Popen(['dotnet', str(ROOT / 'Lumi.Api/bin/Debug/net10.0/Lumi.Api.dll'),
                                   '--urls', f'http://127.0.0.1:{port}'], cwd=ROOT / 'Lumi.Api', env=env, stdout=log, stderr=log)
        try:
            for _ in range(100):
                try:
                    if request(port, '/', method='GET')[0] == 200:
                        break
                except OSError:
                    time.sleep(.1)
            else:
                log.seek(0)
                raise RuntimeError(log.read())
            check(request(port, '/chat.html', method='GET')[0] == 200, 'HTML served')
            check(b'cancelCapture' in request(port, '/chat.html', method='GET')[2], 'new UI served')
            check(request(port, '/chat.js', method='GET')[0] == 200, 'JS served')
            result = json_post(port, '/chat', {'message': 'olá'})
            check(result[0] == 200 and json.loads(result[2])['reply'] == 'resposta de teste', '/chat compatible')
            result = json_post(port, '/chat/stream', {'message': 'olá', 'sessionId': 'test'})
            check(result[0] == 200 and result[2].decode() == 'resposta de teste', '/chat/stream compatible')
            result = upload(port, fixture.read_bytes())
            check(result[0] == 200 and json.loads(result[2])['text'] == 'transcrição de teste', '/audio/transcribe compatible')
            result = json_post(port, '/audio/speak', {'text': 'olá'})
            check(result[0] == 200 and result[1] == 'audio/wav' and result[2][:4] == b'RIFF', '/audio/speak compatible')
            check(json_post(port, '/audio/speak', {'text': 'a' * 4001})[0] == 400, 'TTS text limit HTTP')
            check(upload(port, b'')[0] == 400, 'empty audio HTTP')
            check(upload(port, b'bad', 'text/plain')[0] == 400, 'MIME limit HTTP')
            check(upload(port, b'bad')[0] == 400, 'invalid content HTTP')
            result = json_post(port, '/audio/speak', {'text': 'failure'})
            check(result[0] == 503 and b'private-detail' not in result[2] and str(folder).encode() not in result[2], 'safe engine error HTTP')
            check(json_post(port, '/audio/speak', {'text': 'timeout'})[0] == 504, 'timeout HTTP')
            check(json_post(port, '/audio/speak', {'text': 'after timeout'})[0] == 200, 'recovery after timeout HTTP')
            check(json_post(port, '/audio/speak', {'text': 'crash'})[0] == 503, 'crashed worker HTTP')
            check(json_post(port, '/audio/speak', {'text': 'after crash'})[0] == 200, 'recovery after crash HTTP')
            barrier = threading.Barrier(8)
            def simultaneous(_):
                barrier.wait()
                return json_post(port, '/audio/speak', {'text': 'delay:500'})[0]
            with ThreadPoolExecutor(max_workers=8) as pool:
                statuses = list(pool.map(simultaneous, range(8)))
            check(statuses.count(200) >= 2 and 503 in statuses and set(statuses) <= {200, 503}, 'bounded HTTP queue accepts waiting requests without 429')
            connection = http.client.HTTPConnection('127.0.0.1', port, timeout=5)
            connection.request('POST', '/audio/speak', json.dumps({'text': 'hold'}), {'Content-Type': 'application/json'})
            for _ in range(100):
                events = Path(str(fixture) + '.events').read_text()
                if '"text": "hold"' in events:
                    break
                time.sleep(.01)
            else:
                raise AssertionError('cancel fixture did not start')
            connection.close()
            check(json_post(port, '/audio/speak', {'text': 'after disconnect'})[0] == 200, 'HTTP disconnect cancels active worker and next request recovers')
            check(request(port, '/audio/speak', b'{}', {'Origin': 'http://external.example', 'Content-Type': 'application/json'})[0] == 403, 'cross-origin rejected')
            check(request(port, '/', headers={'Host': 'external.example'}, method='GET')[0] == 400, 'invalid Host rejected')
            check(request(port, '/', headers={'Sec-Fetch-Site': 'cross-site'}, method='GET')[0] == 403, 'cross-site rejected')
            if '--browser' in sys.argv:
                subprocess.run(['node', str(ROOT / 'tests/browser_smoke.cjs'), f'http://127.0.0.1:{port}/chat.html'], check=True, timeout=45)
        finally:
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()
            ollama.shutdown()
