// Mynell Lottie Player — Serializable asset wrapper around a baked Lottie blob.
// Raw byte[] arrays serialize the SoA pools; hydrated lazily into NativeArrays.

using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Mynell.LottiePlayer
{
    /// <summary>
    /// Serialized Lottie animation. Created by <see cref="Editor.LottieImportMenu"/>
    /// or at runtime via <see cref="LottieParser.Parse"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "Mynell/Lottie Asset", fileName = "NewLottieAsset")]
    public sealed class LottieAsset : ScriptableObject
    {
        // v3 → v4: LottieLayer gained LayerInd + ParentInd (explicit hierarchy indices).
        private const int CurrentBlobVersion = 4;

        [Header("Animation Info (read-only)")]
        [SerializeField] private int _blobVersion;
        [SerializeField] private float _frameRate;
        [SerializeField] private float _inFrame;
        [SerializeField] private float _outFrame;
        [SerializeField] private float _width;
        [SerializeField] private float _height;
        [SerializeField] private int _layerCount;
        [SerializeField] private int _layerHeaderOffset;

        [SerializeField] private LottieValidationReport _validationReport;

        [SerializeField, HideInInspector] private byte[] _layersBlob;
        [SerializeField, HideInInspector] private byte[] _precompsBlob;
        [SerializeField, HideInInspector] private byte[] _shapesBlob;
        [SerializeField, HideInInspector] private byte[] _propsBlob;
        [SerializeField, HideInInspector] private byte[] _keyframesBlob;
        [SerializeField, HideInInspector] private byte[] _pathPropsBlob;
        [SerializeField, HideInInspector] private byte[] _pathSnapshotsBlob;
        [SerializeField, HideInInspector] private byte[] _bezierVertsBlob;
        [SerializeField, HideInInspector] private byte[] _scalarBlob;
        [SerializeField, HideInInspector] private byte[] _vec2Blob;
        [SerializeField, HideInInspector] private byte[] _vec4Blob;

        public int BlobVersion { get => _blobVersion; internal set => _blobVersion = value; }
        public float FrameRate { get => _frameRate; internal set => _frameRate = value; }
        public float InFrame { get => _inFrame; internal set => _inFrame = value; }
        public float OutFrame { get => _outFrame; internal set => _outFrame = value; }
        public float Width { get => _width; internal set => _width = value; }
        public float Height { get => _height; internal set => _height = value; }
        public int LayerCount { get => _layerCount; internal set => _layerCount = value; }
        public int LayerHeaderOffset { get => _layerHeaderOffset; internal set => _layerHeaderOffset = value; }
        public LottieValidationReport ValidationReport { get => _validationReport; internal set => _validationReport = value; }

        public float DurationSeconds => _frameRate > 0 ? (_outFrame - _inFrame) / _frameRate : 0f;
        public bool IsBaked => _blobVersion > 0;

        /// <summary>
        /// Allocate a runtime blob from the serialized bytes. Caller owns and must Dispose.
        /// </summary>
        public LottieAnimationBlob CreateRuntimeBlob(Allocator allocator = Allocator.Persistent)
        {
            ValidateBlobLayout();

            var blob = new LottieAnimationBlob
            {
                Header = new LottieHeader
                {
                    FrameRate = _frameRate,
                    InFrame = _inFrame,
                    OutFrame = _outFrame,
                    Width = _width,
                    Height = _height,
                    LayerCount = _layerCount,
                    LayerOffset = _layerHeaderOffset,
                },
                Layers = BytesToNative<LottieLayer>(_layersBlob, allocator),
                Precomps = BytesToNative<LottiePrecomp>(_precompsBlob, allocator),
                Shapes = BytesToNative<LottieShape>(_shapesBlob, allocator),
                Properties = BytesToNative<LottieProperty>(_propsBlob, allocator),
                Keyframes = BytesToNative<LottieKeyframe>(_keyframesBlob, allocator),
                PathProperties = BytesToNative<LottiePathProperty>(_pathPropsBlob, allocator),
                PathSnapshots = BytesToNative<LottiePathSnapshot>(_pathSnapshotsBlob, allocator),
                BezierVertices = BytesToNative<LottieBezierVertex>(_bezierVertsBlob, allocator),
                ScalarPool = BytesToNative<float>(_scalarBlob, allocator),
                Vec2Pool = BytesToNative<float2>(_vec2Blob, allocator),
                Vec4Pool = BytesToNative<float4>(_vec4Blob, allocator),
            };
            return blob;
        }

        /// <summary>Bake an in-memory blob into this asset, attaching an empty validation report.</summary>
        public void StoreBlob(in LottieAnimationBlob blob) => StoreBlob(blob, default);

        /// <summary>
        /// Bake an in-memory blob and its parse-time validation report.
        /// Both arguments come straight from <see cref="LottieParser.Parse(string, out LottieValidationReport)"/>.
        /// </summary>
        public void StoreBlob(in LottieAnimationBlob blob, in LottieValidationReport report)
        {
            _blobVersion = CurrentBlobVersion;
            _validationReport = report;
            _frameRate = blob.Header.FrameRate;
            _inFrame = blob.Header.InFrame;
            _outFrame = blob.Header.OutFrame;
            _width = blob.Header.Width;
            _height = blob.Header.Height;
            _layerCount = blob.Header.LayerCount;
            _layerHeaderOffset = blob.Header.LayerOffset;

            _layersBlob = NativeToBytes(blob.Layers);
            _precompsBlob = NativeToBytes(blob.Precomps);
            _shapesBlob = NativeToBytes(blob.Shapes);
            _propsBlob = NativeToBytes(blob.Properties);
            _keyframesBlob = NativeToBytes(blob.Keyframes);
            _pathPropsBlob = NativeToBytes(blob.PathProperties);
            _pathSnapshotsBlob = NativeToBytes(blob.PathSnapshots);
            _bezierVertsBlob = NativeToBytes(blob.BezierVertices);
            _scalarBlob = NativeToBytes(blob.ScalarPool);
            _vec2Blob = NativeToBytes(blob.Vec2Pool);
            _vec4Blob = NativeToBytes(blob.Vec4Pool);
        }

        private void ValidateBlobLayout()
        {
            if (_blobVersion != CurrentBlobVersion)
            {
                throw new InvalidOperationException(
                    $"LottieAsset '{name}' was baked with an old blob layout (version {_blobVersion}). " +
                    "Re-bake the source JSON: Assets/Mynell/Bake Selected Lottie JSON -> LottieAsset.");
            }

            ValidateBlob<LottieLayer>(_layersBlob, nameof(_layersBlob));
            ValidateBlob<LottiePrecomp>(_precompsBlob, nameof(_precompsBlob));
            ValidateBlob<LottieShape>(_shapesBlob, nameof(_shapesBlob));
            ValidateBlob<LottieProperty>(_propsBlob, nameof(_propsBlob));
            ValidateBlob<LottieKeyframe>(_keyframesBlob, nameof(_keyframesBlob));
            ValidateBlob<LottiePathProperty>(_pathPropsBlob, nameof(_pathPropsBlob));
            ValidateBlob<LottiePathSnapshot>(_pathSnapshotsBlob, nameof(_pathSnapshotsBlob));
            ValidateBlob<LottieBezierVertex>(_bezierVertsBlob, nameof(_bezierVertsBlob));
            ValidateBlob<float>(_scalarBlob, nameof(_scalarBlob));
            ValidateBlob<float2>(_vec2Blob, nameof(_vec2Blob));
            ValidateBlob<float4>(_vec4Blob, nameof(_vec4Blob));
        }

        private static void ValidateBlob<T>(byte[] bytes, string fieldName) where T : unmanaged
        {
            int byteLen = bytes?.Length ?? 0;
            int elem = UnsafeUtility.SizeOf<T>();
            if (byteLen % elem != 0)
            {
                throw new InvalidOperationException(
                    $"Corrupt LottieAsset blob '{fieldName}': {byteLen} bytes is not divisible by {elem}.");
            }
        }

        private static unsafe byte[] NativeToBytes<T>(NativeArray<T> array) where T : unmanaged
        {
            if (!array.IsCreated || array.Length == 0) return Array.Empty<byte>();
            int byteLen = array.Length * UnsafeUtility.SizeOf<T>();
            var bytes = new byte[byteLen];
            fixed (byte* dst = bytes)
            {
                UnsafeUtility.MemCpy(dst, array.GetUnsafeReadOnlyPtr(), byteLen);
            }
            return bytes;
        }

        private static unsafe NativeArray<T> BytesToNative<T>(byte[] bytes, Allocator allocator) where T : unmanaged
        {
            int byteLen = bytes?.Length ?? 0;
            int elem = UnsafeUtility.SizeOf<T>();
            int len = byteLen / elem;
            int allocLen = len > 0 ? len : 1;
            var arr = new NativeArray<T>(allocLen, allocator, NativeArrayOptions.ClearMemory);
            if (len > 0)
            {
                fixed (byte* src = bytes)
                {
                    UnsafeUtility.MemCpy(arr.GetUnsafePtr(), src, byteLen);
                }
            }
            return arr;
        }
    }
}
