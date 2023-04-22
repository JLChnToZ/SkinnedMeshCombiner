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
            var streamCutters = Enumerable.Range((int)VertexAttribute.Position, (int)VertexAttribute.BlendWeight)
                .Select(i => VertexStreamCutter.Get(mesh, (VertexAttribute)i))
                .Where(c => c != null).ToArray();
            NativeArray<BoneWeight1>? boneWeights = null;
            NativeArray<byte>? bonesPerVertex = null;
            Matrix4x4[] bindposes = null;
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
                    foreach (var cutter in streamCutters)
                        cutter.Move(i, vertexOffset);
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
            foreach (var cutter in streamCutters)
                cutter.Apply(vertexOffset);
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

        abstract class VertexStreamCutter {
            protected readonly Mesh mesh;
            protected readonly VertexAttribute attribute;

            public static VertexStreamCutter Get(Mesh mesh, VertexAttribute attribute) {
                if (!mesh.HasVertexAttribute(attribute)) return null;
                switch (attribute) {
                    case VertexAttribute.Position: return new VertexStreamCutter3(mesh, attribute);
                    case VertexAttribute.Normal: return new VertexStreamCutter3(mesh, attribute);
                    case VertexAttribute.Tangent: return new VertexStreamCutter4(mesh, attribute);
                    case VertexAttribute.Color:
                        switch (mesh.GetVertexAttributeFormat(attribute)) {
                            case VertexAttributeFormat.UInt8:
                            case VertexAttributeFormat.UNorm8:
                                return new VertexStreamCutter4C32(mesh, attribute);
                            default:
                                return new VertexStreamCutter4C(mesh, attribute);
                        }
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7: 
                        switch (mesh.GetVertexAttributeDimension(attribute)) {
                            case 2: return new VertexStreamCutter2(mesh, attribute);
                            case 3: return new VertexStreamCutter3(mesh, attribute);
                            case 4: return new VertexStreamCutter4(mesh, attribute);
                        }
                        break;
                }
                return null;
            }
            
            protected VertexStreamCutter(Mesh mesh, VertexAttribute attribute) {
                this.mesh = mesh;
                this.attribute = attribute;
            }

            public abstract void Move(int index, int offset);
            public abstract void Apply(int trimSize);
        }

        abstract class VertexStreamCutter<T> : VertexStreamCutter where T : struct {
            protected readonly List<T> stream;
            
            protected VertexStreamCutter(Mesh mesh, VertexAttribute attribute) : base(mesh, attribute) {
                stream = new List<T>(mesh.vertexCount);
            }

            public override void Move(int index, int offset) {
                if (offset <= 0) return;
                stream[index - offset] = stream[index];
            }

            public override void Apply(int trimSize) {
                stream.RemoveRange(stream.Count - trimSize, trimSize);
            }
        }

        sealed class VertexStreamCutter2 : VertexStreamCutter<Vector2> {
            public VertexStreamCutter2(Mesh mesh, VertexAttribute attribute) : base(mesh, attribute) {
                switch (attribute) {
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.GetUVs(attribute - VertexAttribute.TexCoord0, stream);
                        break;
                    default: throw new NotSupportedException();
                }
            }

            public override void Apply(int trimSize) {
                base.Apply(trimSize);
                switch (attribute) {
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.SetUVs(attribute - VertexAttribute.TexCoord0, stream);
                        break;
                    default: throw new NotSupportedException();
                }
            }
        }

        sealed class VertexStreamCutter3 : VertexStreamCutter<Vector3> {
            public VertexStreamCutter3(Mesh mesh, VertexAttribute attribute) : base(mesh, attribute) {
                switch (attribute) {
                    case VertexAttribute.Position: mesh.GetVertices(stream); break;
                    case VertexAttribute.Normal: mesh.GetNormals(stream); break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.GetUVs(attribute - VertexAttribute.TexCoord0, stream);
                        break;
                    default: throw new NotSupportedException();
                }
            }

            public override void Apply(int trimSize) {
                base.Apply(trimSize);
                switch (attribute) {
                    case VertexAttribute.Position: mesh.SetVertices(stream); break;
                    case VertexAttribute.Normal: mesh.SetNormals(stream); break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.SetUVs(attribute - VertexAttribute.TexCoord0, stream);
                        break;
                    default: throw new NotSupportedException();
                }
            }
        }

        sealed class VertexStreamCutter4 : VertexStreamCutter<Vector4> {
            public VertexStreamCutter4(Mesh mesh, VertexAttribute attribute) : base(mesh, attribute) {
                switch (attribute) {
                    case VertexAttribute.Tangent: mesh.GetTangents(stream); break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.GetUVs(attribute - VertexAttribute.TexCoord0, stream);
                        break;
                    default: throw new NotSupportedException();
                }
            }

            public override void Apply(int trimSize) {
                base.Apply(trimSize);
                switch (attribute) {
                    case VertexAttribute.Tangent: mesh.SetTangents(stream); break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.SetUVs(attribute - VertexAttribute.TexCoord0, stream);
                        break;
                    default: throw new NotSupportedException();
                }
            }
        }

        sealed class VertexStreamCutter4C : VertexStreamCutter<Color> {
            public VertexStreamCutter4C(Mesh mesh, VertexAttribute attribute) : base(mesh, attribute) {
                switch (attribute) {
                    case VertexAttribute.Color: mesh.GetColors(stream); break;
                    default: throw new NotSupportedException();
                }
            }

            public override void Apply(int trimSize) {
                base.Apply(trimSize);
                switch (attribute) {
                    case VertexAttribute.Color: mesh.SetColors(stream); break;
                    default: throw new NotSupportedException();
                }
            }
        }

        sealed class VertexStreamCutter4C32 : VertexStreamCutter<Color32> {
            public VertexStreamCutter4C32(Mesh mesh, VertexAttribute attribute) : base(mesh, attribute) {
                switch (attribute) {
                    case VertexAttribute.Color: mesh.GetColors(stream); break;
                    default: throw new NotSupportedException();
                }
            }

            public override void Apply(int trimSize) {
                base.Apply(trimSize);
                switch (attribute) {
                    case VertexAttribute.Color: mesh.SetColors(stream); break;
                    default: throw new NotSupportedException();
                }
            }
        }
    }
}