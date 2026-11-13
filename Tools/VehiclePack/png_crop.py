"""Crop and enlarge a region of an 8-bit RGB/RGBA PNG (standard library only), to look at small defects.

    python png_crop.py <in.png> <x0> <y0> <x1> <y1> <scale> <out.png>

Coordinates are pixels of the input, y down. Nearest-neighbour enlargement.
"""
import struct
import sys
import zlib


def read(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n'
    pos, idat, width, height, depth, colour = 8, b'', 0, 0, 0, 0
    while pos < len(data):
        n, kind = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + n]
        if kind == b'IHDR':
            width, height, depth, colour = struct.unpack('>IIBB', body[:10])
        elif kind == b'IDAT':
            idat += body
        pos += 12 + n
    assert depth == 8 and colour in (2, 6), 'only 8-bit RGB/RGBA'
    bpp = 3 if colour == 2 else 4
    raw = zlib.decompress(idat)
    stride = width * bpp
    rows, prev = [], bytearray(stride)
    for y in range(height):
        f = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 255
        rows.append(line)
        prev = line
    return width, height, bpp, rows


def write(path, width, height, rows):
    def chunk(kind, body):
        return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body) & 0xffffffff)
    raw = b''.join(b'\x00' + bytes(r) for r in rows)
    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)) +
                           chunk(b'IDAT', zlib.compress(raw, 6)) + chunk(b'IEND', b''))


def main():
    path, x0, y0, x1, y1, scale, out = sys.argv[1], *map(int, sys.argv[2:7]), sys.argv[7]
    width, height, bpp, rows = read(path)
    x1, y1 = min(x1, width), min(y1, height)
    result = []
    for y in range(y0, y1):
        line = bytearray()
        for x in range(x0, x1):
            px = bytes(rows[y][x * bpp:x * bpp + 3])
            line += px * scale
        result.extend([line] * scale)
    write(out, (x1 - x0) * scale, (y1 - y0) * scale, result)
    print('PNG_CROP_OK', out)


if __name__ == '__main__':
    main()
