using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mynell.LottiePlayer
{
    /// <summary>
    /// Interleaved UGUI vertex: Position (float3) + Color (UNorm8x4) + UV0 (float2).
    /// Matches the Default UI shader input layout (POSITION / COLOR / TEXCOORD0).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
    public struct VertexLayout
    {
        public float3 Position;
        public Color32 Color;
        public float2 UV;

        public static readonly VertexAttributeDescriptor[] Descriptors = new[]
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        };

        public static readonly Color32 OpaqueWhite = new Color32(255, 255, 255, 255);
    }
}
