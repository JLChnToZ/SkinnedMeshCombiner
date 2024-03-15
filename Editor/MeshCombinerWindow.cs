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
    using JLChnToZ.CommonUtils;
    using static Utils;
    
    public partial class MeshCombinerWindow : EditorWindow {
        const string COMBINE_INFO = "Click combine to...\n" +
            "- Combine meshes while retains blendshapes and bones\n" +
            "- Merge sub meshes with same material into one\n" +
            "- Create extra bones for each non skinned mesh renderers\n" +
            "- Derefereneces unused bones (but not delete them)\n" +
            "- Bake (freeze state and then removes) selected blendshapes\n" +
            "- Save the combined mesh to a file\n" +
            "- Deactivates combined mesh renderer sources";
        const string MESH_RENDERER_INFO = "The destination is an ordinary mesh renderer, all blend shapes and bones will enforced to combined and dereferenced.";

        public enum Tabs : byte {
            CombineMeshes,
            CombineBones,
            BlendshapesRename,
            Cleanup,
        }

        [Serializable] class TransformSet : SerializableSet<Transform> {}

        static string[] tabNames;
        static GUIContent dropdownIcon;
        Tabs currentTab;
        bool autoCleanup = true;
        Renderer destination;

        [MenuItem("JLChnToZ/Tools/Skinned Mesh Combiner")]
        public static void ShowWindow() => GetWindow<MeshCombinerWindow>("Skinned Mesh Combiner").Show(true);

        protected virtual void OnEnable() {
            if (tabNames == null) tabNames = Array.ConvertAll(Enum.GetNames(typeof(Tabs)), ObjectNames.NicifyVariableName);
            InitCombineMeshTab();
            switch (currentTab) {
                case Tabs.CombineMeshes: RefreshCombineMeshOptions(); break;
                case Tabs.CombineBones: RefreshBones(); break;
                case Tabs.BlendshapesRename: RefreshBlendshapes(); break;
                case Tabs.Cleanup: UpdateSafeDeleteObjects(); break;
            }
            OnListAdd(sourceList);
        }

        protected virtual void OnGUI() {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            currentTab = (Tabs)GUILayout.Toolbar((int)currentTab, tabNames, GUILayout.ExpandWidth(true));
            bool tabChanged = EditorGUI.EndChangeCheck();
            EditorGUILayout.EndHorizontal();
            switch (currentTab) {
                case Tabs.CombineMeshes: 
                    if (tabChanged) RefreshCombineMeshOptions();
                    DrawCombineMeshTab();
                    break;
                case Tabs.CombineBones:
                    if (tabChanged) RefreshBones();
                    DrawCombineBoneTab();
                    break;
                case Tabs.BlendshapesRename:
                    if (tabChanged) RefreshBlendshapes();
                    DrawBlendshapeTab();
                    break;
                case Tabs.Cleanup:
                    if (tabChanged) UpdateSafeDeleteObjects();
                    DrawUnusedObjectsTab();
                    break;
            }
            if (destination is MeshRenderer) EditorGUILayout.HelpBox(MESH_RENDERER_INFO, MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(sources.Count == 0);
            if (GUILayout.Button("Combine")) Combine();
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Clear")) {
                sources.Clear();
                bakeBlendShapeMap.Clear();
                boneReamp.Clear();
                unusedTransforms.Clear();
                safeDeleteTransforms.Clear();
                bonesToMergeUpwards.Clear();
                boneToRenderersMap.Clear();
                boneFolded.Clear();
                allBlendshapeNames.Clear();
                blendshapeNameMap.Clear();
                destination = null;
                currentTab = 0;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(COMBINE_INFO, MessageType.Info);
            HandleDrop();
        }

        void HandleDrop() {
            var ev = Event.current;
            switch (ev.type) {
                case EventType.DragUpdated:
                    if (DragAndDrop.objectReferences.SelectMany(GetRenderers).Any(IsAcceptableObject)) {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                        ev.Use();
                    }
                    break;
                case EventType.DragPerform:
                    var objectRefs = DragAndDrop.objectReferences.SelectMany(GetRenderers).ToArray();
                    if (objectRefs.Any(IsAcceptableObject)) {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                        DragAndDrop.AcceptDrag();
                        sources.AddRange(objectRefs.Where(IsAcceptableObject));
                        if (currentTab != 0) currentTab = 0;
                        RefreshCombineMeshOptions();
                        ev.Use();
                    }
                    break;
            }
        }

        static IEnumerable<Renderer> GetRenderers(UnityObject obj) {
            if (obj is GameObject go)
                return go.GetComponentsInChildren<Renderer>(true);
            else if (obj is Renderer r)
                return new[] { r };
            else
                return Enumerable.Empty<Renderer>();
        }

        bool IsAcceptableObject(Renderer r) =>
            (r is SkinnedMeshRenderer smr && IsAcceptableObject(smr)) ||
            (r is MeshRenderer mr && IsAcceptableObject(mr));

        bool IsAcceptableObject(SkinnedMeshRenderer smr) => smr != null && smr.sharedMesh != null && !sources.Contains(smr);

        bool IsAcceptableObject(MeshRenderer mr) => mr != null && mr.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null && !sources.Contains(mr);

        T AutoCreateRenderer<T>() where T : Renderer {
            var commonParent = FindCommonParent(sources.Select(x => x.transform));
            var type = typeof(T);
            var newChild = type.Name == "MeshRenderer" ?
                new GameObject("Combined Mesh", typeof(MeshFilter), type) :
                new GameObject("Combined Mesh", type);
            if (commonParent != null) newChild.transform.SetParent(commonParent, false);
            GameObjectUtility.EnsureUniqueNameForSibling(newChild);
            Undo.RegisterCreatedObjectUndo(newChild, $"Auto Create {type.Name}");
            return newChild.GetComponent<T>();
        }

        /// <summary>
        /// Auto Order Renderer
        /// </summary>
        /// <param name="order">Default Descending</param>
        void AutoOrderRenderer(bool descending = true)
        {
            // Convert sourceList to List<Renderer> for sorting
            List<Renderer> renderers = new List<Renderer>();
            foreach (var item in sourceList.list)
            {
                renderers.Add(item as Renderer);
            }
            if (descending)
            {
                // Ascending
                renderers.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));
            }
            else
            {
                // Descending
                renderers.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));
            }
            sourceList.list.Clear();
            foreach (var item in renderers)
            {
                sourceList.list.Add(item);
            }
        }

        void Combine() {
            RefreshCombineMeshOptions();
            boneReamp.Clear();
            foreach (var bone in bonesToMergeUpwards) {
                var targetBone = bone;
                while (targetBone != null && bonesToMergeUpwards.Contains(targetBone)) {
                    targetBone = targetBone.parent;
                    boneReamp[bone] = targetBone;
                }
            }
            unusedTransforms.Clear();
            safeDeleteTransforms.Clear();
            foreach (var source in sources) {
                unusedTransforms.Add(source.transform);
                if (source is SkinnedMeshRenderer skinnedMeshRenderer)
                    unusedTransforms.UnionWith(skinnedMeshRenderer.bones.Where(b => b != null));
            }
            bool shouldPingDestination = false;
            if (destination == null) {
                destination = AutoCreateRenderer<SkinnedMeshRenderer>();
                shouldPingDestination = true;
            }
            var saveAssetDefaultPath = sources.Select(source => {
                var skinnedMeshRenderer = source as SkinnedMeshRenderer;
                if (skinnedMeshRenderer != null) return skinnedMeshRenderer.sharedMesh;
                var meshRenderer = source as MeshRenderer;
                if (meshRenderer != null && source.TryGetComponent(out MeshFilter meshFilter)) return meshFilter.sharedMesh;
                return null;
            })
            .Where(m => m != null)
            .Select(AssetDatabase.GetAssetPath)
            .Where(p => !string.IsNullOrEmpty(p) && p.StartsWith("Assets/"))
            .Select(System.IO.Path.GetDirectoryName)
            .FirstOrDefault() ?? string.Empty;
            var mesh = SkinnedMeshCombinerCore.Combine(sources.Select(source => {
                if (bakeBlendShapeMap.TryGetValue(source, out var bakeBlendShapeToggles))
                    return (source, bakeBlendShapeToggles.blendShapeFlags, bakeBlendShapeToggles.combineMeshFlags);
                return (source, null, CombineMeshFlags.None);
            }).ToArray(), destination, mergeFlags, blendShapeCopyMode, boneReamp, blendshapeNameMap);
            if (mesh != null) {
                mesh.Optimize();
                if (destination is SkinnedMeshRenderer skinnedMeshRenderer)
                    unusedTransforms.ExceptWith(skinnedMeshRenderer.bones);
                unusedTransforms.Remove(destination.transform);
                var path = EditorUtility.SaveFilePanelInProject("Save Mesh", mesh.name, "asset", "Save Combined Mesh", saveAssetDefaultPath);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.CreateAsset(mesh, path);
                if (autoCleanup) SafeDeleteAllObjects();
                if (shouldPingDestination) EditorGUIUtility.PingObject(destination);
                currentTab = Tabs.Cleanup;
            } else
                Debug.LogError("Failed to combine meshes.");
        }
    }
}