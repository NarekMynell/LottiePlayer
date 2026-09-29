# Mynell Lottie

A fast, accurate Lottie (Bodymovin) player for Unity. Animations are compiled at import time and rendered on the CPU
by Burst-compiled jobs into textures, with results matched pixel by pixel against lottie-web.

- **Accurate.** The animation, shape, mask, matte, effect and text logic is ported from lottie-web 5.13 and checked
  against its output on 200+ reference animations. Where lottie-web has a bug or a gap, the player follows
  After Effects instead (see [Fidelity](#fidelity)).
- **Fast.** Burst jobs rasterize with exact-area anti-aliasing and scale across CPU cores. Frames identical to the
  previous one are detected by a content hash, so they are neither rasterized nor uploaded again. At 2048 × 2048, the
  lottie-web demo animations render in 0.5–1 ms per frame (the heaviest, navidad, in 6 ms) on an Apple M2 Pro.
- **Complete.** Supports expressions (baked at import), text (embedded glyphs or font files, animators, expression
  selectors, text on a path, colour emoji, animated glyphs), images, precomps, time remapping, masks, track mattes,
  blend modes, merge paths, offset paths, repeaters, trim paths, markers, 3D layers seen through cameras, layer styles
  and 30+ After Effects effects — plus runtime color / opacity / transform overrides by key path.
- **Easy to use.** Drop a `.json` or `.lottie` file into the project, add a **Lottie Image** (uGUI),
  **Lottie Element** (UI Toolkit) or **Lottie Renderer** (world space), and press Play.

Tested with Unity 6000.3 in the Editor and in macOS (Mono and IL2CPP), iOS (simulator), Android (IL2CPP, ARM64) and
WebGL players, which render the reference animations identically. Requires Burst, Collections, Mathematics and uGUI.

---

## Quick start

1. **Import.** Copy a Lottie `.json` or `.lottie` (dotLottie) file into `Assets`. JSON files that look like Lottie
   animations are imported as **Lottie Asset** automatically. For other JSON files, use
   *Assets ▸ Lottie ▸ Import Selected JSON as Lottie*.
2. **Preview.** Select the asset: the Inspector shows its size, frame rate, markers, anything that is not supported,
   and an animated preview.
3. **Show it.**
   - UI: *GameObject ▸ UI ▸ Lottie Image*, then assign the asset (or select the asset first and the new image gets
     it with its native size).
   - World / 2D: *GameObject ▸ 2D Object ▸ Lottie Renderer*.
4. **Press Play.** Animations with *Play On Enable* start automatically.

## Samples

`Samples/LottieSamples.unity` shows three uGUI animations (a looping spinner, a one-shot success check and a
"like" heart driven by markers) and a world-space renderer. `LottieClickControl` is the small script that controls
them on click. The sample animations in `Samples/Animations` were made for this package and can be used freely.

## Components

### Lottie Image (uGUI)

A `MaskableGraphic`, so it works with Canvas groups, `Mask` and `RectMask2D`, and can be tinted with *Color*.

| Setting | Description |
| --- | --- |
| Asset | The animation. |
| Preserve Aspect | Letterboxes the animation inside the rect. *Set Native Size* uses the composition size. |
| Play On Enable, Loop Mode, Loop Count, Speed, Unscaled Time | Playback. Loop count 0 = forever. A negative speed plays backwards. |
| Subframes | Renders fractional frames for smooth motion at any frame rate (like lottie-web). Turn off to snap to whole frames. |
| Resolution Scale | Texture resolution relative to the element's on-screen size in pixels (canvas scale included). |
| Max Texture Size | Upper limit for the texture's longest side. |
| On Complete | Invoked when a non-looping playback ends or the loop count is reached. |

In edit mode the **Preview** section scrubs to any frame (and marker); in play mode it shows transport controls.

### Lottie Renderer (world space)

Draws the animation on a quad with the *Mynell/Lottie/Unlit* shader. Size = composition size / *Pixels Per Unit*;
*Pivot*, *Flip X/Y* and *Color* work like a sprite's. *Texture Scale* sets the texture resolution relative to the
composition size. Sorting layer and order come from the `MeshRenderer`.

### Lottie Element (UI Toolkit)

A `VisualElement` for UI Toolkit documents. In UI Builder it is listed under *Library ▸ Project ▸ Custom Controls*;
all settings of Lottie Image are available as UXML attributes (`asset`, `play-on-attach`, `loop-mode`, `loop-count`,
`speed`, `unscaled-time`, `subframes`, `preserve-aspect`, `resolution-scale`, `max-texture-size`, `tint`).

```csharp
var lottie = new LottieElement(asset) { LoopMode = LottieLoopMode.PingPong };
lottie.style.width = 200;
lottie.style.height = 200;
root.Add(lottie);
lottie.Completed += e => Debug.Log("done");
```

The animation is rendered at the element's size in pixels. UI Toolkit expects straight alpha, so each new frame is
converted on the GPU into a render texture.

### Timeline

With the Timeline package installed, add a **Mynell/Lottie Track**, bind the GameObject that has a Lottie Image or
Lottie Renderer, and add **Lottie Clips**. While a clip is active the timeline drives the frame (scrubbing works in
edit mode). A clip plays the whole animation, a frame range or a marker's range, optionally looping, and can show a
different asset than the component's. Outside clips the component plays on its own again.

## Scripting

```csharp
using Mynell.Lottie;

public class Example : MonoBehaviour
{
    public LottieImage image;

    void Start()
    {
        image.OnComplete.AddListener(() => Debug.Log("done"));
        image.PlayMarker("intro");          // plays the marker's range
    }

    public void Jump() => image.GoToAndStop(24);   // absolute frame
}
```

Both components expose `Play`, `Pause`, `Stop`, `GoToAndStop`, `GoToAndPlay`, `PlaySegment`, `PlayMarker`,
`Frame`, `NormalizedTime`, `IsPlaying` and the underlying `Player`.

### LottiePlayer

`LottiePlayer` is the engine behind the components and can be used on its own, for example to render into a texture
used by your own material:

```csharp
var player = new LottiePlayer(asset) { LoopMode = LottieLoopMode.PingPong, Speed = 1.5f };
player.SetSize(512, 512);
player.Play();
material.mainTexture = player.Texture;   // premultiplied alpha, see "Shaders"
// ...
player.Dispose();
```

- Playback: `Play`, `Pause`, `Stop`, `GoToAndStop`, `GoToAndPlay`, `SetSegment`, `ClearSegment`, `PlaySegment`,
  `PlayMarker`, `Frame` (absolute frame), `Time` (seconds), `NormalizedTime`, `Speed`, `LoopMode`, `LoopCount`,
  `UnscaledTime`, `Subframes`.
- Events: `Completed`, `LoopCompleted`, `FrameRendered` (after a new frame was uploaded).
- Rendering: `SetSize`, `ScaleMode` (`Stretch`, `Fit`, `Fill`), `Texture`, `RenderNow()` (synchronous, e.g. in
  edit mode), `SetDirty()`.
- `AutoAdvance = false` lets you drive time yourself with `Advance(deltaTime)` or `Frame`.

Players are advanced and rendered by `LottieScheduler`, which hooks into the player loop: render jobs are scheduled
at the end of `PreLateUpdate` and completed just before rendering, so they run in parallel with the rest of the
frame.

### Dynamic properties

Colors, opacities, stroke widths and transforms can be overridden at runtime with key paths — names from the
After Effects hierarchy separated by `/` (layers, precomp layers, shape groups, items). `*` matches one level, `**` any
number of levels:

```csharp
var player = image.Player;
player.SetColor("Button/**/Fill 1", new Color(0.2f, 0.6f, 1f));   // every "Fill 1" inside layer "Button"
player.SetOpacity("Badge", 0.5f);                                  // layer opacity
player.SetStrokeWidth("**/Outline", 4f);
player.SetRotation("Arrow/Transform", 90f);                        // a shape group's transform
foreach (var path in player.GetKeyPaths()) Debug.Log(path);        // find the names
player.ClearOverrides();
```

Each setter returns the number of properties changed (0 means the key path matched nothing). Overrides belong to the
player, so several images can show the same asset in different colors.

### Loading at runtime

```csharp
// JSON text or UTF-8 bytes (e.g. downloaded). External images are loaded through the resolver.
LottieAsset asset = LottieAsset.CreateFromJson(json, "Downloaded", path => File.ReadAllBytes(Path.Combine(dir, path)));

// dotLottie archive bytes: the first animation, or the one with the given id.
LottieAsset fromArchive = LottieAsset.CreateFromDotLottie(bytes);
```

Runtime-created assets are ScriptableObjects: destroy them when no longer needed.

Fonts for text layers that are not exported as glyphs can be provided with
`new LottieCompileOptions { FontResolver = request => fontBytes }` (see `LottieFontRequest`).

### Low-level rendering

`LottieAnimation` (compiled data) and `LottieRenderState` (per-instance buffers) render any frame into a
`NativeArray<uint>` from a job: `state.Schedule(animation, frame, transform, pixels, width, height)`.
`LottieRenderState.FitTransform` / `StretchTransform` build the composition-to-pixel transform.

## Shaders

Textures contain **premultiplied** RGBA with sRGB-encoded colors (like lottie-web's canvas). The included shaders
handle this in both linear and gamma color space:

- `Mynell/Lottie/UI` — default material of Lottie Image (supports masking, clipping and tinting).
- `Mynell/Lottie/Unlit` — default material of Lottie Renderer.

If you use `player.Texture` with your own shader, blend with `Blend One OneMinusSrcAlpha` and, in linear color space,
convert the color from sRGB after sampling.

## Performance

- Each player renders in Burst jobs on worker threads. Large targets (more than 512 × 256 pixels) are split into
  horizontal bands rendered in parallel; when many players render in the same frame, the bands are shared out so
  the worker threads are not oversubscribed.
- A frame whose content is identical to the previous one (paused animations, holds, static sections) is neither
  rasterized nor uploaded to the GPU.
- Texture resolution is the main cost. Keep *Resolution Scale* at 1 (on-screen size) and lower *Max Texture Size*
  for large animations when crisp detail is not needed.
- Gaussian blur, shadows, glows, layer styles and large masks/mattes cost more than plain shapes. Effects that move
  pixels across the layer (Motion Tile, Corner Pin, Bulge, CC Sphere, Displacement Map) and 3D layers in perspective
  render their frame as a single band.
- WebGL has no worker threads and no Burst: animations still render (identically), just slower.

## Fidelity

The reference is lottie-web (SVG renderer), which is what most designers preview with. Where lottie-web is known to
deviate from After Effects, or ignores a feature, this player follows After Effects (checked against Skia's Skottie,
which implements After Effects' semantics for those features):

- **Merge Paths** (add, subtract, intersect, exclude) are computed; lottie-web ignores them.
- **3D layers** are seen through the composition's camera layer (one- or two-node, parented, animated zoom) or After
  Effects' default camera, with perspective; lottie-web's SVG renderer flattens them. Layers seen without perspective
  render exactly like lottie-web.
- **Layer styles**: drop shadow, inner shadow, outer glow, inner glow, color overlay and stroke.
- **Effects** beyond lottie-web's set: Gradient Ramp, Hue/Saturation, Brightness & Contrast, Invert, Threshold,
  Black & White, CC Toner, Shift Channels, Levels, Linear Wipe, Radial Wipe, Venetian Blinds, Fractal Noise,
  Motion Tile, Corner Pin, Bulge, Directional Blur, Sharpen, Displacement Map, CC Sphere and Transform. lottie-web's own
  effects (Fill, Tint, Tritone, Pro Levels, Gaussian Blur, Drop Shadow, Stroke, Set Matte, Transform) follow lottie-web.
- **Text**: several selectors per animator add up, animator blur, and colour emoji (see *Text*).
- `loopIn`/`loopOut` cycles, `wiggle` and `random` in expressions are exact and deterministic.
- Luma mattes, chained track mattes, mask modes lighten / darken / difference and mask expansion behave as in
  After Effects.
- Offset Path and Pucker & Bloat follow their amount back to 0 (lottie-web keeps showing the last modified shape).
- Drop shadows and blurs are not cropped at the layer bounds (lottie-web's SVG filter region cuts off far shadows).
- Downscaled images are mipmapped (sampled like Chrome) instead of aliasing.

**Not rendered** (reported in the asset Inspector and in `LottieAsset.Unsupported`): the Bevel & Emboss, Satin and
Gradient Overlay layer styles, SkSL shader effects (Skia specific), and the Twist modifier. 3D layers are drawn in
layer order (After Effects also depth-sorts intersecting 3D layers).

### Expressions

Expressions are evaluated at import time for every frame and stored as keyframes, so they cost nothing at runtime.
The JavaScript subset used by After Effects / Bodymovin exports is supported, including `thisComp`, `thisLayer`,
`comp()`, `layer()`, `effect()`, `toComp`/`fromComp`, `valueAtTime`, `loopIn`/`loopOut`, `wiggle`, `linear`/`ease`,
vector math, `random`/`seedRandom`, markers and text properties. Expressions that depend on runtime input (for example
the time of day) are frozen at import.

### Text

Text layers use the glyph outlines exported with the animation. When the animation only references a font, the
importer looks for it in the project (any `.ttf`, `.otf`, `.ttc` under `Assets`), then in the operating system's font
folders, and bakes the glyphs into the asset. dotLottie files can carry their own fonts. If a font cannot be found,
the text is drawn with a common sans-serif font and the asset Inspector says which font to add. Text animators
(including per-character 3D rotation, projected), range and expression selectors, text boxes, paragraph alignment,
text on a path and glyphs exported as animated compositions are supported.

**Emoji** in text without embedded glyphs are drawn in colour like in a browser: the importer takes them from the
system's colour emoji font (Apple Color Emoji on macOS / iOS, Segoe UI Emoji on Windows, Noto Color Emoji on Android /
Linux), including skin tones, ZWJ sequences, flags and keycaps, and stores the bitmaps in the asset, so they show on
every platform. Bitmap glyphs ('sbix', 'CBDT') are used as they are; vector colour glyphs ('COLR' version 0 layers and
version 1 gradients, transforms and composites) are rendered the way Chrome renders them.
`LottieCompileOptions.EmojiFontPath` selects another colour font.

Text compiled at runtime (`LottieAsset.CreateFromJson`) can only use the fonts of the device; on WebGL there are none,
so import such animations in the Editor or pass a font through `LottieCompileOptions.FontResolver`.

### Images

Embedded (base64) images and dotLottie images are decoded at import. External image files are looked up relative to
the JSON file.

## Troubleshooting

- **Something is missing.** Check the asset Inspector: features that are not rendered are listed there.
- **The animation looks blurry.** Raise *Resolution Scale* or *Max Texture Size*.
- **Dark fringes around edges.** A custom material is not using premultiplied alpha blending (see *Shaders*).
- **A JSON file is imported as a TextAsset.** Use *Assets ▸ Lottie ▸ Import Selected JSON as Lottie*.
