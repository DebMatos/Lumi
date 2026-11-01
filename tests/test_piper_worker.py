"""Exercise the actual Python framing/loop with a tiny fake PiperVoice."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKER = ROOT / 'Lumi.Api/Speech/piper_worker.py'


class WorkerTests(unittest.TestCase):
    def run_worker(self, requests, max_text=4000, raw=None):
        with tempfile.TemporaryDirectory(prefix='lumi-worker-test-') as temp:
            folder = Path(temp)
            log = folder / 'loads'
            (folder / 'piper.py').write_text('''import json, os
class PiperVoice:
    @staticmethod
    def load(path):
        print('private load diagnostic')
        with open(os.environ['LUMI_TEST_WORKER_LOG'],'a') as file: file.write('load\\n')
        return PiperVoice()
    def synthesize_wav(self,text,wav):
        print('private synthesis diagnostic')
        if text=='error': raise ValueError('private exception detail')
        wav.setparams((1,2,16000,0,'NONE','not compressed'))
        data=text.encode('utf-8');wav.writeframes(data + (b' ' if len(data)%2 else b''))
''')
            body = raw if raw is not None else b''.join(json.dumps(value, ensure_ascii=False).encode() + b'\n' for value in requests)
            process = subprocess.run([sys.executable, '-u', str(WORKER), '--model', 'unused', '--max-text-length', str(max_text)],
                                     input=body, capture_output=True, timeout=5,
                                     env=dict(os.environ, PYTHONPATH=temp, LUMI_TEST_WORKER_LOG=str(log)))
            loads = log.read_text().splitlines()
        output = io.BytesIO(process.stdout)
        ready = json.loads(output.readline())
        replies = []
        while line := output.readline():
            header = json.loads(line)
            payload = output.read(header['length'])
            self.assertEqual(len(payload), header['length'])
            replies.append((header, payload))
        return process, ready, replies, loads

    def test_single_load_framing_unicode_errors_and_eof(self):
        process, ready, replies, loads = self.run_worker([
            {'id': '1', 'text': 'Olá\nsegunda linha'}, {'id': '2', 'text': 'error'}, {'id': '3', 'text': 'Até já'}])
        self.assertEqual(process.returncode, 0)
        self.assertEqual(ready, {'ready': True, 'protocol': 1})
        self.assertEqual(loads, ['load'])
        self.assertEqual([reply[0]['id'] for reply in replies], ['1', '2', '3'])
        self.assertEqual([reply[0]['ok'] for reply in replies], [True, False, True])
        self.assertEqual(replies[0][1][:4], b'RIFF')
        self.assertIn('Olá segunda linha'.encode(), replies[0][1])
        self.assertNotIn(b'private', process.stdout)
        self.assertIn(b'private exception detail', process.stderr)

    def test_text_validation_does_not_poison_next_frame(self):
        process, _, replies, loads = self.run_worker([
            {'id': '1', 'text': 'too long for limit'}, {'id': '2', 'text': 'ok'}], max_text=5)
        self.assertEqual(process.returncode, 0)
        self.assertEqual([r[0]['ok'] for r in replies], [False, True])
        self.assertEqual(loads, ['load'])

    def test_empty_input_shuts_down_gracefully(self):
        process, _, replies, loads = self.run_worker([])
        self.assertEqual(process.returncode, 0)
        self.assertEqual(replies, [])
        self.assertEqual(loads, ['load'])

    def test_oversize_protocol_frame_terminates(self):
        process, _, replies, _ = self.run_worker([], raw=b'x' * 40000 + b'\n')
        self.assertNotEqual(process.returncode, 0)
        self.assertEqual(replies, [])

    def test_output_buffer_is_bounded(self):
        spec = importlib.util.spec_from_file_location('lumi_piper_worker', WORKER)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        module.MAX_AUDIO_BYTES = 64
        with module.LimitedBuffer() as buffer:
            buffer.write(b'x' * 64)
            with self.assertRaises(ValueError):
                buffer.write(b'x')


if __name__ == '__main__':
    unittest.main()
