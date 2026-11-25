"""Makes the accent block of a new colour for gom.css from one button colour, and checks its contrast.

    python3 design/palettes.py jade '#1E6B66'

The first six colours (sage to graphite) were picked by hand; jade to moss came from this. Light mode mixes the button colour with
white (tints) and black (the text on a tint); dark mode mixes it with the dark background. Every pair a page draws text on must be at
least 4.5 to 1 (WCAG AA); the server's tests check the blocks in gom.css again, so this is only a first look.
"""
import sys

WHITE, BLACK, LIGHT_BG, DARK_BG = '#FFFFFF', '#000000', '#F7F5EF', '#121513'
TEXT, DARK_TEXT, MUTED, DARK_MUTED = '#1F2A24', '#EEF0EA', '#4A5650', '#AEB5AC'


def rgb(h):
    h = h.lstrip('#')
    return [int(h[i:i + 2], 16) for i in (0, 2, 4)]


def hexed(c):
    return '#%02X%02X%02X' % tuple(round(x) for x in c)


def mix(a, b, t):
    a, b = rgb(a), rgb(b)
    return hexed([a[i] * (1 - t) + b[i] * t for i in range(3)])


def luminance(h):
    c = [x / 255 for x in rgb(h)]
    c = [x / 12.92 if x <= 0.03928 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]


def contrast(a, b):
    a, b = luminance(a), luminance(b)
    return (max(a, b) + 0.05) / (min(a, b) + 0.05)


def block(code, button):
    link_dark = mix(button, WHITE, .55)
    tint, tint_dark = mix(button, WHITE, .87), mix(button, DARK_BG, .8)
    tint_text, tint_text_dark = mix(button, BLACK, .3), mix(button, WHITE, .72)
    out, out_dark = mix(button, WHITE, .83), mix(button, DARK_BG, .68)
    hover, hover_dark = mix(button, BLACK, .3), mix(button, WHITE, .08)
    pattern, pattern_dark = mix(button, LIGHT_BG, .88), mix(button, DARK_BG, .80)
    pairs = {
        'white on the button': contrast(WHITE, button), 'link, light': contrast(button, LIGHT_BG), 'link, dark': contrast(link_dark, DARK_BG),
        'tint text, light': contrast(tint_text, tint), 'tint text, dark': contrast(tint_text_dark, tint_dark),
        'text on your bubble, light': contrast(TEXT, out), 'text on your bubble, dark': contrast(DARK_TEXT, out_dark),
        'times on your bubble, light': contrast(MUTED, out), 'times on your bubble, dark': contrast(DARK_MUTED, out_dark),
        'grey text on the pattern, light': contrast(MUTED, pattern), 'grey text on the pattern, dark': contrast(DARK_MUTED, pattern_dark),
    }
    for name, ratio in pairs.items():
        print(f'{name:32} {ratio:5.2f}{"" if ratio >= 4.5 else "  TOO LOW"}', file=sys.stderr)
    return (f'[data-palette="{code}"] {{ --btn: {button}; --btn-hover: light-dark({hover}, {hover_dark}); '
            f'--badge: {button}; --link: light-dark({button}, {link_dark}); --ring: light-dark({button}, {link_dark});\n'
            f'  --tint: light-dark({tint}, {tint_dark}); --tint-text: light-dark({tint_text}, {tint_text_dark}); --out: light-dark({out}, {out_dark}); --pat: light-dark({pattern}, {pattern_dark}); }}')


if __name__ == '__main__':
    print(block(sys.argv[1], sys.argv[2]))
