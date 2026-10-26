using System;
using System.Threading;
using Mynell.Lottie.Eval;
using Mynell.Lottie.Geometry;
using Mynell.Lottie.Scene;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Raster
{
    /// <summary>Open-addressing table from 64-bit hashes to entry indices; a hash maps to the entry inserted last.</summary>
    internal unsafe struct HashIndex : IDisposable
    {
        struct Slot
        {
            public ulong Hash;
            public int Entry;   // entry index + 1, 0 = empty
            int _pad;
        }

        NativeList<Slot> _slots;

        public static HashIndex Create() => new HashIndex { _slots = new NativeList<Slot>(0, Allocator.Persistent) };

        /// <summary>True when <paramref name="entries"/> entries don't fit at half load (call <see cref="Reset"/>).</summary>
        public bool Full(int entries) => entries * 2 > _slots.Length;

        /// <summary>Empties the table and sizes it for <paramref name="entries"/> entries.</summary>
        public void Reset(int entries)
        {
            int size = math.max(64, math.ceilpow2(entries * 2));
            if (_slots.Length != size) _slots.ResizeUninitialized(size);
            UnsafeUtility.MemClear(_slots.GetUnsafePtr(), (long)size * sizeof(Slot));
        }

        /// <summary>Entry stored for <paramref name="hash"/>, or -1.</summary>
        public int Find(ulong hash)
        {
            int mask = _slots.Length - 1;
            if (mask < 0) return -1;
            var s = (Slot*)_slots.GetUnsafeReadOnlyPtr();
            for (int i = (int)(hash & (uint)mask); ; i = (i + 1) & mask)
            {
                if (s[i].Entry == 0) return -1;
                if (s[i].Hash == hash) return s[i].Entry - 1;
            }
        }

        /// <summary>Maps <paramref name="hash"/> to <paramref name="entry"/> (the table must not be <see cref="Full"/>).</summary>
        public void Insert(ulong hash, int entry)
        {
            int mask = _slots.Length - 1;
            var s = (Slot*)_slots.GetUnsafePtr();
            int i = (int)(hash & (uint)mask);
            while (s[i].Entry != 0 && s[i].Hash != hash) i = (i + 1) & mask;
            s[i].Hash = hash;
            s[i].Entry = entry + 1;
        }

        public void Release()
        {
            _slots.Clear();
            _slots.TrimExcess();
        }

        public void Dispose()
        {
            if (_slots.IsCreated) _slots.Dispose();
        }

        public static ulong Hash(void* data, long length)
        {
            uint2 h = xxHash3.Hash64(data, length);
            return h.x | (ulong)h.y << 32;
        }
    }

    /// <summary>
    /// Device geometry (flattened, dashed and stroked polygons) of the items drawn in the previous frame. An item whose
    /// inputs are bit for bit those of a stored entry (contours, transform, stroke style, dashes, trim, tolerance) copies
    /// the stored polygons instead of building them again. Entry ids are never reused, so items with the same id have the
    /// same polygons: bands key their <see cref="CoverageCache"/> with it.
    /// </summary>
    internal unsafe struct GeometryCache : IDisposable
    {
        /// <summary>What the geometry of an item depends on, besides its contours and dashes.</summary>
        struct KeyHeader
        {
            public Affine2 ToDevice;
            public float Tolerance, StrokeWidth, MiterLimit, DashOffset;
            public float2 Trim;
            public int Style;     // stroke: 1 | cap << 8 | join << 16, fill: 0
            public int Contours, Dashes;
        }

        public struct Entry
        {
            public ulong Hash, Id;
            public int KeyFirst, KeyLength;
            public int PtFirst, PtCount;
            public int PolyFirst, PolyCount;
            public int ChunkFirst, ChunkCount;
            /// <summary>Bounds of the points (empty when there are none).</summary>
            public float4 Bounds;
            /// <summary>Last frame that used the entry.</summary>
            public uint Frame;
        }

        /// <summary>A key written by <see cref="Find"/> that has no entry yet.</summary>
        public struct Pending
        {
            public ulong Hash;
            public int KeyFirst, KeyLength;
        }

        struct State
        {
            public ulong Owner, NextId;
            public uint Frame;
        }

        /// <summary>The cache stops growing past this many points (16 MB).</summary>
        const int MaxPoints = 1 << 21;

        static long s_Owners;

        NativeArray<State> _state;
        NativeList<Entry> _entries;
        NativeList<byte> _keys;
        NativeList<float2> _pts;
        NativeList<Poly> _polys;
        NativeList<FrameRenderer.EdgeChunk> _chunks;
        HashIndex _index;

        public static GeometryCache Create()
        {
            var c = new GeometryCache
            {
                _state = new NativeArray<State>(1, Allocator.Persistent),
                _entries = new NativeList<Entry>(0, Allocator.Persistent),
                _keys = new NativeList<byte>(0, Allocator.Persistent),
                _pts = new NativeList<float2>(0, Allocator.Persistent),
                _polys = new NativeList<Poly>(0, Allocator.Persistent),
                _chunks = new NativeList<FrameRenderer.EdgeChunk>(0, Allocator.Persistent),
                _index = HashIndex.Create(),
            };
            c._state[0] = new State { Owner = (ulong)Interlocked.Increment(ref s_Owners) };
            return c;
        }

        /// <summary>Identifies this cache: ids of different caches are unrelated.</summary>
        public ulong Owner => _state[0].Owner;

        ref State St => ref UnsafeUtility.ArrayElementAsRef<State>(_state.GetUnsafePtr(), 0);

        public ref Entry this[int index] => ref UnsafeUtility.ArrayElementAsRef<Entry>(_entries.GetUnsafePtr(), index);

        /// <summary>Starts a frame: drops the entries the previous frame didn't use.</summary>
        public void BeginFrame()
        {
            ref State st = ref St;
            uint keep = st.Frame++;
            int n = _entries.Length;
            var e = (Entry*)_entries.GetUnsafePtr();
            int live = 0;
            for (int i = 0; i < n; i++)
                if (e[i].Frame == keep) live++;
            if (live == n) return;
            int w = 0, keys = 0, pts = 0, polys = 0, chunks = 0;
            for (int i = 0; i < n; i++)
            {
                Entry x = e[i];
                if (x.Frame != keep) continue;
                x.KeyFirst = Move(_keys, x.KeyFirst, ref keys, x.KeyLength);
                x.PtFirst = Move(_pts, x.PtFirst, ref pts, x.PtCount);
                x.PolyFirst = Move(_polys, x.PolyFirst, ref polys, x.PolyCount);
                x.ChunkFirst = Move(_chunks, x.ChunkFirst, ref chunks, x.ChunkCount);
                e[w++] = x;
            }
            _entries.ResizeUninitialized(w);
            _keys.ResizeUninitialized(keys);
            _pts.ResizeUninitialized(pts);
            _polys.ResizeUninitialized(polys);
            _chunks.ResizeUninitialized(chunks);
            Reindex();
        }

        /// <summary>Moves <paramref name="count"/> elements down to <paramref name="write"/>; returns their new start.</summary>
        static int Move<T>(NativeList<T> list, int from, ref int write, int count) where T : unmanaged
        {
            int to = write;
            if (from != to && count > 0)
            {
                T* p = (T*)list.GetUnsafePtr();
                UnsafeUtility.MemMove(p + to, p + from, (long)count * UnsafeUtility.SizeOf<T>());
            }
            write += count;
            return to;
        }

        void Reindex()
        {
            int n = _entries.Length;
            _index.Reset(n);
            var e = (Entry*)_entries.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < n; i++) _index.Insert(e[i].Hash, i);
        }

        /// <summary>
        /// Looks up the geometry of <paramref name="item"/>. Returns the entry (marked as used by this frame), or -1 with
        /// the item's key in <paramref name="pending"/>, to pass to <see cref="Add"/> once the geometry is built.
        /// </summary>
        public int Find(in FrameData f, in DrawItem item, float tolerance, out Pending pending)
        {
            var header = new KeyHeader { ToDevice = item.ToDevice, Tolerance = tolerance, Contours = item.ContourCount };
            if (item.Kind == ItemKind.Stroke)
            {
                header.StrokeWidth = item.StrokeWidth;
                header.MiterLimit = item.MiterLimit;
                header.Trim = item.Trim;
                header.Style = 1 | item.Cap << 8 | item.Join << 16;
                header.Dashes = item.DashCount;
                if (item.DashCount > 0) header.DashOffset = item.DashOffset;
            }
            var contours = (Contour*)f.Contours.GetUnsafeReadOnlyPtr() + item.ContourFirst;
            int length = sizeof(KeyHeader) + header.Dashes * 4;
            for (int c = 0; c < item.ContourCount; c++) length += 8 + contours[c].Count * 24;

            // The key goes at the end of the key store: kept by Add, dropped again on a hit.
            int first = _keys.Length;
            _keys.ResizeUninitialized(first + length);
            byte* key = (byte*)_keys.GetUnsafePtr() + first;
            *(KeyHeader*)key = header;
            byte* p = key + sizeof(KeyHeader);
            var pts = (float2*)f.Points.GetUnsafeReadOnlyPtr();
            for (int c = 0; c < item.ContourCount; c++)
            {
                Contour ct = contours[c];
                *(int2*)p = new int2(ct.Count, ct.Closed);
                long bytes = (long)ct.Count * 24;   // (vertex, in, out) per vertex
                UnsafeUtility.MemCpy(p + 8, pts + ct.First, bytes);
                p += 8 + bytes;
            }
            if (header.Dashes > 0) UnsafeUtility.MemCpy(p, (float*)f.Dashes.GetUnsafeReadOnlyPtr() + item.DashFirst, header.Dashes * 4L);

            ulong hash = HashIndex.Hash(key, length);
            pending = new Pending { Hash = hash, KeyFirst = first, KeyLength = length };
            int e = _index.Find(hash);
            if (e < 0) return -1;
            ref Entry en = ref this[e];
            if (en.KeyLength != length || UnsafeUtility.MemCmp((byte*)_keys.GetUnsafeReadOnlyPtr() + en.KeyFirst, key, length) != 0)
                return -1;
            _keys.ResizeUninitialized(first);
            en.Frame = St.Frame;
            return e;
        }

        /// <summary>
        /// Stores the geometry built for <paramref name="pending"/>: points, polygons (with <see cref="Poly.First"/>
        /// relative to <paramref name="pts"/> after subtracting <paramref name="ptOrigin"/>) and their bounds.
        /// Returns the new entry, or -1 when the cache is full.
        /// </summary>
        public int Add(in Pending pending, float2* pts, int ptCount, Poly* polys, int polyCount, int ptOrigin, float4 bounds)
        {
            if (_pts.Length + ptCount > MaxPoints)
            {
                _keys.ResizeUninitialized(pending.KeyFirst);
                return -1;
            }
            ref State st = ref St;
            var en = new Entry
            {
                Hash = pending.Hash, Id = ++st.NextId, KeyFirst = pending.KeyFirst, KeyLength = pending.KeyLength,
                PtFirst = _pts.Length, PtCount = ptCount, PolyFirst = _polys.Length, PolyCount = polyCount,
                ChunkFirst = _chunks.Length, Bounds = bounds, Frame = st.Frame,
            };
            _pts.AddRange(pts, ptCount);
            for (int k = 0; k < polyCount; k++)
            {
                Poly q = polys[k];
                q.First -= ptOrigin;
                _polys.Add(q);
                if (q.Count >= 2) FrameRenderer.AddChunks(pts + q.First, q.First, q.Count, _chunks);
            }
            en.ChunkCount = _chunks.Length - en.ChunkFirst;
            int index = _entries.Length;
            _entries.Add(en);
            if (_index.Full(_entries.Length)) Reindex();
            else _index.Insert(en.Hash, index);
            return index;
        }

        /// <summary>Appends an entry's points and polygons to frame geometry; returns where its points start.</summary>
        public int EmitGeometry(int e, NativeList<float2> outPts, NativeList<Poly> outPolys)
        {
            ref Entry en = ref this[e];
            int origin = outPts.Length;
            outPts.AddRange((float2*)_pts.GetUnsafeReadOnlyPtr() + en.PtFirst, en.PtCount);
            var src = (Poly*)_polys.GetUnsafeReadOnlyPtr() + en.PolyFirst;
            for (int k = 0; k < en.PolyCount; k++)
            {
                Poly q = src[k];
                q.First += origin;
                outPolys.Add(q);
            }
            return origin;
        }

        /// <summary>Appends an entry's edge chunks, for points emitted at <paramref name="origin"/>.</summary>
        public void EmitChunks(int e, int origin, NativeList<FrameRenderer.EdgeChunk> outChunks)
        {
            ref Entry en = ref this[e];
            var src = (FrameRenderer.EdgeChunk*)_chunks.GetUnsafeReadOnlyPtr() + en.ChunkFirst;
            for (int k = 0; k < en.ChunkCount; k++)
            {
                FrameRenderer.EdgeChunk ch = src[k];
                ch.First += origin;
                outChunks.Add(ch);
            }
        }

        /// <summary>Drops every entry and frees the memory (ids keep counting).</summary>
        public void Release()
        {
            if (_entries.Capacity == 0 && _pts.Capacity == 0) return;
            _entries.Clear(); _entries.TrimExcess();
            _keys.Clear(); _keys.TrimExcess();
            _pts.Clear(); _pts.TrimExcess();
            _polys.Clear(); _polys.TrimExcess();
            _chunks.Clear(); _chunks.TrimExcess();
            _index.Release();
        }

        public void Dispose()
        {
            if (!_state.IsCreated) return;
            _state.Dispose();
            _entries.Dispose();
            _keys.Dispose();
            _pts.Dispose();
            _polys.Dispose();
            _chunks.Dispose();
            _index.Dispose();
        }
    }

    /// <summary>
    /// Coverage spans of the items a band drew in the previous frame. The spans depend only on the key (geometry id, box,
    /// clip and fill rule), so an item with the same key replays them instead of being rasterized again.
    /// </summary>
    internal unsafe struct CoverageCache : IDisposable
    {
        public struct Key
        {
            public ulong Geometry;
            public int4 Box;
            public float4 Clip;
            public int FillRule, Pad;
        }

        struct Entry
        {
            public Key Key;
            public ulong Hash;
            public int RowFirst, SpanFirst, Rows;
            /// <summary>Last frame that used the entry.</summary>
            public uint Frame;
            /// <summary>All rows recorded: only complete entries are found and kept.</summary>
            public int Complete;
        }

        struct State
        {
            public ulong Owner;
            public uint Frame;
        }

        /// <summary>The cache stops growing past this many spans (12 MB).</summary>
        const int MaxSpans = 1 << 20;

        NativeArray<State> _state;
        NativeList<Entry> _entries;
        NativeList<int> _rows;               // per entry: Rows + 1 span offsets, relative to SpanFirst
        NativeList<CoverageSpan> _spans;
        HashIndex _index;

        public static CoverageCache Create() => new CoverageCache
        {
            _state = new NativeArray<State>(1, Allocator.Persistent),
            _entries = new NativeList<Entry>(0, Allocator.Persistent),
            _rows = new NativeList<int>(0, Allocator.Persistent),
            _spans = new NativeList<CoverageSpan>(0, Allocator.Persistent),
            _index = HashIndex.Create(),
        };

        ref State St => ref UnsafeUtility.ArrayElementAsRef<State>(_state.GetUnsafePtr(), 0);

        /// <summary>
        /// Starts a frame whose geometry ids come from the cache <paramref name="owner"/>: drops the entries the previous
        /// frame didn't use (all of them when the owner changed).
        /// </summary>
        public void BeginFrame(ulong owner)
        {
            ref State st = ref St;
            uint keep = st.Frame++;
            if (st.Owner != owner)
            {
                st.Owner = owner;
                keep = st.Frame;   // matches no entry
            }
            int n = _entries.Length;
            var e = (Entry*)_entries.GetUnsafePtr();
            int live = 0;
            for (int i = 0; i < n; i++)
                if (e[i].Frame == keep && e[i].Complete != 0) live++;
            if (live == n) return;
            int w = 0, rows = 0, spans = 0;
            var rowPtr = (int*)_rows.GetUnsafePtr();
            var spanPtr = (CoverageSpan*)_spans.GetUnsafePtr();
            for (int i = 0; i < n; i++)
            {
                Entry x = e[i];
                if (x.Frame != keep || x.Complete == 0) continue;
                int spanCount = rowPtr[x.RowFirst + x.Rows];
                if (x.RowFirst != rows) UnsafeUtility.MemMove(rowPtr + rows, rowPtr + x.RowFirst, (x.Rows + 1) * 4L);
                if (x.SpanFirst != spans) UnsafeUtility.MemMove(spanPtr + spans, spanPtr + x.SpanFirst, (long)spanCount * sizeof(CoverageSpan));
                x.RowFirst = rows;
                x.SpanFirst = spans;
                rows += x.Rows + 1;
                spans += spanCount;
                e[w++] = x;
            }
            _entries.ResizeUninitialized(w);
            _rows.ResizeUninitialized(rows);
            _spans.ResizeUninitialized(spans);
            Reindex();
        }

        void Reindex()
        {
            int n = _entries.Length;
            _index.Reset(n);
            var e = (Entry*)_entries.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < n; i++)
                if (e[i].Complete != 0) _index.Insert(e[i].Hash, i);
        }

        /// <summary>The spans stored for <paramref name="key"/>: row y is [offsets[y], offsets[y + 1]) of spans.</summary>
        public bool TryGet(in Key key, out CoverageSpan* spans, out int* offsets)
        {
            spans = null;
            offsets = null;
            Key k = key;
            int i = _index.Find(HashIndex.Hash(&k, sizeof(Key)));
            if (i < 0) return false;
            var e = (Entry*)_entries.GetUnsafePtr() + i;
            if (UnsafeUtility.MemCmp(&e->Key, &k, sizeof(Key)) != 0) return false;
            e->Frame = St.Frame;
            spans = (CoverageSpan*)_spans.GetUnsafeReadOnlyPtr() + e->SpanFirst;
            offsets = (int*)_rows.GetUnsafeReadOnlyPtr() + e->RowFirst;
            return true;
        }

        /// <summary>
        /// Starts recording the coverage of <paramref name="key"/>: <paramref name="rows"/> rows, given to
        /// <see cref="AddRow"/> in order. Returns the entry, or -1 when the cache is full.
        /// </summary>
        public int Begin(in Key key, int rows)
        {
            if (_spans.Length > MaxSpans) return -1;
            Key k = key;
            int index = _entries.Length;
            _entries.Add(new Entry
            {
                Key = k, Hash = HashIndex.Hash(&k, sizeof(Key)), RowFirst = _rows.Length, SpanFirst = _spans.Length,
                Rows = rows, Frame = St.Frame,
            });
            _rows.Add(0);
            return index;
        }

        /// <summary>Adds the next row of the entry being recorded; after the last row the entry can be found.</summary>
        public void AddRow(int entry, CoverageSpan* spans, int count)
        {
            var e = (Entry*)_entries.GetUnsafePtr() + entry;
            if (count > 0) _spans.AddRange(spans, count);
            _rows.Add(_spans.Length - e->SpanFirst);
            if (_rows.Length - e->RowFirst != e->Rows + 1) return;
            e->Complete = 1;
            if (_index.Full(_entries.Length)) Reindex();
            else _index.Insert(e->Hash, entry);
        }

        /// <summary>Drops every entry and frees the memory.</summary>
        public void Release()
        {
            if (_entries.Capacity == 0 && _spans.Capacity == 0) return;
            _entries.Clear(); _entries.TrimExcess();
            _rows.Clear(); _rows.TrimExcess();
            _spans.Clear(); _spans.TrimExcess();
            _index.Release();
        }

        public void Dispose()
        {
            if (!_state.IsCreated) return;
            _state.Dispose();
            _entries.Dispose();
            _rows.Dispose();
            _spans.Dispose();
            _index.Dispose();
        }
    }
}
