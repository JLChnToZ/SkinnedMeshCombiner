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
using UnityEditorInternal;
using JLChnToZ.CommonUtils;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    using static Utils;
    public partial class MeshCombinerWindow : EditorWindow {
        const string COMBINE_MESH_INFO = "Select (skinned) mesh renderer(s) and/or its parent, and click \"+\" button to add to the combine list.\n" +
            "Even it is not necessary in many cases, you may drag to change the processing order.\n" +
            "You can expand the options such as baking blendshapes by clicking on the arrow on the left.\n" +
            "You can use the checkbox on the right to toggle all options within the renderer while expanded the view.\n" +
            "It is recommend to bake the blendshapes that unlikely to be changed after build for better performance and smaller file size.\n" +
            "If the blendshape is a \"anti-penetration\" key for hiding skins under clothes, you can use the dropdown to select remove blendshape vertex to reduce triangles and vertices.";
            

        [Serializable]
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

        [Serializable] class BakeBlendShapeMap : SerializableDictionary<Renderer, BakeBlendShapeToggles> {}

        Vector2 sourceListScrollPos;
        List<Renderer> sources = new List<Renderer>();
        BakeBlendShapeMap bakeBlendShapeMap = new BakeBlendShapeMap();
        CombineMeshFlags mergeFlags = CombineMeshFlags.MergeSubMeshes | CombineMeshFlags.RemoveMeshPortionsWithoutBones;
        BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices;
        ReorderableList sourceList;

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

        void InitCombineMeshTab() {
            sourceList = new ReorderableList(sources, typeof(Renderer), true, false, true, true) {
                drawElementCallback = OnListDrawElement,
                elementHeightCallback = OnListGetElementHeight,
                onAddCallback = OnListAdd,
                onRemoveCallback = OnListRemove,
                drawNoneElementCallback = OnListDrawNoneElement,
                headerHeight = 0,
            };
        }

        void DrawCombineMeshTab() {
            EditorGUILayout.HelpBox(COMBINE_MESH_INFO, MessageType.Info);
            sourceListScrollPos = EditorGUILayout.BeginScrollView(sourceListScrollPos);
            sourceList.DoLayoutList();
            EditorGUILayout.EndScrollView();

            #region Add Sorting Button
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Sort by Mesh Sorting Order Ascending ↑", EditorStyles.miniButton, GUILayout.ExpandWidth(false))) AutoOrderRenderer(false);
            if (GUILayout.Button("Sort by Mesh Sorting Order Descending ↓", EditorStyles.miniButton, GUILayout.ExpandWidth(false))) AutoOrderRenderer();
            EditorGUILayout.EndHorizontal();
            #endregion
            
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
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select Unused Blendshapes", EditorStyles.miniButton, GUILayout.ExpandWidth(false))) TryMarkUnusedBlendshapes();
            if (GUILayout.Button("Deselect In-Use Blendshapes", EditorStyles.miniButton, GUILayout.ExpandWidth(false))) TryMarkUnusedBlendshapes(true);
            EditorGUILayout.EndHorizontal();
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
                    rect2.width = rect.width - 128 - rect2.x;
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
                    bakeBlendShapeToggles.blendShapeNames = skinnedMeshRenderer != null ? GetBlendshapeNamesArray(mesh) : new string[0];
                } else {
                    bakeBlendShapeToggles = new BakeBlendShapeToggles(
                        new CombineBlendshapeFlags[length],
                        CombineMeshFlags.CreateBoneForNonSkinnedMesh |
                        CombineMeshFlags.RemoveSubMeshWithoutMaterials |
                        CombineMeshFlags.RemoveMeshPortionsWithoutBones,
                        false,
                        skinnedMeshRenderer != null ? GetBlendshapeNamesArray(mesh) : new string[0]
                    );
                }
                bakeBlendShapeMap[source] = bakeBlendShapeToggles;
                oldSources.Remove(source);
            }
            foreach (var source in oldSources) bakeBlendShapeMap.Remove(source);
        }

        void TryMarkUnusedBlendshapes(bool isDeselect = false) {
            var unusedBlendshapes = new HashSet<string>();
            foreach (var renderer in sources) {
                if (!(renderer is SkinnedMeshRenderer smr)) continue;
                if (!bakeBlendShapeMap.TryGetValue(renderer, out var bakeBlendShapeToggles)) continue;
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                unusedBlendshapes.Clear();
                unusedBlendshapes.UnionWith(EnumerateBlendshapeNames(mesh));
                unusedBlendshapes.ExceptWith(ReportUsedBlendshapes(smr, mesh));
                if (isDeselect) {
                    for (int i = 0, count = mesh.blendShapeCount; i < count; i++)
                        if (!unusedBlendshapes.Contains(mesh.GetBlendShapeName(i)))
                            bakeBlendShapeToggles.blendShapeFlags[i] = CombineBlendshapeFlags.None;
                } else
                    foreach (var key in unusedBlendshapes) {
                        int index = mesh.GetBlendShapeIndex(key);
                        if (index >= 0 && bakeBlendShapeToggles.blendShapeFlags[index] == CombineBlendshapeFlags.None)
                            bakeBlendShapeToggles.blendShapeFlags[index] = CombineBlendshapeFlags.CombineBlendShape;
                    }
            }
        }

        static IEnumerable<string> ReportUsedBlendshapes(Renderer renderer, Mesh mesh) {
            const string PREFIX = "blendShape.";
            var animClips = new HashSet<(Transform, AnimationClip)>();
            foreach (var (component, controller) in GetAnimationSources(renderer)) {
                if (controller == null) continue;
                foreach (var clip in controller.animationClips)
                    animClips.Add((component, clip));
            }
            foreach (var (transform, clip) in animClips) {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip)) {
                    if (binding.type != typeof(SkinnedMeshRenderer) ||
                        binding.path != GetTransformPath(renderer.transform, transform) ||
                        binding.propertyName != PREFIX && !binding.propertyName.StartsWith(PREFIX)) continue;
                    yield return binding.propertyName.Substring(PREFIX.Length);
                }
            }
            // VRChat Avatars (SDK3)
            var type = Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A", false);
            if (type != null) {
                dynamic avatarDescriptor = renderer.GetComponentInParent(type);
                if (avatarDescriptor != null) {
                    if (avatarDescriptor.VisemeSkinnedMesh == renderer) {
                        var visemeBlendShapes = avatarDescriptor.VisemeBlendShapes;
                        if (visemeBlendShapes is string[] visemeBlendShapesArray)
                            foreach (var name in visemeBlendShapesArray)
                                yield return name;
                    }
                    var customEyeLookSettings = avatarDescriptor.customEyeLookSettings;
                    if (customEyeLookSettings.eyelidsSkinnedMesh == renderer) {
                        var eyelidsBlendshapes = customEyeLookSettings.eyelidsBlendshapes;
                        if (eyelidsBlendshapes is int[] eyelidsBlendshapesArray)
                            foreach (var index in eyelidsBlendshapesArray)
                                yield return mesh.GetBlendShapeName(index);
                    }
                }
            }
            // TODO: Add more animation sources from other SDKs
        }

        static IEnumerable<(Transform, RuntimeAnimatorController)> GetAnimationSources(Renderer renderer) {
            var animator = renderer.GetComponentInParent<Animator>();
            if (animator != null) {
                var controller = animator.runtimeAnimatorController;
                if (controller != null) {
                    yield return (animator.transform, controller);
                }
            }
            var gathered = new HashSet<(Transform, RuntimeAnimatorController)>();
            // VRChat Avatars (Universal SDK Versions)
            var type = Type.GetType("VRC.SDKBase.VRC_AvatarDescriptor, VRCSDKBase", false);
            if (type != null) {
                var avatarDescriptor = renderer.GetComponentInParent(type);
                if (avatarDescriptor != null)
                    using (var so = new SerializedObject(avatarDescriptor)) {
                        var prop = so.GetIterator();
                        while (prop.Next(true))
                            if (prop.propertyType == SerializedPropertyType.ObjectReference &&
                                prop.objectReferenceValue is RuntimeAnimatorController rac)
                                gathered.Add((avatarDescriptor.transform, rac));
                    }
            }
            // TODO: Add more animation sources from other SDKs
            foreach (var additionalResuls in gathered) yield return additionalResuls;
        }
    }
}