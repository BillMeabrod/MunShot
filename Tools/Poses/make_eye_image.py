"""Draws MunShot's anime iris: a teal iris with a dark rim and pupil and white highlights, on transparent, for poses'
"eyes" (laid over each eyeball by EyeOverlay). Seen from the front: up is up, right is the viewer's right.

No image library needed: each pixel is worked out (4x4 supersampled for smooth edges) and written as a PNG.
Run: python Tools/Poses/make_eye_image.py
"""
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', '..', 'Assets', 'MunShot', 'Copied', 'Poses', 'anime_teal.png')
SIZE = 256
SAMPLES = 4

RIM = (0, 58, 74)
TOP = (0, 104, 122)        # the iris is darker under the upper lid
BOTTOM = (70, 226, 220)    # and lighter towards the bottom, as anime eyes are
PUPIL = (6, 34, 44)
WHITE = (255, 255, 255)


def blend(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def in_ellipse(x, y, cx, cy, rx, ry):
    return ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1.0


def colour(x, y):
    """Colour and opacity at (x, y), -0.5..0.5 with y up."""
    r = math.hypot(x, y)
    if r > 0.5:
        return (0, 0, 0), 0.0
    if in_ellipse(x, y, 0.15, 0.17, 0.12, 0.09):          # big highlight, upper right
        return WHITE, 1.0
    if in_ellipse(x, y, -0.16, -0.2, 0.055, 0.055):       # small highlight, lower left
        return WHITE, 1.0
    if r > 0.45:
        return RIM, 1.0
    if in_ellipse(x, y, 0.0, 0.02, 0.15, 0.2):            # pupil, a little tall
        return PUPIL, 1.0
    c = blend(TOP, BOTTOM, (0.5 - y))                      # top to bottom
    # A soft glow ring low in the iris, fading in towards the bottom
    glow = max(0.0, 1.0 - abs(r - 0.33) / 0.06) * min(1.0, max(0.0, -y / 0.15))
    return blend(c, (150, 245, 240), 0.5 * glow), 1.0


def render():
    rows = []
    for py in range(SIZE):
        row = bytearray([0])  # PNG filter: none
        for px in range(SIZE):
            acc, alpha = [0.0, 0.0, 0.0], 0.0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    x = (px + (sx + 0.5) / SAMPLES) / SIZE - 0.5
                    y = 0.5 - (py + (sy + 0.5) / SAMPLES) / SIZE   # PNG rows go top to bottom
                    c, a = colour(x, y)
                    acc = [s + v * a for s, v in zip(acc, c)]
                    alpha += a
            n = SAMPLES * SAMPLES
            if alpha > 0:
                rgb = [round(s / alpha) for s in acc]
            else:
                rgb = [0, 0, 0]
            row += bytes(rgb + [round(255 * alpha / n)])
        rows.append(bytes(row))
    return b''.join(rows)


def png(width, height, raw):
    def chunk(kind, data):
        body = kind + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xffffffff)
    header = struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)  # 8-bit RGBA
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')


if __name__ == '__main__':
    with open(OUT, 'wb') as f:
        f.write(png(SIZE, SIZE, render()))
    print(f'{SIZE}x{SIZE} -> {os.path.normpath(OUT)}')
