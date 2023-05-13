using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace JLChnToZ.CommonUtils {
    [Serializable]
    public class SerializableDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, ISerializationCallbackReceiver {
        IDictionary<TKey, TValue> dict;
        [SerializeField] bool isSorted;
        [SerializeField] TKey[] keys;
        [SerializeField] TValue[] values;
        #if UNITY_EDITOR
        bool isDirty;
        #endif

        public TValue this[TKey key] {
            get => dict[key];
            set {
                #if UNITY_EDITOR
                isDirty = true;
                #endif
                dict[key] = value;
            }
        }

        public int Count => dict.Count;

        public ICollection<TKey> Keys => dict.Keys;

        public ICollection<TValue> Values => dict.Values;

        public bool IsSorted => isSorted;

        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => dict.Keys;

        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => dict.Values;

        public SerializableDictionary() => dict = new Dictionary<TKey, TValue>();

        public SerializableDictionary(bool isSorted) {
            this.isSorted = isSorted;
            dict = isSorted ? new SortedDictionary<TKey, TValue>() as IDictionary<TKey, TValue> : new Dictionary<TKey, TValue>() as IDictionary<TKey, TValue>;
        }

        public SerializableDictionary(IEqualityComparer<TKey> comparer) {
            dict = new Dictionary<TKey, TValue>(comparer);
        }

        public SerializableDictionary(int capacity, IEqualityComparer<TKey> comparer) {
            dict = new Dictionary<TKey, TValue>(capacity, comparer);
        }

        public SerializableDictionary(IComparer<TKey> comparer) {
            isSorted = true;
            dict = new SortedDictionary<TKey, TValue>(comparer);
        }

        public bool ContainsKey(TKey key) => dict?.ContainsKey(key) ?? false;

        public void Add(TKey key, TValue value) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            dict.Add(key, value);
        }

        public bool Remove(TKey key) {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            return dict.Remove(key);
        }

        public void Clear() {
            #if UNITY_EDITOR
            isDirty = true;
            #endif
            dict.Clear();
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize() {
            int length = Math.Min(keys?.Length ?? 0, values?.Length ?? 0);
            if (dict == null && (isSorted ? dict is Dictionary<TKey, TValue> : dict is SortedDictionary<TKey, TValue>))
                dict = isSorted ? new SortedDictionary<TKey, TValue>() as IDictionary<TKey, TValue> : new Dictionary<TKey, TValue>(length) as IDictionary<TKey, TValue>;
            else dict.Clear();
            for (int i = 0; i < length; i++)
                if (keys[i] != null && !dict.ContainsKey(keys[i]))
                    dict.Add(keys[i], values[i]);
            #if UNITY_EDITOR
            isDirty = false;
            #endif
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize() {
            #if UNITY_EDITOR
            if (!isDirty) return;
            isSorted = dict is SortedDictionary<TKey, TValue>;
            if (keys == null || keys.Length != dict.Count) keys = new TKey[dict.Count];
            if (values == null || values.Length != dict.Count) values = new TValue[dict.Count];
            int i = 0;
            foreach (var item in dict) {
                keys[i] = item.Key;
                values[i] = item.Value;
                i++;
            }
            isDirty = false;
            #endif
        }

        public bool TryGetValue(TKey key, out TValue value) => dict.TryGetValue(key, out value);

        IEnumerator IEnumerable.GetEnumerator() => dict.GetEnumerator();

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item) => dict?.ContainsKey(item.Key) ?? false;

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => (dict as ICollection<KeyValuePair<TKey, TValue>>)?.CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => dict.GetEnumerator();

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item) => Remove(item.Key);

    }
}