// Mynell Lottie Player — UGUI MaskableGraphic component (Phase D: two-stage parallel pipeline).
// Drop on a Canvas child, assign a LottieAsset, press Play. Zero per-frame GC after warmup.
//
// Per-frame pipeline (Phase D adds Stage 2 parallelism on top of Phase C's direct upload):
//   1. BuildDrawCommandsJob  (IJob, Burst, sequential) — walks the Lottie hierarchy,
//      folds parent matrices, evaluates animated properties at the current frame,
//      flattens bezier paths into a shared PolyPool and prefix-sums each shape's
//      vertex/index slots into a NativeList<DrawCommand>.
//   2. TessellateCommandsJob (IJobParallelFor, Burst, parallel over commandCount) —
//      each iteration reads its own DrawCommand, ear-clips / extrudes against PolyPool,
//      transforms the points through cmd.World, and writes EXACTLY into its disjoint
//      slot in OutVertices / OutIndices.
//   3. UpdateGeometry pushes the resulting interleaved buffers straight into a single
//      persistent Mesh via Mesh.SetVertexBufferData / SetIndexBufferData / SetSubMesh
//      with full MeshUpdateFlags suppression (no bounds recalc, no validation, no notify).
//   4. canvasRenderer.SetMesh(_mesh). Tint applied via canvasRenderer.SetColor.
//
// On WebGL (single-threaded Burst), both stages fall back to .Run(). On standard
// platforms the parallel stage is scheduled across worker threads.
//
// MaskableGraphic is preserved as the base class so RectMask2D / Mask (stencil) clipping
// continue to work transparently (materialForRendering / SetClipRect handled by the base).

using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Mynell.LottiePlayer.Components
{
    [AddComponentMenu("Mynell/Lottie UGUI")]
    [RequireComponent(typeof(CanvasRenderer))]
    [ExecuteAlways]
    public sealed class MynellLottieUGUI : MaskableGraphic
    {
        // Production floors — match project reference (1000×1000 UI, dense illustration).
        private const int kProdMaxDrawCommands = 1024;
        private const int kProdMaxVertices = 16384;
        private const int kProdMaxIndices = 49152;
        private const int kProdMaxPolyPoolSize = 32768;
        private const int kProdMaxVerticesPerShape = 1024;
        private const int kProdParallelBatchSize = 4;

        [SerializeField] private LottieAsset _asset;
        [SerializeField] private bool _autoPlay = true;
        [SerializeField] private bool _loop = true;
        [SerializeField, Min(0f)] private float _speed = 1f;
        [SerializeField, Range(0.1f, 4f), Tooltip("Lower = sharper curves but more triangles.")]
        private float _tessellationQuality = 1f;
        [SerializeField, Min(64)] private int _maxVertices = 16384;
        [SerializeField, Min(192)] private int _maxIndices = 49152;
        [SerializeField, Min(64), Tooltip("Max number of DrawCommands per frame (one per fill+stroke). 1024 covers production-grade Lottie files (interactive volumes, dense logos).")]
        private int _maxDrawCommands = 1024;
        [SerializeField, Min(256), Tooltip("Shared polyline pool size (float2) — total flattened curve points across all shapes.")]
        private int _maxPolyPoolSize = 32768;
        [SerializeField, Min(64), Tooltip("Per-shape polyline cap. Drives the size of the persistent per-thread tessellator scratch buffers (Phase E zero-GC pool). Shapes that would flatten beyond this are clamped + warn-once.")]
        private int _maxVerticesPerShape = 1024;
        [SerializeField, Range(1, 32), Tooltip("Inner-loop batch size for the parallel tessellator. Larger = less scheduling overhead, smaller = better load-balancing.")]
        private int _parallelBatchSize = 4;
        [SerializeField, Tooltip("Letterbox the composition to fit the RectTransform (preserve aspect).")]
        private bool _preserveAspect = true;
        [SerializeField, Tooltip("Log vertex/index counts each frame (debug only).")]
        private bool _verbose;
        [SerializeField, Tooltip("Force opaque white vertex colors (visibility debug).")]
        private bool _debugForceOpaqueWhiteVertices;
        [SerializeField, Tooltip("Replace Lottie output with a centered test quad in the RectTransform.")]
        private bool _debugDrawTestQuad;

        // -- Runtime state --
        private LottieAnimationBlob _blob;
        private bool _blobReady;
        private bool _playing;
        private float _currentFrame;

        // Pre-allocated, interleaved vertex buffer (matches VertexLayout / 24 B per vertex).
        // Kept Allocator.Persistent across the component lifetime; resized only when
        // _maxVertices / _maxIndices changes (which also re-establishes mesh buffer params).
        private NativeArray<VertexLayout> _vertices;
        private NativeArray<ushort> _indices;
        // [vertReserved, indexReserved, overflow(0/1), commandCount, polyUsed]
        private NativeArray<int> _counts;
        // Stage-1 → Stage-2 hand-off. Length = commandCount, Capacity = _maxDrawCommands.
        // Cleared (length=0) before each Stage-1 schedule.
        private NativeList<DrawCommand> _commands;
        // Shared scratch where Stage 1 writes flattened polylines and Stage 2 reads them.
        // Persistent across frames; over-written each frame.
        private NativeArray<float2> _polyPool;
        // ----- Phase E persistent scratch pool (zero per-frame allocations) -----
        // The four arrays below replace the old Allocator.Temp scratchpads inside the
        // parallel tessellator. They are partitioned into JobsUtility.MaxJobThreadCount
        // disjoint slices; each parallel iteration writes only into the slice keyed by
        // its own [NativeSetThreadIndex]. Allocator.Persistent, lifetime tied to the
        // component (allocated in EnsureBuffers, disposed in ReleaseBuffers).
        private NativeArray<int> _scratchPrev;       // length = threadCount * _maxVerticesPerShape
        private NativeArray<int> _scratchNext;       // length = threadCount * _maxVerticesPerShape
        private NativeArray<byte> _scratchAlive;     // length = threadCount * _maxVerticesPerShape
        private NativeArray<int> _scratchLocalIdx;   // length = threadCount * IndexScratchSizePerThread(...)
        // Stage 1 (single-threaded) trim-path scratch. No per-thread partitioning needed.
        private NativeArray<float2> _scratchTrim;    // length = _maxVerticesPerShape
        // Cached at first allocation so EnsureBuffers can detect cap changes consistently.
        private int _scratchThreadCount;
        // Sticky overflow indicator — gates the warning so it fires at most once per
        // (asset, capacity) pair instead of every frame.
        private bool _overflowWarned;

        // Persistent mesh — created once in OnEnable, destroyed in OnDisable. MarkDynamic()
        // signals to Unity that we'll be rewriting it every frame; on most platforms this
        // allocates the buffer in CPU-mapped GPU memory to avoid upload stalls.
        private Mesh _mesh;
        // True when the mesh's buffer params no longer match _maxVertices / _maxIndices and
        // need to be re-declared via SetVertexBufferParams / SetIndexBufferParams.
        private bool _meshLayoutDirty = true;

        // Two-stage handles. We sync-Complete inside RunPipeline, so by the time the
        // method returns nothing is in flight. _hasScheduledJob is kept as a defensive
        // flag in case of partial failure between schedule and complete.
        private JobHandle _stage1Handle;
        private JobHandle _stage2Handle;
        private bool _hasScheduledJob;
        private int _lastVertCount;
        private int _lastIndexCount;
        private int _lastCommandCount;

        public LottieAsset Asset
        {
            get => _asset;
            set
            {
                if (_asset == value) return;
                ReleaseBlob();
                _asset = value;
                _overflowWarned = false; // re-arm the once-per-asset capacity warning.
                if (isActiveAndEnabled) EnsureBlob();
                SetVerticesDirty();
            }
        }

        public bool IsPlaying => _playing;
        public float CurrentFrame => _currentFrame;
        public float DurationSeconds => _asset != null ? _asset.DurationSeconds : 0f;

#if UNITY_EDITOR
        /// <summary>
        /// Applied when the component is first attached or when the user picks
        /// "Reset" in the Inspector context menu. Mirrors the field initializers
        /// above so existing scene/prefab instances can be force-bumped to the
        /// production-grade defaults without manual entry of every field.
        /// </summary>
        protected override void Reset()
        {
            base.Reset();
            _autoPlay = true;
            _loop = true;
            _speed = 1f;
            _tessellationQuality = 1f;
            _maxVertices = kProdMaxVertices;
            _maxIndices = kProdMaxIndices;
            _maxDrawCommands = kProdMaxDrawCommands;
            _maxPolyPoolSize = kProdMaxPolyPoolSize;
            _maxVerticesPerShape = kProdMaxVerticesPerShape;
            _parallelBatchSize = kProdParallelBatchSize;
            _preserveAspect = true;
            _verbose = false;
            _overflowWarned = false;
        }
#endif

        public void Play() { _playing = true; }
        public void Pause() { _playing = false; }
        public void Stop() { _playing = false; _currentFrame = _asset != null ? _asset.InFrame : 0f; SetVerticesDirty(); }
        public void Seek(float normalized)
        {
            if (_asset == null) return;
            normalized = Mathf.Clamp01(normalized);
            _currentFrame = Mathf.Lerp(_asset.InFrame, _asset.OutFrame, normalized);
            SetVerticesDirty();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            EnsureDefaultMaterial();
            EnsureMesh();
            EnsureBuffers();
            EnsureBlob();
            if (_asset != null) _currentFrame = _asset.InFrame;
            _playing = _autoPlay && Application.isPlaying;
        }

        /// <summary>UGUI requires a valid material on the Graphic for CanvasRenderer to draw.</summary>
        private void EnsureDefaultMaterial()
        {
            if (material == null)
                material = Canvas.GetDefaultCanvasMaterial();
        }

        public override Texture mainTexture => Texture2D.whiteTexture;

        // CRITICAL: dispose in OnDisable, not just OnDestroy.
        // Domain reload (entering/exiting Play mode, script recompile) destroys C# managed
        // references but leaves the underlying NativeArray (Persistent) memory leaked.
        // OnDisable is always invoked before reload and before play mode transitions.
        protected override void OnDisable()
        {
            CompleteIfRunning();
            // Detach our persistent mesh from the canvas BEFORE disposing it; otherwise
            // the canvas still holds a reference to the destroyed Mesh and may throw.
            if (canvasRenderer != null) canvasRenderer.SetMesh(null);
            ReleaseBlob();
            ReleaseBuffers();
            ReleaseMesh();
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            // Belt-and-suspenders: OnDisable should have cleaned up already.
            CompleteIfRunning();
            ReleaseBlob();
            ReleaseBuffers();
            ReleaseMesh();
            base.OnDestroy();
        }

        /// <summary>
        /// Re-dirty the mesh whenever a RectMask2D toggles culling so that, on becoming
        /// visible again, we rebuild geometry and push the latest frame instead of stale data.
        /// (UGUI declares this as <c>public virtual</c> on Graphic.)
        /// </summary>
        public override void OnCullingChanged()
        {
            base.OnCullingChanged();
            if (canvasRenderer != null && !canvasRenderer.cull) SetVerticesDirty();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            // Floor at production defaults so legacy serialized values (e.g. 256 commands)
            // auto-upgrade when the asset is touched in the Inspector.
            ApplyProductionBudgetFloors();
            _parallelBatchSize = Mathf.Clamp(_parallelBatchSize, 1, 32);
            // Capacity changed → re-arm the warning so the user sees confirmation that
            // the new budget is sufficient (or hears the same complaint immediately).
            _overflowWarned = false;
            // Hot-swap asset in editor — defer to next editor tick to avoid allocating
            // during serialization callbacks (which can fire before OnEnable).
            if (isActiveAndEnabled)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (this == null) return;
                    ReleaseBlob();
                    EnsureMesh();
                    EnsureBuffers();
                    EnsureBlob();
                    if (_asset != null)
                        _currentFrame = Mathf.Clamp(_currentFrame, _asset.InFrame, _asset.OutFrame);
                    SetVerticesDirty();
                };
            }
        }
#endif

        // ------------------------------ Buffer / mesh lifecycle ------------------------------

        private void EnsureBuffers()
        {
            if (!_vertices.IsCreated || _vertices.Length != _maxVertices)
            {
                if (_vertices.IsCreated) _vertices.Dispose();
                _vertices = new NativeArray<VertexLayout>(_maxVertices, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _meshLayoutDirty = true;
            }
            if (!_indices.IsCreated || _indices.Length != _maxIndices)
            {
                if (_indices.IsCreated) _indices.Dispose();
                _indices = new NativeArray<ushort>(_maxIndices, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _meshLayoutDirty = true;
            }
            if (!_counts.IsCreated)
            {
                // 5 slots: [vertReserved, indexReserved, overflow, commandCount, polyUsed]
                _counts = new NativeArray<int>(5, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            if (!_polyPool.IsCreated || _polyPool.Length != _maxPolyPoolSize)
            {
                if (_polyPool.IsCreated) _polyPool.Dispose();
                _polyPool = new NativeArray<float2>(_maxPolyPoolSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (!_commands.IsCreated || _commands.Capacity != _maxDrawCommands)
            {
                if (_commands.IsCreated) _commands.Dispose();
                _commands = new NativeList<DrawCommand>(_maxDrawCommands, Allocator.Persistent);
            }
            EnsureScratchPool();
        }

        /// <summary>
        /// Phase E: pre-allocate the per-thread tessellator scratch pool sized as
        /// <c>JobsUtility.MaxJobThreadCount * _maxVerticesPerShape</c>. Disposed and
        /// reallocated when either the worker count or the per-shape cap changes.
        /// At default settings (128 threads × 1024 verts) the four scratches consume
        /// ≈ 1.5 MB persistent memory and Stage 2 makes literally zero allocations
        /// per frame.
        /// </summary>
        private void EnsureScratchPool()
        {
            // MaxJobThreadCount is a Burst compile-time constant (typically 128). Any
            // [NativeSetThreadIndex] value falls inside [0, MaxJobThreadCount), so
            // partitioning the buffer this way is collision-free by construction.
            int threadCount = JobsUtility.MaxJobThreadCount;
            int polyPerThread = math.max(8, _maxVerticesPerShape);
            int idxPerThread = TessellateCommandsJob.IndexScratchSizePerThread(polyPerThread);

            int polyTotal = threadCount * polyPerThread;
            int idxTotal = threadCount * idxPerThread;

            // Only realloc when the configuration actually changed — common case (steady
            // state) hits zero of these branches.
            bool resize = _scratchThreadCount != threadCount;

            if (resize || !_scratchPrev.IsCreated || _scratchPrev.Length != polyTotal)
            {
                if (_scratchPrev.IsCreated) _scratchPrev.Dispose();
                _scratchPrev = new NativeArray<int>(polyTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (resize || !_scratchNext.IsCreated || _scratchNext.Length != polyTotal)
            {
                if (_scratchNext.IsCreated) _scratchNext.Dispose();
                _scratchNext = new NativeArray<int>(polyTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (resize || !_scratchAlive.IsCreated || _scratchAlive.Length != polyTotal)
            {
                if (_scratchAlive.IsCreated) _scratchAlive.Dispose();
                _scratchAlive = new NativeArray<byte>(polyTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (resize || !_scratchLocalIdx.IsCreated || _scratchLocalIdx.Length != idxTotal)
            {
                if (_scratchLocalIdx.IsCreated) _scratchLocalIdx.Dispose();
                _scratchLocalIdx = new NativeArray<int>(idxTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (!_scratchTrim.IsCreated || _scratchTrim.Length != polyPerThread)
            {
                if (_scratchTrim.IsCreated) _scratchTrim.Dispose();
                _scratchTrim = new NativeArray<float2>(polyPerThread, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            _scratchThreadCount = threadCount;
        }

        private void ReleaseBuffers()
        {
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            if (_counts.IsCreated) _counts.Dispose();
            if (_polyPool.IsCreated) _polyPool.Dispose();
            if (_commands.IsCreated) _commands.Dispose();
            if (_scratchPrev.IsCreated) _scratchPrev.Dispose();
            if (_scratchNext.IsCreated) _scratchNext.Dispose();
            if (_scratchAlive.IsCreated) _scratchAlive.Dispose();
            if (_scratchLocalIdx.IsCreated) _scratchLocalIdx.Dispose();
            if (_scratchTrim.IsCreated) _scratchTrim.Dispose();
            _scratchThreadCount = 0;
        }

        private void EnsureMesh()
        {
            if (_mesh != null) return;
            _mesh = new Mesh
            {
                name = "MynellLottieUGUI/" + name,
                hideFlags = HideFlags.DontSave,
            };
            _mesh.MarkDynamic();
            _mesh.indexFormat = IndexFormat.UInt16;
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 10000f));
            _meshLayoutDirty = true;
        }

        private void ReleaseMesh()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh);
            else DestroyImmediate(_mesh);
            _mesh = null;
        }

        /// <summary>
        /// Re-declare the mesh's vertex / index buffer parameters when the configured
        /// capacity changes. Called once per resize, never per frame.
        /// </summary>
        private void EnsureMeshLayout()
        {
            if (_mesh == null || !_meshLayoutDirty) return;
            _mesh.Clear(keepVertexLayout: false);
            _mesh.SetVertexBufferParams(_maxVertices, VertexLayout.Descriptors);
            _mesh.SetIndexBufferParams(_maxIndices, IndexFormat.UInt16);
            // One submesh, set sized to 0 initially — UpdateGeometry overwrites it each frame.
            _mesh.subMeshCount = 1;
            _mesh.SetSubMesh(0, new SubMeshDescriptor(0, 0, MeshTopology.Triangles), kSubMeshFlags);
            _meshLayoutDirty = false;
        }

        // Suppress bounds recalc / index validation — we own both. Do NOT set
        // DontNotifyMeshUsers: CanvasRenderer must see mesh buffer updates each frame.
        private const MeshUpdateFlags kBufferFlags =
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontResetBoneBounds;

        private const MeshUpdateFlags kSubMeshFlags =
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices;

        // ------------------------------ Blob lifecycle ------------------------------

        private void EnsureBlob()
        {
            if (_asset == null) { _blobReady = false; return; }
            if (_blobReady) return;
            try
            {
                _blob = _asset.CreateRuntimeBlob(Allocator.Persistent);
                _blobReady = true;
            }
            catch (System.Exception ex)
            {
                _blobReady = false;
                Debug.LogError($"[Mynell.Lottie] Failed to load '{_asset.name}': {ex.Message}", this);
            }
        }

        private void ReleaseBlob()
        {
            if (_blobReady)
            {
                _blob.Dispose();
                _blobReady = false;
            }
        }

        private void CompleteIfRunning()
        {
            if (_hasScheduledJob)
            {
                _stage1Handle.Complete();
                _stage2Handle.Complete();
                _hasScheduledJob = false;
            }
        }

        // ------------------------------ Per-frame ------------------------------

        private void Update()
        {
            if (!_blobReady || _asset == null) return;
            if (_playing && Application.isPlaying)
            {
                float deltaFrames = Time.unscaledDeltaTime * _asset.FrameRate * Mathf.Max(0f, _speed);
                _currentFrame += deltaFrames;
                if (_currentFrame >= _asset.OutFrame)
                {
                    if (_loop)
                    {
                        float dur = math.max(1f, _asset.OutFrame - _asset.InFrame);
                        _currentFrame = _asset.InFrame + ((_currentFrame - _asset.InFrame) % dur);
                    }
                    else
                    {
                        _currentFrame = _asset.OutFrame - 0.0001f;
                        _playing = false;
                    }
                }
            }
            // Skip the rebuild registry round-trip when fully culled by a RectMask2D;
            // OnCullingChanged will re-dirty us when we become visible again.
            if (canvasRenderer == null || canvasRenderer.cull) return;
            SetVerticesDirty();
        }

        /// <summary>
        /// Phase D two-stage pipeline:
        ///   Stage 1 (sequential, IJob)  → emit prefix-summed DrawCommands.
        ///   Stage 2 (parallel, IJobParallelFor) → tessellate + transform + write mesh slots.
        /// We sync-Complete between stages because Stage 2's iteration count depends on
        /// the command count emitted by Stage 1. Stage 1 is fast (~100-400 µs); Stage 2 is
        /// the heavy work and runs across worker threads (or .Run() on WebGL).
        /// </summary>
        /// <summary>
        /// Raises serialized caps to production floors so legacy instances (e.g. 256 commands)
        /// cannot silently clamp dense illustrations.
        /// </summary>
        private void ApplyProductionBudgetFloors()
        {
            _maxDrawCommands = Mathf.Max(_maxDrawCommands, kProdMaxDrawCommands);
            _maxVertices = Mathf.Max(_maxVertices, kProdMaxVertices);
            _maxIndices = Mathf.Max(_maxIndices, kProdMaxIndices);
            _maxPolyPoolSize = Mathf.Max(_maxPolyPoolSize, kProdMaxPolyPoolSize);
            _maxVerticesPerShape = Mathf.Max(_maxVerticesPerShape, kProdMaxVerticesPerShape);
            if (_parallelBatchSize < 1) _parallelBatchSize = kProdParallelBatchSize;
        }

        private void RunPipeline()
        {
            if (!_blobReady || _asset == null) return;
            CompleteIfRunning();
            ApplyProductionBudgetFloors();

            Rect rect = rectTransform.rect;
            float compW = _asset.Width > 0 ? _asset.Width : 1f;
            float compH = _asset.Height > 0 ? _asset.Height : 1f;
            float scaleX = math.max(1e-6f, rect.width / compW);
            float scaleY = math.max(1e-6f, rect.height / compH);
            float2 outScale;
            float2 outOffset;
            if (_preserveAspect)
            {
                float s = math.min(scaleX, scaleY);
                outScale = new float2(s, s);
                float usedW = compW * s;
                float usedH = compH * s;
                // Pivot-centered local space: composition center lands at RectTransform origin (0,0).
                outOffset = new float2(-usedW * 0.5f, usedH * 0.5f);
            }
            else
            {
                outScale = new float2(scaleX, scaleY);
                outOffset = new float2(-rect.width * 0.5f, rect.height * 0.5f);
            }

            // Reset per-frame state. Length=0 keeps the underlying capacity allocation.
            _commands.Clear();
            _counts[0] = 0; _counts[1] = 0; _counts[2] = 0; _counts[3] = 0; _counts[4] = 0;

            // ---------- Stage 1: BuildDrawCommandsJob (sequential) ----------
            var stage1 = new BuildDrawCommandsJob
            {
                Header = _blob.Header,
                Layers = _blob.Layers,
                Precomps = _blob.Precomps,
                Shapes = _blob.Shapes,
                Properties = _blob.Properties,
                Keyframes = _blob.Keyframes,
                PathProperties = _blob.PathProperties,
                PathSnapshots = _blob.PathSnapshots,
                BezierVertices = _blob.BezierVertices,
                ScalarPool = _blob.ScalarPool,
                Vec2Pool = _blob.Vec2Pool,
                Vec4Pool = _blob.Vec4Pool,
                CurrentFrame = _currentFrame,
                Tolerance = math.max(0.05f, _tessellationQuality),
                OutputScale = outScale,
                OutputOffset = outOffset,
                YFlip = -1f, // Lottie y-down → UGUI y-up.
                MaxVertices = _maxVertices,
                MaxIndices = _maxIndices,
                MaxVerticesPerShape = _maxVerticesPerShape,
                PolyPool = _polyPool,
                Commands = _commands,
                Counts = _counts,
                TrimScratch = _scratchTrim,
            };

#if UNITY_WEBGL && !UNITY_EDITOR
            stage1.Run();
#else
            _stage1Handle = stage1.Schedule();
            _stage1Handle.Complete();
#endif

            int cmdCount = _commands.Length;
            _lastVertCount = _counts[0];
            _lastIndexCount = _counts[1];
            _lastCommandCount = cmdCount;
            int overflow = _counts[2];

            if (cmdCount == 0)
            {
                Debug.LogWarning(
                    $"[Mynell.Lottie] Stage 1 emitted 0 draw commands at frame {_currentFrame:0.00}. " +
                    $"Check asset bake, layer visibility (ip/op), track mattes, or zero RectTransform size (rect={rect}).",
                    this);
            }

            if (overflow != 0 && !_overflowWarned)
            {
                _overflowWarned = true;
                Debug.LogWarning(
                    $"[Mynell.Lottie] '{(_asset != null ? _asset.name : "<null>")}': geometry budget exceeded — " +
                    $"verts {_lastVertCount}/{_maxVertices}, indices {_lastIndexCount}/{_maxIndices}, " +
                    $"commands {cmdCount}/{_maxDrawCommands}, polyPool {_counts[4]}/{_maxPolyPoolSize}. " +
                    $"Remaining shapes were clamped this frame; raise the limits in the Inspector if visuals are clipped.",
                    this);
            }

            // ---------- Stage 2: TessellateCommandsJob (parallel) ----------
            if (cmdCount > 0)
            {
                var stage2 = new TessellateCommandsJob
                {
                    Commands = _commands.AsArray(),
                    PolyPool = _polyPool,
                    OutVertices = _vertices,
                    OutIndices = _indices,
                    ScratchPrev = _scratchPrev,
                    ScratchNext = _scratchNext,
                    ScratchAlive = _scratchAlive,
                    ScratchLocalIdx = _scratchLocalIdx,
                    MaxVerticesPerShape = _maxVerticesPerShape,
                    ForceOpaqueWhite = (byte)(_debugForceOpaqueWhiteVertices ? 1 : 0),
                };
#if UNITY_WEBGL && !UNITY_EDITOR
                stage2.Run(cmdCount);
#else
                _stage2Handle = stage2.Schedule(cmdCount, math.max(1, _parallelBatchSize));
                _stage2Handle.Complete();
#endif
                CenterGeneratedVertices(_lastVertCount);
            }

            _hasScheduledJob = false;

            if (_verbose)
            {
                Debug.Log(
                    $"[Mynell.Lottie] frame={_currentFrame:0.00} layers={_blob.Layers.Length} " +
                    $"cmds={cmdCount} verts={_lastVertCount} idx={_lastIndexCount} " +
                    $"poly={_counts[4]}/{_maxPolyPoolSize} overflow={overflow} " +
                    $"rect={rect} scale={outScale} offset={outOffset}", this);
            }
        }

        /// <summary>
        /// Shift generated vertices so the mesh AABB center aligns with the RectTransform
        /// pivot (local origin). Corrects any residual projection drift from the job bridge.
        /// </summary>
        private void CenterGeneratedVertices(int vertexCount)
        {
            if (vertexCount <= 0 || !_vertices.IsCreated) return;

            float2 min = new float2(float.MaxValue, float.MaxValue);
            float2 max = new float2(float.MinValue, float.MinValue);
            for (int i = 0; i < vertexCount; i++)
            {
                float2 p = _vertices[i].Position.xy;
                min = math.min(min, p);
                max = math.max(max, p);
            }
            if (!math.all(math.isfinite(min)) || !math.all(math.isfinite(max))) return;

            float2 center = (min + max) * 0.5f;
            if (math.lengthsq(center) < 1e-8f) return;

            for (int i = 0; i < vertexCount; i++)
            {
                var v = _vertices[i];
                float3 pos = v.Position;
                pos.x -= center.x;
                pos.y -= center.y;
                v.Position = pos;
                _vertices[i] = v;
            }
        }

        /// <summary>
        /// Manual centered quad in RectTransform local space — isolates UGUI upload from tessellation.
        /// </summary>
        private void UploadDebugTestQuad(Rect rect)
        {
            float hx = math.max(1f, rect.width) * 0.45f;
            float hy = math.max(1f, rect.height) * 0.45f;
            var white = VertexLayout.OpaqueWhite;
            var uv = new float2(0.5f, 0.5f);

            _vertices[0] = new VertexLayout { Position = new float3(-hx, -hy, 0f), Color = white, UV = uv };
            _vertices[1] = new VertexLayout { Position = new float3(hx, -hy, 0f), Color = white, UV = uv };
            _vertices[2] = new VertexLayout { Position = new float3(hx, hy, 0f), Color = white, UV = uv };
            _vertices[3] = new VertexLayout { Position = new float3(-hx, hy, 0f), Color = white, UV = uv };

            _indices[0] = 0;
            _indices[1] = 1;
            _indices[2] = 2;
            _indices[3] = 0;
            _indices[4] = 2;
            _indices[5] = 3;

            _lastVertCount = 4;
            _lastIndexCount = 6;
            _lastCommandCount = 0;
        }

        // ------------------------------ Direct mesh upload ------------------------------

        /// <summary>
        /// Bypasses UGUI's VertexHelper / OnPopulateMesh path entirely. Runs the Burst job
        /// and pushes the resulting interleaved buffers straight into the persistent Mesh
        /// via the modern Mesh API, then hands the mesh to the CanvasRenderer.
        ///
        /// MaskableGraphic still drives <c>UpdateMaterial()</c> (for stencil masks /
        /// _ClipRect) on a separate canvas pass, so RectMask2D and stencil-Mask continue
        /// to clip our output transparently.
        /// </summary>
        protected override void UpdateGeometry()
        {
            if (!_blobReady || _asset == null || _mesh == null)
            {
                if (canvasRenderer != null) canvasRenderer.SetMesh(null);
                return;
            }

            EnsureDefaultMaterial();
            UpdateMaterial();

            EnsureMeshLayout();

            if (_debugDrawTestQuad)
            {
                UploadDebugTestQuad(rectTransform.rect);
            }
            else
            {
                RunPipeline();
            }

            int vc = _lastVertCount;
            int actualIndexCountThisFrame = _lastIndexCount;

            if (vc > 0 && actualIndexCountThisFrame > 0)
            {
                _mesh.SetVertexBufferData(_vertices, 0, 0, vc, 0, kBufferFlags);
                _mesh.SetIndexBufferData(_indices, 0, 0, actualIndexCountThisFrame, kBufferFlags);
            }

            if (_verbose)
            {
                Debug.Log(
                    $"[Lottie Render] Vertices: {vc} | Indices: {actualIndexCountThisFrame} | " +
                    $"Commands: {_lastCommandCount} | Bounds: {_mesh.bounds}", this);
            }

            _mesh.subMeshCount = 1;
            _mesh.SetSubMesh(0, new SubMeshDescriptor(0, actualIndexCountThisFrame, MeshTopology.Triangles), kSubMeshFlags);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(1000000f, 1000000f, 1000000f));

            Material renderMat = materialForRendering != null
                ? materialForRendering
                : (material != null ? material : Canvas.GetDefaultCanvasMaterial());
            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(renderMat, 0);
            canvasRenderer.SetTexture(mainTexture);
            canvasRenderer.SetColor(color);
            canvasRenderer.SetMesh(_mesh);

            var assignedMat = canvasRenderer.GetMaterial();
            Debug.Log(
                $"Graphic active: {isActiveAndEnabled}, Material: {(assignedMat != null ? assignedMat.name : "<null>")}, " +
                $"Verts: {vc}, Indices: {actualIndexCountThisFrame}, Cull: {canvasRenderer.cull}",
                this);
        }

        /// <summary>
        /// Neutralise the legacy OnPopulateMesh path. <see cref="UpdateGeometry"/> is now
        /// the entry point, but Graphic.UpdateGeometry's default implementation calls into
        /// OnPopulateMesh via DoMeshGeneration / DoLegacyMeshGeneration — by overriding
        /// UpdateGeometry we never reach this method, but we still leave the override in
        /// place so any third-party caller invoking the legacy hook produces an empty mesh
        /// rather than the Graphic base class's default quad.
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
        }
    }
}
