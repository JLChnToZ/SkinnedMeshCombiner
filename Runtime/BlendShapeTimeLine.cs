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
using UnityEngine;
using UnityEngine.Rendering;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    using static Utils;

    class BlendShapeTimeLine {
        readonly Dictionary<float, Dictionary<(Mesh mesh, int subMeshIndex), int>> frames = new Dictionary<float, Dictionary<(Mesh, int), int>>();
        readonly Dictionary<(Mesh mesh, int subMeshIndex), (SubMeshDescriptor subMesh, int blendShapeIndex, int destOffset, Matrix4x4? transform)> subMeshes =
            new Dictionary<(Mesh, int), (SubMeshDescriptor, int, int, Matrix4x4?)>();

        public void AddFrom(Mesh mesh, int subMeshIndex, int blendShapeIndex, int destOffset, Matrix4x4? transform = null) {
            var subMeshKey = (mesh, subMeshIndex);
            if (subMeshes.ContainsKey(subMeshKey)) return;
            var frameCount = mesh.GetBlendShapeFrameCount(blendShapeIndex);
            if (frameCount < 1) return;
            subMeshes[subMeshKey] = (mesh.GetSubMesh(subMeshIndex), blendShapeIndex, destOffset, transform == null || transform.Value == Matrix4x4.identity ? null : transform);
            for (int i = 0; i < frameCount; i++) {
                var weight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, i);
                LazyInitialize(frames, weight, out var frameIndexMap);
                frameIndexMap[(mesh, subMeshIndex)] = i;
            }
        }

        public void ApplyTo(
            Mesh combinedMesh, string blendShapeName,
            BlendShapeCopyMode copyMode,
            ref Dictionary<int, (Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)> vntArrayCache,
            ref Dictionary<int, (Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)> vntArrayCache2
        ) {
            int destBlendShapeIndex = combinedMesh.GetBlendShapeIndex(blendShapeName);
            if (destBlendShapeIndex >= 0) {
                Debug.LogWarning($"Blend shape {blendShapeName} already exists in the combined mesh. Skipping.");
                return;
            }
            var destVertexCount = combinedMesh.vertexCount;
            var remainingMeshes = new HashSet<(Mesh mesh, int subMeshIndex)>();
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
                    var (srcSubMesh, blendShapeIndex, _, transfrom) = srcSubMeshData;
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
                        var (srcSubMesh, _, destOffset, transfrom) = srcSubMeshData;
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
            (Mesh mesh, int subMeshIndex) key,
            (SubMeshDescriptor subMesh, int blendShapeIndex, int destOffset, Matrix4x4? transform) subMeshData,
            float[] weights, int weightIndex, bool seekAscending, BlendShapeCopyMode copyMode,
            ref Dictionary<int, (Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)> vntArrayCache,
            out (Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents, float weight) result
        ) {
            while (true) {
                if (seekAscending ? ++weightIndex >= weights.Length : --weightIndex < 0) {
                    result = default;
                    return false;
                }
                if (frames[weights[weightIndex]].TryGetValue(key, out var frameIndex)) {
                    var (srcMesh, _) = key;
                    var (_, blendShapeIndex, _, _) = subMeshData;
                    var (deltaVertices, deltaNormals, deltaTangents) = GetVNTArrays(ref vntArrayCache, srcMesh.vertexCount, copyMode);
                    srcMesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                    result = (deltaVertices, deltaNormals, deltaTangents, weights[weightIndex]);
                    return true;
                }
            }
        }
    }
}