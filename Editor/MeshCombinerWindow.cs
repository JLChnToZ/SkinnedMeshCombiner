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
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using UnityObject = UnityEngine.Object;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    using JLChnToZ.CommonUtils;
    using static Utils;
    
    public class MeshCombinerWindow : EditorWindow {
        const string COMBINE_INFO = "Click combine to...\n" +
            "- Combine meshes while retains blendshapes and bones\n" +
            "- Merge sub meshes with same material into one\n" +
            "- Create extra bones for each non skinned mesh renderers\n" +
            "- Derefereneces unused bones (but not delete them)\n" +
            "- Bake (freeze state and then removes) selected blendshapes\n" +
            "- Save the combined mesh to a file\n" +
            "- Deactivates combined mesh renderer sources";
        const string COMBINE_MESH_INFO = "Select (skinned) mesh renderer(s) and/or its parent, and click \"+\" button to add to the combine list.\n" +
            "Even it is not necessary in many cases, you may drag to change the processing order.\n" +
            "You can expand the options such as baking blendshapes by clicking on the arrow on the left.\n" +
            "You can use the checkbox on the right to toggle all options within the renderer while expanded the view.\n" +
            "It is recommend to bake the blendshapes that unlikely to be changed after build for better performance and smaller file size.\n" +
            "If the blendshape is a \"anti-penetration\" key for hiding skins under clothes, you can use the dropdown to select remove blendshape vertex to reduce triangles and vertices.";
        const string COMBINE_BONE_INFO = "Select bones to merge upwards (to its parent in hierarchy).\n" +
            "If a bone does not have weight on any mesh, it will be dereferenced regardless of selection.\n" +
            "You can hold shift to toggle/fold all children of a bone.";
        const string REMOVE_UNUSED_INFO = "Here are the dereferenced objects from the last combine operation.\n" +
            "You can use the one-click button to auto delete or hide them all, or handle them manually by yourself.\n" +
            "If it is an object from a prefab, it will be inactived and set to editor only.\n" +
            "Gray out objects means theres are references pointing to them and/or their children in hierarchy.\n" +
            "You can use Unity's undo function if you accidentally deleted something.";
        const string MESH_RENDERER_INFO = "The destination is an ordinary mesh renderer, all blend shapes and bones will enforced to combined and dereferenced.";
        static readonly string[] tabNames = new[] { "Combine Meshes", "Combine Bones", "Cleanup" };
        static GUIContent dropdownIcon;
        int currentTab;
        Vector2 sourceListScrollPos, boneMergeScrollPos, unusedObjectScrollPos;
        List<Renderer> sources = new List<Renderer>();
        Renderer destination;
        BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices;
        bool autoCleanup = true;
        CombineMeshFlags mergeFlags = CombineMeshFlags.MergeSubMeshes | CombineMeshFlags.RemoveMeshPortionsWithoutBones;
        SerializableDictionary<Renderer, BakeBlendShapeToggles> bakeBlendShapeMap = new SerializableDictionary<Renderer, BakeBlendShapeToggles>();
        SerializableDictionary<Transform, Transform> boneReamp = new SerializableDictionary<Transform, Transform>();
        SerializableSet<Transform> rootTransforms = new SerializableSet<Transform>();
        SerializableDictionary<Transform, SerializableSet<Renderer>> boneToRenderersMap = new SerializableDictionary<Transform, SerializableSet<Renderer>>();
        SerializableDictionary<Transform, bool> boneFolded = new SerializableDictionary<Transform, bool>();
        SerializableSet<Transform> bonesToMergeUpwards = new SerializableSet<Transform>();
        SerializableSet<Transform> unusedTransforms = new SerializableSet<Transform>();
        SerializableSet<Transform> safeDeleteTransforms = new SerializableSet<Transform>();
        ReorderableList sourceList;

        [MenuItem("JLChnToZ/Tools/Skinned Mesh Combiner")]
        public static void ShowWindow() => GetWindow<MeshCombinerWindow>("Skinned Mesh Combiner").Show(true);

        protected virtual void OnEnable() {
            sourceList = new ReorderableList(sources, typeof(Renderer), true, false, true, true) {
                drawElementCallback = OnListDrawElement,
                elementHeightCallback = OnListGetElementHeight,
                onAddCallback = OnListAdd,
                onRemoveCallback = OnListRemove,
                drawNoneElementCallback = OnListDrawNoneElement,
                headerHeight = 0,
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
                destination = null;
                currentTab = 0;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(COMBINE_INFO, MessageType.Info);
            HandleDrop();
        }

        void DrawCombineMeshTab() {
            EditorGUILayout.HelpBox(COMBINE_MESH_INFO, MessageType.Info);
            sourceListScrollPos = EditorGUILayout.BeginScrollView(sourceListScrollPos);
            sourceList.DoLayoutList();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var newDestination = EditorGUILayout.ObjectField("Destination", destination, typeof(Renderer), true) as Renderer;
            if (EditorGUI.EndChangeCheck() && (
                newDestination == null ||
                newDestination is SkinnedMeshRenderer ||
                (newDestination is MeshRenderer && newDestination.TryGetComponent(out MeshFilter _))))
                destination = newDestination;
            if (destination == null) {
                if (GUILayout.Button("Auto Create", EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false))) {
                    destination = AutoCreateRenderer<SkinnedMeshRenderer>();
                    EditorGUIUtility.PingObject(destination);
                }
                if (dropdownIcon == null) dropdownIcon = EditorGUIUtility.IconContent("d_icon dropdown");
                var dropdownButtonSize = EditorStyles.miniButtonRight.CalcSize(dropdownIcon);
                var dropdownButtonRect = GUILayoutUtility.GetRect(dropdownButtonSize.x, dropdownButtonSize.y, EditorStyles.miniButtonRight, GUILayout.ExpandWidth(false));
                if (GUI.Button(dropdownButtonRect, dropdownIcon, EditorStyles.miniButtonRight)) {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Skinned Mesh Renderer"), false, () => {
                        destination = AutoCreateRenderer<SkinnedMeshRenderer>();
                        EditorGUIUtility.PingObject(destination);
                    });
                    menu.AddItem(new GUIContent("Mesh Renderer"), false, () => {
                        destination = AutoCreateRenderer<MeshRenderer>();
                        EditorGUIUtility.PingObject(destination);
                    });
                    menu.DropDown(dropdownButtonRect);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.BeginDisabledGroup(destination is MeshRenderer);
            blendShapeCopyMode = (BlendShapeCopyMode)EditorGUILayout.EnumFlagsField("Blend Shape Copy Mode", blendShapeCopyMode);
            EditorGUI.EndDisabledGroup();
            mergeFlags = DrawFlag(mergeFlags, "Merge sub meshes with same material", CombineMeshFlags.MergeSubMeshes);
        }

        static CombineMeshFlags DrawFlag(CombineMeshFlags mergeFlags, string label, CombineMeshFlags flag) {
            EditorGUI.BeginChangeCheck();
            var state = EditorGUILayout.ToggleLeft(label, mergeFlags.HasFlag(flag));
            if (EditorGUI.EndChangeCheck()) {
                if (state) mergeFlags |= flag;
                else mergeFlags &= ~flag;
            }
            return mergeFlags;
        }

        static CombineMeshFlags DrawFlag(CombineMeshFlags mergeFlags, Rect position, string label, CombineMeshFlags flag) {
            EditorGUI.BeginChangeCheck();
            var state = EditorGUI.ToggleLeft(position, label, mergeFlags.HasFlag(flag));
            if (EditorGUI.EndChangeCheck()) {
                if (state) mergeFlags |= flag;
                else mergeFlags &= ~flag;
            }
            return mergeFlags;
        }

        void DrawCombineBoneTab() {
            EditorGUILayout.HelpBox(COMBINE_BONE_INFO, MessageType.Info);
            boneMergeScrollPos = EditorGUILayout.BeginScrollView(boneMergeScrollPos);
            bool isMeshRenderer = destination is MeshRenderer;
            bool isSkinnedMeshRenderer = destination is SkinnedMeshRenderer;
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
                    EditorGUI.BeginDisabledGroup(isMeshRenderer);
                    isMerge = GUILayout.Toggle(isMeshRenderer || isMerge,
                        EditorGUIUtility.ObjectContent(transform, typeof(Transform)),
                        GUILayout.Height(EditorGUIUtility.singleLineHeight),
                        GUILayout.ExpandWidth(false)
                    );
                    EditorGUI.EndDisabledGroup();
                    if (EditorGUI.EndChangeCheck() && isSkinnedMeshRenderer) {
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
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Auto Select Bones...", GUILayout.ExpandWidth(false));
            if (GUILayout.Button("With Same Name As Parent", GUILayout.ExpandWidth(false))) {
                SelectBonesWithSimilarNameAsParent(false);
                RefreshBones();
            }
            if (GUILayout.Button("Prefixed With Parent Name", GUILayout.ExpandWidth(false))) {
                SelectBonesWithSimilarNameAsParent(true);
                RefreshBones();
            }
            EditorGUILayout.EndHorizontal();
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
            } catch (System.Exception ex) {
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
        
        void OnListDrawElement(Rect rect, int index, bool isActive, bool isFocused) {
            bool isMeshRenderer = destination is MeshRenderer;
            var renderer = sources[index];
            rect.height = EditorGUIUtility.singleLineHeight;
            var rect2 = rect;
            rect2.xMin += 12;
            rect2.xMax -= 150;
            if (GUI.Button(rect2, renderer == null ? new GUIContent("(Missing)") : EditorGUIUtility.ObjectContent(renderer, renderer.GetType()), EditorStyles.label)) {
                EditorGUIUtility.PingObject(renderer);
                if (!isFocused) sourceList.index = index;
            }
            rect2.x = rect.x;
            rect2.width = 12;
            if (!bakeBlendShapeMap.TryGetValue(renderer, out var bakeBlendShapeToggles)) return;
            if (bakeBlendShapeToggles.blendShapeFlags == null || bakeBlendShapeToggles.blendShapeNames == null) return;
            EditorGUI.BeginChangeCheck();
            bakeBlendShapeToggles.toggleState = EditorGUI.Foldout(rect2, bakeBlendShapeToggles.toggleState, GUIContent.none);
            if (EditorGUI.EndChangeCheck()) bakeBlendShapeMap[renderer] = bakeBlendShapeToggles;
            if (!bakeBlendShapeToggles.toggleState) return;
            rect2.width = rect.width;
            rect2.y += EditorGUIUtility.singleLineHeight;
            if (renderer is SkinnedMeshRenderer) {
                var newFlags = bakeBlendShapeToggles.combineMeshFlags;
                newFlags = DrawFlag(newFlags, rect2, "Remove sub meshes without materials", CombineMeshFlags.RemoveSubMeshWithoutMaterials);
                rect2.y += EditorGUIUtility.singleLineHeight;
                newFlags = DrawFlag(newFlags, rect2, "Remove mesh portions with missing bones", CombineMeshFlags.RemoveMeshPortionsWithoutBones);
                rect2.y += EditorGUIUtility.singleLineHeight;
                newFlags = DrawFlag(newFlags, rect2, "Remove mesh portions with 0-scale bones", CombineMeshFlags.RemoveMeshPortionsWithZeroScaleBones);
                rect2.y += EditorGUIUtility.singleLineHeight;
                if (newFlags != bakeBlendShapeToggles.combineMeshFlags) {
                    bakeBlendShapeToggles.combineMeshFlags = newFlags;
                    bakeBlendShapeMap[renderer] = bakeBlendShapeToggles;
                }
                if (bakeBlendShapeToggles.blendShapeFlags.Length <= 0) return;
                bool state = false, isMixed = false, isMixed2 = false;
                var states2 = CombineBlendshapeFlags.None;
                for (var i = 0; i < bakeBlendShapeToggles.blendShapeFlags.Length; i++) {
                    bool currentState = isMeshRenderer || bakeBlendShapeToggles.blendShapeFlags[i] != CombineBlendshapeFlags.None;
                    rect2.width = rect.width - 128;
                    EditorGUI.BeginChangeCheck();
                    EditorGUI.BeginDisabledGroup(isMeshRenderer);
                    currentState = EditorGUI.ToggleLeft(rect2, $"Bake blend shape {bakeBlendShapeToggles.blendShapeNames[i]}", currentState);
                    EditorGUI.EndDisabledGroup();
                    if (EditorGUI.EndChangeCheck())
                        bakeBlendShapeToggles.blendShapeFlags[i] = currentState ? CombineBlendshapeFlags.CombineBlendShape : CombineBlendshapeFlags.None;
                    rect2.x = rect.width - 128;
                    rect2.width = 128;
                    EditorGUI.BeginChangeCheck();
                    var flag = bakeBlendShapeToggles.blendShapeFlags[i];
                    if (isMeshRenderer && flag == CombineBlendshapeFlags.None)
                        flag = CombineBlendshapeFlags.CombineBlendShape;
                    flag = (CombineBlendshapeFlags)EditorGUI.EnumPopup(rect2, flag);
                    if (EditorGUI.EndChangeCheck()) bakeBlendShapeToggles.blendShapeFlags[i] = flag;
                    rect2.y += EditorGUIUtility.singleLineHeight;
                    rect2.x = rect.x;
                    if (i == 0) {
                        state = currentState;
                        states2 = flag;
                    } else {
                        if (currentState != state)
                            isMixed = true;
                        if (flag != states2)
                            isMixed2 = true;
                    }
                }
                rect2.y = rect.y;
                rect2.x = rect.xMax - 150;
                rect2.width = 128;
                EditorGUI.BeginChangeCheck();
                EditorGUI.showMixedValue = isMixed2;
                states2 = (CombineBlendshapeFlags)EditorGUI.EnumPopup(rect2, states2);
                EditorGUI.showMixedValue = false;
                if (EditorGUI.EndChangeCheck())
                    for (var i = 0; i < bakeBlendShapeToggles.blendShapeFlags.Length; i++)
                        bakeBlendShapeToggles.blendShapeFlags[i] = states2;
                rect2.x = rect.xMax - 16;
                rect2.width = 16;
                EditorGUI.BeginChangeCheck();
                EditorGUI.BeginDisabledGroup(isMeshRenderer);
                EditorGUI.showMixedValue = isMixed;
                state = EditorGUI.Toggle(rect2, state);
                EditorGUI.showMixedValue = false;
                EditorGUI.EndDisabledGroup();
                if (EditorGUI.EndChangeCheck())
                    for (var i = 0; i < bakeBlendShapeToggles.blendShapeFlags.Length; i++)
                        bakeBlendShapeToggles.blendShapeFlags[i] = state ? CombineBlendshapeFlags.CombineBlendShape : CombineBlendshapeFlags.None;
            } else if (renderer is MeshRenderer) {
                var newFlags = bakeBlendShapeToggles.combineMeshFlags;
                EditorGUI.BeginChangeCheck();
                EditorGUI.BeginDisabledGroup(isMeshRenderer);
                if (isMeshRenderer) newFlags &= ~CombineMeshFlags.CreateBoneForNonSkinnedMesh;
                newFlags = DrawFlag(newFlags, rect2, "Create bone for this mesh renderer", CombineMeshFlags.CreateBoneForNonSkinnedMesh);
                EditorGUI.EndDisabledGroup();
                if (EditorGUI.EndChangeCheck()) {
                    bakeBlendShapeToggles.combineMeshFlags = newFlags;
                    bakeBlendShapeMap[renderer] = bakeBlendShapeToggles;
                }
            }
        }

        float OnListGetElementHeight(int index) {
            var source = sources[index];
            if (bakeBlendShapeMap.TryGetValue(source, out var bakeBlendShapeToggles)) {
                if (bakeBlendShapeToggles.toggleState)
                    return EditorGUIUtility.singleLineHeight * (
                        source is SkinnedMeshRenderer ?
                        bakeBlendShapeToggles.blendShapeFlags != null && bakeBlendShapeToggles.blendShapeNames != null ?
                        bakeBlendShapeToggles.blendShapeNames.Length + 4 : 4 : 2);
            }
            return EditorGUIUtility.singleLineHeight;
        }

        void OnListAdd(ReorderableList list) {
            var count = sources.Count;
            sources.AddRange(Selection.GetFiltered<Renderer>(SelectionMode.Deep).Where(IsAcceptableObject));
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
                int length = skinnedMeshRenderer != null ? mesh.blendShapeCount : 0;
                if (bakeBlendShapeMap.TryGetValue(source, out var bakeBlendShapeToggles)) {
                    if (bakeBlendShapeToggles.blendShapeFlags.Length != length)
                        bakeBlendShapeToggles.blendShapeFlags = new CombineBlendshapeFlags[length];
                    bakeBlendShapeToggles.blendShapeNames = skinnedMeshRenderer != null ? GetBlendShapeNames(mesh) : new string[0];
                } else {
                    bakeBlendShapeToggles = new BakeBlendShapeToggles(
                        new CombineBlendshapeFlags[length],
                        CombineMeshFlags.CreateBoneForNonSkinnedMesh |
                        CombineMeshFlags.RemoveSubMeshWithoutMaterials |
                        CombineMeshFlags.RemoveMeshPortionsWithoutBones,
                        false,
                        skinnedMeshRenderer != null ? GetBlendShapeNames(mesh) : new string[0]
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
                if (source == null) continue;
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

        void SelectBonesWithSimilarNameAsParent(bool checkPrefix) {
            var stack = new Stack<Transform>();
            foreach (var transform in rootTransforms) stack.Push(transform);
            while (stack.Count > 0) {
                var transform = stack.Pop();
                var parent = transform.parent;
                if (parent != null) {
                    var name = transform.name;
                    var parentName = transform.parent.name;
                    if (checkPrefix ? name.StartsWith(parentName) : name == parentName)
                        bonesToMergeUpwards.Add(transform);
                }
                foreach (Transform child in transform) stack.Push(child);
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
            }).ToArray(), destination, mergeFlags, blendShapeCopyMode, boneReamp);
            if (mesh != null) {
                mesh.Optimize();
                if (destination is SkinnedMeshRenderer skinnedMeshRenderer)
                    unusedTransforms.ExceptWith(skinnedMeshRenderer.bones);
                unusedTransforms.Remove(destination.transform);
                var path = EditorUtility.SaveFilePanelInProject("Save Mesh", mesh.name, "asset", "Save Combined Mesh", saveAssetDefaultPath);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.CreateAsset(mesh, path);
                if (autoCleanup) SafeDeleteAllObjects();
                if (shouldPingDestination) EditorGUIUtility.PingObject(destination);
                currentTab = 2;
            } else
                Debug.LogError("Failed to combine meshes.");
        }

        [System.Serializable]
        struct BakeBlendShapeToggles {
            public CombineBlendshapeFlags[] blendShapeFlags;
            public CombineMeshFlags combineMeshFlags;
            public bool toggleState;
            public string[] blendShapeNames;

            public BakeBlendShapeToggles(CombineBlendshapeFlags[] blendShapeFlags, CombineMeshFlags combineMeshFlags, bool toggleState, string[] blendShapeNames) {
                this.blendShapeFlags = blendShapeFlags;
                this.combineMeshFlags = combineMeshFlags;
                this.toggleState = toggleState;
                this.blendShapeNames = blendShapeNames;
            }
        }
    }
}