using System;
using System.Collections.Generic;
using Mynell.Lottie.Model;
using Unity.Mathematics;

namespace Mynell.Lottie
{
    /// <summary>Properties that can be overridden at runtime through key paths (see <see cref="LottiePlayer.SetColor"/>).</summary>
    public enum LottieProperty
    {
        /// <summary>Fill / stroke color.</summary>
        Color,
        /// <summary>Fill / stroke / transform / layer opacity (0..1).</summary>
        Opacity,
        /// <summary>Stroke width in composition units.</summary>
        StrokeWidth,
        /// <summary>Layer or shape-group transform position.</summary>
        Position,
        /// <summary>Layer or shape-group transform scale (1 = 100%).</summary>
        Scale,
        /// <summary>Layer or shape-group transform rotation in degrees.</summary>
        Rotation,
    }

    /// <summary>
    /// Resolves key paths — names from the After Effects hierarchy separated by '/', like lottie-android's KeyPath:
    /// layer names (precomp layers contain the layers of their composition), then shape groups and items.
    /// '*' matches exactly one level, '**' any number of levels (including none). Example: "Button/**/Fill 1".
    /// </summary>
    public static class LottieKeyPath
    {
        struct Element
        {
            public int Layer;   // global layer index
            public int Node;    // shape node index, -1 for the layer itself
        }

        /// <summary>
        /// Animatable properties addressed by <paramref name="keyPath"/>: (property index, component) where component is
        /// -1 for the whole value, or 0 / 1 for the x / y property of a split position.
        /// </summary>
        public static List<int2> ResolveProperties(LottieData data, string keyPath, LottieProperty property)
        {
            var result = new List<int2>();
            if (data == null || string.IsNullOrEmpty(keyPath)) return result;
            string[] keys = keyPath.Split('/');
            var matched = new HashSet<long>();
            foreach (var root in Roots(data))
                Visit(data, root, keys, 0, matched);
            var seen = new HashSet<int>();
            foreach (long id in matched)
            {
                var e = new Element { Layer = (int)(id >> 32), Node = (int)(id & 0xFFFFFFFF) - 1 };
                foreach (int2 p in Properties(data, e, property))
                    if (p.x >= 0 && p.x < data.Props.Length && seen.Add(p.x)) result.Add(p);
            }
            return result;
        }

        /// <summary>Every key path of the animation (layers, groups and items), for discovery.</summary>
        public static List<string> Enumerate(LottieData data)
        {
            var list = new List<string>();
            if (data == null) return list;
            foreach (var root in Roots(data)) Walk(data, root, "", 0, list);
            return list;
        }

        static void Walk(LottieData data, Element e, string prefix, int depth, List<string> list)
        {
            if (depth > 64) return;
            string path = prefix.Length == 0 ? Name(data, e) : prefix + "/" + Name(data, e);
            list.Add(path);
            foreach (var c in Children(data, e)) Walk(data, c, path, depth + 1, list);
        }

        static IEnumerable<Element> Roots(LottieData data)
        {
            if (data.Comps.Length == 0) yield break;
            var comp = data.Comps[0];
            for (int i = 0; i < comp.LayerCount; i++) yield return new Element { Layer = comp.FirstLayer + i, Node = -1 };
        }

        static string Name(LottieData data, in Element e)
        {
            if (e.Node >= 0) return e.Node < data.NodeNames.Length ? data.NodeNames[e.Node] : "";
            return e.Layer < data.LayerNames.Length ? data.LayerNames[e.Layer] : "";
        }

        static IEnumerable<Element> Children(LottieData data, Element e)
        {
            if (e.Node < 0)
            {
                LayerData L = data.Layers[e.Layer];
                if (L.Kind == LayerKind.Precomp && L.RefComp >= 0 && L.RefComp < data.Comps.Length)
                {
                    var comp = data.Comps[L.RefComp];
                    for (int i = 0; i < comp.LayerCount; i++) yield return new Element { Layer = comp.FirstLayer + i, Node = -1 };
                }
                if (e.Layer < data.LayerShapeRoots.Length)
                {
                    int2 roots = data.LayerShapeRoots[e.Layer];
                    for (int i = 0; i < roots.y; i++) yield return new Element { Layer = e.Layer, Node = roots.x + i };
                }
                yield break;
            }
            ShapeNode n = data.Nodes[e.Node];
            if (n.Kind != ShapeKind.Group) yield break;
            for (int i = 0; i < n.Count; i++) yield return new Element { Layer = e.Layer, Node = n.First + i };
        }

        static void Visit(LottieData data, Element e, string[] keys, int depth, HashSet<long> matched)
        {
            if (depth >= keys.Length || depth > 128) return;
            string key = keys[depth];
            bool last = depth == keys.Length - 1;
            if (key == "**")
            {
                // Zero levels: this element must match the next key.
                if (!last) Visit(data, e, keys, depth + 1, matched);
                // One or more levels: '**' takes this element.
                if (last) matched.Add(Id(e));
                foreach (var c in Children(data, e)) Visit(data, c, keys, depth, matched);
                return;
            }
            if (key != "*" && !string.Equals(key, Name(data, e), StringComparison.Ordinal)) return;
            if (last)
            {
                matched.Add(Id(e));
                return;
            }
            foreach (var c in Children(data, e)) Visit(data, c, keys, depth + 1, matched);
        }

        static long Id(in Element e) => ((long)e.Layer << 32) | (uint)(e.Node + 1);

        static IEnumerable<int2> Properties(LottieData data, Element e, LottieProperty property)
        {
            if (e.Node < 0)
            {
                TransformDesc t = data.Layers[e.Layer].Transform;
                switch (property)
                {
                    case LottieProperty.Opacity: yield return new int2(t.Opacity, -1); break;
                    case LottieProperty.Scale: yield return new int2(t.Scale, -1); break;
                    case LottieProperty.Rotation: yield return new int2(t.Has3D != 0 ? t.RotZ : t.Rotation, -1); break;
                    case LottieProperty.Position:
                        if (t.Split != 0)
                        {
                            yield return new int2(t.PosX, 0);
                            yield return new int2(t.PosY, 1);
                        }
                        else yield return new int2(t.Position, -1);
                        break;
                }
                yield break;
            }
            ShapeNode n = data.Nodes[e.Node];
            switch (n.Kind)
            {
                case ShapeKind.Fill:
                    if (property == LottieProperty.Color) yield return new int2(n.P0, -1);
                    else if (property == LottieProperty.Opacity) yield return new int2(n.P1, -1);
                    break;
                case ShapeKind.Stroke:
                    if (property == LottieProperty.Color) yield return new int2(n.P0, -1);
                    else if (property == LottieProperty.Opacity) yield return new int2(n.P1, -1);
                    else if (property == LottieProperty.StrokeWidth) yield return new int2(n.P2, -1);
                    break;
                case ShapeKind.GradientFill:
                    if (property == LottieProperty.Opacity) yield return new int2(n.P0, -1);
                    break;
                case ShapeKind.GradientStroke:
                    if (property == LottieProperty.Opacity) yield return new int2(n.P0, -1);
                    else if (property == LottieProperty.StrokeWidth) yield return new int2(n.P6, -1);
                    break;
                case ShapeKind.Transform:
                    if (property == LottieProperty.Opacity) yield return new int2(n.P4, -1);
                    else if (property == LottieProperty.Scale) yield return new int2(n.P2, -1);
                    else if (property == LottieProperty.Rotation) yield return new int2(n.P3, -1);
                    else if (property == LottieProperty.Position)
                    {
                        if ((n.Flags & ShapeFlags.SplitPosition) != 0)
                        {
                            yield return new int2(n.P7, 0);
                            yield return new int2(n.P8, 1);
                        }
                        else yield return new int2(n.P1, -1);
                    }
                    break;
            }
        }

        /// <summary>
        /// The override value in Lottie units for a property (e.g. opacity 0..1 becomes 0..100). For split positions,
        /// <paramref name="component"/> selects x (0) or y (1).
        /// </summary>
        internal static float4 ToLottieUnits(LottieProperty property, float4 value, int component)
        {
            switch (property)
            {
                case LottieProperty.Opacity: return new float4(value.x * 100f, 0, 0, 0);
                case LottieProperty.Scale: return new float4(value.x * 100f, value.y * 100f, 100f, 0);
                case LottieProperty.Position: return component < 0 ? new float4(value.x, value.y, 0, 0) : new float4(value[component], 0, 0, 0);
                default: return value;
            }
        }
    }
}
