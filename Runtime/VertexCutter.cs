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

namespace JLChnToZ {
    /// <summary>Helper class that safely cuts (removes) vertices from a mesh.</summary>
    public class VertexCutter : IDisposable {
        readonly HashSet<int> removedVerticeIndecies = new HashSet<int>();
        readonly HashSet<int> aggressiveRemovedVerticeIndecies = new HashSet<int>();
        StreamCutter[] streamCutters;
        readonly TriangleCutter triangleCutter;
        readonly int vertexCount;
        readonly bool supportsSkipToFirstVertex;
        bool isFlushed;
        int firstVertex = int.MaxValue;

        /// <summary><c>true</c> to skip empty sub-meshes.</summary>
        /// <remarks>
        /// When this is <c>true</c>, <see cref="SkippedSubMeshes"/> will be set after flushed.
        /// You can use <see cref="SkippedSubMeshes"/> to determine which sub-meshes are skipped.
        /// </remarks>
        public bool SkipEmptySubMeshes {
            get => triangleCutter.skipEmptySubMeshes;
            set => triangleCutter.skipEmptySubMeshes = value;
        }

        /// <summary>Is this cutter flushed.</summary>
        public bool IsFlushed => isFlushed;

        /// <summary>
        /// Gets an array of booleans indicating whether a sub-mesh is skipped after removing vertices.
        /// <c>null</c> if <see cref="SkipEmptySubMeshes"/> is <c>false</c> or not yet flushed.
        /// </summary>
        public bool[] SkippedSubMeshes => triangleCutter.skippedSubMeshes;

        /// <summary>Construct a new instance.</summary>
        /// <param name="mesh">Mesh to cut.</param>
        /// <exception cref="ArgumentNullException">If <paramref name="mesh"/> is null.</exception>
        /// <remarks>The provided mesh in here should not be modified externally until this cutter is flushed.
        /// Also the mesh will not be altered unless you pass it again when calling <see cref="Apply"/>.</remarks>
        public VertexCutter(Mesh mesh) {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            vertexCount = mesh.vertexCount;
            supportsSkipToFirstVertex = true;
            var streamCutterList = new List<StreamCutter>();
            foreach (var attribute in Enum.GetValues(typeof(VertexAttribute)) as VertexAttribute[]) {
                var streamCutter = StreamCutter.Get(mesh, attribute);
                if (streamCutter == null) continue;
                streamCutterList.Add(streamCutter);
                if (!streamCutter.SupportsSkipToFirstVertex)
                    supportsSkipToFirstVertex = false;
            }
            for (int i = 0, count = mesh.blendShapeCount; i < count; i++)
                for (int j = 0, frameCount = mesh.GetBlendShapeFrameCount(i); j < frameCount; j++)
                    streamCutterList.Add(new BlendShapeCutter(mesh, i, j));
            triangleCutter = new TriangleCutter(mesh, removedVerticeIndecies, aggressiveRemovedVerticeIndecies);
            streamCutterList.Add(triangleCutter);
            streamCutters = streamCutterList.ToArray();
        }

        /// <summary>Remove a vertex from mesh.</summary>
        /// <param name="index">Index of vertex to remove.</param>
        /// <param name="aggressive">If true, the associated triangle will be removed regardless consists of retained vertices.</param>
        /// <exception cref="ObjectDisposedException">Thrown when this cutter is disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when trying to remove vertex after flush.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when index is out of range.</exception>
        public void RemoveVertex(int index, bool aggressive = false) {
            if (streamCutters == null) throw new ObjectDisposedException(nameof(VertexCutter));
            if (isFlushed) throw new InvalidOperationException("Cannot remove vertex after flush.");
            if (index < 0 || index >= vertexCount)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index out of range.");
            if (aggressive)
                aggressiveRemovedVerticeIndecies.Add(index);
            else
                removedVerticeIndecies.Add(index);
            if (index < firstVertex) firstVertex = index;
        }

        /// <summary>Flush all pending changes.</summary>
        /// <exception cref="ObjectDisposedException">Thrown when this cutter is disposed.</exception>
        /// <remarks>You can't remove more vertices after flush unless you construct a new <see cref="VertexCutter"/>.</remarks>
        public void Flush() {
            if (streamCutters == null) throw new ObjectDisposedException(nameof(VertexCutter));
            if (isFlushed) return;
            triangleCutter.BeforeFlush();
            int firstVertex = 0;
            if (supportsSkipToFirstVertex) {
                foreach (var cutter in streamCutters) cutter.Skip(firstVertex);
                firstVertex = this.firstVertex;
            }
            for (int i = firstVertex; i < vertexCount; i++) {
                bool skip = triangleCutter.vertexReferenceCounts[i] == 0;
                foreach (var cutter in streamCutters) cutter.Next(skip);
            }
            triangleCutter.AfterFlush();
            removedVerticeIndecies.Clear();
            aggressiveRemovedVerticeIndecies.Clear();
            isFlushed = true;
        }

        /// <summary>Apply changes to a mesh.</summary>
        /// <param name="mesh">Mesh to apply changes to.</param>
        /// <returns>Mesh with changes applied.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this cutter is disposed.</exception>
        public Mesh Apply(Mesh mesh = null) {
            if (streamCutters == null) throw new ObjectDisposedException(nameof(VertexCutter));
            if (!isFlushed) Flush();
            if (mesh == null)
                mesh = new Mesh();
            else {
                mesh.Clear(true);
                mesh.ClearBlendShapes();
            }
            foreach (var cutter in streamCutters) cutter.Apply(mesh);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }

        /// <summary>Apply material list changes if any.</summary>
        /// <param name="materials">Material list to apply changes to.</param>
        /// <returns>Material list with changes applied.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="materials"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when this cutter is disposed.</exception>
        /// <exception cref="ArgumentException">Thrown when material count mismatch.</exception>
        /// <remarks>No effects if <see cref="SkipEmptySubMeshes"/> is <c>false</c> or not yet flushed.</remarks>
        public Material[] ApplyMaterialChanges(Material[] materials) {
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            if (streamCutters == null) throw new ObjectDisposedException(nameof(VertexCutter));
            var skippedSubMeshes = triangleCutter.skippedSubMeshes;
            if (skippedSubMeshes == null) return materials; // No changes
            if (skippedSubMeshes.Length != materials.Length)
                throw new ArgumentException("Material count mismatch.", nameof(materials));
            int subMeshCount = 0;
            foreach (var skipped in skippedSubMeshes)
                if (!skipped) subMeshCount++;
            var newMaterials = new Material[subMeshCount];
            for (int i = 0, j = 0; i < materials.Length; i++)
                if (!skippedSubMeshes[i])
                    newMaterials[j++] = materials[i];
            return newMaterials;
        }

        /// <summary>Dispose this cutter.</summary>
        public void Dispose() {
            if (streamCutters == null) return;
            foreach (var cutter in streamCutters) cutter?.Dispose();
            streamCutters = null;
        }

        /// <summary>Remove vertices from a mesh.</summary>
        /// <param name="source">Source mesh.</param>
        /// <param name="indexes">Indexes of vertices to remove.</param>
        /// <returns>A new mesh with vertices removed.</returns>
        /// <remarks>All submeshes will be kept regardless of empty or not.</remarks>
        public static Mesh CutVertices(Mesh source, IEnumerable<int> indexes) {
            using (var cutter = new VertexCutter(source)) {
                foreach (var index in indexes) cutter.RemoveVertex(index, true);
                return cutter.Apply();
            }
        }

        /// <summary>Remove vertices from a mesh.</summary>
        /// <param name="source">Source mesh.</param>
        /// <param name="materials">Material list of source mesh.</param>
        /// <param name="indexes">Indexes of vertices to remove.</param>
        /// <returns>A new mesh with vertices removed.</returns>
        /// <remarks>If theres any submeshes become empty after removing vertices, they will be removed.</remarks>
        public static Mesh CutVertices(Mesh source, ref Material[] materials, IEnumerable<int> indexes) {
            using (var cutter = new VertexCutter(source) { SkipEmptySubMeshes = true }) {
                foreach (var index in indexes) cutter.RemoveVertex(index, true);
                var newMesh = cutter.Apply();
                materials = cutter.ApplyMaterialChanges(materials);
                return newMesh;
            }
        }

        abstract class StreamCutter : IDisposable {
            protected int index, offset;

            public virtual bool SupportsSkipToFirstVertex => true;

            public static StreamCutter Get(Mesh mesh, VertexAttribute attribute) {
                if (!mesh.HasVertexAttribute(attribute) && attribute != VertexAttribute.BlendWeight) return null;
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
                    case VertexAttribute.BlendWeight:
                        if (mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
                            return new BoneCutter(mesh);
                        break;
                    case VertexAttribute.BlendIndices:
                        if (!mesh.HasVertexAttribute(VertexAttribute.BlendWeight))
                            Debug.LogWarning("BlendIndices stream found but BlendWeight stream not found.");
                        return new BindposeCutter(mesh);
                }
                return null;
            }
            
            protected StreamCutter() { }

            public virtual void Skip(int index) {
                this.index += index;
            }

            public virtual void Next(bool skip) {
                if (skip) offset++;
                else if (offset > 0) Move();
                index++;
            }

            protected abstract void Move();
            public abstract void Apply(Mesh mesh);
            public virtual void Dispose() { }
        }

        abstract class VertexStreamCutter<T> : StreamCutter where T : struct {
            protected readonly VertexAttribute attribute;
            protected readonly List<T> stream;
            
            protected VertexStreamCutter(Mesh mesh, VertexAttribute attribute) {
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

            public override void Apply(Mesh mesh) {
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

            public override void Apply(Mesh mesh) {
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

            public override void Apply(Mesh mesh) {
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

            public override void Apply(Mesh mesh) {
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

            public override void Apply(Mesh mesh) {
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
            bool hasTrimmed;

            public BlendShapeCutter(Mesh mesh, int shapeIndex, int frame) {
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

            public override void Apply(Mesh mesh) {
                if (!hasTrimmed) {
                    hasTrimmed = true;
                    if (deltaVertices != null) Array.Resize(ref deltaVertices, deltaVertices.Length - offset);
                    if (deltaNormals != null) Array.Resize(ref deltaNormals, deltaNormals.Length - offset);
                    if (deltaTangents != null) Array.Resize(ref deltaTangents, deltaTangents.Length - offset);
                }
                mesh.AddBlendShapeFrame(name, weight, deltaVertices, deltaNormals, deltaTangents);
            }
        }

        sealed class BoneCutter : StreamCutter {
            NativeArray<BoneWeight1> boneWeights;
            NativeArray<byte> bonesPerVertex;
            int boneWeightIndex, boneWeightOffset;

            public override bool SupportsSkipToFirstVertex => false;

            public BoneCutter(Mesh mesh) {
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

            public override void Skip(int index) => throw new NotSupportedException();

            protected override void Move() {
                bonesPerVertex[index - offset] = bonesPerVertex[index];
                NativeArray<BoneWeight1>.Copy(boneWeights, boneWeightIndex, boneWeights, boneWeightIndex - boneWeightOffset, bonesPerVertex[index]);
            }

            public override void Apply(Mesh mesh) {
                mesh.SetBoneWeights(
                    bonesPerVertex.GetSubArray(0, bonesPerVertex.Length - offset),
                    boneWeights.GetSubArray(0, boneWeights.Length - boneWeightOffset)
                );
            }

            public override void Dispose() {
                boneWeights.Dispose();
                bonesPerVertex.Dispose();
            }
        }

        sealed class TriangleCutter : StreamCutter {
            readonly Mesh mesh;
            public readonly int[] vertexReferenceCounts;
            readonly List<int> triangles;
            readonly Vector3Int[][] streams;
            readonly int[] vertexMapping;
            int triangleIndexCount;
            int subMeshCount;
            readonly HashSet<int> removedVerticeIndecies, aggressiveRemovedVerticeIndecies;
            public bool skipEmptySubMeshes;
            public bool[] skippedSubMeshes;

            public TriangleCutter(Mesh mesh, HashSet<int> removedVerticeIndecies, HashSet<int> aggressiveRemovedVerticeIndecies) {
                this.mesh = mesh;
                this.removedVerticeIndecies = removedVerticeIndecies;
                this.aggressiveRemovedVerticeIndecies = aggressiveRemovedVerticeIndecies;
                subMeshCount = mesh.subMeshCount;
                streams = new Vector3Int[subMeshCount][];
                vertexReferenceCounts = new int[mesh.vertexCount];
                vertexMapping = new int[mesh.vertexCount];
                triangles = new List<int>();
                triangleIndexCount = 0;
            }

            public void BeforeFlush() {
                for (int i = 0; i < streams.Length; i++) {
                    mesh.GetTriangles(triangles, i);
                    var triangleStream = new Vector3Int[triangles.Count / 3];
                    streams[i] = triangleStream;
                    for (int j = 0; j < triangleStream.Length; j++) {
                        int offset = j * 3;
                        var entry = new Vector3Int(triangles[offset], triangles[offset + 1], triangles[offset + 2]);
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
                            triangleStream[j] = entry;
                            triangleIndexCount += 3;
                        }
                    }
                }
            }

            public override void Next(bool skip) {
                vertexMapping[index] = index - offset + 1;
                base.Next(skip);
            }

            protected override void Move() {}

            public void AfterFlush() {
                if (skipEmptySubMeshes) {
                    skippedSubMeshes = new bool[subMeshCount];
                    for (int i = 0; i < streams.Length; i++) {
                        bool skip = true;
                        foreach (var entry in streams[i])
                            if (entry.x >= 0) {
                                skip = false;
                                break;
                            }
                        skippedSubMeshes[i] = skip;
                        if (skip) subMeshCount--;
                    }
                } else
                    skippedSubMeshes = null;
            }

            public override void Apply(Mesh mesh) {
                mesh.triangles = new int[triangleIndexCount];
                mesh.subMeshCount = subMeshCount;
                for (int i = 0, subMeshIndexOffset = 0, offset = 0; i < streams.Length; i++) {
                    if (skipEmptySubMeshes && skippedSubMeshes[i]) {
                        subMeshIndexOffset++;
                        continue;
                    }
                    var triangleStream = streams[i];
                    triangles.Clear();
                    foreach (var entry in triangleStream) {
                        if (entry.x < 0) continue;
                        AppendVertex(entry, 0);
                        AppendVertex(entry, 1);
                        AppendVertex(entry, 2);
                    }
                    int count = triangles.Count;
                    mesh.SetSubMesh(i - subMeshIndexOffset, new SubMeshDescriptor(offset, count));
                    mesh.SetTriangles(triangles, i - subMeshIndexOffset);
                    offset += count;
                }
            }

            void AppendVertex(Vector3Int triangle, int i) {
                int srcIndex = triangle[i];
                int destIndex = vertexMapping[srcIndex] - 1;
                triangles.Add(destIndex < 0 ? srcIndex : destIndex);
            }
        }

        sealed class BindposeCutter : StreamCutter {
            readonly Matrix4x4[] bindposes;

            public BindposeCutter(Mesh mesh) {
                bindposes = mesh.bindposes;
            }

            public override void Skip(int index) {} // Nothing to do with bindposes

            public override void Next(bool skip) {} // Nothing to do with bindposes

            protected override void Move() {} // Nothing to do with bindposes

            public override void Apply(Mesh mesh) {
                mesh.bindposes = bindposes;
            }
        }
    }
}