"""Build the app's geometric capture mark without an external image dependency."""
from pathlib import Path
import struct
import zlib

size = 64
pixels = bytearray()
for y in range(size):
    pixels.append(0)
    for x in range(size):
        corner = ((12 <= x < 19 or 45 <= x < 52) and (12 <= y < 29 or 35 <= y < 52)) or ((12 <= y < 19 or 45 <= y < 52) and (12 <= x < 29 or 35 <= x < 52))
        pixels.extend((238, 32, 46, 255) if corner else (8, 8, 9, 255))

def chunk(kind, content):
    return struct.pack('>I', len(content)) + kind + content + struct.pack('>I', zlib.crc32(kind + content))

png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b'')
directory = struct.pack('<HHH', 0, 1, 1) + struct.pack('<BBBBHHII', size, size, 0, 0, 1, 32, len(png), 22)
target = Path(__file__).resolve().parents[1] / 'Assets' / 'dama.ico'
target.parent.mkdir(exist_ok=True)
target.write_bytes(directory + png)
