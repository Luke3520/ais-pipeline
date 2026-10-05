#!/usr/bin/env python3
"""Clip and simplify the Natural Earth 1:10m coastline to Danish waters, for `ais radar`.

Usage:
    curl -LO https://naciscdn.org/naturalearth/10m/physical/ne_10m_coastline.zip
    unzip ne_10m_coastline.zip -d ne
    python3 scripts/make-coastline.py ne/ne_10m_coastline.shp > reference/coastline/danish-waters.csv

Standard library only, on purpose: the radar's coastline is reference data, and a script that
needs a GIS stack to rebuild it is a script nobody reruns. The shapefile polyline record format
is small enough to read with struct.

Output is `line,lon,lat` in degrees, one vertex per row. A coastline that leaves the box and
re-enters becomes two lines rather than one with a chord drawn across the sea.
"""

import struct
import sys

# The box the radar can show, in degrees. Wider than any named region so every region is covered.
MIN_LON, MAX_LON = 3.0, 16.0
MIN_LAT, MAX_LAT = 53.0, 60.0

# Douglas-Peucker tolerance in degrees. ~0.005 deg is ~300 m of latitude: far below one braille
# dot at any terminal size the radar will see, and it cuts the file by an order of magnitude.
TOLERANCE_DEG = 0.005


def read_polylines(path):
    with open(path, "rb") as f:
        f.seek(100)  # fixed-size file header
        while True:
            header = f.read(8)
            if len(header) < 8:
                return
            _, length_words = struct.unpack(">ii", header)
            content = f.read(length_words * 2)
            shape_type = struct.unpack("<i", content[:4])[0]
            if shape_type != 3:  # 3 = PolyLine; anything else is not coastline
                continue
            num_parts, num_points = struct.unpack("<ii", content[36:44])
            parts = list(struct.unpack(f"<{num_parts}i", content[44:44 + 4 * num_parts]))
            offset = 44 + 4 * num_parts
            points = [struct.unpack("<dd", content[offset + 16 * i:offset + 16 * i + 16])
                      for i in range(num_points)]
            parts.append(num_points)
            for a, b in zip(parts, parts[1:]):
                yield points[a:b]


def inside(p):
    return MIN_LON <= p[0] <= MAX_LON and MIN_LAT <= p[1] <= MAX_LAT


def clip(line):
    run = []
    for p in line:
        if inside(p):
            run.append(p)
        elif run:
            yield run
            run = []
    if run:
        yield run


def simplify(points, tol):
    if len(points) < 3:
        return points
    (x1, y1), (x2, y2) = points[0], points[-1]
    dx, dy = x2 - x1, y2 - y1
    norm = (dx * dx + dy * dy) ** 0.5
    best, index = 0.0, 0
    for i in range(1, len(points) - 1):
        px, py = points[i]
        d = (abs(dy * px - dx * py + x2 * y1 - y2 * x1) / norm if norm
             else ((px - x1) ** 2 + (py - y1) ** 2) ** 0.5)
        if d > best:
            best, index = d, i
    if best <= tol:
        return [points[0], points[-1]]
    return simplify(points[:index + 1], tol)[:-1] + simplify(points[index:], tol)


def main():
    print("line,lon,lat")
    n = 0
    for line in read_polylines(sys.argv[1]):
        for run in clip(line):
            run = simplify(run, TOLERANCE_DEG)
            if len(run) < 2:
                continue
            for lon, lat in run:
                print(f"{n},{lon:.4f},{lat:.4f}")
            n += 1


if __name__ == "__main__":
    main()
