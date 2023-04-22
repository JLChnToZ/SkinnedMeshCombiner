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
using Unity.Collections;

namespace JLChnToZ.EditorExtensions.SkinnedMeshCombiner {
    public class VertexCutter {
        readonly Mesh mesh;
        readonly HashSet<int> removedVerticeIndecies = new HashSet<int>();
        readonly HashSet<int> aggressiveRemovedVerticeIndecies = new HashSet<int>();

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
            StreamCutter[] streamCutters;
            int[] vertexReferenceCounts;
            {
                var streamCutterList = new List<StreamCutter>();
                for (var i = VertexAttribute.Position; i < VertexAttribute.BlendIndices; i++) {
                    var streamCutter = StreamCutter.Get(mesh, i);
                    if (streamCutter != null) streamCutterList.Add(streamCutter);
                }
                for (int i = 0, count = mesh.blendShapeCount; i < count; i++)
                    for (int j = 0, frameCount = mesh.GetBlendShapeFrameCount(i); j < frameCount; j++)
                        streamCutterList.Add(new BlendShapeCutter(mesh, i, j));
                var triangleCutter = new TriangleCutter(mesh, removedVerticeIndecies, aggressiveRemovedVerticeIndecies);
                vertexReferenceCounts = triangleCutter.vertexReferenceCounts;
                streamCutterList.Add(triangleCutter);
                streamCutters = streamCutterList.ToArray();
            }
            Matrix4x4[] bindposes = null;
            if (mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
                bindposes = mesh.bindposes;
            for (int i = 0, length = mesh.vertexCount; i < length; i++) {
                bool skip = vertexReferenceCounts[i] == 0;
                foreach (var cutter in streamCutters) cutter.Next(skip);
            }
            mesh.Clear(true);
            mesh.ClearBlendShapes();
            foreach (var cutter in streamCutters) cutter.Apply();
            if (bindposes != null) mesh.bindposes = bindposes;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            removedVerticeIndecies.Clear();
            aggressiveRemovedVerticeIndecies.Clear();
            return mesh;
        }


        abstract class StreamCutter {
            protected readonly Mesh mesh;
            protected int index, offset;

            public static StreamCutter Get(Mesh mesh, VertexAttribute attribute) {
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
                    case VertexAttribute.BlendWeight: return new BoneCutter(mesh);
                }
                return null;
            }
            
            protected StreamCutter(Mesh mesh) {
                this.mesh = mesh;
            }

            public virtual void Next(bool skip) {
                if (skip) offset++;
                else if (offset > 0) Move();
                index++;
            }

            protected abstract void Move();
            public abstract void Apply();
        }

        abstract class VertexStreamCutter<T> : StreamCutter where T : struct {
            protected readonly VertexAttribute attribute;
            protected readonly List<T> stream;
            
            protected VertexStreamCutter(Mesh mesh, VertexAttribute attribute) : base(mesh) {
                this.attribute = attribute;
                stream = new List<T>(mesh.vertexCount);
            }

            protected override void Move() {
                stream[index - offset] = stream[index];
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

            public override void Apply() {
                switch (attribute) {
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.SetUVs(attribute - VertexAttribute.TexCoord0, stream, 0, stream.Count - offset);
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

            public override void Apply() {
                switch (attribute) {
                    case VertexAttribute.Position: mesh.SetVertices(stream, 0, stream.Count - offset); break;
                    case VertexAttribute.Normal: mesh.SetNormals(stream, 0, stream.Count - offset); break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.SetUVs(attribute - VertexAttribute.TexCoord0, stream, 0, stream.Count - offset);
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

            public override void Apply() {
                switch (attribute) {
                    case VertexAttribute.Tangent: mesh.SetTangents(stream, 0, stream.Count - offset); break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                        mesh.SetUVs(attribute - VertexAttribute.TexCoord0, stream, 0, stream.Count - offset);
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

            public override void Apply() {
                switch (attribute) {
                    case VertexAttribute.Color: mesh.SetColors(stream, 0, stream.Count - offset); break;
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

            public override void Apply() {
                switch (attribute) {
                    case VertexAttribute.Color: mesh.SetColors(stream, 0, stream.Count - offset); break;
                    default: throw new NotSupportedException();
                }
            }
        }

        sealed class BlendShapeCutter : StreamCutter {
            readonly float weight;
            readonly string name;
            Vector3[] deltaVertices, deltaNormals, deltaTangents;

            public BlendShapeCutter(Mesh mesh, int shapeIndex, int frame) : base(mesh) {
                name = mesh.GetBlendShapeName(shapeIndex);
                weight = mesh.GetBlendShapeFrameWeight(shapeIndex, frame);
                if (mesh.HasVertexAttribute(VertexAttribute.Position)) deltaVertices = new Vector3[mesh.vertexCount];
                if (mesh.HasVertexAttribute(VertexAttribute.Normal)) deltaNormals = new Vector3[mesh.vertexCount];
                if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) deltaTangents = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(shapeIndex, frame, deltaVertices, deltaNormals, deltaTangents);
            }

            protected override void Move() {
                if (deltaVertices != null) deltaVertices[index - offset] = deltaVertices[index];
                if (deltaNormals != null) deltaNormals[index - offset] = deltaNormals[index];
                if (deltaTangents != null) deltaTangents[index - offset] = deltaTangents[index];
            }

            public override void Apply() {
                Array.Resize(ref deltaVertices, deltaVertices.Length - offset);
                Array.Resize(ref deltaNormals, deltaNormals.Length - offset);
                Array.Resize(ref deltaTangents, deltaTangents.Length - offset);
                mesh.AddBlendShapeFrame(name, weight, deltaVertices, deltaNormals, deltaTangents);
            }
        }

        sealed class BoneCutter : StreamCutter {
            NativeArray<BoneWeight1> boneWeights;
            NativeArray<byte> bonesPerVertex;
            int boneWeightIndex, boneWeightOffset;

            public BoneCutter(Mesh mesh) : base(mesh) {
                var readonlyBoneWeights = mesh.GetAllBoneWeights();
                boneWeights = new NativeArray<BoneWeight1>(readonlyBoneWeights.Length, Allocator.Temp);
                readonlyBoneWeights.CopyTo(boneWeights);
                bonesPerVertex = new NativeArray<byte>(mesh.vertexCount, Allocator.Temp);
                mesh.GetBonesPerVertex().CopyTo(bonesPerVertex);
            }

            public override void Next(bool skip) {
                byte bonePerVertex = bonesPerVertex[index];
                if (skip) {
                    offset++;
                    boneWeightOffset += bonePerVertex;
                } else if (offset > 0) Move();
                index++;
                boneWeightIndex += bonePerVertex;
            }

            protected override void Move() {
                bonesPerVertex[index - offset] = bonesPerVertex[index];
                for (int i = 0; i < bonesPerVertex[index]; i++)
                    boneWeights[boneWeightIndex - boneWeightOffset + i] = boneWeights[boneWeightIndex + i];
            }

            public override void Apply() {
                mesh.SetBoneWeights(
                    bonesPerVertex.GetSubArray(0, bonesPerVertex.Length - offset),
                    boneWeights.GetSubArray(0, boneWeights.Length - boneWeightOffset)
                );
                bonesPerVertex.Dispose();
                boneWeights.Dispose();
            }
        }

        sealed class TriangleCutter : StreamCutter {
            public readonly int[] vertexReferenceCounts;
            readonly List<int> triangles;
            readonly Vector3Int[][] streams;
            readonly int[] vertexMapping;
            readonly int triangleIndexCount;

            public TriangleCutter(Mesh mesh, HashSet<int> removedVerticeIndecies, HashSet<int> aggressiveRemovedVerticeIndecies) : base(mesh) {
                streams = new Vector3Int[mesh.subMeshCount][];
                vertexReferenceCounts = new int[mesh.vertexCount];
                vertexMapping = new int[mesh.vertexCount];
                triangles = new List<int>();
                triangleIndexCount = 0;
                for (int i = 0; i < streams.Length; i++) {
                    mesh.GetTriangles(triangles, i);
                    var triangleStream = new Vector3Int[triangles.Count / 3];
                    streams[i] = triangleStream;
                    for (int j = 0; j < triangleStream.Length; j++) {
                        int offset = j * 3;
                        var entry = new Vector3Int(triangles[offset], triangles[offset + 1], triangles[offset + 2]);
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
            }

            public override void Next(bool skip) {
                vertexMapping[index] = index - offset;
                base.Next(skip);
            }

            protected override void Move() {}

            public override void Apply() {
                mesh.triangles = new int[triangleIndexCount];
                mesh.subMeshCount = streams.Length;
                for (int i = 0, offset = 0; i < streams.Length; i++) {
                    var triangleStream = streams[i];
                    triangles.Clear();
                    var requiredCapacity = triangleStream.Length * 3;
                    if (triangles.Capacity < requiredCapacity) triangles.Capacity = requiredCapacity;
                    foreach (var entry in triangleStream) {
                        if (entry.x < 0) continue;
                        triangles.Add(vertexMapping[entry.x]);
                        triangles.Add(vertexMapping[entry.y]);
                        triangles.Add(vertexMapping[entry.z]);
                    }
                    int count = triangles.Count;
                    mesh.SetSubMesh(i, new SubMeshDescriptor(offset, count, MeshTopology.Triangles));
                    mesh.SetTriangles(triangles, i);
                    offset += count;
                }
            }
        }
    }
}