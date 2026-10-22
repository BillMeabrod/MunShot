"""Draws MunShot's app tray icon: a white camera on transparent, 48x48 like the stock toolbar icons.

No image library needed: shapes are tested per pixel (4x4 supersampled for smooth edges) and written as a PNG.
Run: python Tools/Icons/make_toolbar_icon.py Assets/MunShot/UI/toolbar.png
"""
import struct
import sys
import zlib

SIZE = 48
SAMPLES = 4


def rounded_rect(x, y, left, top, right, bottom, radius):
    if not (left <= x <= right and top <= y <= bottom):
        return False
    cx = min(max(x, left + radius), right - radius)
    cy = min(max(y, top + radius), bottom - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def inside(x, y):
    body = rounded_rect(x, y, 5, 14, 43, 40, 5)
    bump = rounded_rect(x, y, 16, 9, 30, 16, 2)
    lens_outer = (x - 24) ** 2 + (y - 27) ** 2 <= 9.5 ** 2
    lens_inner = (x - 24) ** 2 + (y - 27) ** 2 <= 5.5 ** 2
    flash = rounded_rect(x, y, 35, 18, 39, 21, 1)
    shape = (body or bump) and not (lens_outer and not lens_inner) and not flash
    return shape


def pixels():
    rows = []
    for py in range(SIZE):
        row = bytearray([0])  # PNG filter: none
        for px in range(SIZE):
            hits = 0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    if inside(px + (sx + 0.5) / SAMPLES, py + (sy + 0.5) / SAMPLES):
                        hits += 1
            alpha = round(255 * hits / (SAMPLES * SAMPLES))
            row += bytes([255, 255, 255, alpha])
        rows.append(bytes(row))
    return b"".join(rows)


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def main(path):
    header = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)  # 8-bit RGBA
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(pixels(), 9)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


if __name__ == "__main__":
    main(sys.argv[1])
