using System.Collections.Generic;

namespace OrdoMemory.Sample
{
    /// <summary>
    /// SecondaryKey の索引. 実装は B+Tree を想定するが、このサンプルでは契約のみを定義する.
    /// 索引のキーは (SecondaryKey, PrimaryKey) の組とし、SecondaryKey が重複しても組としては一意になる.
    /// 索引が保持する値は Record の PrimaryKey のみ.
    /// </summary>
    public abstract class SecondaryIndex<TKey>
    {
        /// <summary>索引に登録されている組の数.</summary>
        public abstract int Count { get; }

        /// <summary>(key, primary) を登録する. 既に同じ組がある場合は何もせず false を返す.</summary>
        public abstract bool Insert(TKey key, int primary);

        /// <summary>
        /// 既存の内容と items を合わせて、一括構築する.
        /// 1 件ずつ Insert するよりも分割が起きず、充填率も揃う. 完全に同じ組は 1 件にまとめる (Insert と同じ扱い).
        /// </summary>
        public abstract void AddRange(IReadOnlyList<KeyValuePair<TKey, int>> items);

        /// <summary>(key, primary) を削除する. 存在しない場合は false を返す.</summary>
        public abstract bool Remove(TKey key, int primary);

        /// <summary>min 以上 max 以下の SecondaryKey を持つ PrimaryKey を、(SecondaryKey, PrimaryKey) の昇順で返す.</summary>
        public abstract IEnumerable<int> Range(TKey min, TKey max);

        /// <summary>SecondaryKey が key と等しい PrimaryKey を昇順で返す.</summary>
        public virtual IEnumerable<int> Equal(TKey key)
        {
            return Range(key, key);
        }
    }
}
