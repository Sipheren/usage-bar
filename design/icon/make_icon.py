#!/usr/bin/env python3
"""Builds design/icon/AppIcon.icon: an Icon Composer document for macOS 26.

Layers are drawn full-canvas (1024 x 1024, transparent), supersampled 4x for clean edges. The
background is the document fill (slate gradient); macOS applies the icon shape itself, so there is
no baked-in tile or margin (that is what caused the grey "icon jail" frame).

Run: python3 design/icon/make_icon.py   (needs Pillow)
"""
import json
import math
import os
import shutil

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "AppIcon.icon")
SIZE = 1024
SS = 4  # supersampling factor

ORANGE = (238, 133, 81, 255)   # spark and Personal bar
BLUE = (74, 134, 208, 255)     # Work bar
TRACK = (92, 95, 104, 255)     # bar tracks


def canvas():
    return Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))


def finish(img):
    return img.resize((SIZE, SIZE), Image.LANCZOS)


def spark():
    img = canvas()
    d = ImageDraw.Draw(img)
    cx, cy = 512 * SS, 388 * SS
    rays = 10
    tip = 178 * SS            # distance to the centre of the rounded tip
    base_half = 42 * SS       # half-width of a ray where it meets the centre
    tip_r = 27 * SS           # radius of the rounded tip
    for i in range(rays):
        a = -math.pi / 2 + i * 2 * math.pi / rays
        ux, uy = math.cos(a), math.sin(a)
        px, py = -uy, ux
        tx, ty = cx + ux * tip, cy + uy * tip
        poly = [(cx + px * base_half, cy + py * base_half),
                (tx + px * tip_r, ty + py * tip_r),
                (tx - px * tip_r, ty - py * tip_r),
                (cx - px * base_half, cy - py * base_half)]
        d.polygon(poly, fill=ORANGE)
        d.ellipse([tx - tip_r, ty - tip_r, tx + tip_r, ty + tip_r], fill=ORANGE)
    core = 74 * SS
    d.ellipse([cx - core, cy - core, cx + core, cy + core], fill=ORANGE)
    return finish(img)


def bars():
    img = canvas()
    d = ImageDraw.Draw(img)
    left, right, h = 232, 792, 62
    for top, frac, colour in [(652, 0.60, ORANGE), (768, 0.40, BLUE)]:
        r = h / 2
        box = [left * SS, top * SS, right * SS, (top + h) * SS]
        d.rounded_rectangle(box, radius=r * SS, fill=TRACK)
        fill_right = left + (right - left) * frac
        d.rounded_rectangle([left * SS, top * SS, fill_right * SS, (top + h) * SS], radius=r * SS, fill=colour)
    return finish(img)


def main():
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(os.path.join(OUT, "Assets"))
    spark().save(os.path.join(OUT, "Assets", "spark.png"))
    bars().save(os.path.join(OUT, "Assets", "bars.png"))
    manifest = {
        "fill": {
            "linear-gradient": ["srgb:0.215,0.220,0.235,1.00000", "srgb:0.085,0.085,0.095,1.00000"],
            "orientation": {"start": {"x": 0.5, "y": 0.0}, "stop": {"x": 0.5, "y": 1.0}},
        },
        "groups": [
            {
                "name": "Spark",
                "layers": [{"image-name": "spark.png", "name": "spark", "glass": False}],
                "shadow": {"kind": "neutral", "opacity": 0.5},
                "specular": True,
                "translucency": {"enabled": False, "value": 0.5},
            },
            {
                "name": "Bars",
                "layers": [{"image-name": "bars.png", "name": "bars", "glass": False}],
                "shadow": {"kind": "neutral", "opacity": 0.4},
                "specular": True,
                "translucency": {"enabled": False, "value": 0.5},
            },
        ],
        "supported-platforms": {"squares": "shared"},
    }
    with open(os.path.join(OUT, "icon.json"), "w") as f:
        json.dump(manifest, f, indent=2)
    print("wrote", OUT)


if __name__ == "__main__":
    main()
