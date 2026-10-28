#!/usr/bin/env python3
"""Renders the deterministic OCR test fixtures used by WindowVeil.OcrTests.

Usage (from the repository root):

    python tests/WindowVeil.OcrTests/Fixtures/generate_fixtures.py

Requirements: Pillow, the DejaVu Sans font and WenQuanYi Zen Hei (CJK). Paths can be overridden with the
WINDOWVEIL_FONT_LATIN / WINDOWVEIL_FONT_CJK environment variables.

Writes <name>.png next to this script plus fixtures.json, which records the ground-truth text and the rendered
geometry (text ink boxes and checkbox squares) in image pixel coordinates. The images are committed; re-run this
script only when fixtures change, then regenerate the reference outputs (see reference/README.md).
"""

import json
import os
import random

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
LATIN = os.environ.get("WINDOWVEIL_FONT_LATIN", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
CJK = os.environ.get("WINDOWVEIL_FONT_CJK", "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc")

WHITE = (255, 255, 255)
BLACK = (0, 0, 0)
DIALOG_BG = (240, 240, 240)


def font(path, size):
    return ImageFont.truetype(path, size)


class Fixture:
    def __init__(self, name, size, bg=WHITE):
        self.name = name
        self.image = Image.new("RGB", size, bg)
        self.draw = ImageDraw.Draw(self.image)
        self.items = []

    def text(self, xy, s, fnt, fill=BLACK, kind="text"):
        self.draw.text(xy, s, font=fnt, fill=fill)
        l, t, r, b = self.draw.textbbox(xy, s, font=fnt)
        self.items.append({"kind": kind, "text": s, "box": [l, t, r - l, b - t]})

    def checkbox(self, x, y, size, checked=False):
        self.draw.rectangle([x, y, x + size - 1, y + size - 1], outline=(51, 51, 51), width=1, fill=WHITE)
        if checked:
            self.draw.line([(x + 3, y + size // 2), (x + size // 2 - 1, y + size - 4), (x + size - 3, y + 3)], fill=(0, 95, 184), width=2)
        self.items.append({"kind": "checkbox", "box": [x, y, size, size], "checked": checked})

    def save(self, manifest):
        path = os.path.join(HERE, self.name + ".png")
        self.image.save(path, optimize=True)
        manifest[self.name] = {"file": self.name + ".png", "width": self.image.width, "height": self.image.height, "items": self.items}


def settings(manifest):
    # Two checkboxes with labels, laid out like a WinUI/WPF settings page at 100% scaling.
    f = Fixture("en_settings", (520, 100))
    ui = font(LATIN, 15)
    f.checkbox(16, 22, 16, checked=True)
    f.text((42, 21), "Start automatically when I sign in to Windows", ui)
    f.checkbox(16, 60, 16)
    f.text((42, 59), "Start hidden in the notification area", ui)
    f.save(manifest)


def balance(manifest):
    f = Fixture("en_balance", (360, 56))
    f.text((14, 16), "Account Balance $12,345.67", font(LATIN, 18))
    f.save(manifest)


def zh_hans(manifest):
    f = Fixture("zh_hans", (300, 110))
    cjk = font(CJK, 22)
    f.text((16, 16), "账户余额", cjk)
    f.text((16, 62), "开机时自动启动", cjk)
    f.save(manifest)


def zh_hant(manifest):
    f = Fixture("zh_hant", (300, 110))
    cjk = font(CJK, 22)
    f.text((16, 16), "帳戶餘額", cjk)
    f.text((16, 62), "設定", cjk)
    f.save(manifest)


def japanese(manifest):
    f = Fixture("ja", (300, 110))
    cjk = font(CJK, 22)
    f.text((16, 16), "設定を保存", cjk)
    f.text((16, 62), "ログイン", cjk)
    f.save(manifest)


def multiline(manifest):
    f = Fixture("en_multiline", (560, 150))
    body = font(LATIN, 15)
    lines = [
        "WindowVeil places masks over external windows.",
        "Text that UI Automation cannot expose is found",
        "with on-device OCR and covered automatically.",
        "Version 2.4.1 (build 1187), 3 items selected",
    ]
    for i, line in enumerate(lines):
        f.text((14, 14 + i * 32), line, body)
    f.save(manifest)


def low_contrast(manifest):
    # Light gray text on a noisy near-white background: recognition confidence is expected to be lower.
    f = Fixture("en_low_contrast", (360, 56), bg=(236, 236, 236))
    rng = random.Random(1234)
    px = f.image.load()
    for y in range(f.image.height):
        for x in range(f.image.width):
            v = max(0, min(255, 236 + int(rng.gauss(0, 14))))
            px[x, y] = (v, v, v)
    f.text((14, 18), "Serial number 7Q4-XK2-M9", font(LATIN, 13), fill=(196, 196, 196))
    f.save(manifest)


def blank(manifest):
    f = Fixture("blank", (320, 120))
    f.save(manifest)


def dialog(manifest):
    # A screenshot-like dialog: title bar, labels, a text box, a checkbox and two buttons.
    f = Fixture("dialog", (480, 260), bg=DIALOG_BG)
    d = f.draw
    ui = font(LATIN, 14)
    d.rectangle([0, 0, 479, 31], fill=WHITE)
    d.line([(0, 31), (479, 31)], fill=(204, 204, 204))
    f.text((12, 8), "RetroCap Settings", ui, kind="title")
    # Close glyph in the title bar.
    d.line([(452, 10), (463, 21)], fill=(60, 60, 60), width=1)
    d.line([(463, 10), (452, 21)], fill=(60, 60, 60), width=1)
    f.text((20, 52), "Signed in as:", ui)
    d.rectangle([130, 46, 459, 73], fill=WHITE, outline=(122, 122, 122))
    f.text((138, 52), "john.doe@example.com", ui)
    f.text((20, 96), "License key:", ui)
    d.rectangle([130, 90, 459, 117], fill=WHITE, outline=(122, 122, 122))
    f.text((138, 96), "ABCD-1234-EFGH-5678", ui)
    f.checkbox(20, 140, 15, checked=True)
    f.text((44, 139), "Upload crash reports", ui)
    for x, label in ((276, "OK"), (372, "Cancel")):
        d.rectangle([x, 208, x + 87, 237], fill=(253, 253, 253), outline=(173, 173, 173))
        w = d.textlength(label, font=ui)
        f.text((x + 44 - w / 2, 215), label, ui, kind="button")
    f.save(manifest)


def main():
    manifest = {}
    for gen in (settings, balance, zh_hans, zh_hant, japanese, multiline, low_contrast, blank, dialog):
        gen(manifest)
    with open(os.path.join(HERE, "fixtures.json"), "w", encoding="utf-8") as fp:
        json.dump(manifest, fp, ensure_ascii=False, indent=2)
        fp.write("\n")
    print("wrote", len(manifest), "fixtures to", HERE)


if __name__ == "__main__":
    main()
