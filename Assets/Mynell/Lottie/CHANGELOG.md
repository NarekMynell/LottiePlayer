# Changelog

## [Unreleased]

- Faster drawing with the same pixels: at 1024 x 1024 a frame is 1.9x faster on one core and 1.7x faster with all
  cores. Every frame of the 204 test animations renders bit for bit as before.
- Shapes that didn't change since the previous frame are not flattened, stroked or rasterized again (same pixels).
  At 1024 x 1024 that's another 15% on one core and 9% with all cores.
- Project settings: *Edit > Project Settings > Mynell Lottie* or `LottieSettings` from code. Resolution scale for all
  components, max frame rate, threads per animation and the shape cache.
- `LottieRenderState.CacheShapes` and `LottieRenderState.Threads`.
- A frame drawn in bands now has exactly the same pixels as the frame drawn in one piece, so the result no longer
  depends on how many cores a device has or how many animations play at once. Where an edge crosses a pixel row is
  now computed from its end points instead of being added up row by row: compared with the same rasterizer in double
  precision, the largest coverage error went from 7/255 to 1/255.
- Fixed inner shadows and inner glows in big frames on several cores: the bands didn't read around their edges, so
  glow lines could show along the band borders (up to 191/255 off in the test files). Shadows and directional blurs
  also sample with the same weights wherever a band starts.
- Drag a Lottie Asset into the Scene view or the Hierarchy: on a Canvas it becomes a Lottie Image, anywhere else a
  Lottie Renderer. One undo removes the drop.
- JSON files are recognized as Lottie by their top-level keys anywhere in the file (before, only the first 4 KB were
  read, so files starting with a big embedded image stayed plain text). Lottie JSON files that were in the project
  before the player are offered for import once, and a Lottie JSON imported as text has an *Import as Lottie* button
  in its Inspector.
- Copied into `Assets` without Burst, Collections, Mathematics or Unity UI, the player no longer breaks the build: it
  stays off and asks to install them (*Tools > Mynell Lottie > Install Required Packages*).
- The settings page was read-only until the settings asset existed. Its fields are now editable from the start, frame
  rate and thread limits are on/off switches instead of "0 = no limit", and the settings asset shows the same page.
- Tooltips on every setting of the components and the settings page. JSON files no longer show the archive-only
  *Main Animation* import setting.

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
- 3D layers seen through camera layers (or the default camera) with perspective.
- Layer styles: drop / inner shadow, outer / inner glow, color overlay, stroke.
- After Effects effects beyond lottie-web: Gradient Ramp, Hue/Saturation, Brightness & Contrast, Invert, Threshold,
  Black & White, CC Toner, Shift Channels, Levels, Linear / Radial Wipe, Venetian Blinds, Fractal Noise, Motion Tile,
  Corner Pin, Bulge, Directional Blur, Sharpen, Displacement Map, CC Sphere; lottie-web's Stroke and Set Matte.
- Text: expression selectors, several selectors per animator, animator blur, animated glyph compositions and colour
  emoji from bitmap ('sbix', 'CBDT') and vector ('COLR' versions 0 and 1) colour fonts: Apple Color Emoji, Segoe UI
  Emoji, Noto Color Emoji.
- Mipmapped image sampling.
