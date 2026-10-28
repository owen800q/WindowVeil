# OCR reference outputs

Every `*.json` file here was produced by the **official PaddleOCR pipeline** (`paddleocr.PaddleOCR`, which wraps the
PaddleX `OCR` pipeline) running the pinned PP-OCRv6 small ONNX models on ONNX Runtime. Nothing was edited by hand.
`PaddleReferenceTests` compares `PaddleOcrEngine` against them.

| File | Detection thresholds (`thresh` / `box_thresh` / `unclip_ratio`) |
|---|---|
| `<fixture>.json` | 0.2 / 0.45 / 1.4 — the values in the detection model's `inference.yml` (WindowVeil's default), passed explicitly |
| `<fixture>.pipeline-defaults.json` | 0.3 / 0.6 / 1.5 — the defaults of the OCR pipeline config (`paddlex/configs/pipelines/OCR.yaml`) |

Both sets use the pipeline's resize defaults (`limit_side_len=64`, `limit_type=min`, `max_side_limit=4000`), recognition
batch size 6 and `text_rec_score_thresh=0.0`, with document orientation, unwarping and text-line orientation disabled.

## Environment

| Component | Version |
|---|---|
| paddleocr | 3.7.0 |
| paddlex | 3.7.2 |
| onnxruntime (Python) | 1.31.1 |
| opencv-contrib-python | 4.10.0.84 |
| numpy | 2.3.5 |
| Pillow (fixture rendering) | 13.0.0 |
| Python | 3.13 |

Models (also recorded inside each JSON file):

| Model | Hugging Face repo @ revision | `inference.onnx` sha256 |
|---|---|---|
| PP-OCRv6_small_det | `PaddlePaddle/PP-OCRv6_small_det_onnx` @ `28fe5895c24fd108c19eb3e8479f4ab385fbfc62` | `d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e` |
| PP-OCRv6_small_rec | `PaddlePaddle/PP-OCRv6_small_rec_onnx` @ `b8f84f0b80c529de40b4fbb3544b84fa7233a513` | `5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634` |

## Exact commands

From the repository root:

```bash
tools/download-models.sh --variant small                                  # models/PP-OCRv6_small_{det,rec}/
python tests/WindowVeil.OcrTests/Fixtures/generate_fixtures.py            # *.png + fixtures.json (Pillow)
PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK=True \
  python tests/WindowVeil.OcrTests/Fixtures/generate_reference.py models small   # reference/*.json
python tests/WindowVeil.OcrTests/Fixtures/generate_vectors.py             # vectors.json (OpenCV / PaddleX processors)
```

`generate_reference.py` constructs the pipeline as

```python
PaddleOCR(text_detection_model_name="PP-OCRv6_small_det", text_detection_model_dir="models/PP-OCRv6_small_det",
          text_recognition_model_name="PP-OCRv6_small_rec", text_recognition_model_dir="models/PP-OCRv6_small_rec",
          engine="onnxruntime", use_doc_orientation_classify=False, use_doc_unwarping=False, use_textline_orientation=False)
ocr.predict(png_path, text_det_thresh=0.2, text_det_box_thresh=0.45, text_det_unclip_ratio=1.4)   # <fixture>.json
ocr.predict(png_path)                                                                             # .pipeline-defaults.json
```

and stores `rec_texts`, `rec_scores`, `rec_polys` (as `regions`) and `dt_polys` verbatim.

## Notes on the recorded texts

The references contain the pipeline's own mistakes, and the tests expect them exactly, because the goal is parity with
the official implementation, not ground truth: `en_multiline` reads "extemal" for "external" and "Ul" for "UI",
`en_low_contrast` reads "number704-XK2-M9" (missing "Serial" and the space; the pipeline-defaults run reads
"number 704-XK2-M9"), and `dialog` recognizes the title-bar close glyph as "×". Ground truth for each fixture is in
`../fixtures.json`.
