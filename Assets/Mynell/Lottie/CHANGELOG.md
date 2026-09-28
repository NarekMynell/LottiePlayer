# Changelog

## [1.0.0]

First release.

- Importer for Lottie JSON and dotLottie (`.lottie`) files, with fonts and images baked into the asset.
- Burst renderer: exact-area anti-aliasing, gradients, images, masks, track mattes, blend modes, effects
  (fill, tint, tritone, levels, gaussian blur, drop shadow, transform), text, trim / offset / merge paths, repeaters.
- Expressions evaluated at import.
- Parallel banded rendering for large targets; unchanged frames are neither rendered nor uploaded.
- `LottieImage` (uGUI) and `LottieRenderer` (world space) components with inspectors and edit-mode preview.
- `LottieElement` for UI Toolkit (UXML / UI Builder).
- Timeline track and clips (when com.unity.timeline is installed).
- `LottiePlayer` scripting API: segments, markers, loop modes, events.
- Dynamic properties: override colors, opacities, stroke widths and transforms by key path at runtime.
