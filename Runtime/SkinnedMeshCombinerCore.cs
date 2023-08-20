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
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    using static UnityEngine.Object;
    using static Utils;

    public class SkinnedMeshCombinerCore : IDisposable {
        readonly Dictionary<Material, List<(CombineInstance combineInstance, CombineBlendshapeFlags[] flags)>> combineInstances =
            new Dictionary<Material, List<(CombineInstance, CombineBlendshapeFlags[])>>();
        readonly Dictionary<(Mesh mesh, int index), IEnumerable<BoneWeight>> boneWeights = new Dictionary<(Mesh, int), IEnumerable<BoneWeight>>();
        readonly Dictionary<(Transform bone, Matrix4x4 poseMatrix), int> bindposeMap = new Dictionary<(Transform, Matrix4x4), int>();
        readonly Dictionary<Transform, List<Matrix4x4>> bindposeMap2 = new Dictionary<Transform, List<Matrix4x4>>();
        readonly List<Matrix4x4> bindposes = new List<Matrix4x4>();
        readonly List<Matrix4x4> allBindposes = new List<Matrix4x4>();
        readonly List<Transform> allBones = new List<Transform>();
        readonly HashSet<int> boneHasWeights = new HashSet<int>();
        readonly Dictionary<int, int> boneMapping = new Dictionary<int, int>();
        readonly List<Material> materials = new List<Material>();
        Dictionary<int, (Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)> vntArrayCache = null, vntArrayCache2 = null;
        readonly BlendShapeCopyMode blendShapeCopyMode;
        readonly List<Vector3> vertices, normals;
        readonly List<Vector4> tangents;
        IDictionary<Transform, Transform> boneRemap;
        IDictionary<string, string> blendShapeRemap;
        Dictionary<string, BlendShapeTimeLine> blendShapesStore;
        readonly Dictionary<string, float> blendShapesWeights = new Dictionary<string, float>();
        Bounds bounds;
        Matrix4x4 referenceTransform = Matrix4x4.identity;
        Material dummyMaterial;
        Transform dummyTransform;

        public static Mesh Combine(
            ICollection<(Renderer, CombineBlendshapeFlags[], CombineMeshFlags)> sources, 
            Renderer destination,
            CombineMeshFlags mergeFlags = CombineMeshFlags.MergeSubMeshes,
            BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices,
            IDictionary<Transform, Transform> boneRemap = null,
            IDictionary<string, string> blendShapeRemap = null
        ) {
            var disallowedFlags = CombineMeshFlags.None;
            if (!(destination is SkinnedMeshRenderer)) {
                mergeFlags |= CombineMeshFlags.BakeMesh;
                disallowedFlags = CombineMeshFlags.CreateBoneForNonSkinnedMesh;
                blendShapeCopyMode = BlendShapeCopyMode.None;
            }
            using (var core = new SkinnedMeshCombinerCore(blendShapeCopyMode, boneRemap)) {
                #if UNITY_EDITOR
                int group = -1;
                if (!Application.isPlaying) {
                    Undo.IncrementCurrentGroup();
                    group = Undo.GetCurrentGroup();
                }
                #endif
                foreach (var (source, bakeFlags, localMergeFlags) in sources) {
                    if (source is SkinnedMeshRenderer smr)
                        core.Add(smr, bakeFlags, (mergeFlags | localMergeFlags) & ~disallowedFlags);
                    else if (source is MeshRenderer mr)
                        core.Add(mr, destination, (mergeFlags | localMergeFlags) & ~disallowedFlags);
                }
                if (mergeFlags.HasFlag(CombineMeshFlags.MergeSubMeshes)) core.MergeSubMeshes();
                var result = core.Combine(destination);
                #if UNITY_EDITOR
                if (!Application.isPlaying) {
                    Undo.SetCurrentGroupName("Combine Meshes");
                    Undo.CollapseUndoOperations(group);
                }
                #endif
                return result;
            }
        }

        public SkinnedMeshCombinerCore(
            BlendShapeCopyMode blendShapeCopyMode = BlendShapeCopyMode.Vertices,
            IDictionary<Transform, Transform> boneRemap = null,
            IDictionary<string, string> blendShapeRemap = null
        ) {
            this.blendShapeCopyMode = blendShapeCopyMode;
            this.boneRemap = boneRemap;
            this.blendShapeRemap = blendShapeRemap;
            vertices = new List<Vector3>();
            normals = new List<Vector3>();
            tangents = new List<Vector4>();
        }

        public void Add(SkinnedMeshRenderer source, CombineBlendshapeFlags[] bakeFlags, CombineMeshFlags mergeFlags = CombineMeshFlags.None) {
            var orgMesh = source.sharedMesh;
            if (orgMesh == null || orgMesh.vertexCount <= 0) return;
            if (combineInstances.Count == 0)
                bounds = source.bounds;
            else
                bounds.Encapsulate(source.bounds);
            var mesh = Instantiate(orgMesh);
            if (mergeFlags.HasFlag(CombineMeshFlags.BakeMesh))
                source.BakeMesh(mesh);
            var sharedMaterials = source.sharedMaterials;
            var bones = source.bones;
            mesh.GetBindposes(bindposes);
            var remapTransform = Matrix4x4.identity;
            var inverseRemapTransform = remapTransform;
            var rootBone = source.rootBone;
            if (bindposes.Count > 0) {
                int rootBoneIndex = rootBone != null ? Array.IndexOf(bones, rootBone) : -1;
                if (rootBoneIndex < 0) rootBoneIndex = 0; // Fallback to first bone
                if (combineInstances.Count == 0)
                    referenceTransform = bindposes[rootBoneIndex];
                else {
                    remapTransform = referenceTransform.inverse * bindposes[rootBoneIndex];
                    inverseRemapTransform = remapTransform.inverse;
                }
            }
            if (rootBone == null) rootBone = source.transform;
            var weights = mesh.boneWeights;
            PreAllocate(allBones, bones.Length);
            PreAllocate(allBindposes, bindposes.Count);
            boneMapping.Clear();
            boneHasWeights.Clear();
            using (var vertexCutter = new VertexCutter(mesh)) {
                for (int i = 0, count = mesh.blendShapeCount; i < count; i++) {
                    if ((bakeFlags[i] != CombineBlendshapeFlags.CombineAndRemoveBlendshapeVertex &&
                        bakeFlags[i] != CombineBlendshapeFlags.AggressiveRemoveBlendshapeVertex) ||
                        source.GetBlendShapeWeight(i) <= 0)
                        continue;
                    for (int j = 0, framecount = mesh.GetBlendShapeFrameCount(i); j < framecount; j++) {
                        var (deltaVertices, _, _) = GetVNTArrays(ref vntArrayCache, mesh.vertexCount, BlendShapeCopyMode.Vertices);
                        mesh.GetBlendShapeFrameVertices(i, j, deltaVertices, null, null);
                        for (int k = 0; k < deltaVertices.Length; k++)
                            if (deltaVertices[k] != Vector3.zero)
                                vertexCutter.RemoveVertex(k, bakeFlags[i] == CombineBlendshapeFlags.AggressiveRemoveBlendshapeVertex);
                    }
                }
                if (mergeFlags.HasFlag(CombineMeshFlags.RemoveMeshPortionsWithoutBones) || mergeFlags.HasFlag(CombineMeshFlags.RemoveMeshPortionsWithZeroScaleBones)) {
                    var boneIndexToRemove = new Dictionary<int, bool>();
                    for (int i = 0, count = bindposes.Count; i < count; i++) {
                        if (mergeFlags.HasFlag(CombineMeshFlags.RemoveMeshPortionsWithoutBones) && bones[i] == null) {
                            boneIndexToRemove[i] = true;
                            continue;
                        }
                        if (mergeFlags.HasFlag(CombineMeshFlags.RemoveMeshPortionsWithZeroScaleBones) && bones[i].lossyScale.magnitude < 0.0001F) {
                            if (!boneIndexToRemove.ContainsKey(i)) boneIndexToRemove[i] = false;
                            continue;
                        }
                    }
                    for (int i = 0; i < weights.Length; i++) {
                        var weight = weights[i];
                        bool flag0 = false, flag1 = false, flag2 = false, flag3 = false;
                        var hasFlag0 = weight.weight0 > 0 && boneIndexToRemove.TryGetValue(weight.boneIndex0, out flag0);
                        var hasFlag1 = weight.weight1 > 0 && boneIndexToRemove.TryGetValue(weight.boneIndex1, out flag1);
                        var hasFlag2 = weight.weight2 > 0 && boneIndexToRemove.TryGetValue(weight.boneIndex2, out flag2);
                        var hasFlag3 = weight.weight3 > 0 && boneIndexToRemove.TryGetValue(weight.boneIndex3, out flag3);
                        // The vertex will be removed if any of the following conditions are met:
                        // 1. Any weights mapped to a bone that is removed
                        // 2. All weights mapped to a bone without scale
                        // 3. No weights mapped to a bone
                        if ((hasFlag0 && flag0) || (hasFlag1 && flag1) || (hasFlag2 && flag2) || (hasFlag3 && flag3) ||
                            ((hasFlag0 || hasFlag1 || hasFlag2 || hasFlag3) && (!hasFlag0 || !flag0) && (!hasFlag1 || !flag1) && (!hasFlag2 || !flag2) && (!hasFlag3 || !flag3)) ||
                            (weight.weight0 <= 0 && weight.weight1 <= 0 && weight.weight2 <= 0 && weight.weight3 <= 0))
                            vertexCutter.RemoveVertex(i, flag0 || flag1 || flag2 || flag3);
                    }
                }
                mesh = vertexCutter.Apply(mesh);
            }
            if (mesh.vertexCount <= 0) {
                DestroyImmediate(mesh, false);
                return;
            }
            int defaultBoneIndex = -1;
            if (!mergeFlags.HasFlag(CombineMeshFlags.BakeMesh)) {
                weights = mesh.boneWeights;
                foreach (var weight in weights) {
                    if (weight.weight0 > 0) boneHasWeights.Add(weight.boneIndex0);
                    if (weight.weight1 > 0) boneHasWeights.Add(weight.boneIndex1);
                    if (weight.weight2 > 0) boneHasWeights.Add(weight.boneIndex2);
                    if (weight.weight3 > 0) boneHasWeights.Add(weight.boneIndex3);
                }
                int bindposeCount = bindposes.Count;
                if (bindposeCount > 0)
                    for (int i = 0; i < bindposeCount; i++)  {
                        var targetBone = bones[i];
                        if (!boneHasWeights.Contains(i)) continue;
                        if (targetBone == null) {
                            if (dummyTransform == null) dummyTransform = new GameObject("Temporary GameObject") { hideFlags = HideFlags.HideAndDontSave }.transform;
                            targetBone = dummyTransform;
                        }
                        var poseMatrix = bindposes[i] * inverseRemapTransform;
                        if (boneRemap != null && boneRemap.TryGetValue(targetBone, out var bone)) {
                            poseMatrix = bone.worldToLocalMatrix * targetBone.localToWorldMatrix * poseMatrix;
                            targetBone = bone;
                        }
                        boneMapping[i] = GetBoneIndex(targetBone, poseMatrix);
                    }
                else defaultBoneIndex = GetBoneIndex(rootBone, Matrix4x4.identity);
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
            }
            for (int i = 0, subMeshCount = mesh.subMeshCount; i < subMeshCount; i++) {
                var subMesh = mesh.GetSubMesh(i);
                if (subMesh.vertexCount <= 0) continue;
                var sharedMaterial = i < sharedMaterials.Length ? sharedMaterials[i] : null;
                if (sharedMaterial == null && mergeFlags.HasFlag(CombineMeshFlags.RemoveSubMeshWithoutMaterials)) continue;
                GetCombines(sharedMaterial).Add((new CombineInstance { mesh = mesh, subMeshIndex = i, transform = remapTransform }, bakeFlags));
                if (!mergeFlags.HasFlag(CombineMeshFlags.BakeMesh))
                    boneWeights[(mesh, i)] = weights.Length > 0 ? new ArraySegment<BoneWeight>(weights, subMesh.firstVertex, subMesh.vertexCount) :
                        Enumerable.Repeat(new BoneWeight { boneIndex0 = defaultBoneIndex, weight0 = 1 }, mesh.GetSubMesh(i).vertexCount);
            }
            if (vertices != null) mesh.GetVertices(vertices);
            if (normals != null) mesh.GetNormals(normals);
            if (tangents != null) mesh.GetTangents(tangents);
            bool hasApplyBlendShape = false;
            for (int i = 0, count = mesh.blendShapeCount; i < count; i++)
                if (bakeFlags[i] != CombineBlendshapeFlags.None) {
                    ApplyBlendShape(mesh, vertices, normals, tangents, i, source.GetBlendShapeWeight(i));
                    hasApplyBlendShape = true;
                } else
                    blendShapesWeights[mesh.GetBlendShapeName(i)] = source.GetBlendShapeWeight(i);
            if (hasApplyBlendShape) {
                if (vertices != null && vertices.Count > 0) mesh.SetVertices(vertices);
                if (normals != null && normals.Count > 0) mesh.SetNormals(normals);
                if (tangents != null && tangents.Count > 0) mesh.SetTangents(tangents);
                mesh.UploadMeshData(false);
            }
        }

        public void Add(MeshRenderer source, Renderer destination, CombineMeshFlags mergeFlags = CombineMeshFlags.CreateBoneForNonSkinnedMesh) {
            var sourceTransform = source.transform;
            if (!source.TryGetComponent(out MeshFilter meshFilter)) return;
            var orgMesh = meshFilter.sharedMesh;
            if (orgMesh == null || orgMesh.vertexCount <= 0) return;
            if (combineInstances.Count == 0)
                bounds = source.bounds;
            else
                bounds.Encapsulate(source.bounds);
            var mesh = Instantiate(orgMesh);
            var sharedMaterials = source.sharedMaterials;
            int index = mergeFlags.HasFlag(CombineMeshFlags.CreateBoneForNonSkinnedMesh) ? GetBoneIndex(sourceTransform, Matrix4x4.identity) : 0;
            var transform = mergeFlags.HasFlag(CombineMeshFlags.CreateBoneForNonSkinnedMesh) ? Matrix4x4.identity : sourceTransform.localToWorldMatrix * destination.worldToLocalMatrix;
            for (int i = 0, count = mesh.subMeshCount; i < count; i++) {
                if (mesh.GetSubMesh(i).vertexCount <= 0) continue;
                var sharedMaterial = i < sharedMaterials.Length ? sharedMaterials[i] : null;
                if (sharedMaterial == null && mergeFlags.HasFlag(CombineMeshFlags.RemoveSubMeshWithoutMaterials)) continue;
                GetCombines(sharedMaterial).Add((new CombineInstance { mesh = mesh, subMeshIndex = i, transform = transform }, new[] { CombineBlendshapeFlags.CombineBlendShape }));
                if (!mergeFlags.HasFlag(CombineMeshFlags.BakeMesh))
                    boneWeights[(mesh, i)] = Enumerable.Repeat(mergeFlags.HasFlag(CombineMeshFlags.CreateBoneForNonSkinnedMesh) ? new BoneWeight { boneIndex0 = index, weight0 = 1 } : default, mesh.GetSubMesh(i).vertexCount);
            }
            if (destination != source) {
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.RecordObject(source, "Combine Meshes");
                #endif
                source.enabled = false;
            }
        }

        int GetBoneIndex(Transform bone, Matrix4x4 poseMatrix) {
            LazyInitialize(bindposeMap2, bone, out var list);
            bool hasApproximate = false;
            foreach (var matrix in list)
                if (Approximate(poseMatrix, matrix, 0.001F)) {
                    poseMatrix = matrix;
                    hasApproximate = true;
                    break;
                }
            if (!hasApproximate) list.Add(poseMatrix);
            var key = (bone, poseMatrix);
            if (!bindposeMap.TryGetValue(key, out var index)) {
                bindposeMap[key] = index = bindposeMap.Count;
                allBones.Add(bone == dummyTransform ? null : bone);
                allBindposes.Add(poseMatrix);
            }
            return index;
        }

        public void MergeSubMeshes() {
            foreach (var kv in combineInstances) {
                var combines = kv.Value;
                if (combines.Count < 2) continue;
                var mesh = new Mesh();
                var combineArray = combines.Select(entry => entry.combineInstance).ToArray();
                CheckAndCombineMeshes(mesh, combineArray, true);
                boneWeights[(mesh, 0)] = combineArray.SelectMany(entry =>
                    boneWeights.TryGetValue((entry.mesh, entry.subMeshIndex), out var weights) ?
                    weights : Enumerable.Empty<BoneWeight>()
                ).ToArray();
                if (blendShapeCopyMode != BlendShapeCopyMode.None) CopyBlendShapes(mesh, combines);
                combines.Clear();
                combines.Add((new CombineInstance { mesh = mesh, transform = Matrix4x4.identity }, new CombineBlendshapeFlags[mesh.blendShapeCount]));
            }
        }

        public Mesh Combine(Renderer destination) {
            var bindPoseArray = allBindposes.ToArray();
            var name = destination.name;
            if (!name.EndsWith("mesh", StringComparison.OrdinalIgnoreCase)) name += " Mesh";
            var combinedNewMesh = new Mesh { name = name };
            var combineInstanceArray = materials.SelectMany(material => combineInstances[material]).ToArray();
            CheckAndCombineMeshes(combinedNewMesh, combineInstanceArray.Select(entry => entry.combineInstance).ToArray(), false);
            if (destination is SkinnedMeshRenderer) {
                combinedNewMesh.boneWeights = combineInstanceArray.Select(entry => {
                    boneWeights.TryGetValue((entry.combineInstance.mesh, entry.combineInstance.subMeshIndex), out var weights);
                    return weights;
                }).Where(x => x != null).SelectMany(x => x).ToArray();
                combinedNewMesh.bindposes = bindPoseArray;
                if (blendShapeCopyMode != BlendShapeCopyMode.None) CopyBlendShapes(combinedNewMesh, combineInstanceArray, true);
            }
            foreach (var combines in combineInstances.Values) combines.Clear();
            combinedNewMesh.RecalculateBounds();
            combinedNewMesh.UploadMeshData(false);
            if (destination is SkinnedMeshRenderer skinnedMeshRenderer) {
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.RecordObject(skinnedMeshRenderer, "Combine Meshes");
                #endif
                skinnedMeshRenderer.sharedMesh = combinedNewMesh;
                var rootBone = skinnedMeshRenderer.rootBone;
                if (rootBone == null) {
                    if (allBones.Count > 0) {
                        rootBone = FindCommonParent(allBones);
                        if (rootBone == null) rootBone = allBones[0];
                        else foreach (var bone in allBones) {
                            if (bone == null) continue;
                            if (bone == rootBone) break;
                            if (bone.parent == rootBone) {
                                rootBone = bone;
                                break;
                            }
                        }
                    }
                    if (rootBone == null) rootBone = skinnedMeshRenderer.transform;
                    skinnedMeshRenderer.rootBone = rootBone;
                }
                var localBounds = bounds;
                var rootBoneInverse = rootBone.worldToLocalMatrix;
                localBounds.center = rootBoneInverse.MultiplyPoint(localBounds.center);
                localBounds.extents = rootBoneInverse.MultiplyVector(localBounds.extents);
                skinnedMeshRenderer.localBounds = localBounds;
                skinnedMeshRenderer.bones = allBones.ToArray();
                foreach (var kv in blendShapesWeights) {
                    var index = combinedNewMesh.GetBlendShapeIndex(kv.Key);
                    if (index >= 0) skinnedMeshRenderer.SetBlendShapeWeight(index, kv.Value);
                }
            } else if (destination is MeshRenderer && destination.TryGetComponent(out MeshFilter meshFilter)) {
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.RecordObject(meshFilter, "Combine Meshes");
                #endif
                meshFilter.sharedMesh = combinedNewMesh;
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.RecordObject(destination, "Combine Meshes");
                #endif
            }
            destination.sharedMaterials = materials.ToArray();
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

        void IDisposable.Dispose() => CleanUp();

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
            if (dummyMaterial != null) DestroyImmediate(dummyMaterial, false);
            dummyMaterial = null;
            if (dummyTransform != null) DestroyImmediate(dummyTransform.gameObject, false);
            dummyTransform = null;
        }

        public void CopyBlendShapes(Mesh combinedNewMesh, IEnumerable<(CombineInstance, CombineBlendshapeFlags[])> combineInstances, bool useRemappedName = false) {
            if (!LazyInitialize(ref blendShapesStore)) blendShapesStore.Clear();
            int offset = 0;
            var supportedCopyMode = BlendShapeCopyMode.None;
            foreach (var (entry, bakeFlags) in combineInstances) {
                var mesh = entry.mesh;
                var subMeshIndex = entry.subMeshIndex;
                var subMesh = mesh.GetSubMesh(subMeshIndex);
                for (int k = 0; k < mesh.blendShapeCount; k++) {
                    if (bakeFlags[k] != CombineBlendshapeFlags.None) continue;
                    string key = mesh.GetBlendShapeName(k);
                    LazyInitialize(blendShapesStore, key, out var timeline);
                    timeline.AddFrom(mesh, subMeshIndex, k, offset, entry.transform);
                    supportedCopyMode |= GetSupportedBlendShapeCopyMode(mesh);
                }
                offset += subMesh.vertexCount;
            }
            foreach (var timeline in blendShapesStore) {
                var key = timeline.Key;
                if (useRemappedName && blendShapeRemap != null && blendShapeRemap.TryGetValue(key, out var remappedName))
                    key = remappedName;
                timeline.Value.ApplyTo(combinedNewMesh, key, blendShapeCopyMode & supportedCopyMode, ref vntArrayCache, ref vntArrayCache2);
            }
        }

        static void ApplyBlendShape(List<Vector3> source, Vector3[] blendShapeDataPrev, Vector3[] blendShapeDataNext, float lerp, int offset, int count) {
            if (source == null || source.Count == 0) return;
            for (int i = 0; i < count; i++) {
                var index = offset + i;
                source[index] += blendShapeDataPrev == null ? blendShapeDataNext[index] * lerp :
                    blendShapeDataNext == null || lerp <= 0 ? blendShapeDataPrev[index] :
                    Vector3.LerpUnclamped(blendShapeDataPrev[index], blendShapeDataNext[index], lerp);
            }
        }

        static void ApplyBlendShape(List<Vector4> source, Vector3[] blendShapeDataPrev, Vector3[] blendShapeDataNext, float lerp, int offset, int count) {
            if (source == null || source.Count == 0) return;
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
            var copyMode = GetSupportedBlendShapeCopyMode(mesh);
            var vertexCount = mesh.vertexCount;
            var (deltaVertices, deltaNormals, deltaTangents) = GetVNTArrays(ref vntArrayCache, vertices.Count, copyMode);
            int count = mesh.GetBlendShapeFrameCount(blendShapeIndex);
            if (count == 0) return;
            float frameWeight;
            for (int i = 1; i < count; i++) {
                frameWeight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, i);
                if (frameWeight > weight) {
                    mesh.GetBlendShapeFrameVertices(blendShapeIndex, i - 1, deltaVertices, deltaNormals, deltaTangents);
                    var (deltaVertices2, deltaNormals2, deltaTangents2) = GetVNTArrays(ref vntArrayCache2, vertices.Count, copyMode);
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

        List<(CombineInstance, CombineBlendshapeFlags[])> GetCombines(Material material) {
            var materialToIndex = material;
            if (materialToIndex == null) {
                if (dummyMaterial == null) // To prevents null reference exception when material is null.
                    dummyMaterial = new Material(Shader.Find("Hidden/InternalErrorShader"));
                materialToIndex = dummyMaterial;
            }
            if (LazyInitialize(combineInstances, materialToIndex, out var combines))
                materials.Add(material);
            return combines;
        }

        static BlendShapeCopyMode GetSupportedBlendShapeCopyMode(Mesh mesh) {
            var copyMode = BlendShapeCopyMode.None;
            if (mesh.HasVertexAttribute(VertexAttribute.Position)) copyMode |= BlendShapeCopyMode.Vertices;
            if (mesh.HasVertexAttribute(VertexAttribute.Normal)) copyMode |= BlendShapeCopyMode.Normals;
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) copyMode |= BlendShapeCopyMode.Tangents;
            return copyMode;
        }
    }
}