using System.Collections.Generic;

namespace OrdoMemory.Sample
{
    public abstract class SecondaryIndex<TKey>
    {
        public abstract int Count { get; }

        public abstract bool Insert(TKey key, uint primaryKey);

        public abstract void AddRange(IReadOnlyList<KeyValuePair<TKey, uint>> items);

        public abstract bool Remove(TKey key, uint primaryKey);

        public abstract IEnumerable<uint> Range(TKey min, TKey max);

        public virtual IEnumerable<uint> Equal(TKey key)
        {
            return Range(key, key);
        }
    }
}
