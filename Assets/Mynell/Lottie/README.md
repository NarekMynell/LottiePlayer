# Mynell Lottie

A Lottie player for Unity. Put a `.json` or `.lottie` file into the project, add a component, press Play.

The goal is that every animation looks the same as in lottie-web, the official web player, and plays fast.
The player is written in C# with Burst. There are no native plugins, so there is nothing to build per platform.

What it can do:

- shapes, fills, strokes, gradients, trim paths, repeaters, offset paths, merge paths
- masks, track mattes, blend modes
- images, precomps, time remapping, markers
- text: glyphs from the file or fonts from the project / the OS, text animators, text on a path, colour emoji
- expressions (calculated at import, so they cost nothing at runtime)
- 3D layers and cameras, 6 layer styles, 29 After Effects effects
- changing colours, opacity and transforms from code
- uGUI, UI Toolkit, world space and Timeline

Tested with Unity 6000.3 in the Editor and in macOS (Mono and IL2CPP), iOS (simulator), Android (IL2CPP, ARM64) and
WebGL builds. Needs the Burst, Collections, Mathematics and uGUI packages.

## Quick start

1. Copy a Lottie `.json` or `.lottie` file into `Assets`. It is imported as a **Lottie Asset**. If a JSON file stays a
   plain TextAsset, select it and run *Assets > Lottie > Import Selected JSON as Lottie*.
2. Select the asset. The Inspector shows the size, frame rate, markers and a preview. If the file uses something the
   player doesn't draw, it is listed there.
3. Add it to the scene:
   - UI: *GameObject > UI > Lottie Image*, then assign the asset.
   - World / 2D: *GameObject > 2D Object > Lottie Renderer*.
4. Press Play.

`Samples/LottieSamples.unity` has a spinner, a success check, a "like" heart controlled with markers and a world-space
animation. The sample animations were made for this package and are free to use.

## Components

**Lottie Image** (uGUI) is a normal UI graphic, so Mask, RectMask2D, Canvas Group and Color work with it.

| Setting | What it does |
| --- | --- |
| Asset | The animation. |
| Preserve Aspect | Keeps the aspect ratio inside the rect. *Set Native Size* uses the animation's size. |
| Play On Enable, Loop Mode, Loop Count, Speed, Unscaled Time | Playback. Loop Count 0 means forever. Negative speed plays backwards. |
| Subframes | Draws in-between frames for smooth motion, like lottie-web. Turn it off to snap to whole frames. |
| Resolution Scale | Texture resolution compared to the size on screen. |
| Max Texture Size | Limit for the texture's longest side. |
| On Complete | Called when playback ends (or the loop count is reached). |

In edit mode the **Preview** section lets you scrub to any frame or marker.

**Lottie Renderer** draws the animation on a quad, like a sprite: *Pixels Per Unit*, *Pivot*, *Flip X/Y*, *Color*,
sorting layer and order. *Texture Scale* sets the texture resolution.

**Lottie Element** is the UI Toolkit version. In UI Builder it is under *Library > Project > Custom Controls*, and
all the settings above are UXML attributes (`asset`, `play-on-attach`, `loop-mode`, `loop-count`, `speed`,
`unscaled-time`, `subframes`, `preserve-aspect`, `resolution-scale`, `max-texture-size`, `tint`).

```csharp
var lottie = new LottieElement(asset) { LoopMode = LottieLoopMode.PingPong };
lottie.style.width = 200;
lottie.style.height = 200;
root.Add(lottie);
```

**Timeline:** with the Timeline package installed, add a *Mynell/Lottie Track*, bind a Lottie Image or Lottie Renderer
and add *Lottie Clips*. A clip can play the whole animation, a frame range or a marker, and scrubbing works in edit
mode.

## Code

```csharp
using Mynell.Lottie;

public class Example : MonoBehaviour
{
    public LottieImage image;

    void Start()
    {
        image.OnComplete.AddListener(() => Debug.Log("done"));
        image.PlayMarker("intro");
    }

    public void Jump() => image.GoToAndStop(24);   // frame 24
}
```

Lottie Image and Lottie Renderer have `Play`, `Pause`, `Stop`, `GoToAndStop`, `GoToAndPlay`, `PlaySegment`,
`PlayMarker`, `Frame`, `NormalizedTime`, `IsPlaying` and `Player`.

**Your own texture.** `LottiePlayer` is what the components use, and it works on its own too:

```csharp
var player = new LottiePlayer(asset) { LoopMode = LottieLoopMode.PingPong, Speed = 1.5f };
player.SetSize(512, 512);
player.Play();
material.mainTexture = player.Texture;   // premultiplied alpha, see Shaders
// ...
player.Dispose();
```

It also has segments, markers, `Completed` / `LoopCompleted` / `FrameRendered` events, `ScaleMode`, `RenderNow()`
for edit mode, and `AutoAdvance = false` if you want to drive time yourself.

**Changing colours and transforms.** Key paths are layer and group names from After Effects, separated by `/`.
`*` matches one level, `**` any number of levels:

```csharp
var player = image.Player;
player.SetColor("Button/**/Fill 1", new Color(0.2f, 0.6f, 1f));
player.SetOpacity("Badge", 0.5f);
player.SetStrokeWidth("**/Outline", 4f);
player.SetRotation("Arrow/Transform", 90f);
foreach (var path in player.GetKeyPaths()) Debug.Log(path);   // to find the names
player.ClearOverrides();
```

Each setter returns how many properties it changed, so 0 means the key path matched nothing. Overrides belong to the
player, so the same asset can be shown in different colours.

**Loading at runtime:**

```csharp
LottieAsset asset = LottieAsset.CreateFromJson(json, "Downloaded", path => File.ReadAllBytes(Path.Combine(dir, path)));
LottieAsset fromArchive = LottieAsset.CreateFromDotLottie(bytes);
```

These are ScriptableObjects, so destroy them when you don't need them anymore. For text whose glyphs aren't in the
file, pass a font with `new LottieCompileOptions { FontResolver = request => fontBytes }`.

For full control, `LottieAnimation` and `LottieRenderState` render any frame into a `NativeArray<uint>` from a job
(`state.Schedule(animation, frame, transform, pixels, width, height)`).

## Shaders

Textures have **premultiplied** RGBA with sRGB colours. The included shaders handle this in linear and gamma colour
space: `Mynell/Lottie/UI` (Lottie Image) and `Mynell/Lottie/Unlit` (Lottie Renderer). With your own shader, blend with
`Blend One OneMinusSrcAlpha` and, in linear colour space, convert the colour from sRGB after sampling.

## Comparison with other Lottie players for Unity

Checked on 21 October 2026. I searched the Unity Asset Store, GitHub and OpenUPM. The Asset Store has one other
Lottie player (UI Toolkit: Motion Importer). The other players in the table are free packages on GitHub.

| | **Mynell Lottie** | [ThorVG for Unity](https://github.com/thorvg/thorvg.unity) | [SkiaForUnity](https://github.com/ammariqais/SkiaForUnity) | rlottie players [1] | [Motion Importer](https://assetstore.unity.com/packages/tools/gui/ui-toolkit-motion-importer-380144) |
| --- | --- | --- | --- | --- | --- |
| Version checked | 1.0.0 | 0.1.1 | 4.2.2 | 0.4.5 and 1.1.1 | 1.0.2 |
| Renderer | C# + Burst | ThorVG v1.0-pre34, native | Skia m119 (Skottie), native | rlottie, native | C# |
| Components | uGUI, UI Toolkit, world space, Timeline | world-space quad | uGUI (RawImage), SpriteRenderer | uGUI (RawImage) | UI Toolkit |
| Platforms | no native code; tested on macOS, iOS, Android, WebGL | Windows, macOS (Apple Silicon), Linux, WebGL, Android, iOS | Windows, macOS, Linux, iOS, Android | Windows, macOS, Linux, Android, iOS (+ more in one of them [1]) | says all (verified on macOS) |
| Text | yes, including fonts and emoji | only glyphs stored in the file (and embedded fonts on WebGL) | yes | no | not listed |
| Expressions | yes | no | no | no | not listed |
| Masks and mattes | yes | yes | yes | no lighten / darken / difference masks | not listed |
| Merge paths | yes | no | yes | no | not listed |
| Effects | 29, plus layer styles | 6 | 27, plus layer styles | none | not listed |
| 3D layers and cameras | yes | no cameras | yes | no (files with a camera don't load) | not listed |
| Uses many CPU cores for one frame | yes | no | no | partly (about 1.3 cores) | - |

"Not listed" means the Motion Importer store page doesn't mention it. The page says it draws "image layers and shape
layers (ellipse, fill) with animated transforms". I didn't test it because it's paid.

### Same files, side by side

I rendered the same 199 test animations with every free player above, using each package's own native library,
called the same way the package calls it. Reference: lottie-web 5.13.

| | **Mynell Lottie** | ThorVG for Unity | SkiaForUnity | rlottie players |
| --- | --- | --- | --- | --- |
| Crashed (out of 199 files, macOS) | 0 | 4 | 4 | 2 |
| Didn't load | 0 | 0 | 0 | 10 |
| Pixels that differ from lottie-web, median | 0.5% | 3.4% | 5.5% | 5.4% |
| Frames that match lottie-web (under 1% of pixels differ) | 62% | 33% | 26% | 21 - 22% |
| Old Bodymovin 3 / 4.0 files with wrong colours (out of 9) | 0 | 6 | 6 | 6 |

Speed, in milliseconds per frame (lower is better). 94 of the 95 files from the accuracy rows (one crashes
rlottie), every frame (26,241 frames), drawing only, no GPU upload. Apple M2 Pro.

| | **Mynell, all cores** | Mynell, 1 core | ThorVG for Unity | SkiaForUnity | rlottie players |
| --- | --- | --- | --- | --- | --- |
| 512 x 512 | 0.18 | 0.32 | 0.25 | 0.42 | 0.10 - 0.12 |
| 1024 x 1024 | 0.28 | 0.78 | 0.72 | 1.41 | 0.27 - 0.37 |
| 1024 x 1024, 10 heaviest files | 0.49 | 1.88 | 2.89 | 4.11 | 0.90 - 1.09 |

So rlottie is the fastest on average, but it skips text and effects. The 10 heaviest files are the ones ThorVG takes
longest on (blurs, mattes, masks, effects): there Mynell with all cores is the fastest, and on one core it is faster
than ThorVG and SkiaForUnity. On the 23 files that every player draws the same way, at 1024 x 1024: rlottie
0.20 - 0.26 ms, Mynell 0.21 ms (0.47 ms on one core), ThorVG 0.43 ms, SkiaForUnity 1.21 ms. Mynell also doesn't
redraw or re-upload frames that didn't change (pauses, holds), which this test doesn't show.

How the test was done:

- Files: lottie-web's demo and test animations and Skia's Skottie test files. 3 frames each (at 25%, 50% and 75%),
  fitted into 512 px. 5 files that need external image files were left out.
- A pixel counts as different when a colour channel is off by more than 16 out of 255, on a white background.
- The accuracy rows use the 95 files that lottie-web draws completely. 89 other files use After Effects features
  that lottie-web skips (effects, layer styles, 3D cameras, merge paths), so lottie-web can't be the reference
  there. On those files Mynell is closest to Skia's Skottie (median 1.4% of pixels differ; ThorVG 33%, rlottie 42%).
  lottie-web drew 13 files empty, and 2 are old exports that are counted in the colour row only.
- Crashes: ThorVG while loading 4 text files with animated glyphs, SkiaForUnity while drawing the same 4 files
  (in macOS font code), rlottie while drawing 2 files (a gradient opacity test and an animated Hue/Saturation). The
  10 files rlottie doesn't load all have a 3D camera. A crash in native code closes the whole app.
- Old files: Bodymovin 3.x and 4.0 stored colours as 0 - 255. lottie-web and Mynell convert them, the others don't.
- Cores: ThorVG's Unity build has threads turned off and SkiaForUnity draws in `Update()`, so both use one core.
  rlottie spreads path rasterization over a few threads (about 1.3 cores busy).

Notes:

- **ThorVG for Unity** is the official ThorVG package. Version 0.1.1 ships ThorVG v1.0-pre34, while ThorVG itself is
  at v1.1.3. Its native libraries are built without expressions and threads, and without a font loader except on
  WebGL (`scripts/build-thorvg.py`). The 6 effects are Tint, Fill, Stroke, Tritone, Drop Shadow and Gaussian Blur.
  It can also import SVG files as sprites. MIT.
- **SkiaForUnity** is a SkiaSharp binding for Unity. Next to Lottie it has HarfBuzz text, SVG and UI shapes. Skottie
  only runs expressions with an expression manager, and the SkiaSharp API doesn't have one. No WebGL library in the
  repo. MIT.
- [1] **rlottie players:** [unity-rlottie](https://github.com/gindemit/unity-rlottie) 0.4.5 (MIT, WebGL in the 0.5.0
  experimental release) and [unity-lottie-player](https://github.com/gilzoide/unity-lottie-player) 1.1.1 (Unlicense,
  also tvOS, visionOS and WebGL, renders in jobs). Both use Samsung's [rlottie](https://github.com/Samsung/rlottie),
  whose README says it is deprecated and no longer maintained. Both gave almost the same results in the test.
- **Also found:** [u.movin](https://github.com/leetful/u.movin) (last update May 2019, masks and mattes are on its TODO
  list), [PopLottie](https://github.com/NewChromantics/PopLottie.UnityPackage) (UI Toolkit, its README says trim paths,
  rectangles and text layers aren't implemented), and Coherent Gameface, a paid HTML UI framework whose docs say it
  officially supports "Lottie Light" (no expressions and effects).

## Accuracy

The reference is lottie-web (SVG renderer), because that is what most designers preview with. Where lottie-web skips
an After Effects feature or gets it wrong, this player follows After Effects (checked against Skia's Skottie):

- merge paths (lottie-web ignores them)
- 3D layers seen through a camera, with perspective (lottie-web's SVG renderer flattens them). Layers seen without
  perspective look exactly like in lottie-web.
- layer styles: drop shadow, inner shadow, outer glow, inner glow, colour overlay, stroke
- effects lottie-web skips: Gradient Ramp, Hue/Saturation, Brightness & Contrast, Invert, Threshold, Black & White,
  CC Toner, Shift Channels, Levels, Linear Wipe, Radial Wipe, Venetian Blinds, Fractal Noise, Motion Tile, Corner Pin,
  Bulge, Directional Blur, Sharpen, Displacement Map and CC Sphere. lottie-web's own effects (Fill, Tint,
  Tritone, Pro Levels, Gaussian Blur, Drop Shadow, Stroke, Set Matte, Transform) work like in lottie-web.
- text: several selectors per animator add up, animator blur, colour emoji
- `loopIn` / `loopOut`, `wiggle` and `random` in expressions are exact and repeatable
- luma mattes, chained mattes, lighten / darken / difference masks and mask expansion
- Offset Path and Pucker & Bloat go back to the original shape at 0 (lottie-web keeps the last changed shape)
- drop shadows and blurs aren't cut off at the layer bounds
- scaled-down images are mipmapped, like in Chrome

**Not drawn** (the Inspector lists them, and so does `LottieAsset.Unsupported`): the Bevel & Emboss, Satin and
Gradient Overlay layer styles, SkSL effects (Skia only) and the Twist modifier. 3D layers are drawn in layer order;
After Effects also sorts intersecting 3D layers by depth.

**Expressions** are run at import for every frame and saved as keyframes. The JavaScript that After Effects and
Bodymovin exports use is supported: `thisComp`, `thisLayer`, `comp()`, `layer()`, `effect()`, `toComp` / `fromComp`,
`valueAtTime`, `loopIn` / `loopOut`, `wiggle`, `linear` / `ease`, vector math, `random` / `seedRandom`, markers and text
properties. An expression that depends on something at runtime (like the time of day) is frozen at import.

**Text** uses the glyph outlines from the file. If the file only names a font, the importer looks for it in the
project (`.ttf`, `.otf`, `.ttc` under `Assets`), then in the OS font folders, and stores the glyphs in the asset.
dotLottie files can carry their own fonts. If a font can't be found, a common sans-serif font is used and the
Inspector tells you which font to add. Text animators (including per-character 3D), range and expression selectors,
text boxes, paragraph alignment, text on a path and animated glyphs are supported.

**Emoji** in text are drawn in colour, like in a browser. The importer takes them from the system's colour emoji font
(Apple Color Emoji, Segoe UI Emoji or Noto Color Emoji) and stores them in the asset, so they show on every platform.
`LottieCompileOptions.EmojiFontPath` picks another font. Text compiled at runtime can only use the fonts of the
device, and WebGL has none, so import such animations in the Editor or pass a font with `FontResolver`.

**Images** embedded in the file (base64) or in a dotLottie are decoded at import. External image files are looked up
next to the JSON file.

## Performance tips

- Each player renders in Burst jobs on worker threads. Big frames (more than 512 x 256 pixels) are split into bands
  that render in parallel. When many players render at once, the bands are shared so the threads aren't overloaded.
- A frame that looks the same as the previous one is neither drawn nor uploaded again.
- Texture size is the main cost. Keep *Resolution Scale* at 1 and lower *Max Texture Size* for big animations when
  you don't need sharp detail.
- Blurs, shadows, glows, layer styles and big masks or mattes cost more than plain shapes. Motion Tile, Corner Pin,
  Bulge, CC Sphere, Displacement Map and 3D layers in perspective render their frame as one band.
- WebGL has no worker threads and no Burst. Animations look the same there, they are just slower.

## Troubleshooting

- **Something is missing.** Check the asset in the Inspector, unsupported features are listed there.
- **The animation is blurry.** Raise *Resolution Scale* or *Max Texture Size*.
- **Dark edges.** Your material doesn't use premultiplied alpha blending (see Shaders).
- **A JSON file is imported as a TextAsset.** Use *Assets > Lottie > Import Selected JSON as Lottie*.
