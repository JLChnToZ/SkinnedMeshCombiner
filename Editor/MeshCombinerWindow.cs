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
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditorInternal;

namespace JLChnToZ.EditorExtensions {
    public class MeshCombinerWindow : EditorWindow {
        const string COMBINE_INFO = "Click combine to...\n" +
            "- Combine meshes while retains blendshapes and bones\n" +
            "- Merge sub meshes with same material into one\n" +
            "- Create extra bones for each non skinned mesh renderers\n" +
            "- Derefereneces unused bones (but not delete them)\n" +
            "- Bake (freeze state and then removes) selected blendshapes\n" +
            "- Save the combined mesh to a file\n" +
            "- Deactivates combined mesh renderer sources";
        const string COMBINE_BONE_INFO = "Select bones to merge upwards (to its parent in hierarchy).\n" +
            "You can hold shift to toggle/fold all children of a bone.";
        const string REMOVE_UNUSED_INFO = "Here are the dereferenced objects from the last combine operation.\n" +
            "You can use the one-click button to auto delete or hide them all, or handle them manually by yourself.\n" +
            "If it is an object from a prefab, it will be inactived and set to editor only.\n" +
            "Gray out objects means theres are references pointing to them and/or their children in hierarchy.\n" +
            "You can use Unity's undo function if you accidentally deleted something.";
        static readonly string[] tabNames = new[] { "Combine Meshes", "Combine Bones", "Cleanup" };
        int currentTab;
        Vector2 sourceListScrollPos, boneMergeScrollPos, unusedObjectScrollPos;
        List<Renderer> sources = new List<Renderer>();
        SkinnedMeshRenderer destination;
        BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices;
        bool mergeSubMeshes = true, autoCleanup = true;
        Dictionary<Renderer, (bool[], bool, string[])> bakeBlendShapeMap = new Dictionary<Renderer, (bool[], bool, string[])>();
        Dictionary<Transform, Transform> boneReamp = new Dictionary<Transform, Transform>();
        HashSet<Transform> rootTransforms = new HashSet<Transform>();
        Dictionary<Transform, HashSet<Renderer>> boneToRenderersMap = new Dictionary<Transform, HashSet<Renderer>>();
        Dictionary<Transform, bool> boneFolded = new Dictionary<Transform, bool>();
        HashSet<Transform> bonesToMergeUpwards = new HashSet<Transform>();
        HashSet<Transform> unusedTransforms = new HashSet<Transform>();
        HashSet<Transform> safeDeleteTransforms = new HashSet<Transform>();
        ReorderableList sourceList;

        [MenuItem("JLChnToZ/Tools/Skinned Mesh Combiner")]
        public static void ShowWindow() => GetWindow<MeshCombinerWindow>("Skinned Mesh Combiner").Show(true);

        protected virtual void OnEnable() {
            sourceList = new ReorderableList(sources, typeof(Renderer), true, true, true, true) {
                drawElementCallback = OnListDrawElement,
                elementHeightCallback = OnListGetElementHeight,
                drawHeaderCallback = OnListDrawHeader,
                onAddCallback = OnListAdd,
                onRemoveCallback = OnListRemove,
                drawNoneElementCallback = OnListDrawNoneElement,
            };
            switch (currentTab) {
                case 0: RefreshCombineMeshOptions(); break;
                case 1: RefreshBones(); break;
                case 2: UpdateSafeDeleteObjects(); break;
            }
        }

        protected virtual void OnGUI() {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            currentTab = GUILayout.Toolbar(currentTab, tabNames, GUILayout.ExpandWidth(true));
            bool tabChanged = EditorGUI.EndChangeCheck();
            EditorGUILayout.EndHorizontal();
            switch (currentTab) {
                case 0: 
                    if (tabChanged) RefreshCombineMeshOptions();
                    DrawCombineMeshTab();
                    break;
                case 1:
                    if (tabChanged) RefreshBones();
                    DrawCombineBoneTab();
                    break;
                case 2:
                    if (tabChanged) UpdateSafeDeleteObjects();
                    DrawUnusedObjectsTab();
                    break;
            }
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
                destination = null;
                currentTab = 0;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(COMBINE_INFO, MessageType.Info);
        }

        void DrawCombineMeshTab() {
            sourceListScrollPos = EditorGUILayout.BeginScrollView(sourceListScrollPos);
            sourceList.DoLayoutList();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            destination = EditorGUILayout.ObjectField("Destination", destination, typeof(SkinnedMeshRenderer), true) as SkinnedMeshRenderer;
            if (destination == null && GUILayout.Button("Auto Create", GUILayout.ExpandWidth(false))) {
                destination = AutoCreateSkinnedMeshRenderer();
                EditorGUIUtility.PingObject(destination);
            }
            EditorGUILayout.EndHorizontal();
            blendShapeCopyMode = (BlendShapeCopyMode)EditorGUILayout.EnumFlagsField("Blend Shape Copy Mode", blendShapeCopyMode);
            mergeSubMeshes = EditorGUILayout.ToggleLeft("Merge Sub Meshes With Same Material", mergeSubMeshes);
        }

        void DrawCombineBoneTab() {
            EditorGUILayout.HelpBox(COMBINE_BONE_INFO, MessageType.Info);
            boneMergeScrollPos = EditorGUILayout.BeginScrollView(boneMergeScrollPos);
            var drawStack = new Stack<(Transform, int)>();
            foreach (var transform in rootTransforms)
                drawStack.Push((transform, 0));
            bool mergeChildrenState = false;
            int foldChildrenDepth = -1, toggleChildrenDepth = -1;
            while (drawStack.Count > 0) {
                var (transform, depth) = drawStack.Pop();
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(depth * 12);
                bool folded = false;
                if (transform == null) {
                    EditorGUILayout.LabelField("Has other children");
                } else {
                    boneFolded.TryGetValue(transform, out folded);
                    var isMerge = bonesToMergeUpwards.Contains(transform);
                    EditorGUI.BeginChangeCheck();
                    folded = GUILayout.Toggle(folded, GUIContent.none, EditorStyles.foldout, GUILayout.ExpandWidth(false));
                    if (EditorGUI.EndChangeCheck()) {
                        boneFolded[transform] = folded;
                        if (Event.current.shift) {
                            if (folded)
                                foldChildrenDepth = depth;
                            else foreach (var child in transform.GetComponentsInChildren<Transform>(true))
                                if (boneToRenderersMap.ContainsKey(child))
                                    boneFolded[child] = folded;
                        }
                    } else if (foldChildrenDepth >= 0) {
                        if (foldChildrenDepth >= depth)
                            foldChildrenDepth = -1;
                        else
                            boneFolded[transform] = folded = true;
                    }
                    EditorGUI.BeginChangeCheck();
                    isMerge = GUILayout.Toggle(isMerge, EditorGUIUtility.ObjectContent(transform, typeof(Transform)), GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.ExpandWidth(false));
                    if (EditorGUI.EndChangeCheck()) {
                        if (isMerge)
                            bonesToMergeUpwards.Add(transform);
                        else
                            bonesToMergeUpwards.Remove(transform);
                        if (Event.current.shift) {
                            if (folded) {
                                mergeChildrenState = isMerge;
                                toggleChildrenDepth = depth;
                            } else if (isMerge)
                                bonesToMergeUpwards.UnionWith(transform.GetComponentsInChildren<Transform>(true).Where(boneToRenderersMap.ContainsKey));
                            else
                                bonesToMergeUpwards.ExceptWith(transform.GetComponentsInChildren<Transform>(true));
                        }
                        RefreshBones();
                    } else if (toggleChildrenDepth >= 0) {
                        if (toggleChildrenDepth >= depth)
                            toggleChildrenDepth = -1;
                        else if (mergeChildrenState)
                            bonesToMergeUpwards.Add(transform);
                        else
                            bonesToMergeUpwards.Remove(transform);
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Locate", EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false)))
                        EditorGUIUtility.PingObject(transform);
                    if (GUILayout.Button("Select", EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false)))
                        Selection.activeTransform = transform;
                }
                EditorGUILayout.EndHorizontal();
                if (transform != null && folded) {
                    int childCount = transform.childCount;
                    bool hasChild = false;
                    for (int i = childCount - 1; i >= 0; i--) {
                        var child = transform.GetChild(i);
                        if (boneToRenderersMap.ContainsKey(child)) {
                            drawStack.Push((child, depth + 1));
                            hasChild = true;
                        }
                    }
                    if (childCount > 0 && !hasChild)
                        drawStack.Push((null, depth + 1));
                }
            }
            EditorGUILayout.EndScrollView();
        }

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

        SkinnedMeshRenderer AutoCreateSkinnedMeshRenderer() {
            Transform commonParent = null;
            foreach (var source in sources) {
                if (source == null) continue;
                if (commonParent == null) {
                    commonParent = source.transform;
                    continue;
                }
                var parent = source.transform;
                while (parent != null) {
                    if (parent == commonParent || parent.IsChildOf(commonParent))
                        break;
                    if (commonParent.IsChildOf(parent)) {
                        commonParent = parent;
                        break;
                    }
                    parent = parent.parent;
                }
            }
            var newChild = new GameObject("Combined Mesh", typeof(SkinnedMeshRenderer));
            if (commonParent != null) newChild.transform.SetParent(commonParent, false);
            Undo.RegisterCreatedObjectUndo(newChild, "Auto Create Skinned Mesh Renderer");
            return newChild.GetComponent<SkinnedMeshRenderer>();
        }

        void UpdateSafeDeleteObjects() {
            safeDeleteTransforms.Clear();
            int count = unusedTransforms.Count;
            if (count == 0) return;
            var sceneObjects = new HashSet<Component>(
                unusedTransforms.First().gameObject.scene.GetRootGameObjects()
                .SelectMany(x => x.GetComponentsInChildren<Component>(true))
                .Where(x => x != null && !(x is Transform) && !unusedTransforms.Contains(x.transform))
            );
            var checkObjects = new HashSet<UnityEngine.Object>();
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
                    checkObjects.UnionWith(transform.GetComponents<Component>());
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

        static void OnListDrawHeader(Rect rect) => EditorGUI.LabelField(rect, "(Skinned) Mesh Renderers to Combine");
        
        void OnListDrawElement(Rect rect, int index, bool isActive, bool isFocused) {
            var renderer = sources[index];
            rect.height = EditorGUIUtility.singleLineHeight;
            var rect2 = rect;
            rect2.xMin += 12;
            rect2.xMax -= 16;
            EditorGUI.LabelField(rect2, renderer == null ? new GUIContent("(Missing)") : EditorGUIUtility.ObjectContent(renderer, renderer.GetType()));
            rect2.x = rect.x;
            rect2.width = 12;
            if (!bakeBlendShapeMap.TryGetValue(renderer, out var bakeBlendShapeToggles)) return;
            var (toggles, toggleState, blendShapeNameMap) = bakeBlendShapeToggles;
            if (toggles == null || blendShapeNameMap == null) return;
            EditorGUI.BeginChangeCheck();
            if (blendShapeNameMap.Length > 0) toggleState = EditorGUI.Foldout(rect2, toggleState, GUIContent.none);
            if (EditorGUI.EndChangeCheck()) bakeBlendShapeMap[renderer] = (toggles, toggleState, blendShapeNameMap);
            if (!toggleState) return;
            rect2.width = rect.width;
            rect2.y += EditorGUIUtility.singleLineHeight;
            if (renderer is SkinnedMeshRenderer) {
                bool state = false, isMixed = false;
                for (var i = 0; i < toggles.Length; i++) {
                    toggles[i] = EditorGUI.ToggleLeft(rect2, $"Bake blendshape {blendShapeNameMap[i]}", toggles[i]);
                    rect2.y += EditorGUIUtility.singleLineHeight;
                    if (i == 0)
                        state = toggles[i];
                    else if (toggles[i] != state)
                        isMixed = true;
                }
                rect2.y = rect.y;
                rect2.x = rect.xMax - 16;
                rect2.width = 16;
                EditorGUI.BeginChangeCheck();
                EditorGUI.showMixedValue = isMixed;
                state = EditorGUI.Toggle(rect2, state);
                EditorGUI.showMixedValue = false;
                if (EditorGUI.EndChangeCheck())
                    for (var i = 0; i < toggles.Length; i++)
                        toggles[i] = state;
            } else if (renderer is MeshRenderer)
                toggles[0] = EditorGUI.ToggleLeft(rect2, "Don't create bone for this mesh renderer.", toggles[0]);
        }

        float OnListGetElementHeight(int index) {
            if (bakeBlendShapeMap.TryGetValue(sources[index], out var bakeBlendShapeToggles)) {
                var (toggles, toggleState, blendShapeNameMap) = bakeBlendShapeToggles;
                if (toggles != null && blendShapeNameMap != null && toggleState)
                    return EditorGUIUtility.singleLineHeight * (blendShapeNameMap.Length + 1);
            }
            return EditorGUIUtility.singleLineHeight;
        }

        void OnListAdd(ReorderableList list) {
            var count = sources.Count;
            sources.AddRange(Selection.GetFiltered<Renderer>(SelectionMode.Deep).Where(r => r != null && !sources.Contains(r)));
            if (count == sources.Count) return;
            sourceList.index = sources.Count - 1;
            RefreshCombineMeshOptions();
        }

        void OnListRemove(ReorderableList list) {
            sources.RemoveAt(list.index);
            RefreshCombineMeshOptions();
        }

        private void OnListDrawNoneElement(Rect rect) =>
            EditorGUI.LabelField(rect, "Click \"+\" button to add selected (skinned) mesh renderer to combine.");

        void RefreshCombineMeshOptions() {
            var oldSources = new HashSet<Renderer>(bakeBlendShapeMap.Keys);
            foreach (var source in sources) {
                Mesh mesh = null;
                var skinnedMeshRenderer = source as SkinnedMeshRenderer;
                if (skinnedMeshRenderer != null)
                    mesh = skinnedMeshRenderer.sharedMesh;
                else {
                    var meshRenderer = source as MeshRenderer;
                    if (meshRenderer != null && source.TryGetComponent(out MeshFilter meshFilter))
                        mesh = meshFilter.sharedMesh;
                }
                if (mesh == null) continue;
                int length = skinnedMeshRenderer != null ? mesh.blendShapeCount : 1;
                if (bakeBlendShapeMap.TryGetValue(source, out var bakeBlendShapeToggles)) {
                    if (bakeBlendShapeToggles.Item1.Length != length)
                        bakeBlendShapeToggles.Item1 = new bool[length];
                    bakeBlendShapeToggles.Item3 = skinnedMeshRenderer != null ? GetBlendShapeNames(mesh) : new string[1];
                } else {
                    bakeBlendShapeToggles = (
                        new bool[length], false,
                        skinnedMeshRenderer != null ? GetBlendShapeNames(mesh) : new string[1]
                    );
                }
                bakeBlendShapeMap[source] = bakeBlendShapeToggles;
                oldSources.Remove(source);
            }
            foreach (var source in oldSources) bakeBlendShapeMap.Remove(source);
        }

        void RefreshBones() {
            rootTransforms.Clear();
            boneToRenderersMap.Clear();
            foreach (var source in sources) {
                if (source is SkinnedMeshRenderer skinnedMeshRenderer) {
                    var bones = skinnedMeshRenderer.bones;
                    if (bones == null || bones.Length == 0) continue;
                    foreach (var bone in bones) {
                        if (bone == null) continue;
                        var parent = bone;
                        while (true) {
                            LazyInitialize(boneToRenderersMap, parent, out var renderers);
                            if (parent == bone) renderers.Add(source);
                            if (parent.parent == null) {
                                rootTransforms.Add(parent);
                                break;
                            }
                            parent = parent.parent;
                        }
                    }
                } else if (source is MeshRenderer meshRenderer) {
                    var parent = meshRenderer.transform;
                    while (true) {
                        LazyInitialize(boneToRenderersMap, parent, out var renderers);
                        if (parent == meshRenderer.transform) renderers.Add(source);
                        if (parent.parent == null) {
                            rootTransforms.Add(parent);
                            break;
                        }
                        parent = parent.parent;
                    }
                }
            }
        }

        static string[] GetBlendShapeNames(Mesh mesh) => Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();

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
                destination = AutoCreateSkinnedMeshRenderer();
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
            var mesh = Combine(sources.Select(source => {
                if (bakeBlendShapeMap.TryGetValue(source, out var bakeBlendShapeToggles))
                    return (source, bakeBlendShapeToggles.Item1);
                return (source, null);
            }).ToArray(), destination, mergeSubMeshes, blendShapeCopyMode, boneReamp);
            if (mesh != null) {
                mesh.Optimize();
                unusedTransforms.ExceptWith(destination.bones);
                unusedTransforms.Remove(destination.transform);
                var path = EditorUtility.SaveFilePanelInProject("Save Mesh", mesh.name, "asset", "Save Combined Mesh", saveAssetDefaultPath);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.CreateAsset(mesh, path);
                if (autoCleanup) SafeDeleteAllObjects();
                if (shouldPingDestination) EditorGUIUtility.PingObject(destination);
                currentTab = 2;
            } else
                Debug.LogError("Failed to combine meshes.");
        }

        public static Mesh Combine(
            ICollection<(Renderer, bool[])> sources, 
            SkinnedMeshRenderer destination,
            bool mergeSubMeshes = true,
            BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices,
            IDictionary<Transform, Transform> boneRemap = null
        ) {
            if (sources.Count == 0) return null;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Combine Meshes");
            int undoGroup = Undo.GetCurrentGroup();
            var combineInstances = new Dictionary<Material, List<(CombineInstance, bool[])>>(sources.Count);
            var boneWeights = new Dictionary<(Mesh, int), IEnumerable<BoneWeight>>(sources.Count);
            var bindposeMap = new Dictionary<(Transform, Matrix4x4), int>();
            var bindposes = new List<Matrix4x4>();
            var allBindposes = new List<Matrix4x4>();
            var allBones = new List<Transform>();
            var boneHasWeights = new HashSet<int>();
            var boneMapping = new Dictionary<int, int>();
            var materials = new List<Material>(sources.Count);
            var bakeList = new HashSet<(Renderer, int)>();
            Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache = null, vntArrayCache2 = null;
            Dictionary<string, BlendShapeTimeLine> blendShapesStore = null;
            var blendShapesWeights = new Dictionary<string, float>();
            var vertices = blendShapeCopyMode.HasFlag(BlendShapeCopyMode.Vertices) ? new List<Vector3>() : null;
            var normals = blendShapeCopyMode.HasFlag(BlendShapeCopyMode.Normals) ? new List<Vector3>() : null;
            var tangents = blendShapeCopyMode.HasFlag(BlendShapeCopyMode.Tangents) ? new List<Vector4>() : null;
            foreach (var (source, bakeFlags) in sources) {
                if (source == null) continue;
                var sourceTransform = source.transform;
                if (source is SkinnedMeshRenderer skinnedMeshRenderer) {
                    var orgMesh = skinnedMeshRenderer.sharedMesh;
                    var mesh = Instantiate(orgMesh);
                    var sharedMaterials = skinnedMeshRenderer.sharedMaterials;
                    var bones = skinnedMeshRenderer.bones;
                    mesh.GetBindposes(bindposes);
                    var weights = mesh.boneWeights;
                    PreAllocate(allBones, bones.Length);
                    PreAllocate(allBindposes, bindposes.Count);
                    boneMapping.Clear();
                    boneHasWeights.Clear();
                    foreach (var weight in weights) {
                        if (weight.weight0 > 0) boneHasWeights.Add(weight.boneIndex0);
                        if (weight.weight1 > 0) boneHasWeights.Add(weight.boneIndex1);
                        if (weight.weight2 > 0) boneHasWeights.Add(weight.boneIndex2);
                        if (weight.weight3 > 0) boneHasWeights.Add(weight.boneIndex3);
                    }
                    for (int i = 0; i < bindposes.Count; i++) {
                        if (bones[i] == null || !boneHasWeights.Contains(i)) continue;
                        var targetBone = bones[i];
                        var poseMatrix = bindposes[i];
                        if (boneRemap != null && boneRemap.TryGetValue(targetBone, out var bone)) {
                            poseMatrix = bone.worldToLocalMatrix * targetBone.localToWorldMatrix * poseMatrix;
                            targetBone = bone;
                        }
                        var key = (targetBone, poseMatrix);
                        if (!bindposeMap.TryGetValue(key, out var index)) {
                            bindposeMap[key] = index = bindposeMap.Count;
                            allBones.Add(targetBone);
                            allBindposes.Add(poseMatrix);
                        }
                        boneMapping[i] = index;
                    }
                    for (int i = 0, newIndex; i < weights.Length; i++) {
                        var weight = weights[i];
                        if (boneMapping.TryGetValue(weight.boneIndex0, out newIndex)) weight.boneIndex0 = newIndex;
                        else { weight.boneIndex0 = 0; weight.weight0 = 0; }
                        if (boneMapping.TryGetValue(weight.boneIndex1, out newIndex)) weight.boneIndex1 = newIndex;
                        else { weight.boneIndex1 = 0; weight.weight1 = 0; }
                        if (boneMapping.TryGetValue(weight.boneIndex2, out newIndex)) weight.boneIndex2 = newIndex;
                        else { weight.boneIndex2 = 0; weight.weight2 = 0; }
                        if (boneMapping.TryGetValue(weight.boneIndex3, out newIndex)) weight.boneIndex3 = newIndex;
                        else { weight.boneIndex3 = 0; weight.weight3 = 0; }
                        weights[i] = weight;
                    }
                    var subMeshCount = mesh.subMeshCount;
                    for (int i = 0; i < subMeshCount; i++) {
                        if (LazyInitialize(combineInstances, sharedMaterials[i], out var combines))
                            materials.Add(sharedMaterials[i]);
                        combines.Add((new CombineInstance { mesh = mesh, subMeshIndex = i, transform = Matrix4x4.identity }, bakeFlags));
                        var subMesh = mesh.GetSubMesh(i);
                        boneWeights[(mesh, i)] = new ArraySegment<BoneWeight>(weights, subMesh.firstVertex, subMesh.vertexCount);
                    }
                    if (vertices != null) mesh.GetVertices(vertices);
                    if (normals != null) mesh.GetNormals(normals);
                    if (tangents != null) mesh.GetTangents(tangents);
                    bool hasApplyBlendShape = false;
                    for (int i = 0, count = mesh.blendShapeCount; i < count; i++)
                        if (bakeFlags[i]) {
                            ApplyBlendShape(
                                mesh, vertices, normals, tangents, i,
                                skinnedMeshRenderer.GetBlendShapeWeight(i),
                                blendShapeCopyMode, ref vntArrayCache, ref vntArrayCache2
                            );
                            bakeList.Add((source, i));
                            hasApplyBlendShape = true;
                        } else
                            blendShapesWeights[mesh.GetBlendShapeName(i)] = skinnedMeshRenderer.GetBlendShapeWeight(i);
                    if (hasApplyBlendShape) {
                        if (vertices != null) mesh.SetVertices(vertices);
                        if (normals != null) mesh.SetNormals(normals);
                        if (tangents != null) mesh.SetTangents(tangents);
                        mesh.UploadMeshData(false);
                    }
                    if (source != destination) {
                        Undo.RecordObject(source, "Combine Meshes");
                        source.enabled = false;
                    }
                } else if (source is MeshRenderer meshRenderer && source.TryGetComponent(out MeshFilter meshFilter)) {
                    var mesh = Instantiate(meshFilter.sharedMesh);
                    var sharedMaterials = meshRenderer.sharedMaterials;
                    var subMeshCount = mesh.subMeshCount;
                    int index = 0;
                    if (!bakeFlags[0]) {
                        var key = (sourceTransform, Matrix4x4.identity);
                        if (!bindposeMap.TryGetValue(key, out index)) {
                            bindposeMap[key] = index = bindposeMap.Count;
                            allBindposes.Add(Matrix4x4.identity);
                            allBones.Add(sourceTransform);
                        }
                    }
                    var bakeFlags2 = Array.ConvertAll(bakeFlags, x => true);
                    var transform = bakeFlags[0] ? sourceTransform.localToWorldMatrix * destination.transform.worldToLocalMatrix : Matrix4x4.identity;
                    for (int i = 0; i < subMeshCount; i++) {
                        if (LazyInitialize(combineInstances, sharedMaterials[i], out var combines))
                            materials.Add(sharedMaterials[i]);
                        combines.Add((new CombineInstance { mesh = mesh, subMeshIndex = i, transform = transform }, bakeFlags2));
                        boneWeights[(mesh, i)] = Enumerable.Repeat(bakeFlags[0] ? default : new BoneWeight { boneIndex0 = index, weight0 = 1 }, mesh.GetSubMesh(i).vertexCount);
                    }
                    if (source != destination) {
                        Undo.RecordObject(source, "Combine Meshes");
                        source.enabled = false;
                    }
                }
            }
            if (combineInstances.Count < 1) return null;
            var bindPoseArray = allBindposes.ToArray();
            // 1st pass: merge meshes with same material.
            if (mergeSubMeshes)
                foreach (var kv in combineInstances) {
                    var combines = kv.Value;
                    if (combines.Count < 2) continue;
                    var mesh = new Mesh();
                    var combineArray = combines.Select(entry => entry.Item1).ToArray();
                    mesh.CombineMeshes(combineArray, true, false);
                    boneWeights[(mesh, 0)] = combineArray.SelectMany(entry => boneWeights[(entry.mesh, entry.subMeshIndex)]).ToArray();
                    if (blendShapeCopyMode != BlendShapeCopyMode.None)
                        CopyBlendShapes(mesh, combines, blendShapeCopyMode, ref blendShapesStore, ref vntArrayCache, ref vntArrayCache2);
                    combines.Clear();
                    combines.Add((new CombineInstance { mesh = mesh, transform = Matrix4x4.identity }, new bool[mesh.blendShapeCount]));
                }
            var combinedNewMesh = new Mesh { name = $"{destination.name} Combined Mesh" };
            var combineInstanceArray = materials.SelectMany(material => combineInstances[material]).ToArray();
            combinedNewMesh.CombineMeshes(combineInstanceArray.Select(entry => entry.Item1).ToArray(), false, false);
            combinedNewMesh.boneWeights = combineInstanceArray.Select(entry => {
                boneWeights.TryGetValue((entry.Item1.mesh, entry.Item1.subMeshIndex), out var weights);
                return weights;
            }).Where(x => x != null).SelectMany(x => x).ToArray();
            combinedNewMesh.bindposes = bindPoseArray;
            if (blendShapeCopyMode != BlendShapeCopyMode.None)
                CopyBlendShapes(combinedNewMesh, combineInstanceArray, blendShapeCopyMode, ref blendShapesStore, ref vntArrayCache, ref vntArrayCache2);
            foreach (var combines in combineInstances.Values) combines.Clear();
            combinedNewMesh.RecalculateBounds();
            combinedNewMesh.UploadMeshData(false);
            Undo.RecordObject(destination, "Combine Meshes");
            destination.sharedMesh = combinedNewMesh;
            destination.localBounds = combinedNewMesh.bounds;
            destination.sharedMaterials = materials.ToArray();
            destination.bones = allBones.ToArray();
            if (destination.rootBone == null) destination.rootBone = allBones.Count > 0 ? allBones[0] : destination.transform;
            foreach (var kv in blendShapesWeights) {
                var index = combinedNewMesh.GetBlendShapeIndex(kv.Key);
                if (index >= 0) destination.SetBlendShapeWeight(index, kv.Value);
            }
            foreach (var (mesh, _) in boneWeights.Keys)
                if (mesh != null) DestroyImmediate(mesh, false);
            Undo.CollapseUndoOperations(undoGroup);
            return combinedNewMesh;
        }

        public static void CopyBlendShapes(
            Mesh combinedNewMesh, 
            IEnumerable<(CombineInstance, bool[])> combineInstances,
            BlendShapeCopyMode copyMode = BlendShapeCopyMode.Vertices
        ) {
            Dictionary<string, BlendShapeTimeLine> blendShapesStore = null;
            Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache = null;
            Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache2 = null;
            CopyBlendShapes(combinedNewMesh, combineInstances, copyMode, ref blendShapesStore, ref vntArrayCache, ref vntArrayCache2);
        }

        static void CopyBlendShapes(
            Mesh combinedNewMesh,
            IEnumerable<(CombineInstance, bool[])> combineInstances,
            BlendShapeCopyMode copyMode,
            ref Dictionary<string, BlendShapeTimeLine> blendShapesStore,
            ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache,
            ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache2
        ) {
            if (!LazyInitialize(ref blendShapesStore)) blendShapesStore.Clear();
            int offset = 0;
            foreach (var (entry, bakeFlags) in combineInstances) {
                var mesh = entry.mesh;
                var subMeshIndex = entry.subMeshIndex;
                var subMesh = mesh.GetSubMesh(subMeshIndex);
                for (int k = 0; k < mesh.blendShapeCount; k++) {
                    if (bakeFlags[k]) continue;
                    string key = mesh.GetBlendShapeName(k);
                    LazyInitialize(blendShapesStore, key, out var timeline);
                    timeline.AddFrom(mesh, subMeshIndex, k, offset);
                }
                offset += subMesh.vertexCount;
            }
            foreach (var timeline in blendShapesStore) timeline.Value.ApplyTo(combinedNewMesh, timeline.Key, copyMode, ref vntArrayCache, ref vntArrayCache2);
        }

        static bool LazyInitialize<TKey, TValue>(IDictionary<TKey, TValue> dict, TKey key, out TValue value) where TValue : new() {
            if (!dict.TryGetValue(key, out value)) {
                dict[key] = value = new TValue();
                return true;
            }
            return false;
        }

        static bool LazyInitialize<T>(ref T value) where T : new() {
            if (value == null) {
                value = new T();
                return true;
            }
            return false;
        }

        static void PreAllocate<T>(List<T> list, int capacity) {
            capacity += list.Count;
            if (list.Capacity < capacity) list.Capacity = capacity;
        }
        

        static (Vector3[], Vector3[], Vector3[]) GetVNTArrays(
            ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache,
            int vertexCount, BlendShapeCopyMode copyMode
        ) {
            LazyInitialize(ref vntArrayCache);
            if (!vntArrayCache.TryGetValue(vertexCount, out var vntArray))
                vntArrayCache[vertexCount] = vntArray = (
                    copyMode.HasFlag(BlendShapeCopyMode.Vertices) ? new Vector3[vertexCount] : null,
                    copyMode.HasFlag(BlendShapeCopyMode.Normals) ? new Vector3[vertexCount] : null,
                    copyMode.HasFlag(BlendShapeCopyMode.Tangents) ? new Vector3[vertexCount] : null
                );
            return vntArray;
        }

        static void LerpVNTArray(
            Vector3[] prev, int prevIndex, Vector3[] next, int nextIndex, Vector3[] dest, int destIndex,
            float weight
        ) {
            if (prev != null && next != null && dest != null)
                dest[destIndex] = Vector3.Lerp(prev[prevIndex], next[nextIndex], weight);
        }

        static void CopyVNTArrays(
            (Vector3[], Vector3[], Vector3[], float) frameData, (SubMeshDescriptor, int, int) subMeshData, 
            Vector3[] destDeltaVertices, Vector3[] destDeltaNormals, Vector3[] destDeltaTangents
        ) {
            var (deltaVertices, deltaNormals, deltaTangents, _) = frameData;
            var (subMesh, _, destOffset) = subMeshData;
            var srcOffset = subMesh.firstVertex;
            var srcVertexCount = subMesh.vertexCount;
            if (deltaVertices != null && destDeltaVertices != null) Array.Copy(deltaVertices, srcOffset, destDeltaVertices, destOffset, srcVertexCount);
            if (deltaNormals != null && destDeltaNormals != null) Array.Copy(deltaNormals, srcOffset, destDeltaNormals, destOffset, srcVertexCount);
            if (deltaTangents != null && destDeltaTangents != null) Array.Copy(deltaTangents, srcOffset, destDeltaTangents, destOffset, srcVertexCount);
        }

        static void ApplyBlendShape(List<Vector3> source, Vector3[] blendShapeDataPrev, Vector3[] blendShapeDataNext, float lerp, int offset, int count) {
            if (source == null) return;
            for (int i = 0; i < count; i++) {
                var index = offset + i;
                source[index] += blendShapeDataPrev == null ? blendShapeDataNext[index] * lerp :
                    blendShapeDataNext == null || lerp <= 0 ? blendShapeDataPrev[index] :
                    Vector3.LerpUnclamped(blendShapeDataPrev[index], blendShapeDataNext[index], lerp);
            }
        }

        static void ApplyBlendShape(List<Vector4> source, Vector3[] blendShapeDataPrev, Vector3[] blendShapeDataNext, float lerp, int offset, int count) {
            if (source == null) return;
            for (int i = 0; i < count; i++) {
                var index = offset + i;
                source[index] += (Vector4)(blendShapeDataPrev == null ? blendShapeDataNext[index] * lerp :
                    blendShapeDataNext == null || lerp <= 0 ? blendShapeDataPrev[index] :
                    Vector3.LerpUnclamped(blendShapeDataPrev[index], blendShapeDataNext[index], lerp));
            }
        }

        static void ApplyBlendShape(
            Mesh mesh, List<Vector3> vertices, List<Vector3> normals, List<Vector4> tangents,
            int blendShapeIndex, float weight, BlendShapeCopyMode copyMode,
            ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache,
            ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache2
        ) {
            var vertexCount = mesh.vertexCount;
            var vntArray = GetVNTArrays(ref vntArrayCache, vertices.Count, copyMode);
            var (deltaVertices, deltaNormals, deltaTangents) = vntArray;
            int count = mesh.GetBlendShapeFrameCount(blendShapeIndex);
            if (count == 0) return;
            float frameWeight;
            for (int i = 1; i < count; i++) {
                frameWeight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, i);
                if (frameWeight > weight) {
                    mesh.GetBlendShapeFrameVertices(blendShapeIndex, i - 1, deltaVertices, deltaNormals, deltaTangents);
                    var vntArray2 = GetVNTArrays(ref vntArrayCache2, vertices.Count, copyMode);
                    var (deltaVertices2, deltaNormals2, deltaTangents2) = vntArray2;
                    mesh.GetBlendShapeFrameVertices(blendShapeIndex, i, deltaVertices2, deltaNormals2, deltaTangents2);
                    var nextFrameWeight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, i);
                    var lerp = Mathf.InverseLerp(frameWeight, nextFrameWeight, weight);
                    ApplyBlendShape(vertices, deltaVertices, deltaVertices2, lerp, 0, vertexCount);
                    ApplyBlendShape(normals, deltaNormals, deltaNormals2, lerp, 0, vertexCount);
                    ApplyBlendShape(tangents, deltaTangents, deltaTangents2, lerp, 0, vertexCount);
                    return;
                }
            }
            frameWeight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, count - 1);
            mesh.GetBlendShapeFrameVertices(blendShapeIndex, count - 1, deltaVertices, deltaNormals, deltaTangents);
            weight /= frameWeight;
            ApplyBlendShape(vertices, null, deltaVertices, weight, 0, vertexCount);
            ApplyBlendShape(normals, null, deltaNormals, weight, 0, vertexCount);
            ApplyBlendShape(tangents, null, deltaTangents, weight, 0, vertexCount);
        }

        class BlendShapeTimeLine {
            readonly Dictionary<float, Dictionary<(Mesh, int), int>> frames = new Dictionary<float, Dictionary<(Mesh, int), int>>();
            readonly Dictionary<(Mesh, int), (SubMeshDescriptor, int, int)> subMeshes = new Dictionary<(Mesh, int), (SubMeshDescriptor, int, int)>();

            public void AddFrom(Mesh mesh, int subMeshIndex, int blendShapeIndex, int destOffset) {
                var subMeshKey = (mesh, subMeshIndex);
                if (subMeshes.ContainsKey(subMeshKey)) return;
                var frameCount = mesh.GetBlendShapeFrameCount(blendShapeIndex);
                if (frameCount < 1) return;
                subMeshes[subMeshKey] = (mesh.GetSubMesh(subMeshIndex), blendShapeIndex, destOffset);
                for (int i = 0; i < frameCount; i++) {
                    var weight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, i);
                    LazyInitialize(frames, weight, out var frameIndexMap);
                    frameIndexMap[(mesh, subMeshIndex)] = i;
                }
            }

            public void ApplyTo(
                Mesh combinedMesh, string blendShapeName,
                BlendShapeCopyMode copyMode,
                ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache,
                ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache2
            ) {
                int destBlendShapeIndex = combinedMesh.GetBlendShapeIndex(blendShapeName);
                if (destBlendShapeIndex >= 0) {
                    Debug.LogWarning($"Blend shape {blendShapeName} already exists in the combined mesh. Skipping.");
                    return;
                }
                var destVertexCount = combinedMesh.vertexCount;
                var remainingMeshes = new HashSet<(Mesh, int)>();
                var weights = new float[frames.Count];
                frames.Keys.CopyTo(weights, 0);
                Array.Sort(weights);
                for (int i = 0; i < weights.Length; i++) {
                    var destDeltaVertices = copyMode.HasFlag(BlendShapeCopyMode.Vertices) ? new Vector3[destVertexCount] : null;
                    var destDeltaNormals = copyMode.HasFlag(BlendShapeCopyMode.Normals) ? new Vector3[destVertexCount] : null;
                    var destDeltaTangents = copyMode.HasFlag(BlendShapeCopyMode.Tangents) ? new Vector3[destVertexCount] : null;
                    var weight = weights[i];
                    var frameIndexMap = frames[weight];
                    remainingMeshes.UnionWith(subMeshes.Keys);
                    foreach (var kv in frameIndexMap) {
                        var subMeshKey = kv.Key;
                        var (srcMesh, _) = subMeshKey;
                        var srcSubMeshData = subMeshes[subMeshKey];
                        var (srcSubMesh, blendShapeIndex, _) = srcSubMeshData;
                        var (deltaVertices, deltaNormals, deltaTangents) = GetVNTArrays(ref vntArrayCache, srcMesh.vertexCount, copyMode);
                        srcMesh.GetBlendShapeFrameVertices(blendShapeIndex, kv.Value, deltaVertices, deltaNormals, deltaTangents);
                        CopyVNTArrays(
                            (deltaVertices, deltaNormals, deltaTangents, weight), srcSubMeshData,
                            destDeltaVertices, destDeltaNormals, destDeltaTangents
                        );
                        remainingMeshes.Remove(subMeshKey);
                    }
                    foreach (var key in remainingMeshes) {
                        var srcSubMeshData = subMeshes[key];
                        if (!SeekBlendShapeFrameData(key, srcSubMeshData, weights, i, false, copyMode, ref vntArrayCache, out var prevData)) {
                            if (SeekBlendShapeFrameData(key, srcSubMeshData, weights, i, true, copyMode, ref vntArrayCache, out prevData))
                                CopyVNTArrays(prevData, srcSubMeshData, destDeltaVertices, destDeltaNormals, destDeltaTangents);
                        } else if (!SeekBlendShapeFrameData(key, srcSubMeshData, weights, i, true, copyMode, ref vntArrayCache2, out var nextData)) {
                            if (SeekBlendShapeFrameData(key, srcSubMeshData, weights, i, false, copyMode, ref vntArrayCache, out nextData))
                                CopyVNTArrays(nextData, srcSubMeshData, destDeltaVertices, destDeltaNormals, destDeltaTangents);
                        } else {
                            var (srcSubMesh, _, destOffset) = srcSubMeshData;
                            var (prevDeltaVertices, prevDeltaNormals, prevDeltaTangents, prevWeight) = prevData;
                            var (nextDeltaVertices, nextDeltaNormals, nextDeltaTangents, nextWeight) = nextData;
                            float lerpWeight = Mathf.InverseLerp(prevWeight, nextWeight, weight);
                            for (int j = 0, srcOffset = srcSubMesh.firstVertex, srcVertexCount = srcSubMesh.vertexCount; j < srcVertexCount; j++) {
                                int srcOffset2 = srcOffset + j, destOffset2 = destOffset + j;
                                LerpVNTArray(prevDeltaVertices, srcOffset2, nextDeltaVertices, srcOffset2, destDeltaVertices, destOffset2, lerpWeight);
                                LerpVNTArray(prevDeltaNormals, srcOffset2, nextDeltaNormals, srcOffset2, destDeltaNormals, destOffset2, lerpWeight);
                                LerpVNTArray(prevDeltaTangents, srcOffset2, nextDeltaTangents, srcOffset2, destDeltaTangents, destOffset2, lerpWeight);
                            }
                        }
                    }
                    try {
                        combinedMesh.AddBlendShapeFrame(blendShapeName, weight, destDeltaVertices, destDeltaNormals, destDeltaTangents);
                    } catch (Exception e) {
                        Debug.LogError(e);
                    }
                }
                if (!copyMode.HasFlag(BlendShapeCopyMode.Normals)) combinedMesh.RecalculateNormals();
                if (!copyMode.HasFlag(BlendShapeCopyMode.Tangents)) combinedMesh.RecalculateTangents();
            }

            bool SeekBlendShapeFrameData(
                (Mesh, int) key, (SubMeshDescriptor, int, int) subMeshData,
                float[] weights, int weightIndex, bool seekAscending, BlendShapeCopyMode copyMode,
                ref Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache,
                out (Vector3[], Vector3[], Vector3[], float) result
            ) {
                while (true) {
                    if (seekAscending ? ++weightIndex >= weights.Length : --weightIndex < 0) {
                        result = default;
                        return false;
                    }
                    if (frames[weights[weightIndex]].TryGetValue(key, out var frameIndex)) {
                        var (srcMesh, _) = key;
                        var (_, blendShapeIndex, _) = subMeshData;
                        var (deltaVertices, deltaNormals, deltaTangents) = GetVNTArrays(ref vntArrayCache, srcMesh.vertexCount, copyMode);
                        srcMesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                        result = (deltaVertices, deltaNormals, deltaTangents, weights[weightIndex]);
                        return true;
                    }
                }
            }
        }

        [Flags]
        public enum BlendShapeCopyMode : byte {
            None = 0,
            Vertices = 0x1,
            Normals = 0x2,
            Tangents = 0x4,
        }
    }
}