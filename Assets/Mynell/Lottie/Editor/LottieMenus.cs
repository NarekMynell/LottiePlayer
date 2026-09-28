using UnityEditor;
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
            var go = new GameObject("Lottie Image", typeof(RectTransform), typeof(CanvasRenderer), typeof(LottieImage));
            GameObjectUtility.SetParentAndAlign(go, parent);
            var image = go.GetComponent<LottieImage>();
            image.raycastTarget = false;
            if (Selection.activeObject is LottieAsset asset)
            {
                image.Asset = asset;
                image.SetNativeSize();
            }
            else ((RectTransform)go.transform).sizeDelta = new Vector2(200, 200);
            Undo.RegisterCreatedObjectUndo(go, "Create Lottie Image");
            Selection.activeGameObject = go;
        }

        [MenuItem("GameObject/2D Object/Lottie Renderer", false, 10)]
        static void CreateLottieRenderer(MenuCommand command)
        {
            var go = new GameObject("Lottie Renderer", typeof(MeshFilter), typeof(MeshRenderer), typeof(LottieRenderer));
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            if (Selection.activeObject is LottieAsset asset) go.GetComponent<LottieRenderer>().Asset = asset;
            Undo.RegisterCreatedObjectUndo(go, "Create Lottie Renderer");
            Selection.activeGameObject = go;
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
