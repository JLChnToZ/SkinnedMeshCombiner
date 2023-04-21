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

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    using static UnityEngine.Object;
    using static Utils;

    public class SkinnedMeshCombinerCore {
        readonly Dictionary<Material, List<(CombineInstance, CombineMeshFlags[])>> combineInstances = new Dictionary<Material, List<(CombineInstance, CombineMeshFlags[])>>();
        readonly Dictionary<(Mesh, int), IEnumerable<BoneWeight>> boneWeights = new Dictionary<(Mesh, int), IEnumerable<BoneWeight>>();
        readonly Dictionary<(Transform, Matrix4x4), int> bindposeMap = new Dictionary<(Transform, Matrix4x4), int>();
        readonly List<Matrix4x4> bindposes = new List<Matrix4x4>();
        readonly List<Matrix4x4> allBindposes = new List<Matrix4x4>();
        readonly List<Transform> allBones = new List<Transform>();
        readonly HashSet<int> boneHasWeights = new HashSet<int>();
        readonly Dictionary<int, int> boneMapping = new Dictionary<int, int>();
        readonly List<Material> materials = new List<Material>();
        Dictionary<int, (Vector3[], Vector3[], Vector3[])> vntArrayCache = null, vntArrayCache2 = null;
        readonly BlendShapeCopyMode blendShapeCopyMode;
        readonly List<Vector3> vertices, normals;
        readonly List<Vector4> tangents;
        IDictionary<Transform, Transform> boneRemap;
        Dictionary<string, BlendShapeTimeLine> blendShapesStore;
        readonly Dictionary<string, float> blendShapesWeights = new Dictionary<string, float>();
        Bounds bounds;
        Matrix4x4 referenceTransform = Matrix4x4.identity;

        public static Mesh Combine(
            ICollection<(Renderer, CombineMeshFlags[])> sources, 
            SkinnedMeshRenderer destination,
            bool mergeSubMeshes = true,
            BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices,
            IDictionary<Transform, Transform> boneRemap = null
        ) {
            var core = new SkinnedMeshCombinerCore(blendShapeCopyMode, boneRemap);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            foreach (var (source, bakeFlags) in sources) {
                if (source is SkinnedMeshRenderer smr)
                    core.Add(smr, bakeFlags);
                else if (source is MeshRenderer mr)
                    core.Add(mr, bakeFlags[0] != CombineMeshFlags.None, destination);
            }
            if (mergeSubMeshes) core.MergeSubMeshes();
            var result = core.Combine(destination);
            Undo.SetCurrentGroupName("Combine Meshes");
            Undo.CollapseUndoOperations(group);
            core.CleanUp();
            return result;
        }

        public SkinnedMeshCombinerCore(BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices, IDictionary<Transform, Transform> boneRemap = null) {
            this.blendShapeCopyMode = blendShapeCopyMode;
            this.boneRemap = boneRemap;
            vertices = blendShapeCopyMode.HasFlag(BlendShapeCopyMode.Vertices) ? new List<Vector3>() : null;
            normals = blendShapeCopyMode.HasFlag(BlendShapeCopyMode.Normals) ? new List<Vector3>() : null;
            tangents = blendShapeCopyMode.HasFlag(BlendShapeCopyMode.Tangents) ? new List<Vector4>() : null;
        }

        public void Add(SkinnedMeshRenderer source, CombineMeshFlags[] bakeFlags) {
            if (combineInstances.Count == 0)
                bounds = source.bounds;
            else
                bounds.Encapsulate(source.bounds);
            var orgMesh = source.sharedMesh;
            var mesh = Instantiate(orgMesh);
            var sharedMaterials = source.sharedMaterials;
            var bones = source.bones;
            mesh.GetBindposes(bindposes);
            var remapTransform = Matrix4x4.identity;
            var inverseRemapTransform = remapTransform;
            if (bindposes.Count > 0) {
                var rootBone = source.rootBone;
                int rootBoneIndex = rootBone != null ? Array.IndexOf(bones, rootBone) : -1;
                if (rootBoneIndex < 0) rootBoneIndex = 0; // Fallback to first bone
                if (combineInstances.Count == 0)
                    referenceTransform = bindposes[rootBoneIndex];
                else {
                    remapTransform = referenceTransform.inverse * bindposes[rootBoneIndex];
                    inverseRemapTransform = remapTransform.inverse;
                }
            }
            var vertexCutter = new VertexCutter(mesh);
            for (int i = 0, count = mesh.blendShapeCount; i < count; i++) {
                if ((bakeFlags[i] != CombineMeshFlags.CombineAndRemoveBlendShapeVertex &&
                    bakeFlags[i] != CombineMeshFlags.AggressiveRemoveBlendShapeVertex) ||
                    source.GetBlendShapeWeight(i) <= 0)
                    continue;
                for (int j = 0, framecount = mesh.GetBlendShapeFrameCount(i); j < framecount; j++) {
                    var vntArray = GetVNTArrays(ref vntArrayCache, mesh.vertexCount, BlendShapeCopyMode.Vertices);
                    var (deltaVertices, _, _) = vntArray;
                    mesh.GetBlendShapeFrameVertices(i, j, deltaVertices, null, null);
                    for (int k = 0; k < deltaVertices.Length; k++)
                        if (deltaVertices[k] != Vector3.zero)
                            vertexCutter.RemoveVertex(k, bakeFlags[i] == CombineMeshFlags.AggressiveRemoveBlendShapeVertex);
                }
            }
            mesh = vertexCutter.Apply();
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
                var poseMatrix = bindposes[i] * inverseRemapTransform;
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
                combines.Add((new CombineInstance { mesh = mesh, subMeshIndex = i, transform = remapTransform }, bakeFlags));
                var subMesh = mesh.GetSubMesh(i);
                boneWeights[(mesh, i)] = new ArraySegment<BoneWeight>(weights, subMesh.firstVertex, subMesh.vertexCount);
            }
            if (vertices != null) mesh.GetVertices(vertices);
            if (normals != null) mesh.GetNormals(normals);
            if (tangents != null) mesh.GetTangents(tangents);
            bool hasApplyBlendShape = false;
            for (int i = 0, count = mesh.blendShapeCount; i < count; i++)
                if (bakeFlags[i] != CombineMeshFlags.None) {
                    ApplyBlendShape(mesh, vertices, normals, tangents, i, source.GetBlendShapeWeight(i));
                    hasApplyBlendShape = true;
                } else
                    blendShapesWeights[mesh.GetBlendShapeName(i)] = source.GetBlendShapeWeight(i);
            if (hasApplyBlendShape) {
                if (vertices != null) mesh.SetVertices(vertices);
                if (normals != null) mesh.SetNormals(normals);
                if (tangents != null) mesh.SetTangents(tangents);
                mesh.UploadMeshData(false);
            }
        }

        public void Add(MeshRenderer source, bool createBone, Renderer destination) {
            var sourceTransform = source.transform;
            if (!source.TryGetComponent(out MeshFilter meshFilter)) return;
            if (combineInstances.Count == 0)
                bounds = source.bounds;
            else
                bounds.Encapsulate(source.bounds);
            var mesh = Instantiate(meshFilter.sharedMesh);
            var sharedMaterials = source.sharedMaterials;
            var subMeshCount = mesh.subMeshCount;
            int index = 0;
            if (!createBone) {
                var key = (sourceTransform, Matrix4x4.identity);
                if (!bindposeMap.TryGetValue(key, out index)) {
                    bindposeMap[key] = index = bindposeMap.Count;
                    allBindposes.Add(Matrix4x4.identity);
                    allBones.Add(sourceTransform);
                }
            }
            var transform = createBone ? sourceTransform.localToWorldMatrix * destination.worldToLocalMatrix : Matrix4x4.identity;
            for (int i = 0; i < subMeshCount; i++) {
                if (LazyInitialize(combineInstances, sharedMaterials[i], out var combines))
                materials.Add(sharedMaterials[i]);
                combines.Add((new CombineInstance { mesh = mesh, subMeshIndex = i, transform = transform }, new[] { CombineMeshFlags.CombineBlendShape }));
                boneWeights[(mesh, i)] = Enumerable.Repeat(createBone ? default : new BoneWeight { boneIndex0 = index, weight0 = 1 }, mesh.GetSubMesh(i).vertexCount);
            }
            if (destination != source) {
                Undo.RecordObject(source, "Combine Meshes");
                source.enabled = false;
            }
        }

        public void MergeSubMeshes() {
            foreach (var kv in combineInstances) {
                var combines = kv.Value;
                if (combines.Count < 2) continue;
                var mesh = new Mesh();
                var combineArray = combines.Select(entry => entry.Item1).ToArray();
                CheckAndCombineMeshes(mesh, combineArray, true);
                boneWeights[(mesh, 0)] = combineArray.SelectMany(entry => boneWeights[(entry.mesh, entry.subMeshIndex)]).ToArray();
                if (blendShapeCopyMode != BlendShapeCopyMode.None) CopyBlendShapes(mesh, combines);
                combines.Clear();
                combines.Add((new CombineInstance { mesh = mesh, transform = Matrix4x4.identity }, new CombineMeshFlags[mesh.blendShapeCount]));
            }
        }

        public Mesh Combine(SkinnedMeshRenderer destination) {
            var bindPoseArray = allBindposes.ToArray();
            var name = destination.name;
            if (!name.EndsWith("mesh", StringComparison.OrdinalIgnoreCase)) name += " Mesh";
            var combinedNewMesh = new Mesh { name = name };
            var combineInstanceArray = materials.SelectMany(material => combineInstances[material]).ToArray();
            CheckAndCombineMeshes(combinedNewMesh, combineInstanceArray.Select(entry => entry.Item1).ToArray(), false);
            combinedNewMesh.boneWeights = combineInstanceArray.Select(entry => {
                boneWeights.TryGetValue((entry.Item1.mesh, entry.Item1.subMeshIndex), out var weights);
                return weights;
            }).Where(x => x != null).SelectMany(x => x).ToArray();
            combinedNewMesh.bindposes = bindPoseArray;
            if (blendShapeCopyMode != BlendShapeCopyMode.None) CopyBlendShapes(combinedNewMesh, combineInstanceArray);
            foreach (var combines in combineInstances.Values) combines.Clear();
            combinedNewMesh.RecalculateBounds();
            combinedNewMesh.UploadMeshData(false);
            Undo.RecordObject(destination, "Combine Meshes");
            destination.sharedMesh = combinedNewMesh;
            var rootBone = destination.rootBone;
            if (rootBone == null) {
                if (allBones.Count > 0) {
                    rootBone = FindCommonParent(allBones);
                    if (rootBone == null) rootBone = allBones[0];
                    else foreach (var bone in allBones) {
                        if (bone == rootBone) break;
                        if (bone.parent == rootBone) {
                            rootBone = bone;
                            break;
                        }
                    }
                }
                if (rootBone == null) rootBone = destination.transform;
                destination.rootBone = rootBone;
            }
            var localBounds = bounds;
            var rootBoneInverse = rootBone.worldToLocalMatrix;
            localBounds.center = rootBoneInverse.MultiplyPoint(localBounds.center);
            localBounds.extents = rootBoneInverse.MultiplyVector(localBounds.extents);
            destination.localBounds = localBounds;
            destination.sharedMaterials = materials.ToArray();
            destination.bones = allBones.ToArray();
            foreach (var kv in blendShapesWeights) {
                var index = combinedNewMesh.GetBlendShapeIndex(kv.Key);
                if (index >= 0) destination.SetBlendShapeWeight(index, kv.Value);
            }
            return combinedNewMesh;
        }

        static void CheckAndCombineMeshes(Mesh mesh, CombineInstance[] combineInstances, bool mergeSubMeshes) {
            int vertexCount = 0;
            foreach (var combine in combineInstances) {
                vertexCount += combine.mesh.GetSubMesh(combine.subMeshIndex).vertexCount;
                if (vertexCount > ushort.MaxValue) {
                    mesh.indexFormat = IndexFormat.UInt32;
                    Debug.LogWarning("Mesh has more than 65535 vertices. Index format is changed to 32-bit. Combined mesh may not work on some platforms.");
                    break;
                }
            }
            mesh.CombineMeshes(combineInstances, mergeSubMeshes, true);
        }

        public void CleanUp() {
            foreach (var (mesh, _) in boneWeights.Keys)
                if (mesh != null) DestroyImmediate(mesh, false);
            boneWeights.Clear();
            combineInstances.Clear();
            bindposeMap.Clear();
            bindposes.Clear();
            allBindposes.Clear();
            allBones.Clear();
            boneHasWeights.Clear();
            boneMapping.Clear();
            materials.Clear();
            blendShapesStore?.Clear();
            blendShapesWeights.Clear();
        }

        public void CopyBlendShapes(Mesh combinedNewMesh, IEnumerable<(CombineInstance, CombineMeshFlags[])> combineInstances) {
            if (!LazyInitialize(ref blendShapesStore)) blendShapesStore.Clear();
            int offset = 0;
            foreach (var (entry, bakeFlags) in combineInstances) {
                var mesh = entry.mesh;
                var subMeshIndex = entry.subMeshIndex;
                var subMesh = mesh.GetSubMesh(subMeshIndex);
                for (int k = 0; k < mesh.blendShapeCount; k++) {
                    if (bakeFlags[k] != CombineMeshFlags.None) continue;
                    string key = mesh.GetBlendShapeName(k);
                    LazyInitialize(blendShapesStore, key, out var timeline);
                    timeline.AddFrom(mesh, subMeshIndex, k, offset, entry.transform);
                }
                offset += subMesh.vertexCount;
            }
            foreach (var timeline in blendShapesStore) timeline.Value.ApplyTo(combinedNewMesh, timeline.Key, blendShapeCopyMode, ref vntArrayCache, ref vntArrayCache2);
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

        void ApplyBlendShape(
            Mesh mesh, List<Vector3> vertices, List<Vector3> normals, List<Vector4> tangents,
            int blendShapeIndex, float weight
        ) {
            var vertexCount = mesh.vertexCount;
            var vntArray = GetVNTArrays(ref vntArrayCache, vertices.Count, blendShapeCopyMode);
            var (deltaVertices, deltaNormals, deltaTangents) = vntArray;
            int count = mesh.GetBlendShapeFrameCount(blendShapeIndex);
            if (count == 0) return;
            float frameWeight;
            for (int i = 1; i < count; i++) {
                frameWeight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, i);
                if (frameWeight > weight) {
                    mesh.GetBlendShapeFrameVertices(blendShapeIndex, i - 1, deltaVertices, deltaNormals, deltaTangents);
                    var vntArray2 = GetVNTArrays(ref vntArrayCache2, vertices.Count, blendShapeCopyMode);
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
    }

    public enum CombineMeshFlags {
        None = 0,
        CombineBlendShape = 1,
        CombineAndRemoveBlendShapeVertex = 2,
        AggressiveRemoveBlendShapeVertex = 3,
    }
}