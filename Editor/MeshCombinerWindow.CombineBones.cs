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
using JLChnToZ.CommonUtils;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    using static Utils;
    
    public partial class MeshCombinerWindow : EditorWindow {
        const string COMBINE_BONE_INFO = "Select bones to merge upwards (to its parent in hierarchy).\n" +
            "If a bone does not have weight on any mesh, it will be dereferenced regardless of selection.\n" +
            "You can hold shift to toggle/fold all children of a bone.";

        [Serializable] class BoneFoldedMap : SerializableDictionary<Transform, bool> {}

        [Serializable] class RendererSet : SerializableSet<Renderer> {}

        [Serializable] class BoneRenderersMap : SerializableDictionary<Transform, RendererSet> {}

        [Serializable] class TransformMap : SerializableDictionary<Transform, Transform> {}

        Vector2 boneMergeScrollPos;
        TransformMap boneReamp = new TransformMap();
        BoneFoldedMap boneFolded = new BoneFoldedMap();
        TransformSet bonesToMergeUpwards = new TransformSet();
        BoneRenderersMap boneToRenderersMap = new BoneRenderersMap();

        void DrawCombineBoneTab() {
            EditorGUILayout.HelpBox(COMBINE_BONE_INFO, MessageType.Info);
            boneMergeScrollPos = EditorGUILayout.BeginScrollView(boneMergeScrollPos);
            bool isMeshRenderer = destination is MeshRenderer;
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
                    if (EditorGUI.EndChangeCheck() && !isMeshRenderer) {
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
    }
}