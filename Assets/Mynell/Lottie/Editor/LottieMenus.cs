using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mynell.Lottie.Editor
{
    static class LottieMenus
    {
        [MenuItem("GameObject/UI/Lottie Image", false, 2002)]
        static void CreateLottieImage(MenuCommand command)
        {
            var parent = command.context as GameObject;
            if (parent == null || parent.GetComponentInParent<Canvas>() == null) parent = GetOrCreateCanvas();
            var go = CreateImage(Selection.activeObject as LottieAsset, parent.transform, "Lottie Image");
            Selection.activeGameObject = go;
        }

        [MenuItem("GameObject/2D Object/Lottie Renderer", false, 10)]
        static void CreateLottieRenderer(MenuCommand command)
        {
            var parent = command.context as GameObject;
            var go = CreateRenderer(Selection.activeObject as LottieAsset, parent != null ? parent.transform : null, "Lottie Renderer");
            Selection.activeGameObject = go;
        }

        /// <summary>A Lottie Image under <paramref name="parent"/> (which must be under a Canvas), at its native size.</summary>
        internal static GameObject CreateImage(LottieAsset asset, Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(LottieImage));
            GameObjectUtility.SetParentAndAlign(go, parent.gameObject);
            GameObjectUtility.EnsureUniqueNameForSibling(go);
            var image = go.GetComponent<LottieImage>();
            image.raycastTarget = false;
            if (asset != null)
            {
                image.Asset = asset;
                image.SetNativeSize();
            }
            else ((RectTransform)go.transform).sizeDelta = new Vector2(200, 200);
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            return go;
        }

        /// <summary>A Lottie Renderer under <paramref name="parent"/>, or at the root of the open scene or prefab.</summary>
        internal static GameObject CreateRenderer(LottieAsset asset, Transform parent, string name)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer), typeof(LottieRenderer));
            if (parent != null) GameObjectUtility.SetParentAndAlign(go, parent.gameObject);
            else StageUtility.PlaceGameObjectInCurrentStage(go);
            GameObjectUtility.EnsureUniqueNameForSibling(go);
            if (asset != null) go.GetComponent<LottieRenderer>().Asset = asset;
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            return go;
        }

        static GameObject GetOrCreateCanvas()
        {
            var canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas != null) return canvas.gameObject;
            var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            go.layer = LayerMask.NameToLayer("UI");
            Undo.RegisterCreatedObjectUndo(go, "Create Canvas");
            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem));
                AddInputModule(es);
                Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
            }
            return go;
        }

        static void AddInputModule(GameObject es)
        {
            // Prefer the Input System UI module when the package is present.
            var type = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (type != null) es.AddComponent(type);
            else es.AddComponent<StandaloneInputModule>();
        }
    }
}
