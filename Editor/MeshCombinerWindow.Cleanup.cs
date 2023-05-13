/**
 * The MIT License (MIT)
 *
 * Copyright (c) 2023 Jeremy Lam aka. Vistanz
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityObject = UnityEngine.Object;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    public partial class MeshCombinerWindow : EditorWindow {
        const string REMOVE_UNUSED_INFO = "Here are the dereferenced objects from the last combine operation.\n" +
            "You can use the one-click button to auto delete or hide them all, or handle them manually by yourself.\n" +
            "If it is an object from a prefab, it will be inactived and set to editor only.\n" +
            "Gray out objects means theres are references pointing to them and/or their children in hierarchy.\n" +
            "You can use Unity's undo function if you accidentally deleted something.";
        Vector2 unusedObjectScrollPos;
        TransformSet rootTransforms = new TransformSet();
        TransformSet unusedTransforms = new TransformSet();
        TransformSet safeDeleteTransforms = new TransformSet();

        void DrawUnusedObjectsTab() {
            EditorGUILayout.HelpBox(REMOVE_UNUSED_INFO, MessageType.Info);
            unusedObjectScrollPos = EditorGUILayout.BeginScrollView(unusedObjectScrollPos);
            foreach (var unusedTransform in unusedTransforms) {
                if (unusedTransform == null) continue;
                EditorGUILayout.BeginHorizontal();
                var gameObject = unusedTransform.gameObject;
                EditorGUI.BeginDisabledGroup(!safeDeleteTransforms.Contains(unusedTransform));
                EditorGUILayout.LabelField(EditorGUIUtility.ObjectContent(gameObject, typeof(GameObject)), GUILayout.ExpandWidth(true));
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button("Locate", EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false)))
                    EditorGUIUtility.PingObject(unusedTransform);
                if (GUILayout.Button("Select", EditorStyles.miniButtonMid, GUILayout.ExpandWidth(false)))
                    Selection.activeTransform = unusedTransform;
                if (GUILayout.Button("+", EditorStyles.miniButtonMid, GUILayout.ExpandWidth(false)))
                    Selection.objects = new HashSet<GameObject>(Selection.gameObjects) { unusedTransform.gameObject }.ToArray();
                if (GUILayout.Button("-", EditorStyles.miniButtonMid, GUILayout.ExpandWidth(false)))
                    Selection.objects = Selection.gameObjects.Where(x => x != unusedTransform.gameObject).ToArray();
                if (GUILayout.Button("Set Editor Only", EditorStyles.miniButtonMid, GUILayout.ExpandWidth(false)))
                    SetEditorOnly(unusedTransform.gameObject);
                if (GUILayout.Button("Delete", EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)))
                    SafeDeleteObject(unusedTransform.gameObject);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            autoCleanup = EditorGUILayout.ToggleLeft("Auto Cleanup On Combine", autoCleanup);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(unusedTransforms.Count == 0);
            if (GUILayout.Button("Select All", GUILayout.ExpandWidth(false)))
                Selection.objects = unusedTransforms.Where(x => x != null).Select(x => x.gameObject).ToArray();
            if (GUILayout.Button("Refresh", GUILayout.ExpandWidth(false)))
                UpdateSafeDeleteObjects();
            if (GUILayout.Button("Set All to Editor Only", GUILayout.ExpandWidth(false)) &&
                EditorUtility.DisplayDialog("Confirm", "Proceed on set editor only?\nYou may undo if the result is unexpected.", "Yes", "No"))
                SafeDeleteAllObjects(true);
            if (GUILayout.Button("Safely Delete All", GUILayout.ExpandWidth(false)) &&
                EditorUtility.DisplayDialog("Confirm", "Proceed on safe delete?\nYou may undo if the result is unexpected.", "Yes", "No"))
                SafeDeleteAllObjects(false);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        void UpdateSafeDeleteObjects() {
            safeDeleteTransforms.Clear();
            int count = unusedTransforms.Count;
            if (count == 0) return;
            var sceneObjects = new HashSet<Component>(
                unusedTransforms.Where(x => x != null).Select(x => x.gameObject.scene)
                .Distinct().SelectMany(x => x.GetRootGameObjects())
                .SelectMany(x => x.GetComponentsInChildren<Component>(true))
                .Where(x => x != null && !(x is Transform) && !unusedTransforms.Contains(x.transform))
            );
            var checkObjects = new HashSet<UnityObject>();
            var hierarchyWalker = new Stack<Transform>();
            int i = -1;
            foreach (var unusedObject in unusedTransforms) {
                i++;
                if (unusedObject == null) continue;
                EditorUtility.DisplayProgressBar("Collecting Unused Objects", unusedObject.name, (float)i / count);
                hierarchyWalker.Push(unusedObject);
                checkObjects.Clear();
                while (hierarchyWalker.Count > 0) {
                    var transform = hierarchyWalker.Pop();
                    if (transform == null) continue;
                    checkObjects.Add(transform.gameObject);
                    checkObjects.UnionWith(transform.GetComponents<Component>().SelectMany(InspectKnownComponents));
                    foreach (Transform child in transform) hierarchyWalker.Push(child);
                }
                bool hasReference = false;
                foreach (var sceneObject in sceneObjects) {
                    var so = new SerializedObject(sceneObject);
                    var iterator = so.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference) {
                            var obj = iterator.objectReferenceValue;
                            if (obj != null && checkObjects.Contains(obj)) {
                                hasReference = true;
                                break;
                            }
                        }
                    if (hasReference) break;
                }
                if (!hasReference) safeDeleteTransforms.Add(unusedObject);
            }
            EditorUtility.ClearProgressBar();
        }

        IEnumerable<Component> InspectKnownComponents(Component sourceComponent) {
            if (sourceComponent == null) yield break;
            yield return sourceComponent;
            var type = sourceComponent.GetType();
            Stack<Transform> hierarchyWalker;
            HashSet<Transform> excludeTransformSet = null;
            try {
                if (type.Namespace == "VRC.SDK3.Dynamics.Contact.Components" && type.Name == "VRCPhysBone") {
                    dynamic vrcPhysBone = sourceComponent;
                    Transform root = vrcPhysBone.transform;
                    if (root == null) root = sourceComponent.transform;
                    List<Transform> excludes = vrcPhysBone.ignoreTransforms;
                    if (excludes != null) excludeTransformSet = new HashSet<Transform>(excludes);
                    hierarchyWalker = new Stack<Transform>();
                    hierarchyWalker.Push(root);
                } else if (type.Namespace == "" && type.Name == "DynamicBone") {
                    dynamic dynamicBone = sourceComponent;
                    Transform root = dynamicBone.m_Root;
                    List<Transform> excludes = dynamicBone.m_Exclusions;
                    if (excludes != null) excludeTransformSet = new HashSet<Transform>(excludes);
                    hierarchyWalker = new Stack<Transform>();
                    hierarchyWalker.Push(root);
                } else if (type.Namespace == "VRM" && type.Name == "SpringBone") {
                    dynamic springBone = sourceComponent;
                    Transform[] roots = springBone.RootBones;
                    if (roots == null || roots.Length == 0) yield break;
                    hierarchyWalker = new Stack<Transform>(roots);
                } else yield break;
            } catch (Exception ex) {
                Debug.LogWarning($"Failed to inspect component {sourceComponent.name} ({type.FullName}): {ex.Message}");
                yield break;
            }
            if (hierarchyWalker == null) yield break;
            while (hierarchyWalker.Count > 0) {
                var transform = hierarchyWalker.Pop();
                if (transform == null) continue;
                if (excludeTransformSet == null || !excludeTransformSet.Contains(transform))
                    yield return transform;
                foreach (Transform child in transform) hierarchyWalker.Push(child);
            }
        }

        void SafeDeleteAllObjects(bool hideOnly = false) {
            UpdateSafeDeleteObjects();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(hideOnly ? "Set All to Editor Only" : "Safely Delete All");
            var undoGroup = Undo.GetCurrentGroup();
            foreach (var unusedTransform in safeDeleteTransforms) {
                if (unusedTransform == null) continue;
                if (hideOnly)
                    SetEditorOnly(unusedTransform.gameObject);
                else
                    SafeDeleteObject(unusedTransform.gameObject);
            }
            Undo.CollapseUndoOperations(undoGroup);
        }

        static void SetEditorOnly(GameObject gameObject) {
            Undo.RecordObject(gameObject, "Inactivate and Set to Editor Only");
            gameObject.tag = "EditorOnly";
            gameObject.SetActive(false);
        }

        static void SafeDeleteObject(GameObject gameObject) {
            if (PrefabUtility.IsPartOfAnyPrefab(gameObject))
                SetEditorOnly(gameObject);
            else
                Undo.DestroyObjectImmediate(gameObject);
        }
    }

}