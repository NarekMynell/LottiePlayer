using System.Collections.Generic;
using System.IO;
using System.Text;
using Mynell.Lottie.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mynell.Lottie.Tests
{
    /// <summary>Editor workflow: recognizing Lottie JSON files and dropping assets into the scene.</summary>
    public class LottieEditorTests
    {
        const string Minimal = "{\"v\":\"5.7.0\",\"fr\":30,\"ip\":0,\"op\":60,\"w\":100,\"h\":80,\"nm\":\"x\",\"ddd\":0,\"assets\":[],\"layers\":[]}";

        readonly List<Object> m_Created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in m_Created)
                if (o != null) Object.DestroyImmediate(o);
            m_Created.Clear();
        }

        static bool IsLottie(string json) => LottieImporter.HasLottieKeys(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        [Test]
        public void RecognizesLottieJson()
        {
            Assert.IsTrue(IsLottie(Minimal));
            Assert.IsTrue(IsLottie("﻿" + Minimal), "UTF-8 byte order mark");
            Assert.IsTrue(IsLottie("{\n  \"layers\" : [ {\"ty\":4} ],\n  \"h\": 1, \"w\": 1, \"op\": 1, \"ip\": 0, \"fr\": 30\n}"), "any order and spacing");

            // Big embedded images before the other keys (past the first read buffer).
            string image = new string('A', 200000);
            Assert.IsTrue(IsLottie("{\"assets\":[{\"id\":\"i\",\"w\":1,\"h\":1,\"p\":\"data:image/png;base64," + image +
                                   "\"}],\"v\":\"5\",\"fr\":30,\"ip\":0,\"op\":1,\"w\":1,\"h\":1,\"layers\":[]}"));

            Assert.IsFalse(IsLottie("{\"name\":\"com.example\",\"version\":\"1.0.0\",\"layers\":[]}"), "other JSON");
            Assert.IsFalse(IsLottie("[" + Minimal + "]"), "an array is not an animation");
            Assert.IsFalse(IsLottie("{\"anim\":" + Minimal + "}"), "keys must be at the top level");
            Assert.IsFalse(IsLottie("{\"a\":\"layers\",\"b\":\"fr\",\"c\":\"ip\",\"d\":\"op\",\"e\":\"w\",\"f\":\"h\"}"), "values are not keys");
            Assert.IsFalse(IsLottie("{\"x\":\"\\\"layers\\\":\",\"fr\":1,\"ip\":0,\"op\":1,\"w\":1,\"h\":1}"), "escaped quotes stay inside the string");
            Assert.IsFalse(IsLottie(""));
            Assert.IsFalse(IsLottie("{\"fr\":30,\"ip\":0,\"op\":60,\"w\":100,\"h\":80"), "cut off before layers");
        }

        [Test]
        public void FindsLottieJsonImportedAsText()
        {
            const string dir = "Assets/MynellLottieTextTest";
            const string path = dir + "/Text.json";
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(path, Minimal);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                // A brand-new file is switched to the Lottie importer on the next editor update; until then it is text.
                Assert.IsInstanceOf<TextAsset>(AssetDatabase.LoadMainAssetAtPath(path));
                CollectionAssert.Contains(LottieJsonDetector.LottieFilesImportedAsText(), path);

                AssetDatabase.SetImporterOverride<LottieImporter>(path);
                Assert.IsInstanceOf<LottieAsset>(AssetDatabase.LoadMainAssetAtPath(path));
                CollectionAssert.DoesNotContain(LottieJsonDetector.LottieFilesImportedAsText(), path);
            }
            finally
            {
                AssetDatabase.DeleteAsset(dir);
            }
        }

        LottieAsset Asset(string name)
        {
            var asset = LottieAsset.CreateFromJson(Minimal, name);
            m_Created.Add(asset);
            return asset;
        }

        GameObject Track(GameObject go)
        {
            m_Created.Add(go);
            return go;
        }

        GameObject Canvas()
        {
            var go = Track(new GameObject("Canvas", typeof(RectTransform), typeof(Canvas)));
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            return go;
        }

        [Test]
        public void DropOnCanvasMakesLottieImages()
        {
            var canvas = Canvas();
            var created = LottieDragAndDrop.DropInHierarchy(new List<LottieAsset> { Asset("Spinner"), Asset("Check") },
                canvas, HierarchyDropFlags.DropUpon, null);
            Assert.AreEqual(2, created.Count);
            foreach (var go in created)
            {
                Track(go);
                var image = go.GetComponent<LottieImage>();
                Assert.IsNotNull(image);
                Assert.AreSame(canvas.transform, go.transform.parent);
                Assert.IsNotNull(image.Asset);
                Assert.AreEqual(image.Asset.name, go.name);
                Assert.AreEqual(new Vector2(100, 80), ((RectTransform)go.transform).sizeDelta, "native size");
                Assert.IsFalse(image.raycastTarget);
            }

            // Dropped on a Lottie Image in the Scene view: next to it, not inside it.
            var more = LottieDragAndDrop.DropInScene(new List<LottieAsset> { Asset("Heart") }, created[0], Vector3.zero, null, null);
            Track(more[0]);
            Assert.AreSame(canvas.transform, more[0].transform.parent);
            Assert.IsNotNull(more[0].GetComponent<LottieImage>());
        }

        [Test]
        public void DropOutsideUIMakesLottieRenderers()
        {
            var created = LottieDragAndDrop.DropInHierarchy(new List<LottieAsset> { Asset("Spinner") }, null, HierarchyDropFlags.None, null);
            var go = Track(created[0]);
            Assert.IsNotNull(go.GetComponent<LottieRenderer>());
            Assert.IsNull(go.transform.parent);
            Assert.AreEqual("Spinner", go.name);

            var inScene = LottieDragAndDrop.DropInScene(new List<LottieAsset> { Asset("Check") }, null, new Vector3(1, 2, 3), null, null);
            Track(inScene[0]);
            Assert.IsNotNull(inScene[0].GetComponent<LottieRenderer>());
            Assert.AreEqual(1f, inScene[0].transform.position.x);
            Assert.AreEqual(2f, inScene[0].transform.position.y);

            // Dropped between two children: inserted right after the target, under the same parent.
            var root = Track(new GameObject("Root"));
            var a = new GameObject("A");
            var b = new GameObject("B");
            a.transform.SetParent(root.transform);
            b.transform.SetParent(root.transform);
            var between = LottieDragAndDrop.DropInHierarchy(new List<LottieAsset> { Asset("Heart") }, a, HierarchyDropFlags.DropBetween, null);
            Assert.AreSame(root.transform, between[0].transform.parent);
            Assert.AreEqual(1, between[0].transform.GetSiblingIndex());
            Assert.AreEqual(2, b.transform.GetSiblingIndex());
        }

        [Test]
        public void OneUndoRemovesADrop()
        {
            var created = LottieDragAndDrop.DropInHierarchy(new List<LottieAsset> { Asset("Spinner"), Asset("Check") }, null,
                HierarchyDropFlags.None, null);
            foreach (var go in created) Track(go);
            Undo.PerformUndo();
            foreach (var go in created) Assert.IsTrue(go == null, "undone");
        }
    }
}
