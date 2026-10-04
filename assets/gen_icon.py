"""iPhoneCam app icon pipeline: rounded-transparent master + .ico + Android mipmaps.
Usage: python gen_icon.py <source-png>
Needs Pillow: python -m pip install Pillow
"""
import os
import shutil
import sys

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.abspath(__file__))          # iPhoneCam/assets
APP = os.path.join(os.path.dirname(ROOT), "iPhoneCam.App", "assets")
RES = os.path.join(os.path.dirname(ROOT), "AndroidSender", "app", "src", "main", "res")
RADIUS_RATIO = 0.20  # outer rounded-corner radius (iOS-style)


def rounded(src: Image.Image, radius_ratio: float) -> Image.Image:
    w, h = src.size
    r = int(min(w, h) * radius_ratio)
    scale = 4  # supersampled mask for smooth edges
    mask = Image.new("L", (w * scale, h * scale), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, w * scale, h * scale], radius=r * scale, fill=255)
    mask = mask.resize((w, h), Image.LANCZOS)
    out = src.convert("RGBA")
    out.putalpha(mask)
    return out


def main() -> None:
    src_path = sys.argv[1]
    img = Image.open(src_path).convert("RGB")
    w, h = img.size
    side = min(w, h)
    # center-crop to square (icon must be square)
    img = img.crop(((w - side) // 2, (h - side) // 2, (w + side) // 2, (h + side) // 2))
    shutil.copy(src_path, os.path.join(ROOT, "icon-src.png"))

    icon = rounded(img, RADIUS_RATIO)
    icon.save(os.path.join(ROOT, "icon-rounded.png"))

    # PC .ico (multi-size)
    os.makedirs(APP, exist_ok=True)
    ico_path = os.path.join(APP, "icon.ico")
    icon.save(ico_path, sizes=[(16, 16), (24, 24), (32, 32), (48, 48),
                               (64, 64), (128, 128), (256, 256)])

    # Android mipmaps + play-store 512
    for dpi, px in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96),
                    ("xxhdpi", 144), ("xxxhdpi", 192)):
        d = os.path.join(RES, "mipmap-" + dpi)
        os.makedirs(d, exist_ok=True)
        icon.resize((px, px), Image.LANCZOS).save(os.path.join(d, "ic_launcher.png"))
    icon.resize((512, 512), Image.LANCZOS).save(os.path.join(ROOT, "icon-512.png"))
    print("OK:", ico_path)


if __name__ == "__main__":
    main()
