"""Draws the motif backgrounds in wwwroot/patterns as "tossed" tiles: each motif scattered over one 192 px tile at three sizes and
several angles, with small dots between, so the repeat does not read as a grid (the geometric ones, dots to weave, stay hand-drawn
regular repeats). Run it after changing a motif, then look at the backgrounds in Settings:

    python3 design/patterns.py

A motif is drawn around (0, 0) within about 26 px. The page tints a tile through a mask (see .backdrop in gom.css), so only the shape
counts: lines are black, and keep their width at every size (vector-effect). A motif near an edge is drawn again on the other side,
so the tile repeats without a seam.
"""
import math
import pathlib

OUT = pathlib.Path(__file__).resolve().parent.parent / 'src' / 'Gom.Web' / 'wwwroot' / 'patterns'
TILE = 192
REACH = 30   # how far a motif at full size reaches from its centre

# Where each motif goes: x, y, size, angle. Staggered rows with jitter, three sizes.
PLACES = [(30, 34, 1.0, -14), (118, 22, .62, 22), (170, 92, .9, 8), (76, 96, .7, -30),
          (22, 150, .66, 34), (120, 160, 1.0, -6), (66, 178, .5, 12), (162, 180, .5, -24)]
DOTS = [(70, 40), (150, 56), (40, 100), (116, 108), (92, 140), (170, 140), (10, 80), (140, 6)]


def star(r, points=5, inner=.45):
    corners = []
    for i in range(points * 2):
        rr = r if i % 2 == 0 else r * inner
        a = math.radians(-90 + i * 180 / points)
        corners.append(f'{rr * math.cos(a):.1f} {rr * math.sin(a):.1f}')
    return 'M' + ' L'.join(corners) + 'Z'


def blossom(r):
    petals = ''.join(f'<circle cx="{r * 1.25 * math.cos(math.radians(-90 + i * 72)):.1f}" '
                     f'cy="{r * 1.25 * math.sin(math.radians(-90 + i * 72)):.1f}" r="{r}"/>' for i in range(5))
    return petals + f'<circle r="{r * .45:.1f}" class="f"/>'


def ticks():
    return ''.join(f'M{15 * math.cos(math.radians(a)):.1f} {15 * math.sin(math.radians(a)):.1f}'
                   f'L{19 * math.cos(math.radians(a)):.1f} {19 * math.sin(math.radians(a)):.1f}' for a in range(0, 360, 30))


# code: (the motif, how big it is drawn)
MOTIFS = {
    'bamboo': ('<path class="f" d="M-16 7C-9 -4 2 -10 16 -12C7 -3 -2 3 -16 7Z"/><path d="M-16 7L4 -4"/>', 1.0),
    'clouds': ('<path d="M-20 5c0-7 6-12 12-10 2-6 10-8 15-3 5-2 10 2 9 8 4 0 6 5 2 7H-16c-3 0-4-1-4-2z"/>'
               '<path d="M-2 1c2-2 5-2 6 1"/>', 1.0),
    'lotus': ('<path d="M0 -12c5 6 5 14 0 20-5-6-5-14 0-20z"/><path d="M0 8c-4-8-12-12-18-10 2 7 10 11 18 10z'
              'M0 8c4-8 12-12 18-10-2 7-10 11-18 10z"/><path d="M-14 12h28"/>', 1.0),
    'nonla': ('<path d="M-20 7Q0 14 20 7L0 -16Z"/><path d="M0 -16L-5 9.6M0 -16L5 9.6"/><path d="M-12 10Q-8 19 0 17"/>', 1.0),
    'drum': (f'<circle r="21"/><circle r="10"/><path d="{ticks()}"/><path class="f" d="{star(7, 6, .4)}"/>', .75),
    'blossom': (blossom(6), 1.0),
    'lantern': ('<path d="M0 -22v8"/><path d="M-6 -14h12"/><ellipse rx="12" ry="13"/><path d="M0 -13v26"/>'
                '<ellipse rx="5.5" ry="13"/><path d="M-6 14h12"/><path d="M-2 14v8M2 14v8"/>', 1.0),
    'koi': ('<path d="M-23 0C-15 -11 3 -11 11 0C3 11 -15 11 -23 0Z"/><path d="M11 0L23 -9Q20 0 23 9Z"/>'
            '<path d="M-9 -7Q-5 -14 0 -9"/><circle cx="-16" cy="-1.5" r="1.7" class="f"/>', 1.0),
    'stars': (f'<path d="{star(12)}"/>', 1.0),
}


def copies(x, y, reach):
    """The places to draw something at (x, y): there, and again across each edge it comes near."""
    xs = [x] + [x + TILE for _ in [0] if x - reach < 0] + [x - TILE for _ in [0] if x + reach > TILE]
    ys = [y] + [y + TILE for _ in [0] if y - reach < 0] + [y - TILE for _ in [0] if y + reach > TILE]
    return [(cx, cy) for cx in xs for cy in ys]


def tile(motif, size):
    parts = []
    for x, y, scale, angle in PLACES:
        s = scale * size
        for cx, cy in copies(x, y, REACH * s):
            parts.append(f'<g transform="translate({cx} {cy}) rotate({angle}) scale({s:.2f})">{motif}</g>')
    for x, y in DOTS:
        for cx, cy in copies(x, y, 2):
            parts.append(f'<circle cx="{cx}" cy="{cy}" r="1.8" class="f"/>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{TILE}" height="{TILE}" viewBox="0 0 {TILE} {TILE}" fill="none" '
            'stroke="#000" stroke-width="2.2" stroke-linejoin="round" stroke-linecap="round">'
            '<style>*{vector-effect:non-scaling-stroke}.f{fill:#000;stroke:none}</style>' + ''.join(parts) + '</svg>\n')


if __name__ == '__main__':
    for code, (motif, size) in MOTIFS.items():
        path = OUT / f'{code}.svg'
        path.write_text(tile(motif, size))
        print(f'{path.name:12} {path.stat().st_size:6} bytes')
