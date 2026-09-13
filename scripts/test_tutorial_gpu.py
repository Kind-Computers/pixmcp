import base64
import json
from pathlib import Path
import struct
import tempfile
from types import SimpleNamespace
import unittest
import zlib

from tutorial_gpu import _retrieve_image


def png(width):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, 1, 8, 0, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(b'\0' + b'\x80' * width)) + chunk(b'IEND', b''))


class ImageEvidenceTests(unittest.TestCase):
    def test_inline_thumbnail_does_not_replace_original_with_same_name(self):
        original, thumbnail = png(2), png(1)
        metadata = {'width': 1, 'height': 1}
        response = {'result': {'structuredContent': metadata, 'content': [
            {'type': 'text', 'text': json.dumps(metadata)},
            {'type': 'image', 'data': base64.b64encode(thumbnail).decode()}]}}
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'scene.png'
            source.write_bytes(original)
            ctx = SimpleNamespace(artifacts=Path(directory),
                client=SimpleNamespace(send=lambda *args: response),
                query=lambda *args, **kwargs: {'base64': base64.b64encode(original).decode(),
                    'returnedBytes': len(original), 'totalBytes': len(original), 'offset': 0})
            first = _retrieve_image(ctx, 'artifact', 'scene', str(source))
            second = _retrieve_image(ctx, 'artifact', 'scene', str(source))
            self.assertEqual(original, source.read_bytes())
            self.assertEqual(thumbnail, Path(first['path']).read_bytes())
            self.assertNotEqual(first['path'], second['path'])
            self.assertTrue(first['originalFullyRetrieved'])


if __name__ == '__main__':
    unittest.main()
