using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie
{
    /// <summary>
    /// Per-player property overrides ("dynamic properties"): values that replace a property's animated value.
    /// Read by render jobs through raw pointers, so it is only modified while no job of the owning player runs.
    /// </summary>
    sealed class LottieOverrides : IDisposable
    {
        NativeArray<int> _slots;       // per property: index in _values, -1 = not overridden
        NativeList<float4> _values;
        int _count;

        public bool IsEmpty => _count == 0;

        public void Set(int propCount, int prop, float4 value)
        {
            if (!_slots.IsCreated || _slots.Length != propCount)
            {
                Dispose();
                _slots = new NativeArray<int>(math.max(1, propCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < _slots.Length; i++) _slots[i] = -1;
                _values = new NativeList<float4>(8, Allocator.Persistent);
                _count = 0;
            }
            if (prop < 0 || prop >= _slots.Length) return;
            int slot = _slots[prop];
            if (slot < 0)
            {
                _slots[prop] = _values.Length;
                _values.Add(value);
                _count++;
            }
            else _values[slot] = value;
        }

        public bool Remove(int prop)
        {
            if (!_slots.IsCreated || prop < 0 || prop >= _slots.Length || _slots[prop] < 0) return false;
            _slots[prop] = -1;
            _count--;
            if (_count == 0) _values.Clear();
            return true;
        }

        public void Clear()
        {
            if (!_slots.IsCreated) return;
            for (int i = 0; i < _slots.Length; i++) _slots[i] = -1;
            _values.Clear();
            _count = 0;
        }

        public unsafe int* Slots => _count > 0 ? (int*)_slots.GetUnsafeReadOnlyPtr() : null;
        public unsafe float4* Values => _count > 0 ? (float4*)_values.GetUnsafeReadOnlyPtr() : null;

        public void Dispose()
        {
            if (_slots.IsCreated) _slots.Dispose();
            if (_values.IsCreated) _values.Dispose();
            _count = 0;
        }
    }
}
