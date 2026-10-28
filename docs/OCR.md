# OCR in WindowVeil

WindowVeil uses OCR to find text that UI Automation cannot expose (custom-drawn controls, games, remote sessions, legacy
toolkits) and then places masks or WPF overlays over it. OCR runs **entirely on the local machine** with the official
PaddleOCR **PP-OCRv6** models on ONNX Runtime.

| Package | Contents |
|---|---|
| `WindowVeil.Ocr` (net8.0) | `IOcrProvider`, `OcrResult`/`OcrTextRegion`/`OcrOptions`, `OcrTextMatcher`, `OcrLocatorResolver` (capture → OCR → match → screen coordinates), `UseOcr()` |
| `WindowVeil.Ocr.Paddle` (net8.0) | `PaddleOcrEngine : IOcrProvider` — PP-OCRv6 detection + recognition with the official pre/post-processing re-implemented in C# |

```csharp
// tools/download-models.sh (or .ps1) puts the pinned models in <repo>/models
var ocr = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory("models"));      // throws PaddleOcrModelException on bad models
await using var engine = WindowVeilEngine.CreateBuilder()
    .UseWindowsDesktop()          // window system + capture backend (WindowVeil.Tracking / Native)
    .UseOcr(ocr)                  // OcrLocatorResolver; the engine disposes the provider
    .Build();
var session = await engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
session.AddRule(MaskRule.Create(
    ElementLocator.ByOcrText("Start automatically when I sign in to Windows", TextMatch.Exact)
        .Expanded(new Insets(20, 2, 2, 2), new TextQuery("Start hidden")),   // grow over the checkbox, never over the next label
    MaskAppearance.Solid("#202020")));
```

## 1. Model provenance

All models are the official PaddlePaddle ONNX exports on Hugging Face, **Apache-2.0**, pinned to exact commits in
[`tools/models.lock.json`](../tools/models.lock.json). `tools/download-models.sh` / `tools/download-models.ps1` download
`https://huggingface.co/<repo>/resolve/<revision>/<file>` (never `main`), check size and SHA-256 and fail (exit 1, partial
file deleted) on any mismatch. Files land in `models/<model>/` (git-ignored), e.g. `models/PP-OCRv6_small_det/inference.onnx`.

| Model | Repository @ revision | `inference.onnx` sha256 | `inference.yml` sha256 |
|---|---|---|---|
| PP-OCRv6_tiny_det | `PaddlePaddle/PP-OCRv6_tiny_det_onnx` @ `2ba1506c0380b8f0b03dd142459aac66d4421f6c` | `193bab7a04fca699a6c82e6abb5b81bdb28177f0abd4062552b04908dafb19f8` | `3ac018be6f97499a08faa3bbdeb33640968d9307f6736d152902747a9f259593` |
| PP-OCRv6_tiny_rec | `PaddlePaddle/PP-OCRv6_tiny_rec_onnx` @ `2612ab37152ae0a677521bae4e1e3d4fb4cf7c30` | `9ef676d6ed3c88256a2d92c640c44f25b0c40947e111b14b8be8f594091563e6` | `66170210bad538e83fff3c4a3867e547d6bf20b50d64b20347c4b913f3034ea1` |
| **PP-OCRv6_small_det** (default) | `PaddlePaddle/PP-OCRv6_small_det_onnx` @ `28fe5895c24fd108c19eb3e8479f4ab385fbfc62` | `d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e` | `193f435274bf9f0b5f71a929bbfbcf148282df7e633b34e7c373e8f44741b516` |
| **PP-OCRv6_small_rec** (default) | `PaddlePaddle/PP-OCRv6_small_rec_onnx` @ `b8f84f0b80c529de40b4fbb3544b84fa7233a513` | `5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634` | `ab078671bb49f06228eadccd34f1bb501e157f7a047095ffb943ba81512c77d1` |
| PP-OCRv6_medium_det | `PaddlePaddle/PP-OCRv6_medium_det_onnx` @ `61323801669c338b7891481ec7bac61ce31b576a` | `eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1` | `7298d5ead546584af2504d03355f881ac7a7bc0eb1e282d3e159277c1d0af871` |
| PP-OCRv6_medium_rec | `PaddlePaddle/PP-OCRv6_medium_rec_onnx` @ `50c7eacafc52fa7bcf4194e8cd08e46f8558504b` | `9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba` | `991b700facf5b50a7de193468207d5f4255b538dde0d312ae3b7c7a9b6873129` |

All twelve hashes were computed from files downloaded with `tools/download-models.sh --variant all` (the `.onnx` hashes
also equal the Hugging Face LFS object ids). Each `inference.yml` carries the model name, the DB thresholds and, for
recognition models, the character dictionary; **the `.onnx` and `.yml` of one model belong together**:

* small and medium recognition: 18 708 dictionary entries → 18 710 output classes (CJK, Latin, kana, symbols, emoji);
* tiny recognition: 6 904 entries → 6 906 classes.

`PaddleOcrEngine.Create` therefore checks `classes == entries + 1 (blank) + 1 (space)` and refuses mismatched pairs, as
well as configs that are not `PP-OCRv6_<tiny|small|medium>_det` / `_rec` (`DBPostProcess` / `CTCLabelDecode`).
Detection and recognition sizes can be mixed (each is validated on its own).

## 2. Pipeline and how it maps to the official code

Reference: PaddleOCR 3.7.0 / PaddleX 3.7.2, `paddlex/inference/pipelines/ocr/pipeline.py`, the text detection and
recognition processors, and `paddlex/configs/pipelines/OCR.yaml`. The C# port was checked against those classes and OpenCV
4.10 with recorded vectors (`tests/WindowVeil.OcrTests/Fixtures/vectors.json`, generator `generate_vectors.py`) and
end-to-end against the official pipeline (`Fixtures/reference/`).

| # | Official step | WindowVeil | Parity check |
|---|---|---|---|
| 1 | `ReadImage(format="BGR")` (cv2 BGR, alpha dropped) | `BgrImage.FromBgra` | — |
| 2 | `DetResizeForTest` type 0: images with h+w<64 zero-padded to ≥32×32; ratio from `limit_type`/`limit_side_len`; `int()` truncation; `max_side_limit` clamp; `round(x/32)*32` (banker's rounding), ≥32; `cv2.resize` INTER_LINEAR | `DetectionPreprocessor.ComputeResize` + `ImageOps.ResizeLinear` (port of OpenCV's 11-bit fixed-point bilinear path incl. the INTER_AREA shortcut for exact 2× downscale) | shapes: 33 cases equal; pixels: 10 resize cases **bit-exact** |
| 3 | `NormalizeImage` (mean/std/scale from the yml, `order: hwc`), applied to **B,G,R** channels in that order (mean[0]=0.485 → blue), float32 `x*(scale/std)+(-mean/std)`; `ToCHWImage` | `DetectionPreprocessor.Normalize` → `[1,3,H,W]` | unit test |
| 4 | `DBPostProcess` (`box_type=quad`, `score_mode=fast`, `min_size=3`): `pred > thresh`; `cv2.findContours(RETR_LIST, CHAIN_APPROX_SIMPLE)`; `get_mini_boxes` (`cv2.minAreaRect` + corner order); `box_score_fast` (`cv2.fillPoly` mask mean); `unclip` (pyclipper `JT_ROUND`, distance = area·ratio/perimeter); `get_mini_boxes` again with `min_size+2`; scale (float32 × ratio, round-half-even, clamp) | `DbPostProcessor`: component labeling + port of OpenCV's border follower (same vertices **in the same order**, holes included, reverse discovery order); port of OpenCV `convexHull` (Sklansky + cyclic shift) and `rotatingCalipers` in float32; `PolygonRasterizer` (fillPoly rules); `ClipperOffset` (port of Clipper 6.4.2 round offset, pyclipper's truncating int conversion) | contours: 12 random maps identical; minAreaRect: 44 point sets incl. equal-area ties; unclip: 31 boxes; DB end-to-end: 10 synthetic maps → identical boxes and scores |
| 5 | `SortQuadBoxes`: sort by first corner (y, x), then bubble pass swapping when \|Δy\|<10 and x is smaller | `BoxSorter.Sort` | 10 cases identical |
| 6 | `CropByPolys(det_box_type="quad")` → `get_minarea_rect_crop`: `minAreaRect` of the int box, ordered corners, `get_rotate_crop_image`: size `int(max(‖p0−p1‖,‖p2−p3‖))` × …, `getPerspectiveTransform`, `warpPerspective(INTER_CUBIC, BORDER_REPLICATE)`, `np.rot90` if h/w ≥ 1.5 | `TextLineCropper.Crop` + `ImageOps.WarpPerspectiveCubicReplicate` (OpenCV's 1/32-pixel coordinate quantization, block-wise map evaluation and 15-bit bicubic weight tables) | 11 crops **bit-exact** |
| 7 | Pipeline sorts crops by w/h (stable), predicts in batches of 6; `OCRReisizeNormImg.resize`: per crop `max_wh_ratio=max(320/48, w/h)`, width `min(ceil(48·w/h), int(48·max_wh_ratio))`, cap 3200, INTER_LINEAR, `(x/255−0.5)/0.5`, zero pad; `ToBatch` pads to the widest crop | `RecognitionPreprocessor` + batching in `PaddleOcrEngine.RecognizeBoxes` | 4 batches: identical tensors |
| 8 | `CTCLabelDecode` (`use_space_char=True`): argmax/max per step, drop repeats and blank 0, `character = ["blank"] + dict + [" "]`, score = float32 `np.mean` of kept probabilities (0 if none) | `CharacterDictionary`, `CtcDecoder` (incl. NumPy's pairwise float32 summation) | 4 sequences identical |
| 9 | `rec_score >= text_rec_score_thresh` (default 0.0) | `OcrOptions.MinConfidence` (default 0) | — |

### Defaults

`PaddleOcrModelOptions` reproduces the official **OCR pipeline** config: `limit_side_len=64`, `limit_type=min`,
`max_side_limit=4000`, recognition batch 6, `text_rec_score_thresh=0`, `use_space_char=true`. One deliberate choice: the
pipeline config overrides the DB thresholds with **0.3 / 0.6 / 1.5**, while the models' own `inference.yml` (used by the
stand-alone detection module) ships **0.2 / 0.45 / 1.4** (tiny: box_thresh 0.4). WindowVeil uses the model values by
default (better recall for small UI text); `options.WithPipelineDetectionThresholds()` selects the pipeline values. Both
configurations are covered by reference outputs and tests. ONNX Runtime: `GraphOptimizationLevel.ORT_ENABLE_ALL`,
sequential execution, `IntraOpNumThreads = min(4, cores)`, `InterOpNumThreads = 1`.

### Deviations from the official implementation

* `findContours` holes are discovered by flood-filling enclosed background (4-connected) instead of Suzuki's raster scan;
  the traced vertices and the order match OpenCV on all recorded cases, but pathological shapes where a pixel is part of
  both an outer and an inner border might be ordered differently.
* `convexHull` sorts with a stable sort where OpenCV uses `std::sort`; results can only differ for duplicate points.
* Clipper's final union (cleanup of self-intersections) is not reproduced; for the convex quads produced by `get_mini_boxes`
  the raw offset path is already simple and only its hull is used.
* `cv2.fillPoly` is re-implemented from OpenCV's edge-collection rules; it only affects `box_score_fast` (a mean), and
  matched OpenCV on every recorded case.
* Zero-sized crops (impossible for boxes that passed `min_size`) are skipped instead of hitting OpenCV's "size 0 means
  source size" quirk.
* The official small-image padding quirk is reproduced: for h+w<64 the padded image's map is scaled to the unpadded size.

## 3. Errors, lifetime and threading

* `PaddleOcrEngine.Create` loads both models once and validates them eagerly; it throws `PaddleOcrModelException`
  (`Status = ModelError`, `Code = OcrUnavailable`) for missing, unreadable or corrupt files, unexpected inputs/outputs,
  non-PP-OCRv6 configs and dictionary/class-count mismatches. `TryCreate` returns the message instead.
* `RecognizeAsync` never throws for expected failures: `InvalidImage` (region of interest empty/outside, image larger than
  `OcrOptions.MaxImagePixels`), `ModelError` (unexpected output shapes), `Failed` (ONNX Runtime error). A blank image is
  `Succeeded` with zero regions. Cancellation throws `OperationCanceledException`; it is checked before detection, after
  detection and before every recognition batch, and terminates a running ONNX Runtime call (`RunOptions.Terminate`).
* The engine is thread-safe (ORT sessions support concurrent `Run`); by default work runs on the thread pool so UI threads
  never block. `Dispose` waits for running calls before releasing the sessions; later calls throw `ObjectDisposedException`.

## 4. Coordinate systems

1. **Image-local** (`OcrTextRegion.Polygon` / `BoundingBox`): pixels of the image passed to the provider, origin at its
   top-left corner, x right, y down. With `OcrOptions.RegionOfInterest` only that part is recognized but coordinates stay
   relative to the full image. PaddleOCR polygons are the integer corners of the expanded detection quad, ordered
   top-left, top-right, bottom-right, bottom-left.
2. **Capture → screen**: `OcrLocatorResolver` captures the client area (or `SearchRegion`, which is client-relative) and
   maps every box with `CapturedFrame.ImageToScreen`, i.e. `screen = frame.ScreenRegion.Location + image × frame.Scale`,
   rounded outward. Physical virtual-desktop coordinates; negative origins (monitors left of/above the primary) and scaled
   captures are supported (tested).
3. **Expansion**: `OcrTextLocator.Expand` is in DIPs and converted with the target window's DPI
   (`CoordinateConverter.InsetsToPhysical`), e.g. 20 DIP → 30 px at 150 %.
4. Results carry `WindowBounds`, so the session translates them when the window moves without resizing.

OCR geometry is approximate: the detector expands text boxes (`unclip`), so boxes include a margin of a few pixels and can
swallow adjacent glyphs (in `en_settings` the detected line already includes the checkbox square). Use `Expand` and
`MustNotOverlap` to make the intent explicit and safe.

## 5. Resolver: CPU budget, caching, rate limiting and stale results

`OcrLocatorResolver` (one per engine, shared by all sessions):

* **Provider reuse** — one `IOcrProvider` (and so one pair of ORT sessions) for everything.
* **Content cache** — results are cached by `XxHash64` of the captured pixels (stride padding excluded) + image size +
  `OcrOptions`, in a bounded LRU (`ResultCacheCapacity`, default 16). An unchanged window costs one capture and one hash
  (~2 ms for 1920×1080). Only successful results are cached; results are image-local, so a moved window still hits.
* **Rate limit** — if the previous resolution of the same locator is younger than `OcrTextLocator.MinInterval` (default 1 s,
  measured with the context's `TimeProvider`) and the client size and DPI are unchanged, the resolver returns
  `LocatorStatus.Cached` without capturing: the previous bounds, translated when the window moved (same size). A previous
  failure is returned as is. Size or DPI changes bypass the limit. The timestamp lives in `LocatorResolution.State`
  (`OcrResolutionState`).
* **Bounded concurrency** — a semaphore allows `MaxConcurrentRecognitions` (default 1) inferences at once; at most
  `MaxQueuedRecognitions` (32) wait; beyond that the request keeps its previous geometry (`Cached`) or is `Unavailable`.
* **Newest request wins** — requests are keyed by (window handle, locator). A newer request supersedes an older one: if
  the older is still waiting for a slot it gives up immediately (no inference); if it is already running, its result is
  discarded when it finishes (it returns the previous geometry as `Cached`, or `Unavailable` "superseded" when there is
  none) — the inference result still lands in the content cache, so the newer request usually gets a cache hit.
* **Cancellation** — the caller's token flows into capture and inference; slots are always released.

## 6. Capture feedback avoidance

OCR must never read WindowVeil's own masks: a mask over the text would make the text "disappear", the mask would be
removed, the text would reappear, and the loop would flicker and expose the text. The resolver therefore requests
`CaptureSource.Window` (e.g. `PrintWindow`), which renders the target window alone, without the separate overlay windows.
If the backend cannot capture the window (unsupported, access denied, minimized), the result is
`LocatorStatus.Unavailable` with the capture error. Falling back to a desktop capture is off by default
(`OcrLocatorOptions.AllowDesktopCaptureFallback`): the desktop image already contains the composed overlays, and although
`OverlayWindowRegistry` knows their handles, their pixels cannot be removed from a composed image after the fact. Enable
it only when overlays are excluded from capture by other means (e.g. `WDA_EXCLUDEFROMCAPTURE`) or never cover the text
being located.

## 7. Privacy

* Everything runs locally; no network access at runtime (the download scripts are the only network users).
* Recognized text is never logged or put in diagnostic messages by default: resolution messages contain the query
  pattern, counts and confidences only; `ToString()` of `OcrTextRegion`, `OcrMatch` and `OcrResult` omit text.
  `OcrLocatorOptions.IncludeRecognizedTextInDiagnostics = true` opts in (for debugging only).
* Captured images and OCR results are kept only in the in-memory LRU cache (bounded, cleared on `Dispose`/`ClearCache`).

## 8. Validation results (this repository, Linux x64, ONNX Runtime 1.31.0)

Fixtures (`tests/WindowVeil.OcrTests/Fixtures`, rendered by `generate_fixtures.py` with Pillow 13, DejaVu Sans and
WenQuanYi Zen Hei): English settings page with checkboxes, an account balance line, Simplified Chinese (账户余额 /
开机时自动启动), Traditional Chinese (帳戶餘額 / 設定), Japanese (設定を保存 / ログイン), a 4-line paragraph, a low-contrast
noisy line, a blank image and a screenshot-like dialog.

**Parity with the official pipeline (PP-OCRv6 small):** for all 9 fixtures, with both the model thresholds and the
pipeline-default thresholds (18 comparisons, 46 text lines), WindowVeil returns the same lines in the same order with
**identical text, identical polygons (46/46), and confidences equal to the reference to within 1e-6** (the tests
allow IoU ≥ 0.85 and ±0.05 to absorb ONNX Runtime numeric differences across platforms; each run logs a `PARITY` line
with the actual deviation).

**What this does and does not show:**

* Validated: English, Simplified Chinese, Traditional Chinese and Japanese (kanji + kana) text rendered with the two fonts
  above, at 14–22 px, on white/gray backgrounds; mixed-script UI text (e-mail address, license key, currency).
* The references reproduce the model's own mistakes ("extemal", "Ul", the noisy line read as "number704-XK2-M9"):
  parity is with the official implementation, not with ground truth. Ground truth is in `fixtures.json`.
* Not validated: the other ~44 languages the models support, real Windows screenshots (ClearType sub-pixel rendering,
  dark themes, DPI scaling), vertical text, rotated text beyond the synthetic unit tests, and ONNX Runtime numerics on
  Windows/ARM64. The tiny and medium models were loaded and timed but not compared with references.

## 9. Performance

Measured in this sandbox: Intel(R) Xeon(R) Processor @ 2.80GHz, 4 cores (1 thread per core), Linux x64, .NET 8, ONNX
Runtime 1.31.0 CPU, `IntraOpNumThreads = 4`, Release build. The machine was shared with other jobs (load average ≈ 2.5),
so treat the numbers as indicative; medians of 10 warm runs (5 for 1920×1080). Total = detection + recognition, in ms.

| Image | Lines | tiny (det / rec) | **small** (det / rec) | medium (det / rec) |
|---|---|---|---|---|
| en_balance 360×56 | 1 | 71 (30 / 43) | **83** (25 / 63) | 249 (84 / 166) |
| ja 300×110 | 2 | 39 (14 / 22) | **85** (21 / 68) | 250 (82 / 174) |
| en_settings 520×100 | 2 | 106 (26 / 80) | **184** (41 / 144) | 612 (112 / 497) |
| dialog 480×260 | 9 | 141 (47 / 89) | **273** (72 / 201) | 984 (210 / 778) |
| en_multiline 560×150 | 4 | 130 (34 / 96) | **334** (69 / 264) | 1146 (168 / 962) |
| 1920×1080 screen (tiled fixtures) | ~51 | 847 (390 / 454) | **2352** (708 / 1659) | 8803 (2793 / 6296) |

| Other measurements | |
|---|---|
| Model load (`PaddleOcrEngine.Create`, both sessions) | tiny 0.40 s, small 0.31–0.47 s, medium 0.74–0.88 s |
| XxHash64 of a 1920×1080 BGRA frame | 1.6 ms |
| Resolver, unchanged 1920×1080 window (fake capture copying the frame + hash + matching 51 lines) | 12.9 ms, no inference |
| Resolver within `MinInterval` (no capture) | 1.8 µs |
| Detection pre/post-processing on 1920×1088 (resize, normalize, contours, boxes) | ≈ 100–150 ms of the detection time; the rest is ONNX Runtime (raw ORT detection ≈ 435 ms) |

The official Python pipeline on the same machine and models took 83 ms (en_balance), 197 ms (en_settings), 279 ms
(dialog) and 335 ms (en_multiline), i.e. the C# port has no pre/post-processing penalty; time is dominated by ONNX Runtime
(recognition ≈ 70 % for line-heavy images). For WindowVeil's steady state the cache matters most: an unchanged window
costs a capture plus a hash, and a locator inside `MinInterval` costs no capture at all. Use `SearchRegion` to shrink the
recognized area, the tiny models on slow machines, and keep `MaxConcurrentRecognitions = 1`.

## 10. Known limitations

* OCR geometry is approximate (detector margins, merged neighbors, rotated quads reduced to axis-aligned boxes for masks);
  it is never guaranteed pixel-exact. Prefer UI Automation locators whenever they work.
* Recognition quality depends on rendering; very small (< 10 px) or low-contrast text can be missed or misread, and low
  confidence is reported as `Rejected`, not as a match.
* Text that changes faster than `MinInterval` is tracked with that delay; moving windows are handled by translation.
* Window capture of some applications (DirectX/GPU-rendered, protected content) may fail or return black images; the
  resolver then reports `Unavailable` rather than using desktop capture.
* The first inference after loading is slower (ONNX Runtime warm-up); loading the small models takes ~0.3–0.6 s.
