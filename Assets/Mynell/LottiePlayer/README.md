# Mynell Lottie Player

Real-time, vector-native Lottie (BodyMovin) renderer for Unity 6+.
**Burst two-stage pipeline. No CPU rasterization. No render-to-texture.**

## How It Works

1. **Parse** Lottie JSON once → flat blittable `LottieAsset` (Struct-of-Arrays).
2. **Two-stage Burst pipeline per frame**:
   - **Stage 1 — `BuildDrawCommandsJob` (`IJob`)**: reverse z-order layer walk, parent×local matrix chain, scoped group `tr`, flatten beziers → `DrawCommand` prefix-sum slots.
   - **Stage 2 — `TessellateCommandsJob` (`IJobParallelFor`)**: ear-clip fills + stroke extrusion into disjoint vertex/index slots.
3. **`MynellLottieUGUI`** uploads a persistent dynamic `Mesh` via `SetVertexBufferData` / `SetIndexBufferData` → `CanvasRenderer.SetMesh`.

## Hierarchy Fixes (Picasso / z-order / mattes)

- **Reverse z-order**: layers iterated `count-1 → 0` so background draws first, foreground last.
- **Track mattes**: layers with `"td" > 0` are skipped at parse and render time.
- **Affine TRS**: `T(pos) · R · S(scale/100) · T(-anchor)` via `AffineMatrix.GetMatrixFromTransform`; globals composed as `Parent * Local`.

## Quick Start

1. Bake `.json` → **Mynell → Bake Selected Lottie JSON → LottieAsset**.
2. Add **Mynell → Lottie UGUI** to a Canvas child, assign the asset, Play.

## Dependencies

| Package | Purpose |
|---|---|
| `com.unity.burst` | Burst jobs |
| `com.unity.collections` | `NativeArray` blob |
| `com.unity.mathematics` | `float2` / `float4` math |

## Folder Layout

```
Assets/Mynell/LottiePlayer/
├── Runtime/
│   ├── Core/        # Parser, blob, LottieMath
│   ├── Math/        # BezierMath, EarClipping
│   ├── Jobs/        # BuildDrawCommandsJob, TessellateCommandsJob
│   └── Components/  # MynellLottieUGUI
└── Editor/
```

## License

© Mynell. All rights reserved.
