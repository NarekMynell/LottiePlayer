using System;
using System.Collections.Generic;
using Mynell.Lottie.Model;

namespace Mynell.Lottie.Compiler
{
    /// <summary>
    /// Expands a shape layer item tree into a flat "shape program". This is a structural port of
    /// lottie-web's SVGShapeElement.searchShapes (including repeater cloning / reload semantics) so that
    /// style scoping, modifier ownership, transform chains and paint order match the reference player.
    /// </summary>
    sealed class ShapeProgramBuilder
    {
        readonly LottieCompiler _c;

        // Global output tables (program-relative indices inside each program).
        readonly List<ShapeProgram> _programs = new List<ShapeProgram>();
        readonly List<SPGroup> _groups = new List<SPGroup>();
        readonly List<SPShape> _shapes = new List<SPShape>();
        readonly List<SPModifier> _modifiers = new List<SPModifier>();
        readonly List<SPStyle> _styles = new List<SPStyle>();
        readonly List<SPOp> _ops = new List<SPOp>();
        readonly List<int> _indices = new List<int>();

        public ShapeProgramBuilder(LottieCompiler c) { _c = c; }

        const int MaxRepeaterCopies = 512;

        // ------------------------------------------------------------------ instance model

        sealed class Item
        {
            public int Node;              // -1 for synthetic items
            public ShapeKind Kind;
            public List<Item> Children;   // groups
            public bool Processed;
            public bool Render;
            public bool Hidden;
            // processed data
            public Style StyleData;
            public Group GroupData;
            public Shape ShapeData;
            public Modifier ModData;
            // synthetic repeater copy transform
            public Modifier CopyOf;
        }

        sealed class Group
        {
            public int Id;
            public int Node;
            public Group Parent;
            public int Level;
            public int Transform = -1;
            public Modifier Repeater;
            public Item CopyItem;
            public byte Blend;
            public readonly List<object> Children = new List<object>();
            public void Append(object o) { if (!Children.Contains(o)) Children.Add(o); }
        }

        sealed class Style
        {
            public int Id;
            public int Node;
            public Group Group;
            public bool Closed;
            public readonly List<Shape> Shapes = new List<Shape>();
        }

        sealed class Shape
        {
            public int Id;
            public int Node;
            public Group Group;
            public bool Hidden;
            public readonly List<Style> Styles = new List<Style>();
        }

        sealed class Modifier
        {
            public int Id;
            public int Node;
            public ShapeKind Kind;
            public Group Group;
            public bool Closed;
            public readonly List<Shape> Shapes = new List<Shape>();
            // repeater
            public List<Item> Owner;
            public List<Item> Elements;
            public readonly List<Item> Copies = new List<Item>();
            public bool CopiesCreated;
            // merge paths: styles below the merge (the only ones that still paint the merged paths)
            public HashSet<Style> OpenStyles;
        }

        List<Style> _stylesList;
        List<Modifier> _shapeModifiers;
        List<Shape> _shapeList;
        List<Group> _groupList;

        // ------------------------------------------------------------------ build

        public int Build(int firstNode, int count)
        {
            _stylesList = new List<Style>();
            _shapeModifiers = new List<Modifier>();
            _shapeList = new List<Shape>();
            _groupList = new List<Group>();

            var rootItems = MakeItems(firstNode, count);
            var root = NewGroup(-1, null, 0);

            Search(rootItems, root, 0, true);

            // renderModifiers on the first frame: repeaters create their copies and trigger reloads.
            int guard = 0;
            while (guard++ < 64)
            {
                bool reloaded = false;
                for (int i = _shapeModifiers.Count - 1; i >= 0; i--)
                {
                    var m = _shapeModifiers[i];
                    if (m.Kind != ShapeKind.Repeater || m.CopiesCreated) continue;
                    CreateCopies(m);
                    Search(rootItems, root, 0, true);
                    reloaded = true;
                    break;
                }
                if (!reloaded) break;
            }

            return Emit(root);
        }

        List<Item> MakeItems(int first, int count)
        {
            var list = new List<Item>(count);
            for (int i = 0; i < count; i++) list.Add(MakeItem(first + i));
            return list;
        }

        Item MakeItem(int node)
        {
            var n = _c.GetNode(node);
            var it = new Item { Node = node, Kind = n.Kind, Hidden = (n.Flags & ShapeFlags.Hidden) != 0 };
            if (n.Kind == ShapeKind.Group) it.Children = MakeItems(n.First, n.Count);
            return it;
        }

        Group NewGroup(int node, Group parent, int level)
        {
            var g = new Group { Id = _groupList.Count, Node = node, Parent = parent, Level = level };
            if (node >= 0) g.Blend = _c.GetNode(node).BlendMode;
            _groupList.Add(g);
            return g;
        }

        void Search(List<Item> arr, Group container, int level, bool render)
        {
            var ownStyles = new List<Style>();
            var ownModifiers = new List<Modifier>();
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                var item = arr[i];
                bool processed = item.Processed;
                if (!processed) item.Render = render;
                switch (item.Kind)
                {
                    case ShapeKind.Fill:
                    case ShapeKind.Stroke:
                    case ShapeKind.GradientFill:
                    case ShapeKind.GradientStroke:
                    case ShapeKind.NoStyle:
                    {
                        if (!processed)
                        {
                            item.StyleData = new Style { Id = _stylesList.Count, Node = item.Node, Group = container, Closed = item.Hidden };
                            _stylesList.Add(item.StyleData);
                        }
                        else item.StyleData.Closed = item.Hidden;
                        if (item.Render) container.Append(item.StyleData);
                        ownStyles.Add(item.StyleData);
                        break;
                    }
                    case ShapeKind.Group:
                    {
                        if (!processed)
                        {
                            item.GroupData = NewGroup(item.Node, container, level + 1);
                            item.GroupData.CopyItem = item.CopyOf != null ? item : null;
                        }
                        Search(item.Children, item.GroupData, level + 1, render);
                        if (item.Render) container.Append(item.GroupData);
                        break;
                    }
                    case ShapeKind.Transform:
                    {
                        if (!processed)
                        {
                            if (item.CopyOf != null) container.Repeater = item.CopyOf;
                            else if (container.Transform < 0) container.Transform = item.Node;
                        }
                        break;
                    }
                    case ShapeKind.Path:
                    case ShapeKind.Rect:
                    case ShapeKind.Ellipse:
                    case ShapeKind.Star:
                    {
                        if (!processed)
                        {
                            item.ShapeData = new Shape { Id = _shapeList.Count, Node = item.Node, Group = container, Hidden = item.Hidden };
                            _shapeList.Add(item.ShapeData);
                            foreach (var m in _shapeModifiers)
                                if (!m.Closed && m.Kind != ShapeKind.Repeater) m.Shapes.Add(item.ShapeData);
                        }
                        foreach (var s in _stylesList)
                            if (!s.Closed && !item.ShapeData.Styles.Contains(s)) item.ShapeData.Styles.Add(s);
                        break;
                    }
                    case ShapeKind.Trim:
                    case ShapeKind.RoundCorners:
                    case ShapeKind.PuckerBloat:
                    case ShapeKind.ZigZag:
                    case ShapeKind.OffsetPath:
                    {
                        if (!processed)
                        {
                            item.ModData = new Modifier { Id = _shapeModifiers.Count, Node = item.Node, Kind = item.Kind, Group = container };
                            _shapeModifiers.Add(item.ModData);
                        }
                        else item.ModData.Closed = false;
                        ownModifiers.Add(item.ModData);
                        break;
                    }
                    case ShapeKind.Merge:
                    {
                        // Mode 1 ("merge") only draws the paths together, which is what styles do anyway.
                        if (item.Hidden || _c.GetNode(item.Node).Mode < 2) break;
                        if (!processed)
                        {
                            item.ModData = new Modifier { Id = _shapeModifiers.Count, Node = item.Node, Kind = item.Kind, Group = container };
                            item.ModData.OpenStyles = new HashSet<Style>();
                            foreach (var s in _stylesList)
                                if (!s.Closed) item.ModData.OpenStyles.Add(s);
                            _shapeModifiers.Add(item.ModData);
                        }
                        else item.ModData.Closed = false;
                        ownModifiers.Add(item.ModData);
                        break;
                    }
                    case ShapeKind.Repeater:
                    {
                        if (!processed)
                        {
                            var m = new Modifier { Id = _shapeModifiers.Count, Node = item.Node, Kind = item.Kind, Group = container, Owner = arr };
                            m.Elements = new List<Item>();
                            for (int e = 0; e < i; e++) m.Elements.Add(arr[e]);
                            item.ModData = m;
                            _shapeModifiers.Add(m);
                            render = false;
                        }
                        else item.ModData.Closed = true;
                        ownModifiers.Add(item.ModData);
                        break;
                    }
                }
                item.Processed = true;
            }
            foreach (var s in ownStyles) s.Closed = true;
            foreach (var m in ownModifiers) m.Closed = true;
        }

        void CreateCopies(Modifier m)
        {
            m.CopiesCreated = true;
            var node = _c.GetNode(m.Node);
            float maxCopies = _c.MaxScalar(node.P0, 3f);
            int copies = (int)Math.Ceiling(Math.Max(0f, maxCopies));
            if (copies > MaxRepeaterCopies)
            {
                _c.Warn($"Repeater with {copies} copies clamped to {MaxRepeaterCopies}.");
                copies = MaxRepeaterCopies;
            }
            for (int c = 0; c < copies; c++)
            {
                var group = new Item { Node = -1, Kind = ShapeKind.Group, Children = new List<Item>(), CopyOf = m };
                foreach (var e in m.Elements) group.Children.Add(CloneOriginal(e));
                group.Children.Add(new Item { Node = -1, Kind = ShapeKind.Transform, CopyOf = m });
                m.Owner.Insert(0, group);
                m.Copies.Insert(0, group);
            }
        }

        Item CloneOriginal(Item e)
        {
            if (e.Node < 0)
            {
                // A synthetic copy group from another repeater: rebuild it from its template elements.
                var g = new Item { Node = -1, Kind = e.Kind, CopyOf = e.CopyOf, Hidden = e.Hidden };
                if (e.Children != null)
                {
                    g.Children = new List<Item>();
                    foreach (var c in e.Children) g.Children.Add(CloneOriginal(c));
                }
                return g;
            }
            return MakeItem(e.Node);
        }

        // ------------------------------------------------------------------ emission

        int Emit(Group root)
        {
            var prog = new ShapeProgram
            {
                GroupFirst = _groups.Count,
                ShapeFirst = _shapes.Count,
                ModifierFirst = _modifiers.Count,
                StyleFirst = _styles.Count,
                OpFirst = _ops.Count,
                IndexFirst = _indices.Count,
            };
            var localIndices = new List<int>();

            foreach (var g in _groupList)
            {
                int copyIndex = -1;
                if (g.Repeater != null && g.CopyItem != null) copyIndex = g.Repeater.Copies.IndexOf(g.CopyItem);
                _groups.Add(new SPGroup
                {
                    Node = g.Node,
                    Parent = g.Parent?.Id ?? -1,
                    Transform = g.Transform,
                    Repeater = g.Repeater != null ? ModifierAppIndex(g.Repeater) : -1,
                    CopyIndex = copyIndex,
                    Level = g.Level,
                    BlendMode = g.Blend,
                    Visible = 1,
                });
            }
            foreach (var s in _shapeList)
                _shapes.Add(new SPShape { Node = s.Node, Group = s.Group.Id, Hidden = (byte)(s.Hidden ? 1 : 0) });

            // Modifiers in application order (lottie-web renderModifiers iterates from last to first).
            for (int i = _shapeModifiers.Count - 1; i >= 0; i--)
            {
                var m = _shapeModifiers[i];
                var sm = new SPModifier { Node = m.Node, Group = m.Group.Id, Kind = m.Kind };
                sm.ShapesFirst = localIndices.Count;
                foreach (var s in m.Shapes) localIndices.Add(s.Id);
                sm.ShapesCount = localIndices.Count - sm.ShapesFirst;
                sm.CopiesFirst = localIndices.Count;
                foreach (var c in m.Copies) localIndices.Add(c.GroupData != null ? c.GroupData.Id : -1);
                sm.CopiesCount = localIndices.Count - sm.CopiesFirst;
                _modifiers.Add(sm);
            }

            // Merged paths are only painted by the styles below the merge (lottie-android / After Effects).
            foreach (var m in _shapeModifiers)
                if (m.OpenStyles != null)
                    foreach (var sh in m.Shapes)
                        sh.Styles.RemoveAll(st => !m.OpenStyles.Contains(st));

            // Styles with their shapes (shape creation order).
            foreach (var st in _stylesList)
            {
                var ss = new SPStyle { Node = st.Node, Group = st.Group.Id, ShapesFirst = localIndices.Count };
                foreach (var sh in _shapeList)
                    if (sh.Styles.Contains(st)) localIndices.Add(sh.Id);
                ss.ShapesCount = localIndices.Count - ss.ShapesFirst;
                _styles.Add(ss);
            }

            // Paint order: DOM traversal.
            EmitOps(root);

            prog.GroupCount = _groups.Count - prog.GroupFirst;
            prog.ShapeCount = _shapes.Count - prog.ShapeFirst;
            prog.ModifierCount = _modifiers.Count - prog.ModifierFirst;
            prog.StyleCount = _styles.Count - prog.StyleFirst;
            prog.OpCount = _ops.Count - prog.OpFirst;
            _indices.AddRange(localIndices);
            prog.IndexCount = localIndices.Count;
            _programs.Add(prog);
            return _programs.Count - 1;
        }

        int ModifierAppIndex(Modifier m) => _shapeModifiers.Count - 1 - m.Id;

        void EmitOps(Group g)
        {
            foreach (var child in g.Children)
            {
                if (child is Style st)
                {
                    if (st.Closed && _c.GetNode(st.Node).Flags.HasFlag(ShapeFlags.Hidden)) continue;
                    var kind = _c.GetNode(st.Node).Kind;
                    if (kind == ShapeKind.NoStyle) continue;
                    _ops.Add(new SPOp { Kind = SPOpKind.Style, Index = st.Id });
                }
                else if (child is Group sub)
                {
                    _ops.Add(new SPOp { Kind = SPOpKind.BeginGroup, Index = sub.Id });
                    EmitOps(sub);
                    _ops.Add(new SPOp { Kind = SPOpKind.EndGroup, Index = sub.Id });
                }
            }
        }

        public void Export(LottieData d)
        {
            d.Programs = _programs.ToArray();
            d.PGroups = _groups.ToArray();
            d.PShapes = _shapes.ToArray();
            d.PModifiers = _modifiers.ToArray();
            d.PStyles = _styles.ToArray();
            d.POps = _ops.ToArray();
            d.PIndices = _indices.ToArray();
        }
    }
}
