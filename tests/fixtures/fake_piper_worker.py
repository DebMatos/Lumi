"""Protocol fixture, no dependency on Piper or model files."""
import argparse
import io
import json
import os
from pathlib import Path
import sys
import time
import wave

parser = argparse.ArgumentParser()
parser.add_argument('--model', required=True)
parser.add_argument('--max-text-length')
args = parser.parse_args()
output = sys.stdout.buffer
log = Path(args.model + '.events')


def event(name, **values):
    with log.open('a') as file:
        file.write(json.dumps(dict(event=name, pid=os.getpid(), **values)) + '\n')


def header(value):
    output.write(json.dumps(value).encode() + b'\n')
    output.flush()


event('start')
if Path(args.model).name == 'boot-failure':
    sys.exit(8)
header(dict(ready=True, protocol=1))
for line in sys.stdin.buffer:
    request = json.loads(line)
    text, request_id = request['text'], request['id']
    event('request', text=text)
    if text == 'crash':
        os._exit(7)
    if text in ('hold', 'timeout'):
        time.sleep(10)
    if text.startswith('delay:'):
        time.sleep(int(text.split(':')[1]) / 1000)
    if text == 'failure':
        header(dict(id=request_id, ok=False, length=0, error='private-detail'))
        continue
    if text == 'bad-header':
        output.write(b'x' * 2048 + b'\n')
        output.flush()
        continue
    if text == 'bad-id':
        request_id = 'old-request'
    if text == 'bad-length':
        header(dict(id=request_id, ok=True, length=100_000_000))
        continue
    if text == 'stderr':
        sys.stderr.write('x' * 200000)
        sys.stderr.flush()
    buffer = io.BytesIO()
    with wave.open(buffer, 'wb') as wav:
        wav.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
        data = text.encode('utf-8')
        wav.writeframes(data + (b' ' if len(data) % 2 else b''))
    audio = buffer.getvalue()
    header(dict(id=request_id, ok=True, length=len(audio)))
    if text == 'truncated':
        output.write(audio[:10])
        output.flush()
        os._exit(9)
    output.write(audio)
    output.flush()
event('eof')
