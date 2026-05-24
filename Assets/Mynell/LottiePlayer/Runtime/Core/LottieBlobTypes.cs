// Mynell Lottie Player — Blittable runtime data (Struct-of-Arrays).
// All cross-references are int offsets into flat NativeArrays.
// Burst-safe, GC-free, serializable as byte[] blobs.

using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Mynell.LottiePlayer
{
    /// <summary>Top-level animation header. Times are in *frames*, not seconds.</summary>
    [Serializable]
    public struct LottieHeader
    {
        public float FrameRate;
        public float InFrame;
        public float OutFrame;
        public float Width;
        public float Height;
        public int LayerCount;
        public int LayerOffset; // into Layers array
    }

    /// <summary>How a property is animated.</summary>
    public enum LottiePropertyKind : byte
    {
        Constant = 0,
        AnimatedScalar = 1,
        AnimatedVec2 = 2,
        AnimatedVec3 = 3,
        AnimatedVec4 = 4,
        AnimatedPath = 5,
    }

    /// <summary>
    /// Animatable property descriptor. Indexes into the relevant pool by kind.
    /// For Constant: ValueIndex points to a single Vec4 in Vec4Pool (xyzw used as needed).
    /// For AnimatedX: KeyframeOffset/KeyframeCount + ValueStride per keyframe in the relevant pool.
    /// </summary>
    [Serializable]
    public struct LottieProperty
    {
        public LottiePropertyKind Kind;
        public int ValueIndex;       // for Constant kinds
        public int KeyframeOffset;   // for Animated kinds
        public int KeyframeCount;
    }

    /// <summary>Keyframe with cubic-bezier easing in/out tangents (Lottie spec).</summary>
    [Serializable]
    public struct LottieKeyframe
    {
        public float Time;         // in frames
        public int ValueOffset;    // into per-kind value pool (start value index)
        public int OutTanIndex;    // index into ScalarPool*2 (cubic bezier control x,y)
        public int InTanIndex;     // same
        public byte Hold;          // 1 = hold (no interpolation)
        public byte HasOutTan;
        public byte HasInTan;
        public byte _pad;
    }

    /// <summary>
    /// Bezier path keyframe: vertices + in/out tangents (relative to vertex).
    /// All paths share the same flat BezierVertexPool; index range described here.
    /// </summary>
    [Serializable]
    public struct LottiePathSnapshot
    {
        public int VertexOffset;
        public int VertexCount;
        public byte Closed;
        public byte _pad0;
        public byte _pad1;
        public byte _pad2;
    }

    /// <summary>Single bezier control: vertex + outgoing tangent + incoming tangent (relative).</summary>
    [Serializable]
    public struct LottieBezierVertex
    {
        public float2 V;   // vertex
        public float2 In;  // in-tangent (relative)
        public float2 Out; // out-tangent (relative)
    }

    /// <summary>Shape primitive type.</summary>
    public enum LottieShapeKind : byte
    {
        Path = 0,    // generic bezier path ("sh")
        Rect = 1,    // "rc"
        Ellipse = 2, // "el"
        Group = 3,   // "gr" — has children
        Fill = 4,    // "fl" — solid color (also used for "gf" gradient → first-stop solid)
        Stroke = 5,  // "st" — quad-extruded polyline
        Transform = 6, // "tr" inside a group
        TrimPath = 7,  // "tm" — start/end percent + offset deg trim modifier
    }

    /// <summary>
    /// Shape descriptor. Polymorphic via 'Kind' + property indices.
    /// Children for Group: ChildOffset..ChildOffset+ChildCount into Shapes array.
    /// </summary>
    [Serializable]
    public struct LottieShape
    {
        public LottieShapeKind Kind;
        public byte Direction;   // 1 = CW, 0 = CCW (Lottie 'd')
        public ushort _pad;

        // Generic property slots (interpretation depends on Kind):
        // Path:    P0 = LottiePathProperty index (into PathProperties pool)
        // Rect:    P0 = position(Vec2), P1 = size(Vec2), P2 = roundness(Scalar)
        // Ellipse: P0 = position(Vec2), P1 = size(Vec2)
        // Fill:    P0 = color(Vec4 RGBA 0..1), P1 = opacity(Scalar 0..1, parsed from 0..100)
        // Stroke:  same as Fill + P2 = width(Scalar)
        // Group:   ChildOffset/ChildCount used; no properties
        // Transform: P0=anchor(Vec2), P1=position(Vec2), P2=scale(Vec2 percent),
        //            P3=rotation(Scalar deg), P4=opacity(Scalar 0..1, parsed from 0..100)
        public int P0;
        public int P1;
        public int P2;
        public int P3;
        public int P4;

        public int ChildOffset;
        public int ChildCount;
    }

    /// <summary>Animated path: same shape (vertex count) across all keyframes.</summary>
    [Serializable]
    public struct LottiePathProperty
    {
        public LottiePropertyKind Kind;        // Constant or AnimatedPath
        public int VertexCount;                // per snapshot
        public byte Closed;
        public byte _pad0;
        public byte _pad1;
        public byte _pad2;
        public int SnapshotOffset;             // into PathSnapshots
        public int SnapshotCount;              // 1 if constant, N if animated
        public int KeyframeOffset;             // into KeyframePool (time + easing only)
    }

    /// <summary>Layer type (Lottie 'ty').</summary>
    public enum LottieLayerKind : byte
    {
        Precomp = 0,
        Solid = 1,
        Image = 2,
        Null = 3,
        Shape = 4,
        Text = 5,
    }

    /// <summary>
    /// Layer header. Supports Shape (ty=4) and Precomp (ty=0) layers.
    /// Transform is stored as 5 property indices: anchor, position, scale, rotation, opacity.
    /// </summary>
    [Serializable]
    public struct LottieLayer
    {
        public LottieLayerKind Kind;
        public byte Is3D;
        /// <summary>1 when Lottie <c>td</c> track-matte source — must not be drawn.</summary>
        public byte IsTrackMatte;
        public byte _pad;

        public int ShapeOffset;  // into Shapes (Shape layers only)
        public int ShapeCount;

        // Precomp linkage: index into Precomps[] for ty=0 layers; -1 otherwise.
        public int PrecompId;
        public float PrecompWidth;
        public float PrecompHeight;

        // Sibling parenting (Lottie 'parent' → parent layer 'ind'):
        // LayerInd / ParentInd mirror the JSON indices; ParentLocalIndex is the parent's
        // local index within the enclosing composition (LayerOffset-relative), or -1.
        public int LayerInd;
        public int ParentInd;
        public int ParentLocalIndex;

        // Transform property indices into Properties:
        public int AnchorProp;
        public int PositionProp;
        public int ScaleProp;
        public int RotationProp;
        public int OpacityProp;

        public float InFrame;
        public float OutFrame;
        public float StartFrame; // 'st'
        public float StretchTimeMul; // 1 / sr  (precomputed)
    }

    /// <summary>
    /// Precomposition (nested layer collection). Layer range refers to entries inside
    /// the flat Layers[] array. Precomps may be nested (precomp inside precomp).
    /// </summary>
    [Serializable]
    public struct LottiePrecomp
    {
        public int LayerOffset;
        public int LayerCount;
    }

    /// <summary>
    /// Parse-time validation summary. Populated by <see cref="LottieParser.Parse(string, out LottieValidationReport)"/>
    /// and serialized inside <see cref="LottieAsset"/>. Pure counters + a precomputed
    /// summary message so Inspectors can render without re-running parse logic.
    /// All public fields are int → safely UnitySerializable inside a serialized parent.
    /// </summary>
    [Serializable]
    public struct LottieValidationReport
    {
        /// <summary>Total layers in the baked composition (top-level + precomp expansions).</summary>
        public int TotalLayers;
        /// <summary>Total shape primitives (groups, paths, rects, fills, strokes, transforms, trims).</summary>
        public int TotalShapes;
        /// <summary>Total path properties (constant + animated).</summary>
        public int TotalPaths;

        /// <summary>Layers whose 'ty' is unsupported (Solid / Image / Text / unknown). They are still parsed for transform inheritance but do not render.</summary>
        public int UnsupportedLayerCount;
        /// <summary>Shape kinds that are unsupported (e.g. 'sr' star, 'rp' repeater). Skipped silently.</summary>
        public int UnsupportedShapeCount;

        /// <summary>Path snapshots dropped because v.length &lt; 2 OR mismatched i/o/v lengths. The path becomes constant-empty for that keyframe.</summary>
        public int DroppedPathSnapshots;
        /// <summary>Animated paths whose snapshots have inconsistent vertex counts (would cause morphing crashes). Replaced with the first valid topology.</summary>
        public int DegradedAnimatedPaths;
        /// <summary>Keyframes that contained no valid 's' data and no previous snapshot to hold against. Skipped entirely.</summary>
        public int MalformedSnapshotKeyframes;
        /// <summary>
        /// Shapes that the parser rendered with a visually-acceptable fallback rather than
        /// the spec-exact result. Counts: gradient fills/strokes baked to first-stop solid,
        /// and merge-paths ('mm') treated as sequential additive (no boolean CSG). The
        /// geometry IS emitted — this counter just tells the user "your file uses features
        /// approximated by the v1 pipeline".
        /// </summary>
        public int ApproximatedShapes;

        /// <summary>True if any non-zero counter is set (used by the Inspector to toggle a HelpBox).</summary>
        public bool HasIssues =>
            UnsupportedLayerCount > 0 || UnsupportedShapeCount > 0 ||
            DroppedPathSnapshots > 0 || DegradedAnimatedPaths > 0 ||
            MalformedSnapshotKeyframes > 0 || ApproximatedShapes > 0;
    }

    /// <summary>
    /// Owning container for a baked Lottie animation. Holds NativeArrays with
    /// Allocator.Persistent. Dispose when no longer needed.
    /// </summary>
    public struct LottieAnimationBlob : IDisposable
    {
        public LottieHeader Header;

        // SoA pools — referenced by int offsets:
        public NativeArray<LottieLayer> Layers;
        public NativeArray<LottiePrecomp> Precomps;
        public NativeArray<LottieShape> Shapes;
        public NativeArray<LottieProperty> Properties;
        public NativeArray<LottieKeyframe> Keyframes;
        public NativeArray<LottiePathProperty> PathProperties;
        public NativeArray<LottiePathSnapshot> PathSnapshots;
        public NativeArray<LottieBezierVertex> BezierVertices;

        // Typed value pools (constants and keyframe values both live here):
        public NativeArray<float> ScalarPool;   // also stores easing tangents (xy pairs)
        public NativeArray<float2> Vec2Pool;
        public NativeArray<float4> Vec4Pool;

        public bool IsCreated => Layers.IsCreated;

        public void Dispose()
        {
            if (Layers.IsCreated) Layers.Dispose();
            if (Precomps.IsCreated) Precomps.Dispose();
            if (Shapes.IsCreated) Shapes.Dispose();
            if (Properties.IsCreated) Properties.Dispose();
            if (Keyframes.IsCreated) Keyframes.Dispose();
            if (PathProperties.IsCreated) PathProperties.Dispose();
            if (PathSnapshots.IsCreated) PathSnapshots.Dispose();
            if (BezierVertices.IsCreated) BezierVertices.Dispose();
            if (ScalarPool.IsCreated) ScalarPool.Dispose();
            if (Vec2Pool.IsCreated) Vec2Pool.Dispose();
            if (Vec4Pool.IsCreated) Vec4Pool.Dispose();
        }
    }
}
