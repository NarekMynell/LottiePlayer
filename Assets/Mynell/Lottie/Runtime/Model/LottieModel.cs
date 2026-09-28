using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Mynell.Lottie.Model
{
    // ------------------------------------------------------------------------------------------
    // Compiled, blittable representation of a Lottie animation. Everything references other
    // tables by index; every numeric value (static values, keyframe values, easing data, path
    // vertices, ...) lives in one float pool. The layout is designed to be consumed from Burst.
    // ------------------------------------------------------------------------------------------

    public static class LottieFormat
    {
        /// <summary>Bump whenever the compiled layout changes (forces re-import).</summary>
        public const int Version = 3;
        /// <summary>Number of samples lottie-web uses for bezier length tables.</summary>
        public const int CurveSegments = 150;
        /// <summary>lottie-web's ellipse / rounded corner constant.</summary>
        public const float RoundCorner = 0.5519f;
    }

    [Flags]
    public enum PropFlags : ushort
    {
        None = 0,
        Path = 1 << 0,          // value is a bezier path: Dim = 6 * vertexCount (v, i, o absolute)
        Closed = 1 << 1,        // path is closed
        Spatial = 1 << 2,       // at least one keyframe carries spatial tangents
    }

    /// <summary>Descriptor of an (optionally animated) property.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropDesc
    {
        /// <summary>Static: offset of the value in the float pool. Animated: index of the first keyframe.</summary>
        public int Offset;
        /// <summary>0 for static properties.</summary>
        public int KeyCount;
        /// <summary>Number of float components.</summary>
        public ushort Dim;
        public PropFlags Flags;
        public bool IsAnimated => KeyCount > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyframeData
    {
        public float Time;
        /// <summary>Float offset of the start value (Dim floats).</summary>
        public int Start;
        /// <summary>Float offset of the end value (next keyframe start or legacy 'e'); -1 if none.</summary>
        public int End;
        /// <summary>Float offset of easing data: EaseDims * (outX, outY, inX, inY). -1 = none.</summary>
        public int Ease;
        /// <summary>Float offset of spatial tangents (to.xyz, ti.xyz) or -1.</summary>
        public int Spatial;
        /// <summary>Runtime only: offset of the spatial arc-length table (-1).</summary>
        public int Table;
        public byte Hold;
        public byte EaseDims;
        public ushort Reserved;
    }

    public enum LayerKind : byte
    {
        Precomp = 0, Solid = 1, Image = 2, Null = 3, Shape = 4, Text = 5, Audio = 6,
        Camera = 13, Data = 15, Unknown = 255
    }

    [Flags]
    public enum LayerFlags : ushort
    {
        None = 0,
        Hidden = 1 << 0,
        MatteSource = 1 << 1,
        AutoOrient = 1 << 2,
        Is3D = 1 << 3,
        SplitPosition = 1 << 4,
        Has3DRotation = 1 << 5,
        HasMasks = 1 << 6,
        HasSkew = 1 << 7,
        HasTransform = 1 << 8,
    }

    /// <summary>Transform property indices (-1 = absent / default).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TransformDesc
    {
        public int Anchor, Position, PosX, PosY, PosZ, Scale, Rotation, Opacity, Skew, SkewAxis;
        public int RotX, RotY, RotZ, Orientation;
        public byte Split;      // 1 = separate position dimensions
        public byte Has3D;      // 1 = rx/ry/rz/or rotation model

        public static TransformDesc Empty => new TransformDesc
        {
            Anchor = -1, Position = -1, PosX = -1, PosY = -1, PosZ = -1, Scale = -1, Rotation = -1, Opacity = -1,
            Skew = -1, SkewAxis = -1, RotX = -1, RotY = -1, RotZ = -1, Orientation = -1
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CompData
    {
        public int FirstLayer;
        public int LayerCount;
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LayerData
    {
        public LayerKind Kind;
        public byte MatteMode;      // tt: 0 none, 1 alpha, 2 alpha inverted, 3 luma, 4 luma inverted
        public byte BlendMode;      // bm
        public byte Reserved;
        public LayerFlags Flags;
        public ushort Reserved2;
        public int Ind;             // 'ind'
        public int Parent;          // global layer index, -1
        public int MatteSource;     // global layer index of the matte provider, -1
        public int Comp;            // owning composition
        public float InPoint, OutPoint, StartTime, Stretch;
        public TransformDesc Transform;
        public int TimeRemap;       // prop, -1
        public int RefComp;         // precomp composition, -1
        public float Width, Height; // precomp size or solid size
        public float4 SolidColor;   // rgba 0..1
        public int Image;           // image asset index, -1
        public int ShapeProgram;    // shape program index, -1
        public int MaskFirst, MaskCount;
        public int EffectFirst, EffectCount;
        public int Text;            // text data index, -1
    }

    public enum ShapeKind : byte
    {
        Group, Path, Rect, Ellipse, Star,
        Fill, Stroke, GradientFill, GradientStroke, NoStyle,
        Transform,
        Trim, RoundCorners, Repeater, PuckerBloat, ZigZag, OffsetPath, Merge, Twist,
        Unknown
    }

    [Flags]
    public enum ShapeFlags : byte
    {
        None = 0,
        Hidden = 1 << 0,
        Reversed = 1 << 1,     // d == 3
        SplitPosition = 1 << 2,
        HasDirection = 1 << 3, // 'd' present with 1/2 (rect uses this)
    }

    /// <summary>A node of a shape layer item tree.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShapeNode
    {
        public ShapeKind Kind;
        public ShapeFlags Flags;
        /// <summary>Fill rule (1 nonzero, 2 evenodd) | trim mode | star type | repeater composite.</summary>
        public byte Mode;
        /// <summary>Gradient type (1 linear, 2 radial) | line cap (1 butt, 2 round, 3 square).</summary>
        public byte Mode2;
        /// <summary>Line join (1 miter, 2 round, 3 bevel).</summary>
        public byte Mode3;
        public byte BlendMode;
        public byte Direction;  // raw 'd' value
        public byte LineCap;
        /// <summary>Children range for groups (indices in the node table).</summary>
        public int First, Count;
        public int P0, P1, P2, P3, P4, P5, P6, P7, P8;
        public float Value;     // miter limit
        public int Extra;       // gradient color stop count
        public int DashFirst;   // stroke dashes (range in the dash table)
        public int DashCount;
    }

    public enum MaskMode : byte { None = 0, Add = 1, Subtract = 2, Intersect = 3, Lighten = 4, Darken = 5, Difference = 6 }

    [StructLayout(LayoutKind.Sequential)]
    public struct MaskData
    {
        public MaskMode Mode;
        public byte Inverted;
        public ushort Reserved;
        public int Path;
        public int Opacity;
        public int Expansion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DashData
    {
        public byte Kind;   // 0 dash, 1 gap, 2 offset
        public byte R0, R1, R2;
        public int Prop;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageAsset
    {
        public float Width, Height;
        public int Texture;     // index in the managed texture list, -1 when missing
        public int Reserved;
    }

    public enum EffectKind : byte
    {
        Unknown = 0, Tint = 20, Fill = 21, Stroke = 22, Tritone = 23, ProLevels = 24,
        DropShadow = 25, RadialWipe = 26, DisplacementMap = 27, Matte3 = 28, GaussianBlur = 29,
        Twirl = 30, MeshWarp = 31, Wavy = 32, Spherize = 33, Puppet = 34, Transform = 35
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EffectData
    {
        public EffectKind Kind;
        public byte Enabled;
        public ushort Reserved;
        /// <summary>Range in the effect-parameter table.</summary>
        public int ParamFirst, ParamCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EffectParam
    {
        public byte Type;    // lottie effect value type (0 slider, 1 angle, 2 color, 3 point, 4 checkbox, 7 dropdown, 10 layer...)
        public byte R0, R1, R2;
        public int Prop;     // -1 when the parameter has no value
    }


    // ---------------- Text ----------------

    /// <summary>A glyph outline (font size 100 units, y down, baseline at 0) and its advance width.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphData
    {
        public int ContourFirst, ContourCount;   // into GlyphContours
        public float Advance;
    }

    /// <summary>Glyph contour: Count vertices, 3 points each (v, in, out absolute) in GlyphPoints.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphContour
    {
        public int PointFirst, Count;
        public byte Closed;
        public byte R0; public ushort R1;
    }

    /// <summary>A laid-out text document (lottie-web TextProperty.completeTextData).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TextDocData
    {
        public int LetterFirst, LetterCount;
        public int LineFirst, LineCount;         // line widths in TextFloats
        public float Size, Tracking, BaselineShift, YOffset, Ascent, BoxWidth, JustifyOffset, StrokeWidth;
        public float2 BoxPos;
        public float4 FillColor, StrokeColor;
        public byte Justify, HasFill, HasStroke, StrokeOverFill;
        public byte HasBox, FillAnim, StrokeAnim, StrokeWidthAnim;
        public int AnimatorTotalFirst;           // totalChars per animator in TextInts
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextLetter
    {
        public int Glyph;                        // -1 when nothing is drawn
        public float L, An, Add, Extra;
        public int Line, Ind;
        public byte NewLine;
        public byte R0; public ushort R1;
        public int AnIndexFirst;                 // per-animator indexes in TextInts
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextDocKey
    {
        public float Time;
        public int Doc;
    }

    /// <summary>A text animator: range selector + animated properties (prop indices, -1 when absent).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TextAnimatorData
    {
        public int S, E, O, Xe, Ne, Sm, A;
        public byte Shape, Based, Range, Randomize;
        public byte HasE, R0; public ushort R1;
        public int R, Rx, Ry, Sk, Sa, Scale, Anchor, Opacity, Position, StrokeWidth, StrokeColor, FillColor, FillHue, FillSat, FillBright, Tracking;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextLayerInfo
    {
        public int DocKeyFirst, DocKeyCount;
        public int AnimatorFirst, AnimatorCount;
        public int Alignment;                    // prop, -1
        public byte Grouping;
        public byte HasPath, PathReverse, R0;
        public int PathMask;                     // mask index (global), -1
        public int PathFirstMargin, PathLastMargin, PathForceAlign, PathPerpendicular, PathReverseProp;
    }

    // ---------------- Expanded shape programs (built at load time) ----------------

    /// <summary>A group instance inside an expanded shape program.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SPGroup
    {
        public int Node;          // Group node (-1 = layer root)
        public int Parent;        // parent SPGroup (program-relative), -1 for root
        public int Transform;     // Transform node, -1
        public int Repeater;      // SPModifier (program-relative) of the repeater owning this copy, -1
        public int CopyIndex;     // position of the copy inside the repeater's array
        public int Level;
        public byte BlendMode;
        public byte Visible;      // 0 = originals replaced by a repeater (never painted)
        public ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SPShape
    {
        public int Node;
        public int Group;         // SPGroup (program-relative)
        public byte Hidden;
        public byte R0; public ushort R1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SPModifier
    {
        public int Node;
        public int Group;
        public ShapeKind Kind;
        public byte R0; public ushort R1;
        /// <summary>Shapes affected (range in the program index list), in lottie-web order.</summary>
        public int ShapesFirst, ShapesCount;
        /// <summary>Repeater only: copy groups ordered by array position (range in the index list).</summary>
        public int CopiesFirst, CopiesCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SPStyle
    {
        public int Node;
        public int Group;
        public int ShapesFirst, ShapesCount;
    }

    public enum SPOpKind : byte { BeginGroup = 0, EndGroup = 1, Style = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct SPOp
    {
        public SPOpKind Kind;
        public byte R0; public ushort R1;
        public int Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShapeProgram
    {
        public int GroupFirst, GroupCount;
        public int ShapeFirst, ShapeCount;
        public int ModifierFirst, ModifierCount;   // in application order
        public int StyleFirst, StyleCount;
        public int OpFirst, OpCount;               // paint order (bottom -> top)
        public int IndexFirst, IndexCount;         // index list base (lists are program-relative)
    }

    [Serializable]
    public struct LottieMarker
    {
        public string Name;
        public float Time;
        public float Duration;
    }
}
