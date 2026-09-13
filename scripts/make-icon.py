#!/usr/bin/env python3
"""Draws the application icon and writes a multi-resolution Windows .ico.

Committed alongside the icon it produces, because an icon nobody can regenerate is a binary
somebody has to redraw by hand the first time it needs changing.

No image library is used. PNG and ICO are both simple enough to write directly, and adding a
dependency to a fail-closed licence audit to draw one picture is a poor trade.
"""
import math, struct, zlib, pathlib

# The app's own colours. Cyan marks a place throughout the interface, and the icon is a place.
INK = (0x56, 0xB8, 0xC6)
GROUND = (0x14, 0x19, 0x20)
SIZES = [16, 24, 32, 48, 64, 128, 256]
SUPERSAMPLE = 4


def shade(x, y, n):
    """Colour at a point, in a 0..1 square. A reticle: ring, gap, four ticks, centre dot.

    Chosen because it has to survive sixteen pixels, which is the size it is seen at most
    often. One shape, no scene, and nothing that becomes mush when it is smaller than a word.
    """
    cx, cy = x - 0.5, y - 0.5
    r = math.hypot(cx, cy)

    # Rounded tile. Keeps the mark legible on any taskbar colour, light or dark.
    corner = 0.18
    inside = max(abs(cx), abs(cy)) <= 0.5
    dx, dy = abs(cx) - (0.5 - corner), abs(cy) - (0.5 - corner)
    if dx > 0 and dy > 0 and math.hypot(dx, dy) > corner:
        inside = False
    if not inside:
        return None

    ring_r, ring_w = 0.30, 0.052
    if abs(r - ring_r) <= ring_w / 2:
        # Gaps at the diagonals, so the ring reads as an instrument rather than a full circle.
        angle = (math.degrees(math.atan2(cy, cx)) + 360) % 90
        if 28 < angle < 62:
            return GROUND
        return INK

    if r < 0.075:
        return INK

    tick_w = 0.048
    if abs(cy) <= tick_w / 2 and 0.36 <= abs(cx) <= 0.455:
        return INK
    if abs(cx) <= tick_w / 2 and 0.36 <= abs(cy) <= 0.455:
        return INK

    return GROUND


def render(size):
    n = size * SUPERSAMPLE
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            r = g = b = a = 0
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    fx = (px * SUPERSAMPLE + sx + 0.5) / n
                    fy = (py * SUPERSAMPLE + sy + 0.5) / n
                    c = shade(fx, fy, n)
                    if c is None:
                        continue
                    r += c[0]; g += c[1]; b += c[2]; a += 255
            total = SUPERSAMPLE * SUPERSAMPLE
            if a == 0:
                row += b"\x00\x00\x00\x00"
            else:
                covered = a // 255
                row += bytes((r // covered, g // covered, b // covered, a // total))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + row for row in rows)
    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def main():
    images = [(s, png(s, render(s))) for s in SIZES]
    out = pathlib.Path(__file__).resolve().parent.parent / "src/TarkovCompanion.App/Assets"
    out.mkdir(parents=True, exist_ok=True)

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries, blobs = b"", b""
    for size, blob in images:
        # 256 is written as 0 in an ICO directory, which is the format's one real quirk.
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        blobs += blob
    (out / "TarkovCompanion.ico").write_bytes(header + entries + blobs)
    # The window icon is taken from a PNG rather than the ico, so ship the largest separately.
    (out / "TarkovCompanion.png").write_bytes(images[-1][1])
    print(f"wrote {out/'TarkovCompanion.ico'} with {len(images)} sizes")


if __name__ == "__main__":
    main()
