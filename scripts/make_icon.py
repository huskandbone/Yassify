# Yassify icon: pink-to-lilac rounded square, a soft glow, a white heart with a gloss line, and one sparkle
# cluster (big + medium + small, like the sparkles emoji) in the top-right corner, clear of the heart.
import math, sys
from PIL import Image, ImageDraw, ImageFilter
N = 512; K = 4; W = N * K
def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))
top, bottom = (247, 104, 170), (176, 136, 250)
grad = Image.new("RGB", (W, W)); gp = grad.load()
for y in range(W):
    for x in range(W):
        t = (0.35 * x + 0.65 * y) / W
        gp[x, y] = lerp(top, bottom, t)
mask = Image.new("L", (W, W), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, W - 1], radius=int(W * 0.22), fill=255)
img = Image.new("RGBA", (W, W), (0, 0, 0, 0)); img.paste(grad, (0, 0), mask)

def soft(color, draw, blur, alpha=255):
    m = Image.new("L", (W, W), 0); draw(ImageDraw.Draw(m)); m = m.filter(ImageFilter.GaussianBlur(blur))
    if alpha < 255: m = m.point(lambda a: a * alpha // 255)
    layer = Image.new("RGBA", (W, W), color + (0,)); layer.putalpha(m); return layer

def clip(layer):
    a = Image.new("L", (W, W), 0); a.paste(layer.split()[3], (0, 0), mask); layer.putalpha(a); return layer

# soft light from the top-left, and a glow behind the heart
img = Image.alpha_composite(img, clip(soft((255, 220, 240), lambda d: d.ellipse([W * 0.18, W * 0.22, W * 0.76, W * 0.88], fill=255), W * 0.06, 95)))
img = Image.alpha_composite(img, clip(soft((255, 255, 255), lambda d: d.ellipse([-W * 0.25, -W * 0.30, W * 0.55, W * 0.40], fill=255), W * 0.06, 45)))

hx, hy, hs = 0.46, 0.55, 0.0185          # heart a touch left and low, so the sparkles have the corner
def heart(cx, cy, s):
    pts = []
    for i in range(900):
        t = 2 * math.pi * i / 900
        x = 16 * math.sin(t) ** 3
        y = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
        pts.append((cx + x * s, cy - y * s))
    return pts
img = Image.alpha_composite(img, soft((110, 30, 95), lambda d: d.polygon(heart(W * hx, W * (hy + 0.025), W * hs), fill=255), W * 0.02, 120))
hd = Image.new("RGBA", (W, W), (0, 0, 0, 0))
ImageDraw.Draw(hd).polygon(heart(W * hx, W * hy, W * hs), fill=(255, 247, 251, 255))
img = Image.alpha_composite(img, hd)
# a clean gloss line on the heart's left lobe (a curved stroke with round ends, nothing else)
gloss = Image.new("L", (W, W), 0); gd2 = ImageDraw.Draw(gloss)
cx, cy, r = W * (hx - 0.13), W * (hy - 0.105), W * 0.075
pts = [(cx + r * math.cos(math.radians(t)), cy + r * math.sin(math.radians(t))) for t in range(200, 256, 2)]
wd = int(W * 0.020)
gd2.line(pts, fill=255, width=wd, joint="curve")
for (px, py) in (pts[0], pts[-1]): gd2.ellipse([px - wd / 2, py - wd / 2, px + wd / 2, py + wd / 2], fill=255)
gl = Image.new("RGBA", (W, W), (255, 196, 226, 0)); gl.putalpha(gloss.point(lambda v: v * 200 // 255))
img = Image.alpha_composite(img, gl)

# sparkles: four-point stars with curved (concave) sides
def sparkle_pts(cx, cy, r, pinch=0.30, n=200):
    pts = []
    for i in range(n):
        t = 2 * math.pi * i / n
        c, s = math.cos(t), math.sin(t)
        # superellipse with exponent < 1 -> concave four-point star
        e = 2.0 / (1 + pinch * 6)
        x = r * math.copysign(abs(c) ** (2 / (2 * e)) , c)
        y = r * math.copysign(abs(s) ** (2 / (2 * e)) , s)
        pts.append((cx + x, cy + y))
    return pts
def astroid(cx, cy, r, n=240, power=3.0):
    pts = []
    for i in range(n):
        t = 2 * math.pi * i / n
        c, s = math.cos(t), math.sin(t)
        pts.append((cx + r * math.copysign(abs(c) ** power, c), cy + r * math.copysign(abs(s) ** power, s)))
    return pts
sp = Image.new("RGBA", (W, W), (0, 0, 0, 0)); sd = ImageDraw.Draw(sp)
cluster = [  # (x, y, radius) - big, medium, small, like the sparkles emoji
    (0.765, 0.255, 0.115),
    (0.875, 0.455, 0.060),
    (0.635, 0.110, 0.045),
]
for (x, y, r) in cluster:
    sd.polygon(astroid(W * x, W * y, W * r), fill=(255, 250, 253, 255))
img = Image.alpha_composite(img, soft((110, 30, 95), lambda d: [d.polygon(astroid(W * x + W * 0.006, W * y + W * 0.012, W * r), fill=255) for (x, y, r) in cluster], W * 0.012, 110))
img = Image.alpha_composite(img, sp)
img = img.resize((N, N), Image.LANCZOS)
img.save(sys.argv[1])
small = img.resize((64, 64), Image.LANCZOS); small.save(sys.argv[1].replace(".png", "_64.png"))
print("ok")
