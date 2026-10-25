#!/usr/bin/env python3
"""Traces the Claude spark into a vector path for the menu bar glyph.

Input:  the tray icon template from an installed Claude app (TrayIconTemplate@3x.png, 72 x 72,
        anti-aliased). It is read at build-design time only; the app ships the traced vector.
Output: macos/Sources/UsageBar/ClaudeSparkPath.swift (a CGPath in code) and design/spark/ClaudeSpark.svg.

Method: marching squares at alpha 0.5 gives a sub-pixel outline. A light smoothing pass removes the
pixel staircase, Douglas-Peucker drops redundant points, and a centripetal Catmull-Rom spline through
the rest becomes closed cubic Beziers. Coordinates are normalised to the glyph's bounding box, y down.

Run: python3 design/spark/trace_spark.py [path/to/TrayIconTemplate@3x.png]   (needs numpy, Pillow)
"""
import math
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC = sys.argv[1] if len(sys.argv) > 1 else "/Applications/Claude.app/Contents/Resources/TrayIconTemplate@3x.png"
SWIFT_OUT = os.path.join(ROOT, "macos", "Sources", "UsageBar", "ClaudeSparkPath.swift")
SVG_OUT = os.path.join(HERE, "ClaudeSpark.svg")
ISO = 0.5


def load_alpha(path):
    a = np.asarray(Image.open(path).convert("RGBA").split()[3], dtype=np.float64) / 255.0
    return np.pad(a, 1)  # a transparent border so every outline closes


def marching_squares(a, iso):
    """Returns edge segments ((x0, y0), (x1, y1)) of the iso-contour, in pixel-centre coordinates."""
    h, w = a.shape
    segs = []

    def interp(p1, v1, p2, v2):
        if v1 == v2:  # no crossing on this edge; the value is never used
            return p1
        t = (iso - v1) / (v2 - v1)
        return (p1[0] + t * (p2[0] - p1[0]), p1[1] + t * (p2[1] - p1[1]))

    for y in range(h - 1):
        for x in range(w - 1):
            tl, tr, br, bl = a[y, x], a[y, x + 1], a[y + 1, x + 1], a[y + 1, x]
            idx = (tl > iso) << 3 | (tr > iso) << 2 | (br > iso) << 1 | (bl > iso)
            if idx in (0, 15):
                continue
            top = interp((x, y), tl, (x + 1, y), tr)
            right = interp((x + 1, y), tr, (x + 1, y + 1), br)
            bottom = interp((x, y + 1), bl, (x + 1, y + 1), br)
            left = interp((x, y), tl, (x, y + 1), bl)
            centre = (tl + tr + br + bl) / 4
            table = {
                1: [(left, bottom)], 2: [(bottom, right)], 3: [(left, right)], 4: [(top, right)],
                6: [(top, bottom)], 7: [(left, top)], 8: [(left, top)], 9: [(top, bottom)],
                11: [(top, right)], 12: [(left, right)], 13: [(bottom, right)], 14: [(left, bottom)],
                5: [(left, top), (bottom, right)] if centre > iso else [(left, bottom), (top, right)],
                10: [(left, bottom), (top, right)] if centre > iso else [(left, top), (bottom, right)],
            }
            segs.extend(table[idx])
    return segs


def chain(segs):
    """Joins segments into closed loops."""
    key = lambda p: (round(p[0], 6), round(p[1], 6))
    nbrs = {}
    for a, b in segs:
        nbrs.setdefault(key(a), []).append(key(b))
        nbrs.setdefault(key(b), []).append(key(a))
    seen, loops = set(), []
    for start in nbrs:
        if start in seen:
            continue
        loop, prev, cur = [start], None, start
        seen.add(start)
        while True:
            nxt = [n for n in nbrs[cur] if n != prev and n not in seen]
            if not nxt:
                break
            prev, cur = cur, nxt[0]
            seen.add(cur)
            loop.append(cur)
        if len(loop) > 8:
            loops.append(loop)
    return loops


def smooth(pts, passes=2):
    p = np.array(pts)
    for _ in range(passes):
        p = (np.roll(p, 1, 0) + 2 * p + np.roll(p, -1, 0)) / 4
    return [tuple(q) for q in p]


def dp(points, eps):
    """Douglas-Peucker on an open polyline."""
    if len(points) < 3:
        return points
    (x0, y0), (x1, y1) = points[0], points[-1]
    dx, dy = x1 - x0, y1 - y0
    norm = math.hypot(dx, dy) or 1e-9
    dists = [abs(dy * (x - x0) - dx * (y - y0)) / norm for x, y in points[1:-1]]
    i = int(np.argmax(dists)) + 1 if dists else 0
    if dists and dists[i - 1] > eps:
        return dp(points[:i + 1], eps)[:-1] + dp(points[i:], eps)
    return [points[0], points[-1]]


def simplify_closed(pts, eps):
    far = max(range(len(pts)), key=lambda i: math.dist(pts[0], pts[i]))
    a = dp(pts[:far + 1], eps)
    b = dp(pts[far:] + [pts[0]], eps)
    return a[:-1] + b[:-1]


def catmull_rom_beziers(pts, alpha=0.5):
    """Closed centripetal Catmull-Rom through pts, as cubic Bezier (c1, c2, end) triples."""
    n = len(pts)
    out = []
    for i in range(n):
        p0, p1, p2, p3 = (np.array(pts[(i + k) % n]) for k in (-1, 0, 1, 2))
        t01 = max(np.linalg.norm(p1 - p0) ** alpha, 1e-6)
        t12 = max(np.linalg.norm(p2 - p1) ** alpha, 1e-6)
        t23 = max(np.linalg.norm(p3 - p2) ** alpha, 1e-6)
        m1 = (p2 - p1 + t12 * ((p1 - p0) / t01 - (p2 - p0) / (t01 + t12))) / 3
        m2 = (p2 - p1 + t12 * ((p3 - p2) / t23 - (p3 - p1) / (t12 + t23))) / 3
        out.append((tuple(p1 + m1), tuple(p2 - m2), tuple(p2)))
    return out


def main():
    a = load_alpha(SRC)
    loops = chain(marching_squares(a, ISO))
    outline = max(loops, key=len)
    outline = simplify_closed(smooth(outline), eps=0.12)
    # Normalise to the box the PNG renderer trimmed to (pixels with alpha > 8/255, pixel edges), so the
    # vector lands at the same size and position as the original at any point size.
    ys_, xs_ = np.nonzero(a > 8 / 255)
    x0, x1 = xs_.min() - 0.5, xs_.max() + 0.5
    y0, y1 = ys_.min() - 0.5, ys_.max() + 0.5
    size = max(x1 - x0, y1 - y0)
    ox = x0 - (size - (x1 - x0)) / 2
    oy = y0 - (size - (y1 - y0)) / 2
    norm = [((x - ox) / size, (y - oy) / size) for x, y in outline]
    curves = catmull_rom_beziers(norm)

    f = lambda v: f"{v:.5f}"
    lines = [
        "import CoreGraphics",
        "",
        "/// The Claude spark as a vector: a trace of the Claude app's tray icon template",
        "/// (design/spark/trace_spark.py generates this file; don't edit it by hand).",
        "/// Coordinates are in a unit square, y down; `path(in:)` scales them to any rect.",
        "enum ClaudeSparkPath {",
        f"    static let start = CGPoint(x: {f(norm[0][0])}, y: {f(norm[0][1])})",
        "    /// Closed sequence of cubic Beziers: (control 1, control 2, end point).",
        "    static let curves: [(CGPoint, CGPoint, CGPoint)] = [",
    ]
    for c1, c2, e in curves:
        lines.append(f"        (CGPoint(x: {f(c1[0])}, y: {f(c1[1])}), CGPoint(x: {f(c2[0])}, y: {f(c2[1])}), "
                     f"CGPoint(x: {f(e[0])}, y: {f(e[1])})),")
    lines += [
        "    ]",
        "",
        "    /// The spark scaled into `rect` (y down, matching a flipped drawing context).",
        "    static func path(in rect: CGRect) -> CGPath {",
        "        let p = CGMutablePath()",
        "        func pt(_ u: CGPoint) -> CGPoint { CGPoint(x: rect.minX + u.x * rect.width, y: rect.minY + u.y * rect.height) }",
        "        p.move(to: pt(start))",
        "        for (c1, c2, end) in curves { p.addCurve(to: pt(end), control1: pt(c1), control2: pt(c2)) }",
        "        p.closeSubpath()",
        "        return p",
        "    }",
        "}",
        "",
    ]
    with open(SWIFT_OUT, "w") as fh:
        fh.write("\n".join(lines))

    d = [f"M {f(norm[0][0] * 100)} {f(norm[0][1] * 100)}"]
    for c1, c2, e in curves:
        d.append("C " + " ".join(f(v * 100) for v in (*c1, *c2, *e)))
    d.append("Z")
    with open(SVG_OUT, "w") as fh:
        fh.write('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">'
                 f'<path d="{" ".join(d)}" fill="#000"/></svg>\n')
    print(f"{len(loops)} loop(s); outline {len(outline)} points -> {len(curves)} curves")
    print("wrote", os.path.relpath(SWIFT_OUT, ROOT), "and", os.path.relpath(SVG_OUT, ROOT))


if __name__ == "__main__":
    main()
