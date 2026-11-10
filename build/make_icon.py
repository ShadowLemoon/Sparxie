"""生成 sparxie.ico。

- 48 / 64 / 128 / 256：完整图（留约 3% 边距）；
- 16 / 20 / 24 / 32：最简图——保留帽子、兔耳和头发，去掉五官与红黑色细节，4 色（深紫 / 白发 / 深紫灰发丝 / 浅肤色脸）；

用法：python build/make_icon.py full.png small-c.png small-h.png assets/sparxie.ico
三张 png 是 Edge 无头渲染 sparxie-mark.svg / smallC.svg / smallH.svg 得到的 1024x1024 透明底位图（见 assets/README.md）。
"""
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

full_png, c_png, h_png, out = sys.argv[1:5]



def square(im, margin):
    im = im.crop(im.getbbox())
    side = int(max(im.size) * (1 + margin))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.alpha_composite(im, ((side - im.width) // 2, (side - im.height) // 2))
    return canvas.resize((1024, 1024), Image.Resampling.LANCZOS)


def tiny_version(c_img, h_img):
    # 头发+脸部整体轮廓：由 H 版（只有头发）闭运算得到。先四周补透明边：PIL 的 MinFilter 在图像边缘不会腐蚀。
    pad = 120
    alpha = h_img.getchannel("A").point(lambda v: 255 if v > 40 else 0)
    padded = Image.new("L", (alpha.width + 2 * pad, alpha.height + 2 * pad), 0)
    padded.paste(alpha, (pad, pad))
    closed = padded.filter(ImageFilter.MaxFilter(91)).filter(ImageFilter.MinFilter(91)).crop(
        (pad, pad, pad + alpha.width, pad + alpha.height))

    # 下巴处被去掉的深色块留下缺口：把两条马尾之间的列向下补到接近马尾底部，当作下巴。
    mask = np.array(closed)
    ys, xs = np.nonzero(mask)
    x_lo, x_hi, target = xs.min(), xs.max(), int(ys.max() * 0.93)
    for x in range(int(x_lo + 0.28 * (x_hi - x_lo)), int(x_lo + 0.72 * (x_hi - x_lo))):
        col = np.nonzero(mask[:, x])[0]
        if len(col) and col.max() < target:
            mask[col.max():target, x] = 255
    body = mask > 0

    # 以去掉五官的 C 版为底，按颜色分成 4 类：深（轮廓/帽子）、白（头发）、灰（发丝阴影）、肤色。
    # 头发轮廓内部的“深色”（遮脸卡片留下的）改成肤色，这样脸和头发是两种不同的颜色，发丝仍然看得清。
    arr = np.array(c_img).astype(np.float32)
    src = np.array([[0x3A, 0x31, 0x48], [0xFF, 0xF9, 0xF9], [0xEB, 0xE2, 0xE8], [0xFF, 0xE9, 0xE1]], dtype=np.float32)
    cls = ((arr[..., None, :3] - src[None, None]) ** 2).sum(-1).argmin(-1)
    cls[body & (cls == 0)] = 3
    # 显示颜色：灰阴影加深、肤色略偏暖，拉开与白发的对比
    dst = np.array([[0x3A, 0x31, 0x48], [0xFF, 0xFA, 0xFA], [0x5A, 0x4B, 0x69], [0xF0, 0xC4, 0xBA]], dtype=np.uint8)
    flat = np.dstack([dst[cls], arr[..., 3].astype(np.uint8)])
    return square(Image.fromarray(flat), 0.0)


full = Image.open(full_png).convert("RGBA")
tiny = tiny_version(Image.open(c_png).convert("RGBA"), Image.open(h_png).convert("RGBA"))
big = square(full, 0.06)

# Pillow 在给了 append_images 时只写出提供了的尺寸，所以每个尺寸都显式提供。
frames = [tiny.resize((s, s), Image.Resampling.LANCZOS) for s in (16, 20, 24, 32)] + \
         [big.resize((s, s), Image.Resampling.LANCZOS) for s in (48, 64, 128, 256)]
sizes = [f.size for f in frames]
frames[-1].save(out, format="ICO", sizes=sizes, append_images=frames[:-1])
big.resize((256, 256), Image.Resampling.LANCZOS).save(out.replace(".ico", "-preview.png"))
print("ok", sizes)
