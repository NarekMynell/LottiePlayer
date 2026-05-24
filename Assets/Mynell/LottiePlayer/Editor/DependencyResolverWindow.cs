// Mynell Lottie Player — Editor popup that lists missing packages with one-click install.

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Mynell.LottiePlayer.Editor
{
    public sealed class DependencyResolverWindow : EditorWindow
    {
        private List<(string id, string version, string label, string purpose)> _missing;
        private AddAndRemoveRequest _addRequest;
        private string _status = "Mynell Lottie Player requires these packages.";

        public static void Open(List<(string id, string version, string label, string purpose)> missing)
        {
            var w = GetWindow<DependencyResolverWindow>(utility: true, title: "Mynell Dependency Resolver", focus: true);
            w._missing = missing;
            w.minSize = new Vector2(520, 320);
            w.maxSize = new Vector2(700, 520);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12);
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField("Mynell Lottie Player", new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 });
                    EditorGUILayout.LabelField("Dependency Resolver", EditorStyles.miniLabel);
                }
                GUILayout.Space(12);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(_status, MessageType.Info);
            EditorGUILayout.Space(8);

            if (_missing == null || _missing.Count == 0)
            {
                EditorGUILayout.HelpBox("No missing dependencies. You're good to go.", MessageType.None);
            }
            else
            {
                EditorGUILayout.LabelField("Missing packages:", EditorStyles.boldLabel);
                foreach (var m in _missing)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField($"{m.label}  ({m.id})", EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"Min version: {m.version}", EditorStyles.miniLabel);
                        EditorGUILayout.LabelField(m.purpose, EditorStyles.wordWrappedLabel);
                    }
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(_addRequest != null && !_addRequest.IsCompleted))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Remind Me Later", GUILayout.Height(28)))
                    {
                        Close();
                    }
                    GUILayout.FlexibleSpace();
                    GUI.backgroundColor = new Color(0.34f, 0.74f, 0.98f);
                    if (GUILayout.Button("Install Missing Packages", GUILayout.Height(28), GUILayout.MinWidth(220)))
                    {
                        InstallMissing();
                    }
                    GUI.backgroundColor = Color.white;
                }
            }
            EditorGUILayout.Space(8);
        }

        private void InstallMissing()
        {
            if (_missing == null || _missing.Count == 0) { Close(); return; }
            var toAdd = new string[_missing.Count];
            for (int i = 0; i < _missing.Count; i++) toAdd[i] = $"{_missing[i].id}@{_missing[i].version}";
            _status = "Installing: " + string.Join(", ", toAdd);
            _addRequest = Client.AddAndRemove(packagesToAdd: toAdd, packagesToRemove: null);
            EditorApplication.update += PollInstall;
        }

        private void PollInstall()
        {
            if (_addRequest == null || !_addRequest.IsCompleted) return;
            EditorApplication.update -= PollInstall;
            if (_addRequest.Status == StatusCode.Success)
            {
                _status = "Done. Packages installed.";
                _missing.Clear();
                Repaint();
                EditorApplication.delayCall += Close;
            }
            else
            {
                _status = "Install failed: " + (_addRequest.Error != null ? _addRequest.Error.message : "unknown");
                Repaint();
            }
        }
    }
}
