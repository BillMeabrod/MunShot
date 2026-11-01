"""Renders MunShot's FX tab icon (three sparkles, white on transparent, 128 px) without any image library.

Usage: python Tools/Icons/make_fx_tab_icon.py Assets/MunShot/UI/Icons/tab-fx.png
"""
import struct, sys, zlib

SIZE = 128
SS = 4  # samples per pixel each way

def cubic(p0, p1, p2, p3, n=24):
    pts = []
    for i in range(1, n + 1):
        t = i / n; u = 1 - t
        pts.append((u*u*u*p0[0] + 3*u*u*t*p1[0] + 3*u*t*t*p2[0] + t*t*t*p3[0],
                    u*u*u*p0[1] + 3*u*u*t*p1[1] + 3*u*t*t*p2[1] + t*t*t*p3[1]))
    return pts

def sparkle(cx, cy, r, near=0.1, far=0.29):
    """A four-pointed star: points r out, each side curving in through control points near*r and far*r from the middle."""
    top, right, bottom, left = (cx, cy - r), (cx + r, cy), (cx, cy + r), (cx - r, cy)
    n, f = near * r, far * r
    poly = [top]
    for a, b, c1, c2 in ((top, right, (cx + n, cy - f), (cx + f, cy - n)),
                         (right, bottom, (cx + f, cy + n), (cx + n, cy + f)),
                         (bottom, left, (cx - n, cy + f), (cx - f, cy + n)),
                         (left, top, (cx - f, cy - n), (cx - n, cy - f))):
        poly += cubic(a, c1, c2, b)
    return poly

def inside(poly, x, y):
    hit = False
    j = len(poly) - 1
    for i in range(len(poly)):
        xi, yi = poly[i]; xj, yj = poly[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            hit = not hit
        j = i
    return hit

# The same three sparkles as the chosen mock (B), in its 128-unit box
shapes = [sparkle(52, 52, 42), sparkle(98, 92, 22), sparkle(100, 24, 14)]
bounds = [(min(p[0] for p in s), min(p[1] for p in s), max(p[0] for p in s), max(p[1] for p in s)) for s in shapes]

rows = []
for py in range(SIZE):
    row = bytearray([0])
    for px in range(SIZE):
        covered = 0
        for sy in range(SS):
            y = py + (sy + 0.5) / SS
            for sx in range(SS):
                x = px + (sx + 0.5) / SS
                for s, (x0, y0, x1, y1) in zip(shapes, bounds):
                    if x0 <= x <= x1 and y0 <= y <= y1 and inside(s, x, y):
                        covered += 1
                        break
        a = round(255 * covered / (SS * SS))
        row += bytes((255, 255, 255, a))
    rows.append(bytes(row))

def chunk(tag, data):
    return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)

png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', SIZE, SIZE, 8, 6, 0, 0, 0)) \
      + chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)
print('wrote', sys.argv[1], len(png), 'bytes')
