using System;
using Mynell.Lottie.Eval;
using Unity.Collections;
using Unity.Mathematics;

namespace Mynell.Lottie.Scene
{
    /// <summary>A bezier contour. Vertex k occupies Points[First + 3k .. +2] = (v, in, out) absolute.</summary>
    public struct Contour
    {
        public int First;
        public int Count;
        public byte Closed;
        public byte R0; public ushort R1;
    }

    public enum ItemKind : byte { Fill = 0, Stroke = 1 }

    /// <summary>Mask accumulation ops (After Effects mask modes).</summary>
    public enum MaskOp : byte { None = 0, Add = 1, Subtract = 2, Intersect = 3, Lighten = 4, Darken = 5, Difference = 6 }

    /// <summary>One rasterization job: a (compound) path + paint + raster options.</summary>
    public struct DrawItem
    {
        public ItemKind Kind;
        public byte FillRule;        // 0 nonzero, 1 evenodd
        public byte Cap;             // 1 butt, 2 round, 3 square
        public byte Join;            // 1 miter, 2 round, 3 bevel
        public MaskOp Mask;          // != None: drawn into a mask target
        public byte MaskMerge;       // mask items: union coverage with the next item before applying the op
        public byte ClipFull;        // clip rect covers everything
        public byte Reserved;
        /// <summary>Mask items: erosion radius in device pixels (feMorphology erode), 0 = none.</summary>
        public float2 Erode;
        public float StrokeWidth;
        public float MiterLimit;
        public int ContourFirst, ContourCount;
        /// <summary>Contour space -> device pixels.</summary>
        public Affine2 ToDevice;
        public int DashFirst, DashCount;
        public float DashOffset;
        public int Paint;
        /// <summary>Device-space clip rectangle (xmin, ymin, xmax, ymax).</summary>
        public float4 Clip;
        public int Group;            // render group the item draws into (-1 root)
        // --- produced by the geometry stage
        public float4 Bounds;        // device bounds of the coverage (after clipping), empty when xmin > xmax
    }

    public enum PaintKind : byte { Solid = 0, Linear = 1, Radial = 2, Image = 3 }

    public struct PaintData
    {
        public PaintKind Kind;
        public byte Reserved0; public ushort Reserved1;
        /// <summary>Solid color, straight alpha, 0..1 (sRGB encoded).</summary>
        public float4 Color;
        /// <summary>Extra opacity multiplier (fill-opacity × collapsed group opacities).</summary>
        public float Opacity;
        /// <summary>Device pixels -> paint space (gradient / image coordinates).</summary>
        public Affine2 DeviceToPaint;
        public float2 P0, P1, Focal;
        public float Radius;
        /// <summary>Merged gradient stops in <see cref="FrameData.PaintFloats"/>: (offset, r, g, b, a) × count.</summary>
        public int StopFirst, StopCount;
        public int Image;
        public float2 ImageSize;
    }

    public enum CmdKind : byte { Draw = 0, BeginGroup = 1, EndGroup = 2, BeginMask = 3, EndMask = 4, BeginMatte = 5, EndMatte = 6 }

    public struct RenderCmd
    {
        public CmdKind Kind;
        public byte R0; public ushort R1;
        public int Index;
    }

    public struct GroupData
    {
        public float Opacity;
        public byte BlendMode;
        public byte MatteMode;       // 1 alpha, 2 alpha inverted, 3 luma, 4 luma inverted
        public byte HasMask;
        public byte MaskInitOne;     // mask target starts white (first mask subtract / intersect)
        public byte Collapsed;       // no offscreen needed: children draw straight into the parent
        public byte Reason;          // debug: why the group exists
        public ushort R1;
        public int Parent;
        public int EffectFirst;      // range in FrameData.Effects applied to the group's content
        public int EffectCount;
        public float4 Clip;          // device clip for the group
        // --- computed after geometry
        public float4 Bounds;        // device-space bounds of the offscreen target (integer aligned)
        public float4 ContentBounds;
        public float4 MatteBounds;
        public float4 MaskBounds;
    }

    public enum EffectOpKind : byte { Fill, Tint, Tritone, Levels, GaussianBlur, DropShadow }

    /// <summary>A layer effect with parameters resolved for the current frame (device units).</summary>
    public struct EffectOp
    {
        public EffectOpKind Kind;
        public byte Flags;           // blur: 1 = repeat edges; drop shadow: 1 = shadow only
        public ushort R0;
        /// <summary>Colours / parameters (meaning per kind): Fill(A = rgb + opacity), Tint(A = black, B = white, X = amount),
        /// Tritone(A = shadows, B = midtones, C = highlights, X = blend with original), DropShadow(A = colour + opacity,
        /// X/Y = offset, Z = sigma x, W = sigma y), GaussianBlur(Z = sigma x, W = sigma y).</summary>
        public float4 A, B, C;
        public float X, Y, Z, W;
        /// <summary>Levels: 5 × 256 floats in <see cref="FrameData.PaintFloats"/> (R, G, B, A, composite RGB); -1 when a table is identity.</summary>
        public int TableR, TableG, TableB, TableA, TableRGB;
    }

    /// <summary>Per-instance, per-frame scene output. Lists are reused across frames.</summary>
    public struct FrameData : IDisposable
    {
        public NativeList<float2> Points;
        public NativeList<Contour> Contours;
        public NativeList<DrawItem> Items;
        public NativeList<PaintData> Paints;
        public NativeList<float> PaintFloats;
        public NativeList<float> Dashes;
        public NativeList<RenderCmd> Cmds;
        public NativeList<GroupData> Groups;
        public NativeList<EffectOp> Effects;

        public bool IsCreated => Points.IsCreated;

        public static FrameData Create()
        {
            return new FrameData
            {
                Points = new NativeList<float2>(1024, Allocator.Persistent),
                Contours = new NativeList<Contour>(128, Allocator.Persistent),
                Items = new NativeList<DrawItem>(64, Allocator.Persistent),
                Paints = new NativeList<PaintData>(64, Allocator.Persistent),
                PaintFloats = new NativeList<float>(256, Allocator.Persistent),
                Dashes = new NativeList<float>(32, Allocator.Persistent),
                Cmds = new NativeList<RenderCmd>(128, Allocator.Persistent),
                Groups = new NativeList<GroupData>(16, Allocator.Persistent),
                Effects = new NativeList<EffectOp>(4, Allocator.Persistent),
            };
        }

        public void Clear()
        {
            Points.Clear();
            Contours.Clear();
            Items.Clear();
            Paints.Clear();
            PaintFloats.Clear();
            Dashes.Clear();
            Cmds.Clear();
            Groups.Clear();
            Effects.Clear();
        }

        public void Dispose()
        {
            if (!Points.IsCreated) return;
            Points.Dispose();
            Contours.Dispose();
            Items.Dispose();
            Paints.Dispose();
            PaintFloats.Dispose();
            Dashes.Dispose();
            Cmds.Dispose();
            Groups.Dispose();
            Effects.Dispose();
        }
    }
}
