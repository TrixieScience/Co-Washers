"""Prepare the co-op avatar's textures from KoboldKare's (CC0) at 1024 px, made SFW for Drag'n Wash co-op:
- the areolas (one UV spot, mirrored onto both sides) are painted over with the belly colour around them
- the genital interior island (the blue area KoboldKare's mask flags as metallic) is filled with belly colour,
  so the closed slit reads as nothing more than a crease
Writes avatar/kobold_body.png and avatar/kobold_eye.png (run with system python: PIL + numpy).
"""
import os
import numpy as np
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "koboldkare", "Assets", "KoboldKare", "Textures")
N = 1024

body = np.asarray(Image.open(os.path.join(SRC, "Kobold_low_KoboldBody_BaseMap.png")).convert("RGB")
                  .resize((N, N), Image.LANCZOS)).astype(np.float32)
mask = np.asarray(Image.open(os.path.join(SRC, "Kobold_low_KoboldBody_MaskMap.png")).convert("RGBA")
                  .resize((N, N), Image.LANCZOS))
yy, xx = np.mgrid[0:N, 0:N]


def uv_px(u, v):
    return u * N, (1 - v) * N


# belly colour: the pink strip around the navel
bx, by = uv_px(0.3125, 0.465)
belly = np.median(body[int(by) - 12:int(by) + 12, int(bx) - 12:int(bx) + 12].reshape(-1, 3), axis=0)

# 1. areolas: fill a disc with the colour of the ring just outside it, feathered
cx, cy = uv_px(0.2695, 0.254)
r = 0.03 * N
d = np.hypot(xx - cx, yy - cy)
ring = (d > r * 1.25) & (d < r * 1.6)
ring_col = np.median(body[ring], axis=0)
w = np.clip((r * 1.2 - d) / (r * .35), 0, 1)[..., None]
body = body * (1 - w) + ring_col * w

# 2. the interior island (mask metallic): belly colour, edges softened a little
metal = (mask[..., 0] > 128).astype(np.float32)
soft = np.asarray(Image.fromarray((metal * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(2))).astype(np.float32)[..., None] / 255
body = body * (1 - soft) + belly * soft

out = np.dstack([np.clip(body, 0, 255).astype(np.uint8), np.full((N, N), 255, np.uint8)])
Image.fromarray(out, "RGBA").save(os.path.join(HERE, "kobold_body.png"), optimize=True)
Image.open(os.path.join(SRC, "KoboldEye_Diffuse.png")).convert("RGBA").resize((512, 512), Image.LANCZOS) \
    .save(os.path.join(HERE, "kobold_eye.png"), optimize=True)
print("belly", belly.round(), "areola ring", ring_col.round(), "interior px", int(metal.sum()))
