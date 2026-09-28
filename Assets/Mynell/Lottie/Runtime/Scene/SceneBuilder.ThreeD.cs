using Mynell.Lottie.Eval;
using Mynell.Lottie.Model;
using Unity.Mathematics;

namespace Mynell.Lottie.Scene
{
    // 3D layers seen through a camera (the composition's camera layer, or After Effects' default camera in 3D
    // compositions), following Skottie's matrices. A layer whose plane is seen in perspective is rendered in a flat
    // raster space and warped onto the target (like browsers render CSS 3D layers).
    public unsafe partial struct SceneBuilder
    {
        /// <summary>Size of the composition being rendered (the camera viewport).</summary>
        internal float2 _compSize;

        const float DefaultCameraZoom = 879.13f;
        const float MaxRasterSize = 4096f;

        static float4x4 Translate(float x, float y, float z) => float4x4.Translate(new float3(x, y, z));

        /// <summary>Skottie TransformAdapter3D: T(p) · Rx · Ry · Rz · S · T(-a) (degrees, orientation + axis rotations).</summary>
        float4x4 Local3D(int li, float t)
        {
            LayerData L = Layers[li];
            if ((L.Flags & LayerFlags.HasTransform) == 0) return float4x4.identity;
            TransformDesc tr = L.Transform;
            float3 a = P.Vec3(tr.Anchor, t, float3.zero);
            float3 p = tr.Split != 0
                ? new float3(P.Scalar(tr.PosX, t, 0), P.Scalar(tr.PosY, t, 0), P.Scalar(tr.PosZ, t, 0))
                : P.Vec3(tr.Position, t, float3.zero);
            float3 s = P.Vec3(tr.Scale, t, new float3(100, 100, 100)) * 0.01f;
            float3 r;
            if (tr.Has3D != 0)
                r = P.Vec3(tr.Orientation, t, float3.zero) +
                    new float3(P.Scalar(tr.RotX, t, 0), P.Scalar(tr.RotY, t, 0), P.Scalar(tr.RotZ, t, 0));
            else r = new float3(0, 0, P.Scalar(tr.Rotation, t, 0));
            r = math.radians(r);
            return math.mul(Translate(p.x, p.y, p.z),
                   math.mul(float4x4.RotateX(r.x),
                   math.mul(float4x4.RotateY(r.y),
                   math.mul(float4x4.RotateZ(r.z),
                   math.mul(float4x4.Scale(s.x, s.y, s.z), Translate(-a.x, -a.y, -a.z))))));
        }

        /// <summary>The layer's 3D transform chain (parents first), in composition space.</summary>
        float4x4 Chain3D(int li, float t)
        {
            float4x4 m = Local3D(li, t);
            int p = Layers[li].Parent, guard = 0;
            while (p >= 0 && guard++ < 64)
            {
                m = math.mul(Local3D(p, t), m);
                p = Layers[p].Parent;
            }
            return m;
        }

        /// <summary>
        /// Composition -> viewport matrix of the composition's camera (Skottie ComputeCameraMatrix); false when the
        /// composition has no camera and is not 3D.
        /// </summary>
        bool CameraMatrix(int comp, float t, float2 viewport, out float4x4 camera)
        {
            camera = float4x4.identity;
            CompData c = Comps[comp];
            float3 pos, poi, rot;
            float zoom;
            float4x4 parentInverse = float4x4.identity;
            if (c.Camera >= 0)
            {
                LayerData L = Layers[c.Camera];
                TransformDesc tr = L.Transform;
                pos = tr.Split != 0
                    ? new float3(P.Scalar(tr.PosX, t, 0), P.Scalar(tr.PosY, t, 0), P.Scalar(tr.PosZ, t, 0))
                    : P.Vec3(tr.Position, t, float3.zero);
                rot = tr.Has3D != 0
                    ? P.Vec3(tr.Orientation, t, float3.zero) + new float3(P.Scalar(tr.RotX, t, 0), P.Scalar(tr.RotY, t, 0), P.Scalar(tr.RotZ, t, 0))
                    : new float3(0, 0, P.Scalar(tr.Rotation, t, 0));
                if (L.CameraTwoNode != 0)
                {
                    float3 ap = P.Vec3(tr.Anchor, t, float3.zero);
                    poi = new float3(ap.x, ap.y, -ap.z);
                }
                else poi = new float3(pos.x, pos.y, -pos.z - 1);
                zoom = L.CameraZoom >= 0 ? P.Scalar(L.CameraZoom, t, DefaultCameraZoom) : DefaultCameraZoom;
                // A camera's parents move the camera itself: they pre-compose inverted.
                if (L.Parent >= 0) parentInverse = math.inverse(Chain3D(L.Parent, t));
            }
            else if (c.Ddd != 0)
            {
                pos = new float3(viewport * 0.5f, -DefaultCameraZoom);
                poi = new float3(pos.x, pos.y, -pos.z - 1);
                rot = float3.zero;
                zoom = DefaultCameraZoom;
            }
            else return false;
            if (!(viewport.x > 0) || !(viewport.y > 0) || !(zoom > 0)) return false;

            rot = math.radians(rot);
            float4x4 camT = math.mul(float4x4.RotateZ(-rot.z),
                            math.mul(float4x4.RotateY(rot.y),
                            math.mul(float4x4.RotateX(rot.x),
                            math.mul(LookAt(new float3(pos.x, pos.y, -pos.z), poi, new float3(0, 1, 0)), float4x4.Scale(1, 1, -1)))));
            float viewSize = math.max(viewport.x, viewport.y);
            float viewAngle = math.atan(viewSize * 0.5f / zoom);
            float4x4 persp = math.mul(float4x4.Scale(viewSize * 0.5f, viewSize * 0.5f, 1), Perspective(0, zoom, 2 * viewAngle));
            camera = math.mul(Translate(viewport.x * 0.5f, viewport.y * 0.5f, 0), math.mul(persp, math.mul(camT, parentInverse)));
            return true;
        }

        /// <summary>SkM44::LookAt.</summary>
        static float4x4 LookAt(float3 eye, float3 center, float3 up)
        {
            float3 f = math.normalizesafe(center - eye), u = math.normalizesafe(up);
            float3 s = math.normalizesafe(math.cross(f, u));
            var m = new float4x4(new float4(s, 0), new float4(math.cross(s, f), 0), new float4(-f, 0), new float4(eye, 1));
            float det = math.determinant(m);
            return math.abs(det) > 1e-12f ? math.inverse(m) : float4x4.identity;
        }

        /// <summary>
        /// Pinhole projection (SkM44::Perspective, except w = distance: Skia keeps w = 1 + distance, which shrinks the
        /// focal plane by zoom / (zoom + 1); After Effects shows a layer at the focal plane unscaled).
        /// </summary>
        static float4x4 Perspective(float near, float far, float angle)
        {
            float denomInv = 1f / (far - near);
            float cot = 1f / math.tan(angle * 0.5f);
            var m = float4x4.identity;
            m.c0.x = cot;
            m.c1.y = cot;
            m.c2.z = (far + near) * denomInv;
            m.c3.z = 2 * far * near * denomInv;
            m.c2.w = -1;
            m.c3.w = 0;
            return m;
        }

        static float4x4 ToMatrix4(in Affine2 m) =>
            new float4x4(m.a, m.c, 0, m.tx,
                         m.b, m.d, 0, m.ty,
                         0, 0, 1, 0,
                         0, 0, 0, 1);

        /// <summary>
        /// The layer-plane (z = 0) -> device homography of a 3D layer seen through its composition's camera, false for
        /// layers rendered without one (lottie-web's flattened 3D).
        /// </summary>
        internal bool CameraHomography(int li, float t, in Affine2 compToDevice, in Affine2 effectMatrix, bool hasTransformEffect, out float3x3 h)
        {
            h = float3x3.identity;
            LayerData L = Layers[li];
            if ((L.Flags & LayerFlags.Is3D) == 0) return false;
            if (!CameraMatrix(L.Comp, t, _compSize, out float4x4 camera)) return false;
            float4x4 m = math.mul(ToMatrix4(compToDevice), math.mul(camera, Chain3D(li, t)));
            if (hasTransformEffect) m = math.mul(m, ToMatrix4(effectMatrix));
            // Rows / columns x, y, w of the 4x4 (the layer content lies in its z = 0 plane).
            h = new float3x3(m.c0.x, m.c1.x, m.c3.x,
                             m.c0.y, m.c1.y, m.c3.y,
                             m.c0.w, m.c1.w, m.c3.w);
            return true;
        }

        /// <summary>
        /// True when the homography is affine over the layer box (no visible perspective): <paramref name="affine"/>
        /// then maps layer space to device pixels.
        /// </summary>
        static bool IsAffine(in float3x3 h, float2 box, out Affine2 affine)
        {
            affine = Affine2.Identity;
            float w = h.c2.z;
            float extent = math.max(math.max(box.x, box.y), 1f);
            if (!(math.abs(w) > 1e-12f) || (math.abs(h.c0.z) + math.abs(h.c1.z)) * extent > 1e-4f * math.abs(w)) return false;
            affine = new Affine2(h.c0.x / w, h.c0.y / w, h.c1.x / w, h.c1.y / w, h.c2.x / w, h.c2.y / w);
            return true;
        }

        /// <summary>
        /// Raster space of a layer seen in perspective: layer space scaled by the largest on-screen scale of the plane
        /// (bounded), with a margin for content outside the layer box. Returns false when nothing is in front of the camera.
        /// </summary>
        static bool RasterSpace(in float3x3 h, float4 layerBox, out Affine2 layerToRaster, out float4 rasterRect)
        {
            layerToRaster = Affine2.Identity;
            rasterRect = EmptyRect;
            float scale = 0;
            for (int i = 0; i < 9; i++)
            {
                float2 q = math.lerp(layerBox.xy, layerBox.zw, new float2(i % 3, i / 3) * 0.5f);
                float3 p = math.mul(h, new float3(q, 1));
                if (!(p.z > 1e-6f)) continue;
                float2 d = p.xy / p.z;
                // Jacobian of the projection at q.
                float2 jx = (new float2(h.c0.x, h.c0.y) - d * h.c0.z) / p.z;
                float2 jy = (new float2(h.c1.x, h.c1.y) - d * h.c1.z) / p.z;
                scale = math.max(scale, math.max(math.length(jx), math.length(jy)));
            }
            if (!(scale > 0)) return false;
            scale = math.clamp(scale, 1f / 64f, 16f);
            float2 size = (layerBox.zw - layerBox.xy) * scale;
            float big = math.cmax(size);
            if (big > MaxRasterSize) scale *= MaxRasterSize / big;
            layerToRaster = Affine2.Translate(-layerBox.x, -layerBox.y).Then(Affine2.Scale(scale, scale));
            rasterRect = new float4(0, 0, math.ceil((layerBox.zw - layerBox.xy) * scale));
            return true;
        }

        /// <summary>Layer box used as the raster region of a perspective layer.</summary>
        float4 PerspectiveBox(in LayerData L)
        {
            float2 box = LayerBoxSize(L);
            if (L.Kind == LayerKind.Shape || L.Kind == LayerKind.Text)
            {
                // Shape / text content may extend beyond the (composition sized) layer box.
                float2 margin = box * 0.25f;
                return new float4(-margin, box + margin);
            }
            return new float4(0, 0, box);
        }

        static bool Same(in Affine2 x, in Affine2 y)
        {
            const float Eps = 1e-3f;
            return math.abs(x.a - y.a) < Eps && math.abs(x.b - y.b) < Eps && math.abs(x.c - y.c) < Eps && math.abs(x.d - y.d) < Eps &&
                   math.abs(x.tx - y.tx) < 0.01f && math.abs(x.ty - y.ty) < 0.01f;
        }

        static float3x3 ToMatrix3(in Affine2 m) => new float3x3(m.a, m.c, m.tx, m.b, m.d, m.ty, 0, 0, 1);
    }
}
