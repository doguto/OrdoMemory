using System.Collections.Generic;

namespace OrdoMemory.Sample
{
    /// <summary>
    /// SecondaryKey の索引の契約. このサンプルでは契約のみを定義する.
    /// 実装 (生成コード) では、種類 (Ordered / Hash / Bitmap) ごとの具象型と struct の列挙子を使い、
    /// 仮想呼び出しと列挙のアロケーションを無くす.
    /// 非 Unique の索引のキーは (SecondaryKey, PK) の組とし、SecondaryKey が重複しても組としては一意になる.
    /// 索引が保持する値は Record の PK (ハンドル) のみ.
    /// </summary>
    public abstract class SecondaryIndex<TKey>
    {
        /// <summary>索引に登録されている組の数.</summary>
        public abstract int Count { get; }

        /// <summary>(key, pk) を登録する. 既に同じ組がある場合は何もせず false を返す.</summary>
        public abstract bool Insert(TKey key, int pk);

        /// <summary>
        /// 既存の内容に items を加える. 件数が既存に比べて多い場合は、整列して作り直す.
        /// 完全に同じ組は 1 件にまとめる (Insert と同じ扱い).
        /// </summary>
        public abstract void AddRange(IReadOnlyList<KeyValuePair<TKey, int>> items);

        /// <summary>(key, pk) を削除する. 存在しない場合は false を返す.</summary>
        public abstract bool Remove(TKey key, int pk);

        /// <summary>min 以上 max 以下の SecondaryKey を持つ PK を、(SecondaryKey, PK) の昇順で返す.</summary>
        public abstract IEnumerable<int> Range(TKey min, TKey max);

        /// <summary>SecondaryKey が key と等しい PK を返す.</summary>
        public virtual IEnumerable<int> Equal(TKey key)
        {
            return Range(key, key);
        }
    }
}
