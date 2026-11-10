"""Renders MunShot's Filters tab icon (a round camera filter, its top half shaded as a graduated filter's, white on
transparent, 128 px) without any image library.

Usage: python Tools/Icons/make_filters_tab_icon.py Assets/MunShot/UI/Icons/tab-filters.png
"""
import struct, sys, zlib

SIZE = 128
SS = 4  # samples per pixel each way
CX = CY = 64
OUTER, INNER = 58, 46  # the filter's rim
GLASS = 38             # the shaded glass, inside a gap from the rim
EDGE = 66              # where the shading ends: a little below the middle


def covered(x, y):
    d2 = (x - CX) ** 2 + (y - CY) ** 2
    if INNER * INNER <= d2 <= OUTER * OUTER:
        return True
    return d2 <= GLASS * GLASS and y <= EDGE


rows = []
for py in range(SIZE):
    row = bytearray([0])
    for px in range(SIZE):
        hits = sum(covered(px + (sx + 0.5) / SS, py + (sy + 0.5) / SS) for sy in range(SS) for sx in range(SS))
        row += bytes((255, 255, 255, round(255 * hits / (SS * SS))))
    rows.append(bytes(row))


def chunk(tag, data):
    return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)


png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', SIZE, SIZE, 8, 6, 0, 0, 0)) \
      + chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)
print('wrote', sys.argv[1], len(png), 'bytes')
