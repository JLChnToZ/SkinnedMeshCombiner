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
using UnityObject = UnityEngine.Object;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    using JLChnToZ.CommonUtils;
    using static Utils;
    
    public partial class MeshCombinerWindow : EditorWindow {
        const string RENAME_BLENDSHAPE_INFO = "You can rename blendshapes in this tab.";

        [Serializable] class StringMap : SerializableDictionary<string, string> {}

        Vector2 blendshapeScrollPos;
        HashSet<string> allBlendshapeNames = new HashSet<string>();
        StringMap blendshapeNameMap = new StringMap();
        

        void RefreshBlendshapes() {
            allBlendshapeNames.Clear();
            foreach (var renderer in sources) {
                if (renderer is SkinnedMeshRenderer smr) {
                    var mesh = smr.sharedMesh;
                    if (mesh != null) allBlendshapeNames.UnionWith(EnumerateBlendshapeNames(mesh));
                } else if (renderer is MeshRenderer mr && mr.TryGetComponent(out MeshFilter mf)) {
                    var mesh = mf.sharedMesh;
                    if (mesh != null) allBlendshapeNames.UnionWith(EnumerateBlendshapeNames(mesh));
                }
            }
        }

        void DrawBlendshapeTab() {
            EditorGUILayout.HelpBox(RENAME_BLENDSHAPE_INFO, MessageType.Info);
            using (var scroll = new EditorGUILayout.ScrollViewScope(blendshapeScrollPos)) {
                blendshapeScrollPos = scroll.scrollPosition;
                foreach (var blendshapeName in allBlendshapeNames) {
                    if (!blendshapeNameMap.TryGetValue(blendshapeName, out var mappedName))
                        mappedName = blendshapeName;
                    using (new EditorGUILayout.HorizontalScope()) {
                        using (var changed = new EditorGUI.ChangeCheckScope()) {
                            mappedName = EditorGUILayout.TextField(blendshapeName, mappedName);
                            if (changed.changed) blendshapeNameMap[blendshapeName] = mappedName;
                        }
                        if (GUILayout.Button("Reset", EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                            blendshapeNameMap.Remove(blendshapeName);
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.ExpandWidth(false))) RefreshBlendshapes();
                if (GUILayout.Button("Reset All", EditorStyles.miniButton, GUILayout.ExpandWidth(false))) blendshapeNameMap.Clear();
            }
        }
    }
}