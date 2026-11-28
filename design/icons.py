"""Draws Gom's mark everywhere it is used, from the one drawing below (H chose it on 2026-11-28: "G, family dots").

    python3 design/icons.py

A white speech bubble with three dots in it, big, middle and small, like a family; the dots are holes, so the same shape works on
the sage tile, in Android's themed (one-colour) icons and in the status bar. It writes the browser's icon (wwwroot/icon.svg), the
installed web app's PNGs (ImageMagick with librsvg), and the Android app's launcher and notification icons. The page's own logo
(Scripts/icons.ts) carries the same path; a server test keeps the copies together.
"""
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
SAGE = '#3F6B57'

# In a 100 by 100 box: the bubble, then the three dots (holes, by the even-odd rule).
GLYPH = ('M24 18h52a12 12 0 0 1 12 12v32a12 12 0 0 1-12 12H46L30 86V74h-6a12 12 0 0 1-12-12V30a12 12 0 0 1 12-12z'
         'M24 47a9 9 0 1 0 18 0a9 9 0 1 0-18 0z'
         'M46.5 48a7.5 7.5 0 1 0 15 0a7.5 7.5 0 1 0-15 0z'
         'M65 49a6 6 0 1 0 12 0a6 6 0 1 0-12 0z')


def tile(rounded, scale):
    """The mark on sage: a rounded tile (the browser's icon) or a full square that the system rounds or masks."""
    corner = ' rx="23"' if rounded else ''
    move = (100 - 100 * scale) / 2
    placed = '' if scale == 1 else f' transform="translate({move:g} {move:g}) scale({scale:g})"'
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect width="100" height="100"{corner} fill="{SAGE}"/>'
            f'<path{placed} fill="#fff" fill-rule="evenodd" d="{GLYPH}"/></svg>\n')


def png(svg, size, out):
    with tempfile.NamedTemporaryFile('w', suffix='.svg', delete=False) as f:
        f.write(svg)
    try:
        subprocess.run(['magick', '-background', 'none', '-density', '600', f'rsvg:{f.name}', '-resize', f'{size}x{size}', '-depth', '8', '-strip', out], check=True)
    finally:
        pathlib.Path(f.name).unlink()


def android(name, viewport, size, scale, move, note):
    """A vector drawable: the same path, placed by a group (Android draws the holes with fillType evenOdd)."""
    return (f'<?xml version="1.0" encoding="utf-8"?>\n<!-- {note} Made by design/icons.py. -->\n'
            f'<vector xmlns:android="http://schemas.android.com/apk/res/android"\n'
            f'    android:width="{size}dp" android:height="{size}dp" android:viewportWidth="{viewport}" android:viewportHeight="{viewport}">\n'
            f'    <group android:translateX="{move:g}" android:translateY="{move:g}" android:scaleX="{scale:g}" android:scaleY="{scale:g}">\n'
            f'        <path android:pathData="{GLYPH}" android:fillColor="#FFFFFF" android:fillType="evenOdd" />\n'
            f'    </group>\n</vector>\n')


web = ROOT / 'src/Gom.Web/wwwroot'
(web / 'icon.svg').write_text(tile(True, 1))
png(tile(True, 1), 512, web / 'icons/icon-512.png')
png(tile(True, 1), 192, web / 'icons/icon-192.png')
# The system rounds these itself: a full square, the mark smaller (a maskable icon keeps it inside the middle 80% circle).
png(tile(False, .8), 180, web / 'icons/apple-touch-icon.png')
png(tile(False, .62), 512, web / 'icons/icon-maskable-512.png')

res = ROOT / 'android/app/src/main/res/drawable'
# The adaptive icon is 108 units with a 66-unit safe circle: the mark at 0.6 (60 units across), centred.
(res / 'ic_launcher_foreground.xml').write_text(android('foreground', 108, 108, .6, 24, 'The launcher icon: the mark at 0.6, centred in the safe zone.'))
(res / 'ic_notification.xml').write_text(android('notification', 24, 24, .24, 0, 'The status bar icon: only its shape shows.'))
print('wrote icon.svg, 4 PNGs, 2 Android icons')
