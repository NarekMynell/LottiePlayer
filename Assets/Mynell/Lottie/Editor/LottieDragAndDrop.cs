using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary>
    /// Lottie Assets dragged into the Scene view or the Hierarchy become objects that play them: a Lottie Image when
    /// dropped on UI (anything under a Canvas), a Lottie Renderer anywhere else.
    /// </summary>
    [InitializeOnLoad]
    static class LottieDragAndDrop
    {
        static LottieDragAndDrop()
        {
#if UNITY_6000_3_OR_NEWER
            DragAndDrop.AddDropHandlerV2(OnSceneDrop);
            DragAndDrop.AddDropHandlerV2(OnHierarchyDrop);
#else
            DragAndDrop.AddDropHandler(OnSceneDrop);
            DragAndDrop.AddDropHandler(OnHierarchyDrop);
#endif
        }

        static DragAndDropVisualMode OnSceneDrop(Object dropUpon, Vector3 worldPosition, Vector2 viewportPosition,
            Transform parentForDraggedObjects, bool perform)
        {
            var assets = DraggedAssets();
            if (assets == null) return DragAndDropVisualMode.None;
            if (perform)
            {
                var target = dropUpon as GameObject;
                Ray? ray = Event.current != null ? HandleUtility.GUIPointToWorldRay(Event.current.mousePosition) : (Ray?)null;
                Select(DropInScene(assets, target, worldPosition, ray, parentForDraggedObjects));
            }
            return DragAndDropVisualMode.Copy;
        }

#if UNITY_6000_3_OR_NEWER
        static DragAndDropVisualMode OnHierarchyDrop(EntityId dropTarget, HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects, bool perform)
        {
            var assets = DraggedAssets();
            if (assets == null) return DragAndDropVisualMode.None;
            if (perform)
                Select(DropInHierarchy(assets, EditorUtility.EntityIdToObject(dropTarget) as GameObject, dropMode, parentForDraggedObjects));
            return DragAndDropVisualMode.Copy;
        }
#else
        static DragAndDropVisualMode OnHierarchyDrop(int dropTarget, HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects, bool perform)
        {
            var assets = DraggedAssets();
            if (assets == null) return DragAndDropVisualMode.None;
            if (perform)
                Select(DropInHierarchy(assets, EditorUtility.InstanceIDToObject(dropTarget) as GameObject, dropMode, parentForDraggedObjects));
            return DragAndDropVisualMode.Copy;
        }
#endif

        /// <summary>The dragged Lottie Assets, or null when anything else is dragged too (Unity handles that drag).</summary>
        static List<LottieAsset> DraggedAssets()
        {
            var objects = DragAndDrop.objectReferences;
            if (objects == null || objects.Length == 0) return null;
            var assets = new List<LottieAsset>(objects.Length);
            foreach (var o in objects)
            {
                if (!(o is LottieAsset asset)) return null;
                assets.Add(asset);
            }
            return assets;
        }

        /// <summary>
        /// Dropped in the Scene view on <paramref name="target"/> (the object under the mouse, or null). On UI the
        /// images become children of the target, placed where the <paramref name="ray"/> meets it; elsewhere renderers
        /// are placed at <paramref name="worldPosition"/>.
        /// </summary>
        internal static List<GameObject> DropInScene(List<LottieAsset> assets, GameObject target, Vector3 worldPosition, Ray? ray,
            Transform forcedParent)
        {
            var created = new List<GameObject>();
            int group = BeginUndo();
            if (target != null && IsUI(target.transform))
            {
                // Another Lottie Image is not a container: drop next to it.
                var parent = target.GetComponent<LottieImage>() != null && IsUI(target.transform.parent) ? target.transform.parent : target.transform;
                foreach (var asset in assets)
                {
                    var go = LottieMenus.CreateImage(asset, parent, asset.name);
                    if (ray.HasValue && new Plane(parent.forward, parent.position).Raycast(ray.Value, out float distance))
                    {
                        go.transform.position = ray.Value.GetPoint(distance);
                        var local = go.transform.localPosition;
                        go.transform.localPosition = new Vector3(local.x, local.y, 0);
                    }
                    created.Add(go);
                }
            }
            else
            {
                var view = SceneView.lastActiveSceneView;
                if (view != null && view.in2DMode) worldPosition.z = 0;
                foreach (var asset in assets)
                {
                    var go = LottieMenus.CreateRenderer(asset, forcedParent, asset.name);
                    go.transform.position = worldPosition;
                    created.Add(go);
                }
            }
            EndUndo(group, assets);
            return created;
        }

        /// <summary>Dropped in the Hierarchy on, above or below <paramref name="target"/>, or on empty space (null).</summary>
        internal static List<GameObject> DropInHierarchy(List<LottieAsset> assets, GameObject target, HierarchyDropFlags mode,
            Transform forcedParent)
        {
            Transform parent = forcedParent;
            int sibling = -1;
            if (parent == null && target != null)
            {
                var t = target.transform;
                if ((mode & HierarchyDropFlags.DropUpon) != 0) parent = t;
                else if ((mode & HierarchyDropFlags.DropAfterParent) != 0 && t.parent != null)
                {
                    parent = t.parent.parent;
                    sibling = t.parent.GetSiblingIndex() + 1;
                }
                else
                {
                    parent = t.parent;
                    sibling = t.GetSiblingIndex() + ((mode & HierarchyDropFlags.DropAbove) != 0 ? 0 : 1);
                }
            }

            var created = new List<GameObject>();
            int group = BeginUndo();
            bool ui = IsUI(parent);
            foreach (var asset in assets)
            {
                var go = ui ? LottieMenus.CreateImage(asset, parent, asset.name) : LottieMenus.CreateRenderer(asset, parent, asset.name);
                // Dropped next to a root object of another loaded scene: that scene, not the active one.
                if (parent == null && target != null && go.scene != target.scene)
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, target.scene);
                if (sibling >= 0) go.transform.SetSiblingIndex(sibling++);
                created.Add(go);
            }
            EndUndo(group, assets);
            return created;
        }

        static bool IsUI(Transform t) => t != null && t is RectTransform && t.GetComponentInParent<Canvas>(true) != null;

        static int BeginUndo()
        {
            Undo.IncrementCurrentGroup();
            return Undo.GetCurrentGroup();
        }

        static void EndUndo(int group, List<LottieAsset> assets)
        {
            Undo.SetCurrentGroupName(assets.Count == 1 ? "Create " + assets[0].name : "Create Lottie Objects");
            Undo.CollapseUndoOperations(group);
        }

        static void Select(List<GameObject> objects)
        {
            Selection.objects = objects.ToArray();
            DragAndDrop.AcceptDrag();
        }
    }
}
