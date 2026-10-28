#!/usr/bin/env python3
"""Generates unit-test vectors (Fixtures/vectors.json) from the OFFICIAL PaddleX processor classes and OpenCV.

Usage (from the repository root, inside the reference environment described in reference/README.md):

    python tests/WindowVeil.OcrTests/Fixtures/generate_vectors.py

Every expected value is computed by the library code WindowVeil.Ocr.Paddle re-implements:
  * cv2.resize (INTER_LINEAR, uint8)                         -> ImageOps.ResizeLinear
  * DBPostProcess.get_mini_boxes / cv2.minAreaRect           -> MinAreaRect / DbPostProcessor.GetMiniBox
  * DBPostProcess.unclip (pyclipper JT_ROUND)                -> ClipperOffset / DbPostProcessor.Unclip
  * DBPostProcess.__call__ on synthetic probability maps     -> DbPostProcessor.Process
  * CropByPolys.get_minarea_rect_crop (warpPerspective)      -> TextLineCropper.Crop
  * SortQuadBoxes                                            -> BoxSorter.Sort
  * DetResizeForTest.resize (shapes only)                    -> DetectionPreprocessor.ComputeResize
  * OCRReisizeNormImg.resize_norm_img + ToBatch              -> RecognitionPreprocessor
  * CTCLabelDecode                                           -> CtcDecoder
Arrays are stored base64-encoded (uint8 / little-endian float32 / uint16 quantized maps); large tensors only as sums.
"""

import base64
import json
import os
import random

import cv2
import numpy as np
from paddlex.inference.models.text_detection.processors import DBPostProcess, DetResizeForTest
from paddlex.inference.models.text_recognition.processors import CTCLabelDecode, OCRReisizeNormImg, ToBatch
from paddlex.inference.pipelines.components import CropByPolys, SortQuadBoxes

HERE = os.path.dirname(os.path.abspath(__file__))


def b64(a):
    return base64.b64encode(np.ascontiguousarray(a).tobytes()).decode("ascii")


def resize_vectors(rng):
    out = []
    cases = [((5, 7), (9, 13)), ((13, 17), (6, 8)), ((8, 8), (4, 4)), ((10, 30), (48, 144)), ((31, 45), (32, 64)),
             ((20, 9), (48, 22)), ((48, 100), (48, 100)), ((33, 70), (48, 102)), ((64, 64), (96, 32)), ((3, 50), (48, 400))]
    for (h, w), (dh, dw) in cases:
        src = rng.integers(0, 256, size=(h, w, 3), dtype=np.uint8)
        dst = cv2.resize(src, (dw, dh))
        out.append({"h": h, "w": w, "dh": dh, "dw": dw, "src": b64(src), "dst": b64(dst)})
    return out


def min_area_rect_vectors(rng):
    db = DBPostProcess()
    out = []
    sets = [np.array([[0, 0], [10, 0], [10, 5], [0, 5]]), np.array([[3, 1], [10, 4], [8, 9], [1, 6]]),
            np.array([[0, 0], [5, 5], [10, 10]]), np.array([[2, 2], [2, 9], [2, 4], [3, 9]])]
    for _ in range(40):
        n = int(rng.integers(3, 40))
        cx, cy = rng.uniform(20, 200, size=2)
        ang = rng.uniform(-np.pi, np.pi)
        wl, hl = rng.uniform(2, 120), rng.uniform(1, 30)
        u = rng.uniform(-0.5, 0.5, size=(n, 2)) * [wl, hl]
        rot = np.array([[np.cos(ang), -np.sin(ang)], [np.sin(ang), np.cos(ang)]])
        sets.append(np.round(u @ rot.T + [cx, cy]))
    for pts in sets:
        pts = pts.astype(np.int32)
        rect = cv2.minAreaRect(pts.reshape(-1, 1, 2))
        box, sside = db.get_mini_boxes(pts.reshape(-1, 1, 2))
        out.append({"points": pts.tolist(), "center": [float(rect[0][0]), float(rect[0][1])], "size": [float(rect[1][0]), float(rect[1][1])],
                    "angle": float(rect[2]), "box": [[float(p[0]), float(p[1])] for p in box], "sside": float(sside)})
    return out


def unclip_vectors(rng):
    db = DBPostProcess()
    out = []
    boxes = [np.array([[10, 10], [60, 10], [60, 22], [10, 22]], dtype=np.float32)]
    for _ in range(30):
        cx, cy = rng.uniform(20, 300, size=2)
        ang = rng.uniform(-0.6, 0.6) if rng.uniform() < 0.7 else 0.0
        wl, hl = rng.uniform(4, 200), rng.uniform(3, 40)
        rect = ((float(cx), float(cy)), (float(wl), float(hl)), float(np.degrees(ang)))
        boxes.append(cv2.boxPoints(rect).astype(np.float32))
    for box in boxes:
        ratio = float(rng.choice([1.4, 1.5, 2.0]))
        expanded = db.unclip(box, ratio).reshape(-1, 2)
        mini, sside = db.get_mini_boxes(expanded.reshape(-1, 1, 2))
        out.append({"box": box.tolist(), "ratio": ratio, "expanded": expanded.tolist(),
                    "mini": [[float(p[0]), float(p[1])] for p in mini], "sside": float(sside)})
    return out


def db_vectors(rng):
    out = []
    configs = [(0.2, 0.45, 1.4), (0.3, 0.6, 1.5)]
    for case in range(10):
        h, w = [(64, 96), (96, 160), (128, 128), (32, 256)][case % 4]
        pred = (rng.uniform(0, 0.15, size=(h, w))).astype(np.float32)
        for _ in range(int(rng.integers(1, 6))):
            cx, cy = rng.uniform(10, w - 10), rng.uniform(6, h - 6)
            bw, bh = rng.uniform(4, w / 2), rng.uniform(3, 14)
            ang = rng.uniform(-25, 25) if case % 3 == 0 else 0.0
            pts = cv2.boxPoints(((cx, cy), (bw, bh), ang)).astype(np.int32)
            canvas = np.zeros((h, w), np.uint8)
            cv2.fillPoly(canvas, [pts], 1)
            val = rng.uniform(0.5, 0.98)
            pred[canvas > 0] = np.maximum(pred[canvas > 0], (val + rng.uniform(-0.15, 0.02, size=int(canvas.sum()))).astype(np.float32))
        # Quantize to k/1000 so the map can be stored as uint16 and rebuilt bit-exactly as float32(k / 1000).
        q = np.round(np.clip(pred, 0, 1) * 1000).astype(np.uint16)
        pred = (q / 1000.0).astype(np.float32)
        thresh, box_thresh, unclip = configs[case % 2]
        src_h, src_w = int(h * rng.uniform(0.5, 2.5)), int(w * rng.uniform(0.5, 2.5))
        img_shape = np.array([src_h, src_w, h / src_h, w / src_w])
        db = DBPostProcess(thresh=thresh, box_thresh=box_thresh, unclip_ratio=unclip, max_candidates=3000)
        boxes, scores = db([pred[None, None, :, :]], [img_shape])
        out.append({"h": h, "w": w, "src_h": src_h, "src_w": src_w, "thresh": thresh, "box_thresh": box_thresh, "unclip_ratio": unclip,
                    "pred_q1000": b64(q.astype("<u2")), "boxes": np.array(boxes[0]).astype(int).tolist(), "scores": [float(s) for s in scores[0]]})
    return out


def crop_vectors(rng):
    cropper = CropByPolys(det_box_type="quad")
    img = rng.integers(0, 256, size=(60, 90, 3), dtype=np.uint8)
    # Smooth it a little so interpolation differences would be visible but not dominated by noise.
    img = cv2.GaussianBlur(img, (3, 3), 0)
    polys = [np.array([[5, 5], [60, 5], [60, 20], [5, 20]]), np.array([[0, 0], [89, 0], [89, 59], [0, 59]]),
             np.array([[10, 30], [12, 3], [25, 4], [23, 31]]), np.array([[40, 50], [85, 40], [87, 50], [42, 59]]),
             np.array([[70, 2], [88, 2], [88, 58], [70, 58]])]
    for _ in range(6):
        cx, cy = rng.uniform(15, 75), rng.uniform(10, 50)
        pts = cv2.boxPoints(((cx, cy), (rng.uniform(5, 50), rng.uniform(4, 18)), rng.uniform(-30, 30)))
        polys.append(np.clip(np.round(pts), 0, [89, 59]).astype(np.int32))
    out = []
    for p in polys:
        crop = cropper.get_minarea_rect_crop(img, p.astype(np.int16))
        out.append({"poly": p.astype(int).tolist(), "h": int(crop.shape[0]), "w": int(crop.shape[1]), "dst": b64(np.ascontiguousarray(crop))})
    return {"h": 60, "w": 90, "src": b64(img), "cases": out}


def contour_vectors(rng):
    out = []
    for case in range(12):
        h, w = int(rng.integers(8, 40)), int(rng.integers(8, 60))
        m = np.zeros((h, w), np.uint8)
        for _ in range(int(rng.integers(1, 6))):
            x, y = int(rng.integers(0, w)), int(rng.integers(0, h))
            bw, bh = int(rng.integers(1, max(2, w // 2))), int(rng.integers(1, max(2, h // 2)))
            m[y:y + bh, x:x + bw] = 1
            if rng.uniform() < 0.5 and bw > 4 and bh > 4:  # punch a hole
                m[y + 2:y + bh - 2, x + 2:x + bw - 2] = 0
        if case % 3 == 2:  # sprinkle noise for odd shapes
            m |= (rng.uniform(size=(h, w)) < 0.08).astype(np.uint8)
        contours, _ = cv2.findContours(m * 255, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
        out.append({"h": h, "w": w, "bits": b64(m), "contours": [c.reshape(-1, 2).tolist() for c in contours]})
    return out


def sort_vectors(rng):
    sorter = SortQuadBoxes()
    out = []
    for _ in range(10):
        boxes = []
        for _ in range(int(rng.integers(1, 12))):
            x, y = int(rng.integers(0, 300)), int(rng.integers(0, 120))
            bw, bh = int(rng.integers(5, 80)), int(rng.integers(5, 20))
            boxes.append([[x, y], [x + bw, y], [x + bw, y + bh], [x, y + bh]])
        res = sorter(np.array(boxes, dtype=np.int16))
        out.append({"boxes": boxes, "sorted": [np.array(b).astype(int).tolist() for b in res]})
    return out


def det_resize_vectors():
    out = []
    shapes = [(1, 1), (10, 20), (20, 43), (31, 32), (60, 40), (100, 300), (720, 1280), (1080, 1920), (3000, 5000), (5000, 200), (17, 4100)]
    for limit_side_len, limit_type in [(64, "min"), (960, "max"), (736, "min")]:
        op = DetResizeForTest(limit_side_len=limit_side_len, limit_type=limit_type)
        for h, w in shapes:
            img = np.zeros((h, w, 3), np.uint8)
            res, shape = op.resize(img, limit_side_len, limit_type, 4000)
            out.append({"h": h, "w": w, "limit_side_len": limit_side_len, "limit_type": limit_type, "max_side_limit": 4000,
                        "rh": int(res.shape[0]), "rw": int(res.shape[1]), "ratio_h": float(shape[2]), "ratio_w": float(shape[3])})
    return out


def rec_resize_vectors(rng):
    op = OCRReisizeNormImg(rec_image_shape=[3, 48, 320])
    out = []
    groups = [[(20, 60), (16, 200), (30, 30)], [(4, 300)], [(12, 400), (40, 41)], [(25, 7)]]
    for group in groups:
        imgs = [rng.integers(0, 256, size=(h, w, 3), dtype=np.uint8) for h, w in group]
        batch = ToBatch()(op(imgs))[0]
        out.append({"images": [{"h": h, "w": w, "src": b64(im)} for (h, w), im in zip(group, imgs)],
                    "shape": list(batch.shape), "sum": float(batch.astype(np.float64).sum()), "abs_sum": float(np.abs(batch.astype(np.float64)).sum()),
                    "data": b64(batch.astype("<f4")) if batch.size <= 200000 else None})
    return out


def ctc_vectors(rng):
    chars = ["a", "b", "c", "'", "\\", "中", "😀"]
    dec = CTCLabelDecode(character_list=chars)
    out = []
    seqs = [[0, 1, 1, 0, 1, 2, 2, 8, 8, 3], [0, 0, 0], [6, 6, 0, 6, 7, 7, 5, 4], [8, 1, 8, 8, 2]]
    for s in seqs:
        t = len(s)
        logits = rng.uniform(0, 0.4, size=(1, t, len(chars) + 2)).astype(np.float32)
        for i, k in enumerate(s):
            logits[0, i, k] = rng.uniform(0.5, 1.0)
        texts, scores = dec([logits])
        out.append({"t": t, "c": len(chars) + 2, "probs": b64(logits.astype("<f4")), "text": texts[0], "score": float(scores[0])})
    return {"characters": chars, "cases": out}


def main():
    rng = np.random.default_rng(20261028)
    random.seed(20261028)
    doc = {
        "generator": "generate_vectors.py",
        "opencv": cv2.__version__,
        "numpy": np.__version__,
        "resize": resize_vectors(rng),
        "minAreaRect": min_area_rect_vectors(rng),
        "unclip": unclip_vectors(rng),
        "db": db_vectors(rng),
        "crop": crop_vectors(rng),
        "sort": sort_vectors(rng),
        "contours": contour_vectors(rng),
        "detResize": det_resize_vectors(),
        "recResize": rec_resize_vectors(rng),
        "ctc": ctc_vectors(rng),
    }
    with open(os.path.join(HERE, "vectors.json"), "w", encoding="utf-8") as fp:
        json.dump(doc, fp, ensure_ascii=False)
        fp.write("\n")
    print("wrote vectors.json", os.path.getsize(os.path.join(HERE, "vectors.json")), "bytes")


if __name__ == "__main__":
    main()
