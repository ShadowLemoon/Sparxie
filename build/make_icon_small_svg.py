"""小尺寸图标用的简化版 SVG（从 sparxie-mark.svg 派生，用于 build/make_icon.py）。

用法：python build/make_icon_small_svg.py C|H
读取当前目录的 sparxie-mark.svg，输出 small<变体>.svg：

- C：去掉脸部细节与五官（眼睛、心形、睫毛、小标记），保留帽子、兔耳、头发和轮廓；
- H：只留头发（去掉最外层深色块、帽子与饰品、肤色、红黑色），白色 / 灰白发块合并成两种颜色。
     make_icon.py 把它盖在 C 上，让脸部变成干净的白色。
"""
import re
import sys

variant = sys.argv[1] if len(sys.argv) > 1 else 'C'
if variant not in ('C', 'H'):
    sys.exit('变体只能是 C 或 H')

s = open('sparxie-mark.svg', encoding='utf-8').read()
head = s[: s.index('<path')]
elems = re.findall(r'<path d="([^"]*)" fill="([^"]*)" transform="translate\(([-\d.]+),([-\d.]+)\)"\s*/>', s)
WHITES = {'#FFF9F9', '#FEF8F8'}
EYES = (10, 11, 41, 43)
# 脸部小块与睫毛线、心形、下巴小圆等（路径序号来自对该 SVG 的逐条分析）
FACE_DETAIL = (46, 12, 35, 9, 36, 20, 39, 23, 42)


def bbox(d, tx, ty):
    nums = [float(n) for n in re.findall(r'-?\d+\.?\d*', d)]
    xs, ys = nums[0::2], nums[1::2]
    return min(xs) + float(tx), min(ys) + float(ty), max(xs) + float(tx), max(ys) + float(ty)


out = []
for i, (d, fill, tx, ty) in enumerate(elems):
    f = fill.upper()
    x0, y0, x1, y1 = bbox(d, tx, ty)
    if variant == 'C':
        in_face = 400 <= x0 and x1 <= 960 and 900 <= y0 and y1 <= 1400
        if (in_face and max(x1 - x0, y1 - y0) < 70) or i in FACE_DETAIL or i in EYES:
            continue
        out.append(f'<path d="{d}" fill="{fill}" transform="translate({tx},{ty})"/>')
        continue

    # H：只留头发
    if i == 0 or f == '#FFE9E1':
        continue  # 外层深色块（轮廓 + 帽子底色）、肤色
    if not (f in WHITES or f.startswith(('#E8', '#E9', '#EA', '#EB', '#E2'))):
        continue  # 红 / 黑 / 深紫
    if y1 < 600 and x0 > 380 and x1 < 700:
        continue  # 帽子上的兔耳 / 蝴蝶结
    if 440 <= x0 and x1 <= 960 and y0 >= 1000 and max(x1 - x0, y1 - y0) < 300:
        continue  # 脸部小块（下巴圆点等）
    color = '#FFF9F9' if f in WHITES else '#D6C9D4'
    out.append(f'<path d="{d}" fill="{color}" transform="translate({tx},{ty})"/>')

open(f'small{variant}.svg', 'w', encoding='utf-8').write(head + '\n'.join(out) + '\n</svg>\n')
print(variant, len(out), 'paths')
