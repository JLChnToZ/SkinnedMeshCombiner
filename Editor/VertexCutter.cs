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
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    public class VertexCutter {
        readonly Mesh mesh;
        HashSet<int> removedVerticeIndecies = new HashSet<int>();
        HashSet<int> aggressiveRemovedVerticeIndecies = new HashSet<int>();

        public VertexCutter(Mesh mesh) {
            this.mesh = mesh;
        }

        public void RemoveVertex(int index, bool aggressive = false) {
            if (aggressive)
                aggressiveRemovedVerticeIndecies.Add(index);
            else
                removedVerticeIndecies.Add(index);
        }

        public Mesh Apply() {
            if (aggressiveRemovedVerticeIndecies.Count == 0 && removedVerticeIndecies.Count == 0)
                return mesh;
            List<Vector3> vertices = null;
            List<Vector3> normals = null;
            List<Vector4> tangents = null;
            List<Vector2> uvs = null, uv2s = null, uv3s = null, uv4s = null, uv5s = null, uv6s = null, uv7s = null, uv8s = null;
            List<Color> colors = null;
            NativeArray<BoneWeight1>? boneWeights = null;
            NativeArray<byte>? bonesPerVertex = null;
            Matrix4x4[] bindposes = null;
            if (mesh.HasVertexAttribute(VertexAttribute.Position)) {
                vertices = new List<Vector3>(mesh.vertexCount);
                mesh.GetVertices(vertices);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.Normal)) {
                normals = new List<Vector3>(mesh.vertexCount);
                mesh.GetNormals(normals);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) {
                tangents = new List<Vector4>(mesh.vertexCount);
                mesh.GetTangents(tangents);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) {
                uvs = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(0, uvs);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) {
                uv2s = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(1, uv2s);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord2)) {
                uv3s = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(2, uv3s);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord3)) {
                uv4s = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(3, uv4s);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord4)) {
                uv5s = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(4, uv5s);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord5)) {
                uv6s = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(5, uv6s);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord6)) {
                uv7s = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(6, uv7s);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord7)) {
                uv8s = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(7, uv8s);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.Color)) {
                colors = new List<Color>(mesh.vertexCount);
                mesh.GetColors(colors);
            }
            if (mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
                bindposes = mesh.bindposes;
            if (mesh.HasVertexAttribute(VertexAttribute.BlendWeight)) {
                var array = mesh.GetAllBoneWeights();
                var temp = new NativeArray<BoneWeight1>(array.Length, Allocator.Temp);
                array.CopyTo(temp);
                boneWeights = temp;
                var array2 = new NativeArray<byte>(mesh.vertexCount, Allocator.Temp);
                mesh.GetBonesPerVertex().CopyTo(array2);
                bonesPerVertex = array2;
            }
            var trianglesStreams = new Vector3Int[mesh.subMeshCount][];
            var triangles = new List<int>();
            var vertexReferenceCounts = new int[mesh.vertexCount];
            var vertexMapping = new int[mesh.vertexCount];
            int triangleIndexCount = 0;
            for (int i = 0; i < trianglesStreams.Length; i++) {
                mesh.GetTriangles(triangles, i);
                var triangleStream = new Vector3Int[triangles.Count / 3];
                trianglesStreams[i] = triangleStream;
                for (int j = 0; j < triangleStream.Length; j++) {
                    int offset2 = j * 3;
                    var entry = new Vector3Int(triangles[offset2], triangles[offset2 + 1], triangles[offset2 + 2]);
                    triangleStream[j] = entry;
                    if (aggressiveRemovedVerticeIndecies.Contains(entry.x) ||
                        aggressiveRemovedVerticeIndecies.Contains(entry.y) ||
                        aggressiveRemovedVerticeIndecies.Contains(entry.z) ||
                        (removedVerticeIndecies.Contains(entry.x) &&
                        removedVerticeIndecies.Contains(entry.y) &&
                        removedVerticeIndecies.Contains(entry.z))) {
                        triangleStream[j] = new Vector3Int(-1, -1, -1);
                    } else {
                        vertexReferenceCounts[entry.x]++;
                        vertexReferenceCounts[entry.y]++;
                        vertexReferenceCounts[entry.z]++;
                        triangleIndexCount += 3;
                    }
                }
            }
            var blendShapeArrays = new List<Vector3[]>();
            var blendShapes = new Dictionary<string, Dictionary<float, (Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] deltaTangents)>>();
            for (int i = 0; i < mesh.blendShapeCount; i++) {
                var name = mesh.GetBlendShapeName(i);
                var frames = new Dictionary<float, (Vector3[], Vector3[], Vector3[])>();
                for (int j = 0; j < mesh.GetBlendShapeFrameCount(i); j++) {
                    var frameWeight = mesh.GetBlendShapeFrameWeight(i, j);
                    var deltaVertices = mesh.HasVertexAttribute(VertexAttribute.Position) ? new Vector3[mesh.vertexCount] : null;
                    var deltaNormals = mesh.HasVertexAttribute(VertexAttribute.Normal) ? new Vector3[mesh.vertexCount] : null;
                    var deltaTangents = mesh.HasVertexAttribute(VertexAttribute.Tangent) ? new Vector3[mesh.vertexCount] : null;
                    mesh.GetBlendShapeFrameVertices(i, j, deltaVertices, deltaNormals, deltaTangents);
                    frames.Add(frameWeight, (deltaVertices, deltaNormals, deltaTangents));
                    if (deltaVertices != null) blendShapeArrays.Add(deltaVertices);
                    if (deltaNormals != null) blendShapeArrays.Add(deltaNormals);
                    if (deltaTangents != null) blendShapeArrays.Add(deltaTangents);
                }
                blendShapes.Add(name, frames);
            }
            int vertexOffset = 0, boneWeightOffset = 0;
            for (int i = 0, b = 0, length = mesh.vertexCount; i < length; b += bonesPerVertex.HasValue ? bonesPerVertex.Value[i] : 0, i++) {
                vertexMapping[i] = i - vertexOffset;
                if (vertexReferenceCounts[i] == 0) {
                    vertexOffset++;
                    if (boneWeights != null) boneWeightOffset += bonesPerVertex.Value[i];
                    continue;
                }
                if (vertexOffset > 0) {
                    if (vertices != null) vertices[i - vertexOffset] = vertices[i];
                    if (normals != null) normals[i - vertexOffset] = normals[i];
                    if (tangents != null) tangents[i - vertexOffset] = tangents[i];
                    if (uvs != null) uvs[i - vertexOffset] = uvs[i];
                    if (uv2s != null) uv2s[i - vertexOffset] = uv2s[i];
                    if (uv3s != null) uv3s[i - vertexOffset] = uv3s[i];
                    if (uv4s != null) uv4s[i - vertexOffset] = uv4s[i];
                    if (uv5s != null) uv5s[i - vertexOffset] = uv5s[i];
                    if (uv6s != null) uv6s[i - vertexOffset] = uv6s[i];
                    if (uv7s != null) uv7s[i - vertexOffset] = uv7s[i];
                    if (uv8s != null) uv8s[i - vertexOffset] = uv8s[i];
                    if (colors != null) colors[i - vertexOffset] = colors[i];
                    if (bonesPerVertex.HasValue) {
                        var array = bonesPerVertex.Value;
                        array[i - vertexOffset] = array[i];
                        if (boneWeights.HasValue) {
                            for (int j = 0; j < array[i]; j++) {
                                var array2 = boneWeights.Value;
                                array2[b - boneWeightOffset + j] = array2[b + j];
                            }
                        }
                    }
                    foreach (var blendshape in blendShapeArrays)
                        blendshape[i - vertexOffset] = blendshape[i];
                }
            }
            int newSize = mesh.vertexCount - vertexOffset;
            mesh.Clear(true);
            if (vertices != null) {
                vertices.RemoveRange(newSize, vertexOffset);
                mesh.SetVertices(vertices);
            }
            if (normals != null) {
                normals.RemoveRange(newSize, vertexOffset);
                mesh.SetNormals(normals);
            }
            if (tangents != null) {
                tangents.RemoveRange(newSize, vertexOffset);
                mesh.SetTangents(tangents);
            }
            if (uvs != null) {
                uvs.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(0, uvs);
            }
            if (uv2s != null) {
                uv2s.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(1, uv2s);
            }
            if (uv3s != null) {
                uv3s.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(2, uv3s);
            }
            if (uv4s != null) {
                uv4s.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(3, uv4s);
            }
            if (uv5s != null) {
                uv5s.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(4, uv5s);
            }
            if (uv6s != null) {
                uv6s.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(5, uv6s);
            }
            if (uv7s != null) {
                uv7s.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(6, uv7s);
            }
            if (uv8s != null) {
                uv8s.RemoveRange(newSize, vertexOffset);
                mesh.SetUVs(7, uv8s);
            }
            if (colors != null) {
                colors.RemoveRange(newSize, vertexOffset);
                mesh.SetColors(colors);
            }
            if (boneWeights.HasValue && bonesPerVertex.HasValue) {
                mesh.SetBoneWeights(
                    bonesPerVertex.Value.GetSubArray(0, newSize),
                    boneWeights.Value.GetSubArray(0, boneWeights.Value.Length - boneWeightOffset)
                );
            }
            if (bindposes != null) mesh.bindposes = bindposes;
            mesh.triangles = new int[triangleIndexCount];
            mesh.subMeshCount = trianglesStreams.Length;
            for (int i = 0, triangleCount = 0; i < trianglesStreams.Length; i++) {
                var triangleStream = trianglesStreams[i];
                triangles.Clear();
                var requiredCapacity = triangleStream.Length * 3;
                if (triangles.Capacity < requiredCapacity * 3) triangles.Capacity = requiredCapacity * 3;
                for (int j = 0; j < triangleStream.Length; j++) {
                    var entry = triangleStream[j];
                    if (entry.x < 0) continue;
                    triangles.Add(vertexMapping[entry.x]);
                    triangles.Add(vertexMapping[entry.y]);
                    triangles.Add(vertexMapping[entry.z]);
                }
                mesh.SetSubMesh(i, new SubMeshDescriptor(triangleCount, triangles.Count, MeshTopology.Triangles));
                mesh.SetTriangles(triangles, i);
                triangleCount += triangles.Count;
            }
            mesh.ClearBlendShapes();
            foreach (var kv in blendShapes)
                foreach (var weight in kv.Value.Keys.OrderBy(x => x)) {
                    var (deltaVertices, deltaNormals, deltaTangents) = kv.Value[weight];
                    if (deltaVertices != null) Array.Resize(ref deltaVertices, newSize);
                    if (deltaNormals != null) Array.Resize(ref deltaNormals, newSize);
                    if (deltaTangents != null) Array.Resize(ref deltaTangents, newSize);
                    mesh.AddBlendShapeFrame(kv.Key, weight, deltaVertices, deltaNormals, deltaTangents);
                }
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            removedVerticeIndecies.Clear();
            aggressiveRemovedVerticeIndecies.Clear();
            return mesh;
        }
    }
}