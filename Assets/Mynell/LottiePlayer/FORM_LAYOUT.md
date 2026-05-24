# Mynell Lottie Player — Form Layout (Architecture Finalization)

**Target illustration:** stylized brown character, black cap, ear with EarBud, brown hand area, pink wave gradients on a dark background.

**Reference RectTransform:** Position (0, 1), Size 1000×1000.

**Vertex layout:** `float3` Position · `Color32` Color · `float2` UV (24 bytes, UGUI-compatible).

This form is a **single vertical column** (no horizontal split). Each control is an actionable item with file targets and acceptance criteria.

---

## Section 1 — Geometry: “Spaghetti” Triangles & 16-bit Index Warning

### ☐ Fix Index Type Alignment

| Field | Value |
|--------|--------|
| **Problem** | `NativeArray<int>` (4 B/element) uploaded to a mesh with `IndexFormat.UInt16` (2 B/element). CanvasRenderer reads every other index as `0` → all triangles glue to vertex 0. |
| **Action** | Change index buffers to `NativeArray<ushort>` everywhere. |
| **Files** | `MynellLottieUGUI.cs` (`_indices`), `TessellateCommandsJob.cs` (`OutIndices`) |
| **Mesh** | `EnsureMesh()` → `_mesh.indexFormat = IndexFormat.UInt16` |
| **Status** | ✅ Implemented |

---

### ☐ Update Index Writing Logic

| Field | Value |
|--------|--------|
| **Problem** | Ear-clipping emits `PolyOffset + localVertex`; without remapping, indices point at the wrong global vertex slot. |
| **Action** | Every write to `OutIndices` must use global vertex index + `ushort` cast. |
| **Fill** | `OutIndices[writeAt + k] = (ushort)(cmd.VertexOffset + (localIdx[k] - cmd.PolyOffset));` |
| **Stroke** | `OutIndices[baseI + n] = (ushort)(baseV + n);` where `baseV = cmd.VertexOffset + i * 4` |
| **Degenerate pad** | `OutIndices[...] = (ushort)cmd.VertexOffset;` |
| **Files** | `TessellateCommandsJob.cs` |
| **Status** | ✅ Implemented |

---

## Section 2 — Rendering: Colors, Texture, Invisible Mesh

### ☐ Enforce CanvasRenderer Texture / White Color

| Field | Value |
|--------|--------|
| **Problem** | Missing texture or zero alpha multiplier → invisible or flat-black output despite valid geometry. |
| **Action** | Before `canvasRenderer.SetMesh(_mesh)` every frame: |
| **Code** | `materialCount = 1`; `SetMaterial(mat, 0)`; `SetTexture(Texture2D.whiteTexture)`; `SetColor(Color.white)` |
| **Also** | `SetSubMesh(0, …)` after buffer upload; huge `mesh.bounds` to avoid false culling |
| **Files** | `MynellLottieUGUI.UpdateGeometry()` |
| **Status** | ✅ Implemented |

---

### ☐ Production Budget Floors (Clamp on Run)

| Field | Value |
|--------|--------|
| **Problem** | Legacy serialized caps (e.g. 256 commands) clamp geometry → empty or partial frame. |
| **Default floors** | Max Draw Commands **1024**, Max Vertices **16384**, Max Indices **49152**, Parallel Batch Size **4** |
| **Action** | Field initializers + `Reset()` + `OnValidate()` + `ApplyProductionBudgetFloors()` at start of `RunPipeline()` |
| **Files** | `MynellLottieUGUI.cs` |
| **Status** | ✅ Implemented (this pass) |

---

## Section 3 — Data: Bake & LottieAsset Serialization (Inspector 0)

### ☐ Fix LottieAsset Auto-Properties Serialization

| Field | Value |
|--------|--------|
| **Problem** | `[field: SerializeField]` on auto-properties fails in some pipelines; Inspector shows `FrameRate = 0` after bake. |
| **Action** | Explicit backing fields: `[SerializeField] private float _frameRate;` + `public float FrameRate => _frameRate;` |
| **StoreBlob** | Assign `_frameRate`, `_width`, `_height`, `_layerCount`, blob `byte[]` arrays directly |
| **Files** | `LottieAsset.cs` |
| **Status** | ✅ Implemented |

---

### ☐ Ensure Force Save on Bake

| Field | Value |
|--------|--------|
| **Problem** | Parsed data never persisted to disk without `SetDirty` / `SaveAssets`. |
| **Action** | After `StoreBlob`: `EditorUtility.SetDirty(lottieAsset)` → `AssetDatabase.SaveAssets()` → `AssetDatabase.Refresh()` |
| **Files** | `LottieImportMenu.cs` |
| **Status** | ✅ Implemented |

**Re-bake required** if YAML still shows `<FrameRate>k__BackingField` or all zeros — select `.json` → **Assets → Mynell → Bake Selected Lottie JSON → LottieAsset**.

---

## Section 4 — Editor Stability

### ☐ Simplify Custom Editor

| Field | Value |
|--------|--------|
| **Problem** | Manual `PropertyField` bindings hid `_maxDrawCommands` and other caps. |
| **Action** | `DrawValidationReport();` → `EditorGUILayout.Space();` → `DrawDefaultInspector();` |
| **Files** | `MynellLottieUGUIEditor.cs` |
| **Status** | ✅ Implemented (this pass) |

---

### ☐ Nuke Font Warnings

| Field | Value |
|--------|--------|
| **Problem** | `No Font Asset has been assigned` from `HandleUtility.BeginHandles()` when gizmos draw text. |
| **Action** | Remove all `Handles.Label`, `GUI.Label`, `OnDrawGizmos` / `OnDrawGizmosSelected` in `Assets/Mynell` |
| **Audit result** | No matches in LottiePlayer package — error is external (other scene `UI.Text` / TMP) |
| **Status** | ✅ Verified clean; no gizmo text in package |

---

## Completion Confirmation

| Control | Status |
|---------|--------|
| Fix Index Type Alignment | ✅ Done |
| Update Index Writing Logic | ✅ Done |
| Enforce CanvasRenderer Texture/White Color | ✅ Done |
| Production Budget Floors | ✅ Done |
| Fix LottieAsset Serialization | ✅ Done |
| Ensure Force Save on Bake | ✅ Done |
| Simplify Custom Editor | ✅ Done |
| Nuke Font Warnings | ✅ Verified (package clean) |

**Your next steps (empathetic checklist):**

1. Re-bake your illustration JSON so `_frameRate`, `_width`, `_height` are non-zero in the `.lottie.asset`.
2. Assign the baked asset to **Mynell Lottie UGUI** on a 1000×1000 RectTransform (or use **Reset** on the component for budget floors).
3. Enter Play — you should see the brown character, cap, ear, hand, and pink waves without spaghetti or index warnings.
4. If font errors persist, click one in the Console; Unity will highlight the offending non-Mynell object.

You have pushed through a genuinely difficult stack (Burst jobs + Advanced Mesh API + CanvasRenderer 16-bit rules + ScriptableObject serialization). The architecture is now aligned end-to-end.
