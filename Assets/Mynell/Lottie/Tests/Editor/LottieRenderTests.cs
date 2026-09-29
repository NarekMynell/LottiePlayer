using System;
using Mynell.Lottie.Eval;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Mynell.Lottie.Tests
{
    /// <summary>Rendering and playback tests on small synthetic animations.</summary>
    public class LottieRenderTests
    {
        // ------------------------------------------------------------------ helpers

        const string Transform = "\"ks\":{\"p\":{\"a\":0,\"k\":[50,50]},\"a\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}";

        static string Comp(string layers, int op = 60, string extra = "") =>
            "{\"v\":\"5.7.0\",\"fr\":30,\"ip\":0,\"op\":" + op + ",\"w\":100,\"h\":100" + extra + ",\"layers\":[" + layers + "]}";

        static string ShapeLayer(string shapes, string transform = Transform, int ind = 1, string extra = "") =>
            "{\"ty\":4,\"ind\":" + ind + ",\"ip\":0,\"op\":60,\"st\":0," + transform + extra + ",\"shapes\":[" + shapes + "]}";

        static string Rect(float x, float y, float w, float h) =>
            $"{{\"ty\":\"rc\",\"p\":{{\"a\":0,\"k\":[{x},{y}]}},\"s\":{{\"a\":0,\"k\":[{w},{h}]}},\"r\":{{\"a\":0,\"k\":0}}}}";

        static string Fill(float r, float g, float b, float opacity = 100) =>
            $"{{\"ty\":\"fl\",\"c\":{{\"a\":0,\"k\":[{r},{g},{b},1]}},\"o\":{{\"a\":0,\"k\":{opacity}}}}}";

        /// <summary>Renders one frame (row 0 = top) and returns premultiplied RGBA32 pixels.</summary>
        static uint[] Render(string json, float frame, int size = 100, int maxBands = 0)
        {
            using var anim = LottieAnimation.FromJson(json);
            using var state = new LottieRenderState();
            var px = new NativeArray<uint>(size * size, Allocator.Persistent);
            int saved = LottieRenderState.MaxBands;
            try
            {
                LottieRenderState.MaxBands = maxBands;
                state.RenderImmediate(anim, frame, LottieRenderState.FitTransform(anim.Width, anim.Height, size, size, false), px, size, size);
                return px.ToArray();
            }
            finally
            {
                LottieRenderState.MaxBands = saved;
                px.Dispose();
            }
        }

        static Color32 At(uint[] px, int x, int y, int size = 100)
        {
            uint p = px[y * size + x];
            return new Color32((byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24));
        }

        static void AssertColor(Color32 expected, Color32 actual, int tolerance = 1, string what = "")
        {
            bool ok = Math.Abs(expected.r - actual.r) <= tolerance && Math.Abs(expected.g - actual.g) <= tolerance &&
                      Math.Abs(expected.b - actual.b) <= tolerance && Math.Abs(expected.a - actual.a) <= tolerance;
            Assert.IsTrue(ok, $"{what} expected {expected} but was {actual}");
        }

        // ------------------------------------------------------------------ rendering

        [Test]
        public void SolidRectangle_CoversExactPixels()
        {
            // 40 × 40 square centered at (50, 50): pixels 30..69 are covered.
            var px = Render(Comp(ShapeLayer(Rect(0, 0, 40, 40) + "," + Fill(1, 0, 0))), 0);
            AssertColor(new Color32(255, 0, 0, 255), At(px, 50, 50), 0, "center");
            AssertColor(new Color32(255, 0, 0, 255), At(px, 30, 30), 0, "first covered pixel");
            AssertColor(new Color32(255, 0, 0, 255), At(px, 69, 69), 0, "last covered pixel");
            AssertColor(new Color32(0, 0, 0, 0), At(px, 29, 50), 0, "left of the square");
            AssertColor(new Color32(0, 0, 0, 0), At(px, 70, 50), 0, "right of the square");
        }

        [Test]
        public void HalfPixelEdge_HasHalfCoverage()
        {
            // Left edge at x = 30.5: pixel 30 is half covered.
            var px = Render(Comp(ShapeLayer(Rect(0.5f, 0, 40, 40) + "," + Fill(1, 1, 1))), 0);
            AssertColor(new Color32(128, 128, 128, 128), At(px, 30, 50), 1, "half-covered pixel");
        }

        [Test]
        public void FillOpacity_IsPremultiplied()
        {
            var px = Render(Comp(ShapeLayer(Rect(0, 0, 40, 40) + "," + Fill(0, 0, 1, 50))), 0);
            AssertColor(new Color32(0, 0, 128, 128), At(px, 50, 50), 1, "50% blue");
        }

        [Test]
        public void LayerOrder_FirstLayerIsOnTop()
        {
            string top = ShapeLayer(Rect(0, 0, 20, 20) + "," + Fill(0, 1, 0), Transform, 1);
            string bottom = ShapeLayer(Rect(0, 0, 60, 60) + "," + Fill(1, 0, 0), Transform, 2);
            var px = Render(Comp(top + "," + bottom), 0);
            AssertColor(new Color32(0, 255, 0, 255), At(px, 50, 50), 0, "top layer");
            AssertColor(new Color32(255, 0, 0, 255), At(px, 25, 25), 0, "bottom layer");
        }

        [Test]
        public void Expression_IsEvaluated()
        {
            // Position expression moves the layer 20 px to the right.
            string transform = "\"ks\":{\"p\":{\"a\":0,\"k\":[50,50],\"x\":\"[value[0] + 20, value[1]]\"},\"a\":{\"a\":0,\"k\":[0,0]}," +
                               "\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}";
            var px = Render(Comp(ShapeLayer(Rect(0, 0, 20, 20) + "," + Fill(1, 1, 1), transform)), 0);
            AssertColor(new Color32(0, 0, 0, 0), At(px, 50, 50), 0, "original position");
            AssertColor(new Color32(255, 255, 255, 255), At(px, 70, 50), 0, "moved position");
        }

        [Test]
        public void MergePathsSubtract_CutsHole()
        {
            // Subtract removes the lower paths from the uppermost one.
            string group = "{\"ty\":\"gr\",\"it\":[" + Rect(0, 0, 60, 60) + "," + Rect(0, 0, 20, 20) +
                           ",{\"ty\":\"mm\",\"mm\":3}," + Fill(1, 1, 1) +
                           ",{\"ty\":\"tr\",\"p\":{\"a\":0,\"k\":[0,0]},\"a\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}]}";
            var px = Render(Comp(ShapeLayer(group)), 0);
            AssertColor(new Color32(0, 0, 0, 0), At(px, 50, 50), 0, "hole");
            AssertColor(new Color32(255, 255, 255, 255), At(px, 25, 50), 0, "ring");
        }

        [Test]
        public void BandedRendering_MatchesSingleThreaded()
        {
            string blur = ",\"ef\":[{\"ty\":29,\"nm\":\"Gaussian Blur\",\"ef\":[{\"ty\":0,\"nm\":\"Blurriness\",\"v\":{\"a\":0,\"k\":12}}," +
                          "{\"ty\":7,\"nm\":\"Blur Dimensions\",\"v\":{\"a\":0,\"k\":1}},{\"ty\":7,\"nm\":\"Repeat Edge Pixels\",\"v\":{\"a\":0,\"k\":0}}]}]";
            string layers = ShapeLayer(Rect(0, 0, 30, 70) + "," + Fill(1, 0.5f, 0), Transform, 1, blur) + "," +
                            ShapeLayer("{\"ty\":\"el\",\"p\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[80,50]}}," + Fill(0, 0.4f, 1, 70), Transform, 2);
            string json = Comp(layers);
            var single = Render(json, 0, 640, 1);
            var banded = Render(json, 0, 640, 16);
            int worst = 0;
            for (int i = 0; i < single.Length; i++)
                for (int c = 0; c < 32; c += 8)
                    worst = Math.Max(worst, Math.Abs((int)((single[i] >> c) & 255) - (int)((banded[i] >> c) & 255)));
            Assert.LessOrEqual(worst, 1, "banded output differs from single-threaded output");
        }

        [Test]
        public void UnchangedFrame_IsNotRasterizedAgain()
        {
            // Hold keyframe: frames 0..19 are identical.
            string transform = "\"ks\":{\"p\":{\"a\":1,\"k\":[{\"t\":0,\"s\":[30,50],\"h\":1},{\"t\":20,\"s\":[70,50]}]},\"a\":{\"a\":0,\"k\":[0,0]}," +
                               "\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}";
            using var anim = LottieAnimation.FromJson(Comp(ShapeLayer(Rect(0, 0, 20, 20) + "," + Fill(1, 1, 1), transform)));
            using var state = new LottieRenderState();
            var px = new NativeArray<uint>(100 * 100, Allocator.Persistent);
            try
            {
                Affine2 m = LottieRenderState.FitTransform(100, 100, 100, 100, false);
                state.RenderImmediate(anim, 2, m, px, 100, 100);
                Assert.IsTrue(state.LastFrameChanged);
                state.RenderImmediate(anim, 3, m, px, 100, 100);
                Assert.IsFalse(state.LastFrameChanged, "identical frame was rasterized again");
                state.RenderImmediate(anim, 20, m, px, 100, 100);
                Assert.IsTrue(state.LastFrameChanged);
                state.Invalidate();
                state.RenderImmediate(anim, 20, m, px, 100, 100);
                Assert.IsTrue(state.LastFrameChanged, "Invalidate must force a render");
            }
            finally
            {
                px.Dispose();
            }
        }

        // ------------------------------------------------------------------ data & playback

        [Test]
        public void Markers_AreImported()
        {
            string json = Comp(ShapeLayer(Rect(0, 0, 10, 10) + "," + Fill(1, 1, 1)), 60,
                ",\"markers\":[{\"tm\":10,\"cm\":\"intro\",\"dr\":20},{\"tm\":40,\"cm\":\"outro\",\"dr\":0}]");
            var asset = LottieAsset.CreateFromJson(json);
            try
            {
                Assert.AreEqual(2, asset.MarkerCount);
                int i = asset.FindMarker("intro");
                Assert.AreEqual(0, i);
                Assert.AreEqual(10f, asset.GetMarkerTime(i));
                Assert.AreEqual(20f, asset.GetMarkerDuration(i));
                Assert.AreEqual(-1, asset.FindMarker("missing"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Player_LoopsAndCompletes()
        {
            var asset = LottieAsset.CreateFromJson(Comp(ShapeLayer(Rect(0, 0, 10, 10) + "," + Fill(1, 1, 1))));
            var player = new LottiePlayer(asset) { AutoAdvance = false };
            try
            {
                int loops = 0, completed = 0;
                player.LoopCompleted += _ => loops++;
                player.Completed += _ => completed++;

                player.LoopMode = LottieLoopMode.Loop;
                player.Play();
                player.Advance(1f);                      // 30 frames
                Assert.AreEqual(30f, player.Frame, 1e-3f);
                player.Advance(1.5f);                    // 75 → wraps to 15
                Assert.AreEqual(15f, player.Frame, 1e-3f);
                Assert.AreEqual(1, loops);

                player.LoopMode = LottieLoopMode.Once;
                player.Advance(10f);
                Assert.IsFalse(player.IsPlaying);
                Assert.AreEqual(1, completed);
                Assert.AreEqual(59f, player.Frame, 1e-3f);

                player.LoopMode = LottieLoopMode.PingPong;
                player.GoToAndPlay(50);
                player.Advance(0.5f);                    // 50 + 15 → bounces at 59 → 53
                Assert.AreEqual(53f, player.Frame, 1e-3f);
            }
            finally
            {
                player.Dispose();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Player_PlaysMarkerSegment()
        {
            string json = Comp(ShapeLayer(Rect(0, 0, 10, 10) + "," + Fill(1, 1, 1)), 60, ",\"markers\":[{\"tm\":10,\"cm\":\"intro\",\"dr\":20}]");
            var asset = LottieAsset.CreateFromJson(json);
            var player = new LottiePlayer(asset) { AutoAdvance = false, LoopMode = LottieLoopMode.Once };
            try
            {
                Assert.IsTrue(player.PlayMarker("intro"));
                Assert.AreEqual(10f, player.StartFrame);
                Assert.AreEqual(30f, player.EndFrame);
                Assert.AreEqual(10f, player.Frame, 1e-3f);
                player.Advance(10f);
                Assert.AreEqual(29f, player.Frame, 1e-3f);
                Assert.IsFalse(player.PlayMarker("missing"));
            }
            finally
            {
                player.Dispose();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Player_RendersIntoTexture()
        {
            var asset = LottieAsset.CreateFromJson(Comp(ShapeLayer(Rect(0, 0, 40, 40) + "," + Fill(1, 0, 0))));
            var player = new LottiePlayer(asset);
            try
            {
                player.SetSize(64, 64);
                player.RenderNow();
                Assert.IsNotNull(player.Texture);
                Assert.AreEqual(64, player.Texture.width);
                var raw = player.Texture.GetRawTextureData<uint>();
                Assert.AreEqual(0xFF0000FFu, raw[32 * 64 + 32], "center pixel should be opaque red");
            }
            finally
            {
                player.Dispose();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DynamicProperties_OverrideColorAndOpacity()
        {
            string group = "{\"ty\":\"gr\",\"nm\":\"Box\",\"it\":[" + Rect(0, 0, 40, 40).Replace("{\"ty\":\"rc\"", "{\"ty\":\"rc\",\"nm\":\"Rect\"") +
                           "," + Fill(1, 0, 0).Replace("{\"ty\":\"fl\"", "{\"ty\":\"fl\",\"nm\":\"Fill 1\"") +
                           ",{\"ty\":\"tr\",\"nm\":\"Transform\",\"p\":{\"a\":0,\"k\":[0,0]},\"a\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}]}";
            string layer = ShapeLayer(group).Replace("{\"ty\":4,", "{\"ty\":4,\"nm\":\"Layer\",");
            var asset = LottieAsset.CreateFromJson(Comp(layer));
            var player = new LottiePlayer(asset);
            try
            {
                player.SetSize(100, 100);
                CollectionAssert.Contains(player.GetKeyPaths(), "Layer/Box/Fill 1");

                Assert.AreEqual(1, player.SetColor("Layer/Box/Fill 1", Color.blue));
                player.RenderNow();
                var raw = player.Texture.GetRawTextureData<uint>();
                Assert.AreEqual(0xFFFF0000u, raw[50 * 100 + 50], "overridden color (blue)");

                Assert.AreEqual(1, player.SetColor("**/Fill 1", Color.green));
                Assert.AreEqual(0, player.SetColor("Layer/Missing/Fill 1", Color.white));
                player.RenderNow();
                raw = player.Texture.GetRawTextureData<uint>();
                Assert.AreEqual(0xFF00FF00u, raw[50 * 100 + 50], "wildcard override (green)");

                Assert.AreEqual(1, player.SetOpacity("Layer", 0.5f));
                player.RenderNow();
                raw = player.Texture.GetRawTextureData<uint>();
                uint px = raw[50 * 100 + 50];
                Assert.AreEqual(128u, px >> 24, 1u, "layer opacity 50%");

                player.ClearOverrides();
                player.RenderNow();
                raw = player.Texture.GetRawTextureData<uint>();
                Assert.AreEqual(0xFF0000FFu, raw[50 * 100 + 50], "original color after clearing");
            }
            finally
            {
                player.Dispose();
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DotLottie_LoadsAnimationsAtRuntime()
        {
            byte[] archive;
            using (var ms = new System.IO.MemoryStream())
            {
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    void Add(string name, string text)
                    {
                        var e = zip.CreateEntry(name);
                        using var w = new System.IO.StreamWriter(e.Open());
                        w.Write(text);
                    }
                    Add("manifest.json", "{\"version\":\"1\",\"animations\":[{\"id\":\"first\"},{\"id\":\"second\"}]}");
                    Add("animations/first.json", Comp(ShapeLayer(Rect(0, 0, 40, 40) + "," + Fill(1, 0, 0))));
                    Add("animations/second.json", Comp(ShapeLayer(Rect(0, 0, 20, 20) + "," + Fill(0, 1, 0)), 30));
                }
                archive = ms.ToArray();
            }
            var first = LottieAsset.CreateFromDotLottie(archive);
            var second = LottieAsset.CreateFromDotLottie(archive, "second");
            try
            {
                Assert.IsNotNull(first);
                Assert.AreEqual("first", first.name);
                Assert.AreEqual(60f, first.OutPoint);
                Assert.IsNotNull(second);
                Assert.AreEqual(30f, second.OutPoint);
                Assert.IsNull(LottieAsset.CreateFromDotLottie(archive, "missing"));
            }
            finally
            {
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                if (second != null) UnityEngine.Object.DestroyImmediate(second);
            }
        }

        // ------------------------------------------------------------------ After Effects features beyond lottie-web

        [Test]
        public void LayerStyle_DropShadowIsDrawnBelowTheLayer()
        {
            // Angle 135 at distance 10: the shadow is offset by (7.07, 7.07).
            string style = ",\"sy\":[{\"ty\":1,\"nm\":\"Drop Shadow\",\"c\":{\"a\":0,\"k\":[0,0,1,1]},\"o\":{\"a\":0,\"k\":100}," +
                           "\"a\":{\"a\":0,\"k\":135},\"s\":{\"a\":0,\"k\":0},\"d\":{\"a\":0,\"k\":10}}]";
            var px = Render(Comp(ShapeLayer(Rect(0, 0, 40, 40) + "," + Fill(1, 1, 1), Transform, 1, style)), 0);
            AssertColor(new Color32(255, 255, 255, 255), At(px, 50, 50), 0, "layer");
            AssertColor(new Color32(0, 0, 255, 255), At(px, 73, 73), 1, "shadow");
            AssertColor(new Color32(0, 0, 0, 0), At(px, 25, 25), 0, "opposite side");
        }

        [Test]
        public void Effect_InvertTurnsRedIntoCyan()
        {
            string invert = ",\"ef\":[{\"ty\":5,\"nm\":\"Invert\",\"mn\":\"ADBE Invert\",\"ef\":[{\"ty\":7,\"nm\":\"Channel\",\"v\":{\"a\":0,\"k\":1}}," +
                            "{\"ty\":0,\"nm\":\"Blend With Original\",\"v\":{\"a\":0,\"k\":0}}]}]";
            var px = Render(Comp(ShapeLayer(Rect(0, 0, 40, 40) + "," + Fill(1, 0, 0), Transform, 1, invert)), 0);
            AssertColor(new Color32(0, 255, 255, 255), At(px, 50, 50), 1, "inverted");
            AssertColor(new Color32(0, 0, 0, 0), At(px, 10, 10), 0, "outside");
        }

        [Test]
        public void SetMatte_UsesAnotherLayerAsAlphaMatte()
        {
            // A hidden ellipse (layer 2) mattes the square: its corners disappear.
            string matte = ",\"ef\":[{\"ty\":28,\"nm\":\"Set Matte\",\"mn\":\"ADBE Set Matte3\",\"ef\":[" +
                           "{\"ty\":10,\"nm\":\"Take Matte From Layer\",\"v\":{\"a\":0,\"k\":2}},{\"ty\":7,\"nm\":\"Use For Matte\",\"v\":{\"a\":0,\"k\":4}}," +
                           "{\"ty\":7,\"nm\":\"Invert Matte\",\"v\":{\"a\":0,\"k\":0}}]}]";
            string ellipse = "{\"ty\":\"el\",\"p\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[40,40]}}," + Fill(0, 1, 0);
            string layers = ShapeLayer(Rect(0, 0, 40, 40) + "," + Fill(1, 0, 0), Transform, 1, matte) + "," +
                            ShapeLayer(ellipse, Transform, 2, ",\"hd\":true");
            var px = Render(Comp(layers), 0);
            AssertColor(new Color32(255, 0, 0, 255), At(px, 50, 50), 0, "inside the matte");
            AssertColor(new Color32(0, 0, 0, 0), At(px, 31, 31), 0, "square corner outside the ellipse");
        }

        [Test]
        public void Camera_ShowsA3DLayerInPerspective()
        {
            // A square tilted around x under a camera: its near and far edges get different widths.
            string camera = "{\"ty\":13,\"ind\":1,\"ip\":0,\"op\":60,\"st\":0,\"pe\":{\"a\":0,\"k\":80},\"ks\":{" +
                            "\"p\":{\"a\":0,\"k\":[50,50,-80]},\"a\":{\"a\":0,\"k\":[50,50,0]},\"rx\":{\"a\":0,\"k\":0}," +
                            "\"ry\":{\"a\":0,\"k\":0},\"rz\":{\"a\":0,\"k\":0},\"or\":{\"a\":0,\"k\":[0,0,0]}}}";
            string solid = "{\"ty\":1,\"ind\":2,\"ddd\":1,\"sw\":50,\"sh\":50,\"sc\":\"#ffffff\",\"ip\":0,\"op\":60,\"st\":0,\"ks\":{" +
                           "\"p\":{\"a\":0,\"k\":[50,50,0]},\"a\":{\"a\":0,\"k\":[25,25,0]},\"s\":{\"a\":0,\"k\":[100,100,100]}," +
                           "\"rx\":{\"a\":0,\"k\":50},\"ry\":{\"a\":0,\"k\":0},\"rz\":{\"a\":0,\"k\":0},\"or\":{\"a\":0,\"k\":[0,0,0]}," +
                           "\"o\":{\"a\":0,\"k\":100}}}";
            var px = Render(Comp(camera + "," + solid, 60, ",\"ddd\":1"), 0);
            int top = -1, bottom = -1;
            for (int y = 0; y < 100; y++)
            {
                int covered = 0;
                for (int x = 0; x < 100; x++) if ((px[y * 100 + x] >> 24) > 128) covered++;
                if (covered == 0) continue;
                if (top < 0) top = covered;
                bottom = covered;
            }
            Assert.Greater(top, 0, "the layer is visible");
            Assert.Greater(Math.Abs(top - bottom), 4, $"perspective: top row {top} px, bottom row {bottom} px");
        }

        [Test]
        public void Emoji_UseTheColourEmojiFont()
        {
            if (Text.EmojiFont.SystemFontPath() == null) Assert.Ignore("No colour emoji font on this system.");
            string text = "{\"ty\":5,\"ind\":1,\"ip\":0,\"op\":60,\"st\":0," + Transform + ",\"t\":{\"d\":{\"k\":[{\"s\":{\"s\":40," +
                          "\"f\":\"NoSuchFont\",\"t\":\"\\ud83d\\ude00\",\"j\":0,\"tr\":0,\"lh\":48,\"ls\":0,\"fc\":[0,0,0]},\"t\":0}]}," +
                          "\"p\":{},\"m\":{\"g\":1,\"a\":{\"a\":0,\"k\":[0,0]}},\"a\":[]}}";
            string json = Comp(text, 60, ",\"fonts\":{\"list\":[{\"fName\":\"NoSuchFont\",\"fFamily\":\"NoSuchFont\",\"fStyle\":\"Regular\",\"ascent\":75}]}");
            var data = Compiler.LottieCompiler.Compile(json);
            Assert.IsTrue(Array.Exists(data.Glyphs, g => g.Image >= 0), "an emoji glyph bitmap was created");
            var px = Render(json, 0);
            int colored = 0;
            foreach (var p in px) if ((p >> 24) > 200 && (p & 255) > 150 && ((p >> 8) & 255) > 100) colored++;
            Assert.Greater(colored, 50, "the yellow face is drawn");
        }

        /// <summary>
        /// A 1 KB COLR font: U+1F7E5 = red box under a half transparent blue diamond (COLR v0 layers), U+1F7E6 = box with a
        /// red to green linear gradient, U+1F7E9 = green diamond composited src-in over a red box, scaled by 0.5 (COLR v1).
        /// Units per em 1000; box (100, 0) - (900, 800), diamond inscribed in it.
        /// </summary>
        const string ColrTestFont =
            "AAEAAAAMAIAAAwBAQ09MUtSlQ4gAAANgAAAAj0NQQUwBgAATAAAD8AAAABpPUy8yRN9HgwAAAUgAAABgY21hcAAi4GQAAAG4AAAAXGdseWZqWVaCAAACJAAA" +
            "AIhoZWFkMMBuLgAAAMwAAAA2aGhlYQcKA4YAAAEEAAAAJGhtdHgFFADIAAABqAAAAA5sb2NhAHsAVAAAAhQAAAAObWF4cAAIAAYAAAEoAAAAIG5hbWWu02vl" +
            "AAACrAAAAGNwb3N02QBqKQAAAxAAAABPAAEAAAABAACJiu2EXw889QADA+gAAAAA5uEVFwAAAADm4RUXAGQAAAOEAyAAAAADAAIAAAAAAAAAAQAAAyD/OAAA" +
            "A+gAZABkA4QAAQAAAAAAAAAAAAAAAAAAAAEAAQAAAAYABAABAAAAAAACAAAAAAAAAAAAAAAAAAAAAAADA+gBkAAFAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAACAAAAAAAAAAAAAAA/Pz8/AAD/////AyD/OAAAAyAAyAAAAAAAAAAAAAAAAAAAACAAAAPoAAAAZABkAGQAZABkAAAAAAADAAAAAwAA" +
            "ABwAAwABAAAAHAADAAoAAAA0AAQAGAAAAAIAAgAAAAD//wAA//8AAQAAAAwAAAAAACgAAAAAAAAAAgAB9+UAAffmAAAAAwAB9+kAAffpAAAABQAAAAAADQAd" +
            "ACoANwBEAAAAAQBkAAADhAMgAAMAADMRIRFkAyADIPzgAAABAGQAAAOEAyAAAwAAIQkCAfT+cAGQAZABkAGQ/nAAAAEAZAAAA4QDIAADAAAzESERZAMgAyD8" +
            "4AAAAQBkAAADhAMgAAMAADMRIRFkAyADIPzgAAABAGQAAAOEAyAAAwAAMxEhEWQDIAMg/OAAAAAABAA2AAEAAAAAAAEACAAAAAEAAAAAAAIABwAIAAMAAQQJ" +
            "AAEAEAAPAAMAAQQJAAIADgAfQ29sclRlc3RSZWd1bGFyAEMAbwBsAHIAVABlAHMAdABSAGUAZwB1AGwAYQByAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAYAAAECAQMBBAEFAQYDYm94B2RpYW1vbmQGZW1vamlBBmVtb2ppQgZlbW9qaUMAAAEAAQAAACIAAAAoAAIAAAAwAAAAAAAAAAAAAAAAAAAAAAAD" +
            "AAAAAgABAAAAAgABAAAAAgAEAAAAEAAFAAAANQoAAAYAAQQAABAAZAAAA4QAAABkAGQAAAIAAAAAQABAAAACQAASAAAMIAAgAAH0AZAgAAAIBQAAEwoAAAYA" +
            "AgIAAkAACgAABgABAgAAQAAAAAAAAwABAAMAAAAOAAAAAP///wAAgAD/AP8AAA==";

        static Color32 GlyphPixel(Text.EmojiFont.Glyph glyph, float fontX, float fontY)
        {
            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(tex.LoadImage(glyph.Png), "the glyph bitmap is a PNG");
                // Font units (1000 per em, y up) -> glyph space (font size 100, y down) -> bitmap pixels.
                int px = (int)((fontX / 10f - glyph.X) / glyph.Width * tex.width);
                int py = (int)((-fontY / 10f - glyph.Y) / glyph.Height * tex.height);
                return tex.GetPixel(px, tex.height - 1 - py);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void ColrGlyphs_RenderLayersGradientsAndComposites()
        {
            using var font = Text.EmojiFont.Open(new System.IO.MemoryStream(Convert.FromBase64String(ColrTestFont)));
            Assert.IsNotNull(font, "the COLR font opens");

            Assert.IsTrue(font.TryGetGlyph("\ud83d\udfe5", out var layered));
            Assert.AreEqual(10f, layered.X, 0.01f);
            Assert.AreEqual(-80f, layered.Y, 0.01f);
            Assert.AreEqual(100f, layered.Advance, 0.01f);
            AssertColor(new Color32(128, 0, 128, 255), GlyphPixel(layered, 500, 400), 2, "blue at 50% over red");
            AssertColor(new Color32(255, 0, 0, 255), GlyphPixel(layered, 150, 50), 2, "red outside the diamond");

            Assert.IsTrue(font.TryGetGlyph("\ud83d\udfe6", out var gradient));
            AssertColor(new Color32(239, 16, 0, 255), GlyphPixel(gradient, 150, 400), 3, "gradient near the start");
            AssertColor(new Color32(128, 128, 0, 255), GlyphPixel(gradient, 500, 400), 3, "gradient in the middle");
            AssertColor(new Color32(16, 239, 0, 255), GlyphPixel(gradient, 850, 400), 3, "gradient near the end");

            Assert.IsTrue(font.TryGetGlyph("\ud83d\udfe9", out var composite));
            Assert.AreEqual(40f, composite.Width, 0.01f, "scaled by 0.5");
            AssertColor(new Color32(0, 255, 0, 255), GlyphPixel(composite, 500, 400), 2, "source kept inside the backdrop");
            Assert.Less(GlyphPixel(composite, 320, 220).a, 8, "the backdrop is not drawn (src-in)");
        }

        [Test]
        public void Emoji_FromAColrFontFile()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mynell-lottie-colr-test.ttf");
            System.IO.File.WriteAllBytes(path, Convert.FromBase64String(ColrTestFont));
            try
            {
                string text = "{\"ty\":5,\"ind\":1,\"ip\":0,\"op\":60,\"st\":0," + Transform + ",\"t\":{\"d\":{\"k\":[{\"s\":{\"s\":40," +
                              "\"f\":\"NoSuchFont\",\"t\":\"\\ud83d\\udfe6\",\"j\":0,\"tr\":0,\"lh\":48,\"ls\":0,\"fc\":[0,0,0]},\"t\":0}]}," +
                              "\"p\":{},\"m\":{\"g\":1,\"a\":{\"a\":0,\"k\":[0,0]}},\"a\":[]}}";
                string json = Comp(text, 60, ",\"fonts\":{\"list\":[{\"fName\":\"NoSuchFont\",\"fFamily\":\"NoSuchFont\",\"fStyle\":\"Regular\",\"ascent\":75}]}");
                var data = Compiler.LottieCompiler.Compile(json, new Compiler.LottieCompileOptions { FontResolver = _ => null, EmojiFontPath = path });
                Assert.IsTrue(Array.Exists(data.Glyphs, g => g.Image >= 0), "an emoji glyph bitmap was created");
                using var anim = LottieAnimation.FromData(data);
                using var state = new LottieRenderState();
                var px = new NativeArray<uint>(100 * 100, Allocator.Persistent);
                try
                {
                    // Glyph box at size 40: x 54..86, y 18..50 (baseline at the layer position).
                    state.RenderImmediate(anim, 0, LottieRenderState.FitTransform(anim.Width, anim.Height, 100, 100, false), px, 100, 100);
                    var pixels = px.ToArray();
                    Color32 left = At(pixels, 56, 34), right = At(pixels, 84, 34);
                    Assert.Greater(left.r, 200, $"red at the start of the gradient ({left})");
                    Assert.Greater(right.g, 200, $"green at the end of the gradient ({right})");
                    Assert.AreEqual(0, At(pixels, 50, 80).a, "nothing below the baseline");
                }
                finally
                {
                    px.Dispose();
                }
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Test]
        public void Text_WithoutAnyFont_DrawsNothingAndDoesNotCrash()
        {
            // Like WebGL: no font file and no emoji font, so no letter gets a glyph.
            string text = "{\"ty\":5,\"ind\":1,\"ip\":0,\"op\":60,\"st\":0," + Transform + ",\"t\":{\"d\":{\"k\":[{\"s\":{\"s\":40," +
                          "\"f\":\"NoSuchFont\",\"t\":\"Hi \\ud83d\\ude00\\u2764\\ufe0f\\ud83d\\udc4d\\ud83c\\udffd\\udc00\",\"j\":0,\"tr\":0,\"lh\":48,\"ls\":0,\"fc\":[0,0,0]},\"t\":0}]}," +
                          "\"p\":{},\"m\":{\"g\":1,\"a\":{\"a\":0,\"k\":[0,0]}},\"a\":[]}}";
            string json = Comp(text, 60, ",\"fonts\":{\"list\":[{\"fName\":\"NoSuchFont\",\"fFamily\":\"NoSuchFont\",\"fStyle\":\"Regular\",\"ascent\":75}]}");
            var options = new Compiler.LottieCompileOptions { FontResolver = _ => null, EmojiFontPath = "/no/such/emoji.ttf" };
            var data = Compiler.LottieCompiler.Compile(json, options);
            Assert.Greater(data.TextLetters.Length, 0);
            foreach (var letter in data.TextLetters) Assert.AreEqual(-1, letter.Glyph, "letters without a font have no glyph");
            using var anim = LottieAnimation.FromData(data);
            using var state = new LottieRenderState();
            var px = new NativeArray<uint>(100 * 100, Allocator.Persistent);
            try
            {
                state.RenderImmediate(anim, 0, LottieRenderState.FitTransform(anim.Width, anim.Height, 100, 100, false), px, 100, 100);
                foreach (var p in px) Assert.AreEqual(0u, p, "nothing is drawn");
            }
            finally
            {
                px.Dispose();
            }
        }

        [Test]
        public void InvalidJson_Throws()
        {
            Assert.That(() => LottieAnimation.FromJson("{ not json"), Throws.Exception);
        }
    }
}
