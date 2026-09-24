# Builds multiplayer.png / multiplayer_hover.png (the main menu sticker) from the source art.
# Needs Pillow (pip install pillow). Works from any folder.
import os
from PIL import Image, ImageFilter

A = os.path.dirname(os.path.abspath(__file__)) + "/"

def trimmed(path):
    im = Image.open(A + path).convert("RGBA")
    return im.crop(im.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox())

sign, globe = trimmed("multiplayer_source.png"), trimmed("multiplayer_icon_source.png")
W, H = 1260, 480          # 3x the game's 420x160 sticker textures
SIGN_W = 760              # sign width on the normal sticker
GLOBE_W = 0.17            # globe width, relative to the sign
GLOBE_AT = (0.945, 0.07)  # globe centre relative to the sign: sitting on its folded top-right corner
GLOBE_TILT = -12          # degrees, clockwise like the game's corner icons

def sticker(scale, outline):
    w = round(SIGN_W * scale)
    s = sign.resize((w, round(w * sign.height / sign.width)), Image.LANCZOS)
    gw = round(w * GLOBE_W)
    g = globe.resize((gw, round(gw * globe.height / globe.width)), Image.LANCZOS).rotate(GLOBE_TILT, Image.BICUBIC, expand=True)
    art = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    x, y = (W - s.width) // 2, (H - s.height) // 2
    art.alpha_composite(s, (x, y))
    art.alpha_composite(g, (round(x + s.width * GLOBE_AT[0] - g.width / 2), round(y + s.height * GLOBE_AT[1] - g.height / 2)))
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    if outline:
        border = art.getchannel("A").filter(ImageFilter.MaxFilter(outline * 2 + 1)).point(lambda v: 255 if v > 40 else 0).filter(ImageFilter.GaussianBlur(1.6))
        white = Image.new("RGBA", (W, H), (255, 255, 255, 0))
        white.putalpha(border)
        canvas = Image.alpha_composite(canvas, white)
    canvas.alpha_composite(art)
    return canvas

sticker(1.0, 0).save(A + "multiplayer.png", optimize=True)
sticker(1.08, 17).save(A + "multiplayer_hover.png", optimize=True)

# Co-op menu buttons (Host a game / Join a game): the art trimmed and scaled to BUTTON_H pixels tall, on a
# canvas with room for the hover image's white edge; the hover image is the same art with that edge.
BUTTON_H, EDGE = 300, 19

def button(source, name):
    art = trimmed(source)
    art = art.resize((round(BUTTON_H * art.width / art.height), BUTTON_H), Image.LANCZOS)
    pad = EDGE + 8
    size = (art.width + 2 * pad, art.height + 2 * pad)
    normal = Image.new("RGBA", size, (0, 0, 0, 0))
    normal.alpha_composite(art, (pad, pad))
    border = normal.getchannel("A").filter(ImageFilter.MaxFilter(EDGE * 2 + 1)).point(lambda v: 255 if v > 40 else 0).filter(ImageFilter.GaussianBlur(1.6))
    hover = Image.new("RGBA", size, (255, 255, 255, 0))
    hover.putalpha(border)
    hover.alpha_composite(normal)
    normal.save(A + name + ".png", optimize=True)
    hover.save(A + name + "_hover.png", optimize=True)

button("host_source.png", "host")
button("join_source.png", "join")

# Page titles (MULTIPLAYER, Lobby): the ink lettering trimmed and scaled to TITLE_H pixels tall, cut out with a
# white sticker edge (dark letters would vanish on the menu's dark side) and a soft drop shadow; TITLE_PAD of
# transparent margin on each side (the menu allows for it).
TITLE_H, TITLE_PAD, TITLE_EDGE = 260, 40, 16

def title(source, name):
    art = trimmed(source)
    art = art.resize((round(TITLE_H * art.width / art.height), TITLE_H), Image.LANCZOS)
    size = (art.width + 2 * TITLE_PAD, art.height + 2 * TITLE_PAD)
    letters = Image.new("RGBA", size, (0, 0, 0, 0))
    letters.alpha_composite(art, (TITLE_PAD, TITLE_PAD))
    edge = letters.getchannel("A").filter(ImageFilter.MaxFilter(TITLE_EDGE * 2 + 1)).point(lambda v: 255 if v > 40 else 0).filter(ImageFilter.GaussianBlur(1.6))
    shadow_alpha = Image.new("L", size, 0)
    shadow_alpha.paste(edge, (5, 9))
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.putalpha(shadow_alpha.filter(ImageFilter.GaussianBlur(7)).point(lambda v: v * 50 // 100))
    white = Image.new("RGBA", size, (255, 255, 255, 0))
    white.putalpha(edge)
    canvas.alpha_composite(white)
    canvas.alpha_composite(letters)
    canvas.save(A + name + ".png", optimize=True)

title("title_multiplayer_source.png", "title_multiplayer")
title("title_lobby_source.png", "title_lobby")

# Save slot card: blank art the menu tints per slot. slot_card.png is the art trimmed, CARD_H pixels tall, turned
# white where the art is its main colour (darker parts stay proportionally darker) so any tint works;
# slot_card_edge.png is the white hover edge around it, CARD_PAD larger on each side.
CARD_H, CARD_EDGE = 300, 19
CARD_PAD = CARD_EDGE + 8

def card(source, name):
    art = trimmed(source)
    art = art.resize((round(CARD_H * art.width / art.height), CARD_H), Image.LANCZOS)
    value = art.convert("HSV").getchannel("V")
    alpha = art.getchannel("A")
    solid = [v for v, a in zip(value.tobytes(), alpha.tobytes()) if a > 200]
    main = sorted(solid)[len(solid) // 2]
    white = value.point(lambda v: min(255, v * 255 // main))
    tintable = Image.merge("RGBA", (white, white, white, alpha))
    tintable.save(A + name + ".png", optimize=True)
    size = (art.width + 2 * CARD_PAD, art.height + 2 * CARD_PAD)
    shape = Image.new("L", size, 0)
    shape.paste(alpha, (CARD_PAD, CARD_PAD))
    border = shape.filter(ImageFilter.MaxFilter(CARD_EDGE * 2 + 1)).point(lambda v: 255 if v > 40 else 0).filter(ImageFilter.GaussianBlur(1.6))
    edge = Image.new("RGBA", size, (255, 255, 255, 0))
    edge.putalpha(border)
    edge.save(A + name + "_edge.png", optimize=True)

card("slot_card_source.png", "slot_card")
