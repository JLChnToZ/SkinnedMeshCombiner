using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace JLChnToZ.CommonUtils {
    [Serializable]
    public class SerializableSet<T> : ISet<T>, IReadOnlyCollection<T>, ISerializationCallbackReceiver {
        ISet<T> set;
        [SerializeField] bool isSorted;
        [SerializeField] T[] values;
        #if UNITY_EDITOR
        bool isDirty;
        #endif

        public int Count => set.Count;

        bool ICollection<T>.IsReadOnly => false;

        public bool IsSorted => isSorted;

        public SerializableSet() => set = new HashSet<T>();

        public SerializableSet(bool isSorted) {
            this.isSorted = isSorted;
            set = isSorted ? new SortedSet<T>() as ISet<T> : new HashSet<T>() as ISet<T>;
        }

        public SerializableSet(IEqualityComparer<T> comparer) {
            set = new HashSet<T>(comparer);
        }

        public SerializableSet(IComparer<T> comparer) {
            isSorted = true;
            set = new SortedSet<T>(comparer);
        }

        public bool Contains(T key) => set?.Contains(key) ?? false;

        public bool IsProperSubsetOf(IEnumerable<T> other) => set?.IsProperSubsetOf(other) ?? false;

        public bool IsProperSupersetOf(IEnumerable<T> other) => set?.IsProperSupersetOf(other) ?? false;

        public bool IsSubsetOf(IEnumerable<T> other) => set?.IsSubsetOf(other) ?? false;

        public bool IsSupersetOf(IEnumerable<T> other) => set?.IsSupersetOf(other) ?? false;

        public bool Overlaps(IEnumerable<T> other) => set?.Overlaps(other) ?? false;

        public bool SetEquals(IEnumerable<T> other) => set?.SetEquals(other) ?? false;

        public bool Add(T item) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            return set.Add(item);
        }

        public void ExceptWith(IEnumerable<T> other) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            set.ExceptWith(other);
        }

        public void IntersectWith(IEnumerable<T> other) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            set.IntersectWith(other);
        }

        public void SymmetricExceptWith(IEnumerable<T> other) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            set.SymmetricExceptWith(other);
        }

        public void UnionWith(IEnumerable<T> other) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            set.UnionWith(other);
        }

        public bool Remove(T key) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            return set.Remove(key);
        }

        public void Clear() {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            set.Clear();
        }

        public void CopyTo(T[] array, int arrayIndex) => set.CopyTo(array, arrayIndex);

        public IEnumerator<T> GetEnumerator() => set.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => set.GetEnumerator();

        void ISerializationCallbackReceiver.OnBeforeSerialize() {
            #if UNITY_EDITOR
            if (!isDirty) return;
            isDirty = false;
            isSorted = set is SortedSet<T>;
            values = new T[set.Count];
            set.CopyTo(values, 0);
            #endif
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize() {
            if (set == null || (isSorted ? set is HashSet<T> : set is SortedSet<T>))
                set = isSorted ? new SortedSet<T>(values) as ISet<T> : new HashSet<T>(values) as ISet<T>;
            else {
                set.Clear();
                set.UnionWith(values);
            }
            #if UNITY_EDITOR
            isDirty = false;
            #endif
        }

        void ICollection<T>.Add(T key) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            set.Add(key);
        }
    }
}