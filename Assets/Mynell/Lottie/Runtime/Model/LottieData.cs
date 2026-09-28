using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Model
{
    /// <summary>
    /// Managed container of a compiled animation. Serializable to a compact binary blob and
    /// convertible to <see cref="LottieNativeData"/> for Burst jobs.
    /// </summary>
    public sealed class LottieData
    {
        public int FormatVersion = LottieFormat.Version;
        public string LottieVersion;
        public string Name;
        public float Width, Height, FrameRate, InPoint, OutPoint;

        public CompData[] Comps = Array.Empty<CompData>();
        public LayerData[] Layers = Array.Empty<LayerData>();
        public ShapeNode[] Nodes = Array.Empty<ShapeNode>();
        public PropDesc[] Props = Array.Empty<PropDesc>();
        public KeyframeData[] Keys = Array.Empty<KeyframeData>();
        public float[] Floats = Array.Empty<float>();
        public MaskData[] Masks = Array.Empty<MaskData>();
        public DashData[] Dashes = Array.Empty<DashData>();
        public ImageAsset[] Images = Array.Empty<ImageAsset>();
        public EffectData[] Effects = Array.Empty<EffectData>();
        public EffectParam[] EffectParams = Array.Empty<EffectParam>();

        public ShapeProgram[] Programs = Array.Empty<ShapeProgram>();
        public SPGroup[] PGroups = Array.Empty<SPGroup>();
        public SPShape[] PShapes = Array.Empty<SPShape>();
        public SPModifier[] PModifiers = Array.Empty<SPModifier>();
        public SPStyle[] PStyles = Array.Empty<SPStyle>();
        public SPOp[] POps = Array.Empty<SPOp>();
        public int[] PIndices = Array.Empty<int>();

        public GlyphData[] Glyphs = Array.Empty<GlyphData>();
        public GlyphContour[] GlyphContours = Array.Empty<GlyphContour>();
        public float2[] GlyphPoints = Array.Empty<float2>();
        public TextDocData[] TextDocs = Array.Empty<TextDocData>();
        public TextLetter[] TextLetters = Array.Empty<TextLetter>();
        public int[] TextInts = Array.Empty<int>();
        public float[] TextFloats = Array.Empty<float>();
        public TextDocKey[] TextDocKeys = Array.Empty<TextDocKey>();
        public TextAnimatorData[] TextAnimators = Array.Empty<TextAnimatorData>();
        public TextSelectorData[] TextSelectors = Array.Empty<TextSelectorData>();
        public TextLayerInfo[] TextLayers = Array.Empty<TextLayerInfo>();

        /// <summary>Names ('nm') parallel to <see cref="Layers"/> and <see cref="Nodes"/>, for key paths.</summary>
        public string[] LayerNames = Array.Empty<string>();
        public string[] NodeNames = Array.Empty<string>();
        /// <summary>Per layer: (first node, count) of the shape items at the root of a shape layer; (-1, 0) otherwise.</summary>
        public int2[] LayerShapeRoots = Array.Empty<int2>();

        public LottieMarker[] Markers = Array.Empty<LottieMarker>();
        /// <summary>Image asset ids / file names / embedded data (data: URIs), parallel to <see cref="Images"/>.</summary>
        public string[] ImageIds = Array.Empty<string>();
        public string[] ImagePaths = Array.Empty<string>();
        public byte[][] ImageBytes = Array.Empty<byte[]>();
        public List<string> Warnings = new List<string>();
        public List<string> Unsupported = new List<string>();

        public float Duration => FrameRate > 0 ? (OutPoint - InPoint) / FrameRate : 0;
        public float TotalFrames => OutPoint - InPoint;

        // ---------------------------------------------------------------- serialization

        const uint Magic = 0x4C4E594D; // "MYNL"

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(64 * 1024);
            using var w = new BinaryWriter(ms);
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(LottieVersion ?? "");
            w.Write(Name ?? "");
            w.Write(Width); w.Write(Height); w.Write(FrameRate); w.Write(InPoint); w.Write(OutPoint);
            WriteArr(w, Comps); WriteArr(w, Layers); WriteArr(w, Nodes); WriteArr(w, Props); WriteArr(w, Keys);
            WriteArr(w, Floats); WriteArr(w, Masks); WriteArr(w, Dashes); WriteArr(w, Images); WriteArr(w, Effects);
            WriteArr(w, EffectParams); WriteArr(w, Programs); WriteArr(w, PGroups); WriteArr(w, PShapes);
            WriteArr(w, PModifiers); WriteArr(w, PStyles); WriteArr(w, POps); WriteArr(w, PIndices);
            WriteArr(w, Glyphs); WriteArr(w, GlyphContours); WriteArr(w, GlyphPoints); WriteArr(w, TextDocs);
            WriteArr(w, TextLetters); WriteArr(w, TextInts); WriteArr(w, TextFloats); WriteArr(w, TextDocKeys);
            WriteArr(w, TextAnimators); WriteArr(w, TextLayers); WriteArr(w, TextSelectors);
            WriteStrings(w, LayerNames); WriteStrings(w, NodeNames); WriteArr(w, LayerShapeRoots);
            w.Write(Markers.Length);
            foreach (var m in Markers) { w.Write(m.Name ?? ""); w.Write(m.Time); w.Write(m.Duration); }
            w.Write(ImageIds.Length);
            for (int i = 0; i < ImageIds.Length; i++)
            {
                w.Write(ImageIds[i] ?? "");
                w.Write(ImagePaths[i] ?? "");
                var b = ImageBytes[i];
                w.Write(b?.Length ?? -1);
                if (b != null) w.Write(b);
            }
            w.Write(Warnings.Count);
            foreach (var s in Warnings) w.Write(s);
            w.Write(Unsupported.Count);
            foreach (var s in Unsupported) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }

        public static bool TryDeserialize(byte[] blob, out LottieData data)
        {
            data = null;
            if (blob == null || blob.Length < 8) return false;
            using var ms = new MemoryStream(blob, false);
            using var r = new BinaryReader(ms);
            if (r.ReadUInt32() != Magic) return false;
            int version = r.ReadInt32();
            if (version != LottieFormat.Version) return false;
            var d = new LottieData { FormatVersion = version };
            d.LottieVersion = r.ReadString();
            d.Name = r.ReadString();
            d.Width = r.ReadSingle(); d.Height = r.ReadSingle(); d.FrameRate = r.ReadSingle();
            d.InPoint = r.ReadSingle(); d.OutPoint = r.ReadSingle();
            d.Comps = ReadArr<CompData>(r); d.Layers = ReadArr<LayerData>(r); d.Nodes = ReadArr<ShapeNode>(r);
            d.Props = ReadArr<PropDesc>(r); d.Keys = ReadArr<KeyframeData>(r); d.Floats = ReadArr<float>(r);
            d.Masks = ReadArr<MaskData>(r); d.Dashes = ReadArr<DashData>(r); d.Images = ReadArr<ImageAsset>(r);
            d.Effects = ReadArr<EffectData>(r); d.EffectParams = ReadArr<EffectParam>(r);
            d.Programs = ReadArr<ShapeProgram>(r); d.PGroups = ReadArr<SPGroup>(r); d.PShapes = ReadArr<SPShape>(r);
            d.PModifiers = ReadArr<SPModifier>(r); d.PStyles = ReadArr<SPStyle>(r); d.POps = ReadArr<SPOp>(r);
            d.PIndices = ReadArr<int>(r);
            d.Glyphs = ReadArr<GlyphData>(r); d.GlyphContours = ReadArr<GlyphContour>(r); d.GlyphPoints = ReadArr<float2>(r);
            d.TextDocs = ReadArr<TextDocData>(r); d.TextLetters = ReadArr<TextLetter>(r); d.TextInts = ReadArr<int>(r);
            d.TextFloats = ReadArr<float>(r); d.TextDocKeys = ReadArr<TextDocKey>(r); d.TextAnimators = ReadArr<TextAnimatorData>(r);
            d.TextLayers = ReadArr<TextLayerInfo>(r);
            d.TextSelectors = ReadArr<TextSelectorData>(r);
            d.LayerNames = ReadStrings(r); d.NodeNames = ReadStrings(r); d.LayerShapeRoots = ReadArr<int2>(r);
            int mc = r.ReadInt32();
            d.Markers = new LottieMarker[mc];
            for (int i = 0; i < mc; i++) d.Markers[i] = new LottieMarker { Name = r.ReadString(), Time = r.ReadSingle(), Duration = r.ReadSingle() };
            int ic = r.ReadInt32();
            d.ImageIds = new string[ic]; d.ImagePaths = new string[ic]; d.ImageBytes = new byte[ic][];
            for (int i = 0; i < ic; i++)
            {
                d.ImageIds[i] = r.ReadString();
                d.ImagePaths[i] = r.ReadString();
                int len = r.ReadInt32();
                d.ImageBytes[i] = len >= 0 ? r.ReadBytes(len) : null;
            }
            int wc = r.ReadInt32();
            for (int i = 0; i < wc; i++) d.Warnings.Add(r.ReadString());
            int uc = r.ReadInt32();
            for (int i = 0; i < uc; i++) d.Unsupported.Add(r.ReadString());
            data = d;
            return true;
        }

        static void WriteStrings(BinaryWriter w, string[] a)
        {
            w.Write(a.Length);
            foreach (var x in a) w.Write(x ?? "");
        }

        static string[] ReadStrings(BinaryReader r)
        {
            int n = r.ReadInt32();
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadString();
            return a;
        }

        static unsafe void WriteArr<T>(BinaryWriter w, T[] arr) where T : unmanaged
        {
            arr ??= Array.Empty<T>();
            w.Write(arr.Length);
            if (arr.Length == 0) return;
            var bytes = MemoryMarshal.AsBytes(arr.AsSpan());
            w.Write(bytes);
        }

        static T[] ReadArr<T>(BinaryReader r) where T : unmanaged
        {
            int n = r.ReadInt32();
            if (n <= 0) return Array.Empty<T>();
            var arr = new T[n];
            var bytes = MemoryMarshal.AsBytes(arr.AsSpan());
            int read = r.Read(bytes);
            if (read != bytes.Length) throw new EndOfStreamException();
            return arr;
        }
    }

    /// <summary>
    /// Read-only native mirror of <see cref="LottieData"/> consumed by Burst jobs.
    /// Owned by the animation (one copy shared by every player).
    /// </summary>
    public struct LottieNativeData : IDisposable
    {
        [ReadOnly] public NativeArray<CompData> Comps;
        [ReadOnly] public NativeArray<LayerData> Layers;
        [ReadOnly] public NativeArray<ShapeNode> Nodes;
        [ReadOnly] public NativeArray<PropDesc> Props;
        [ReadOnly] public NativeArray<KeyframeData> Keys;
        [ReadOnly] public NativeArray<float> Floats;
        [ReadOnly] public NativeArray<MaskData> Masks;
        [ReadOnly] public NativeArray<DashData> Dashes;
        [ReadOnly] public NativeArray<ImageAsset> Images;
        [ReadOnly] public NativeArray<EffectData> Effects;
        [ReadOnly] public NativeArray<EffectParam> EffectParams;
        [ReadOnly] public NativeArray<ShapeProgram> Programs;
        [ReadOnly] public NativeArray<SPGroup> PGroups;
        [ReadOnly] public NativeArray<SPShape> PShapes;
        [ReadOnly] public NativeArray<SPModifier> PModifiers;
        [ReadOnly] public NativeArray<SPStyle> PStyles;
        [ReadOnly] public NativeArray<SPOp> POps;
        [ReadOnly] public NativeArray<int> PIndices;
        [ReadOnly] public NativeArray<GlyphData> Glyphs;
        [ReadOnly] public NativeArray<GlyphContour> GlyphContours;
        [ReadOnly] public NativeArray<float2> GlyphPoints;
        [ReadOnly] public NativeArray<TextDocData> TextDocs;
        [ReadOnly] public NativeArray<TextLetter> TextLetters;
        [ReadOnly] public NativeArray<int> TextInts;
        [ReadOnly] public NativeArray<float> TextFloats;
        [ReadOnly] public NativeArray<TextDocKey> TextDocKeys;
        [ReadOnly] public NativeArray<TextAnimatorData> TextAnimators;
        [ReadOnly] public NativeArray<TextLayerInfo> TextLayers;
        [ReadOnly] public NativeArray<TextSelectorData> TextSelectors;
        /// <summary>Spatial arc-length tables (runtime generated).</summary>
        [ReadOnly] public NativeArray<float> Tables;
        public float Width, Height, FrameRate, InPoint, OutPoint;
        public int MaxGroups, MaxShapes;

        public bool IsCreated => Layers.IsCreated;

        public static LottieNativeData Create(LottieData d)
        {
            var n = new LottieNativeData
            {
                Width = d.Width, Height = d.Height, FrameRate = d.FrameRate, InPoint = d.InPoint, OutPoint = d.OutPoint,
                Comps = Make(d.Comps), Layers = Make(d.Layers), Nodes = Make(d.Nodes), Props = Make(d.Props),
                Floats = Make(d.Floats), Masks = Make(d.Masks), Dashes = Make(d.Dashes),
                Images = Make(d.Images), Effects = Make(d.Effects), EffectParams = Make(d.EffectParams),
                Programs = Make(d.Programs), PGroups = Make(d.PGroups), PShapes = Make(d.PShapes),
                PModifiers = Make(d.PModifiers), PStyles = Make(d.PStyles), POps = Make(d.POps), PIndices = Make(d.PIndices),
                Glyphs = Make(d.Glyphs), GlyphContours = Make(d.GlyphContours), GlyphPoints = Make(d.GlyphPoints),
                TextDocs = Make(d.TextDocs), TextLetters = Make(d.TextLetters), TextInts = Make(d.TextInts),
                TextFloats = Make(d.TextFloats), TextDocKeys = Make(d.TextDocKeys), TextAnimators = Make(d.TextAnimators),
                TextLayers = Make(d.TextLayers), TextSelectors = Make(d.TextSelectors),
            };
            // Keyframes get their spatial arc-length tables resolved here (lottie-web: buildBezierData, 150 samples).
            var keys = (KeyframeData[])d.Keys.Clone();
            var tables = new List<float>();
            SpatialTables.Build(d, keys, tables);
            n.Keys = Make(keys);
            n.Tables = Make(tables.ToArray());
            int maxG = 1, maxS = 1;
            foreach (var p in d.Programs) { maxG = Math.Max(maxG, p.GroupCount); maxS = Math.Max(maxS, p.ShapeCount); }
            n.MaxGroups = maxG;
            n.MaxShapes = maxS;
            return n;
        }

        static unsafe NativeArray<T> Make<T>(T[] src) where T : unmanaged
        {
            int len = src?.Length ?? 0;
            var arr = new NativeArray<T>(Math.Max(1, len), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (len > 0)
            {
                fixed (T* p = src)
                    UnsafeUtility.MemCpy(arr.GetUnsafePtr(), p, (long)len * sizeof(T));
            }
            return arr;
        }

        public void Dispose()
        {
            if (!Layers.IsCreated) return;
            Comps.Dispose(); Layers.Dispose(); Nodes.Dispose(); Props.Dispose(); Keys.Dispose(); Floats.Dispose();
            Masks.Dispose(); Dashes.Dispose(); Images.Dispose(); Effects.Dispose(); EffectParams.Dispose();
            Programs.Dispose(); PGroups.Dispose(); PShapes.Dispose(); PModifiers.Dispose(); PStyles.Dispose();
            POps.Dispose(); PIndices.Dispose(); Tables.Dispose();
            Glyphs.Dispose(); GlyphContours.Dispose(); GlyphPoints.Dispose(); TextDocs.Dispose(); TextLetters.Dispose();
            TextInts.Dispose(); TextFloats.Dispose(); TextDocKeys.Dispose(); TextAnimators.Dispose(); TextLayers.Dispose();
            TextSelectors.Dispose();
            this = default;
        }
    }

    /// <summary>Builds the spatial bezier arc-length tables exactly like lottie-web's buildBezierData.</summary>
    static class SpatialTables
    {
        public static void Build(LottieData d, KeyframeData[] keys, List<float> tables)
        {
            var f = d.Floats;
            for (int k = 0; k < keys.Length; k++)
            {
                ref var key = ref keys[k];
                key.Table = -1;
                if (key.Spatial < 0 || key.End < 0) continue;
                // Find the property dimension: stored right before tangents as a count.
                int dim = (int)f[key.Spatial];
                int n = LottieFormat.CurveSegments;
                // Layout: [segmentLength, (partialLength, x, y, z) * n]
                int start = tables.Count;
                key.Table = start;
                tables.Add(0);
                double added = 0;
                double lx = 0, ly = 0, lz = 0;
                for (int s = 0; s < n; s++)
                {
                    double perc = s / (double)(n - 1);
                    double u = 1 - perc;
                    double x = 0, y = 0, z = 0;
                    for (int c = 0; c < Math.Min(dim, 3); c++)
                    {
                        double p1 = f[key.Start + c];
                        double p2 = f[key.End + c];
                        double p3 = p1 + f[key.Spatial + 1 + c];     // to
                        double p4 = p2 + f[key.Spatial + 4 + c];     // ti
                        double v = u * u * u * p1 + 3 * u * u * perc * p3 + 3 * u * perc * perc * p4 + perc * perc * perc * p2;
                        if (c == 0) x = v; else if (c == 1) y = v; else z = v;
                    }
                    double dist = 0;
                    if (s > 0)
                    {
                        dist = (x - lx) * (x - lx) + (y - ly) * (y - ly);
                        if (dim > 2) dist += (z - lz) * (z - lz);
                        dist = Math.Sqrt(dist);
                    }
                    added += dist;
                    tables.Add((float)dist);
                    tables.Add((float)x);
                    tables.Add((float)y);
                    tables.Add((float)z);
                    lx = x; ly = y; lz = z;
                }
                tables[start] = (float)added;
            }
        }
    }
}
