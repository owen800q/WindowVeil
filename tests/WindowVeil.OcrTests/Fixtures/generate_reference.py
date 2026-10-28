#!/usr/bin/env python3
"""Generates reference OCR outputs for the fixtures with the OFFICIAL PaddleOCR pipeline (onnxruntime engine).

Usage (from the repository root):

    PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK=True \
    python tests/WindowVeil.OcrTests/Fixtures/generate_reference.py <models-root> [variant]

<models-root> uses the layout produced by tools/download-models.sh, e.g. models/PP-OCRv6_small_det/inference.onnx.
Writes reference/<fixture>.json. Nothing in this script post-processes the pipeline output except converting numpy
values to JSON; see reference/README.md for the recorded versions and the exact command.

Detection parameters: the official OCR pipeline config (paddlex/configs/pipelines/OCR.yaml) sets
limit_side_len=64, limit_type=min, max_side_limit=4000 and DB thresholds 0.3/0.6/1.5. WindowVeil uses the same
resize parameters but takes the DB thresholds from the detection model's own inference.yml (PostProcess section),
so the reference passes those explicitly (text_det_thresh/box_thresh/unclip_ratio). A second set of references
(suffix ".pipeline-defaults.json") is generated with the pipeline config defaults for comparison.
"""

import hashlib
import json
import os
import sys

import yaml

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "reference")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fp:
        for chunk in iter(lambda: fp.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
    root = sys.argv[1]
    variant = sys.argv[2] if len(sys.argv) > 2 else "small"
    det_dir = os.path.join(root, f"PP-OCRv6_{variant}_det")
    rec_dir = os.path.join(root, f"PP-OCRv6_{variant}_rec")

    import paddleocr
    import paddlex
    import onnxruntime
    import cv2
    import numpy
    from paddleocr import PaddleOCR

    with open(os.path.join(det_dir, "inference.yml"), encoding="utf-8") as fp:
        det_cfg = yaml.safe_load(fp)
    post = det_cfg["PostProcess"]

    configs = {
        "": dict(text_det_thresh=post["thresh"], text_det_box_thresh=post["box_thresh"], text_det_unclip_ratio=post["unclip_ratio"]),
        ".pipeline-defaults": dict(),
    }

    ocr = PaddleOCR(
        text_detection_model_name=f"PP-OCRv6_{variant}_det",
        text_detection_model_dir=det_dir,
        text_recognition_model_name=f"PP-OCRv6_{variant}_rec",
        text_recognition_model_dir=rec_dir,
        engine="onnxruntime",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
    )

    with open(os.path.join(HERE, "fixtures.json"), encoding="utf-8") as fp:
        fixtures = json.load(fp)

    tools = {
        "paddleocr": paddleocr.__version__,
        "paddlex": paddlex.__version__,
        "onnxruntime": onnxruntime.__version__,
        "opencv": cv2.__version__,
        "numpy": numpy.__version__,
        "python": sys.version.split()[0],
    }
    models = {
        "det": {"dir": os.path.basename(det_dir), "onnx_sha256": sha256(os.path.join(det_dir, "inference.onnx")), "yml_sha256": sha256(os.path.join(det_dir, "inference.yml"))},
        "rec": {"dir": os.path.basename(rec_dir), "onnx_sha256": sha256(os.path.join(rec_dir, "inference.onnx")), "yml_sha256": sha256(os.path.join(rec_dir, "inference.yml"))},
    }

    os.makedirs(OUT, exist_ok=True)
    for suffix, params in configs.items():
        for name, fx in fixtures.items():
            image_path = os.path.join(HERE, fx["file"])
            res = ocr.predict(image_path, **params)[0]
            regions = []
            for text, score, poly in zip(res["rec_texts"], res["rec_scores"], res["rec_polys"]):
                regions.append({"text": text, "score": float(score), "poly": [[int(p[0]), int(p[1])] for p in poly]})
            doc = {
                "fixture": fx["file"],
                "width": fx["width"],
                "height": fx["height"],
                "variant": variant,
                "text_det_params": {k: (v.item() if hasattr(v, "item") else v) for k, v in res["text_det_params"].items()},
                "text_rec_score_thresh": float(res["text_rec_score_thresh"]),
                "dt_polys": [[[int(p[0]), int(p[1])] for p in poly] for poly in res["dt_polys"]],
                "regions": regions,
                "tools": tools,
                "models": models,
            }
            with open(os.path.join(OUT, f"{name}{suffix}.json"), "w", encoding="utf-8") as fp:
                json.dump(doc, fp, ensure_ascii=False, indent=1)
                fp.write("\n")
            print(f"{name}{suffix}: " + " | ".join(f"{r['text']} ({r['score']:.3f})" for r in regions))


if __name__ == "__main__":
    main()
