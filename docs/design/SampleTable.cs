using System;
using System.Collections.Generic;
using OrdoMemory.Sample;

namespace OrdoMemory.Design
{
    /// <summary>
    /// SampleSchema (Key = int) に対応する Runtime テーブルのサンプル実装.
    /// 構造は docs/design/RuntimeTable.md に従う.
    /// SampleSchema は SecondaryKey を持たないため、B+Tree 索引は含まない.
    /// </summary>
    public class SampleTable
    {
        // 配列の最大長 (Array.MaxLength 相当. Unity では参照できないため定数で保持する).
        private const int MaxArrayLength = 0x7FFFFFC7;

        // 初期 capacity が 0 の場合の最小拡張長.
        private const int MinGrowLength = 4;

        // 添字 = 主キー - start.
        private readonly int start;

        // 実データ.
        private SampleSchema[] data;

        // スロットの使用状況. 削除済みスロットを All / FindRange で読み飛ばすために使う.
        private bool[] alive;

        // 次に払い出す未使用の Key. この Key 以降は一度も使われていない.
        // int.MaxValue の払い出し後にオーバーフローしないよう long で保持する.
        private long nextFreshKey;

        // 削除によって空いた主キー. 再利用はこちらを優先する.
        private readonly Stack<int> freeKeys = new Stack<int>();

        public int Count { get; private set; }

        public int Capacity => data.Length;

        public SampleTable(int start, int capacity)
        {
            if (capacity < 0 || capacity > MaxLength(start))
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            this.start = start;
            data = new SampleSchema[capacity];
            alive = new bool[capacity];
            nextFreshKey = start;
        }

        // ---- 書き込み ----

        /// <summary>
        /// Record を挿入する. Id は DB 側で割り当て、record.Id に書き戻す.
        /// Key の上限に達している場合は何も挿入せず false を返す.
        /// </summary>
        public bool Insert(SampleSchema record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            if (!TryAllocateKey(out var key)) return false;

            Store(key, record);
            return true;
        }

        /// <summary>
        /// Record を一括挿入する.
        /// 必要な件数分を事前に拡張し、上限を超える場合は何も挿入せず false を返す.
        /// </summary>
        public bool BulkInsert(IReadOnlyList<SampleSchema> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));

            // freeKeys で賄えない分だけ、未使用の Key を払い出す.
            var freshCount = Math.Max(0, records.Count - freeKeys.Count);
            if (!EnsureCapacity(nextFreshKey - start + freshCount)) return false;

            for (var i = 0; i < records.Count; i++)
            {
                // 事前に拡張済みのため失敗しない.
                TryAllocateKey(out var key);
                Store(key, records[i]);
            }

            return true;
        }

        /// <summary>
        /// record.Id に対応する Record を置き換える. 対象が存在しない場合は false を返す.
        /// </summary>
        public bool Update(SampleSchema record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!TryGetAliveIndex(record.Id, out var index)) return false;

            data[index] = record;
            return true;
        }

        /// <summary>
        /// Key に対応する Record を削除する. 対象が存在しない場合は false を返す.
        /// </summary>
        public bool Remove(int key)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;

            data[index] = null;
            alive[index] = false;
            freeKeys.Push(key);
            Count--;
            return true;
        }

        // ---- 読み取り ----

        /// <summary>
        /// Key での単一検索. 存在しない場合は null を返す.
        /// </summary>
        public SampleSchema Find(int key)
        {
            return TryGetAliveIndex(key, out var index) ? data[index] : null;
        }

        /// <summary>
        /// Key の範囲 [min, max] での検索. Key の昇順で返す.
        /// </summary>
        public IEnumerable<SampleSchema> FindRange(int min, int max)
        {
            // 未使用の領域 (nextFreshKey 以降) を読まないよう、範囲を使用済みの Key に絞る.
            var lower = Math.Max((long)min, start);
            var upper = Math.Min((long)max, nextFreshKey - 1);

            for (var key = lower; key <= upper; key++)
            {
                var index = (int)(key - start);
                if (alive[index]) yield return data[index];
            }
        }

        /// <summary>
        /// 全 Record を Key の昇順で返す.
        /// </summary>
        public IEnumerable<SampleSchema> All()
        {
            var used = (int)(nextFreshKey - start);
            for (var index = 0; index < used; index++)
            {
                if (alive[index]) yield return data[index];
            }
        }

        // ---- 内部処理 ----

        // freeKeys を優先して Key を払い出す. 空の場合は nextFreshKey を進める.
        private bool TryAllocateKey(out int key)
        {
            if (freeKeys.Count > 0)
            {
                key = freeKeys.Pop();
                return true;
            }

            if (!EnsureCapacity(nextFreshKey - start + 1))
            {
                key = default;
                return false;
            }

            key = (int)nextFreshKey;
            nextFreshKey++;
            return true;
        }

        private void Store(int key, SampleSchema record)
        {
            var index = (int)((long)key - start);
            record.Id = key;
            data[index] = record;
            alive[index] = true;
            Count++;
        }

        // 範囲内かつ使用中のスロットの添字を求める.
        private bool TryGetAliveIndex(int key, out int index)
        {
            // int 同士の減算はオーバーフローし得るため long で計算する.
            var offset = (long)key - start;
            if (offset < 0 || offset >= nextFreshKey - start)
            {
                index = -1;
                return false;
            }

            index = (int)offset;
            return alive[index];
        }

        // 配列の長さが required 以上になるよう拡張する. 上限を超える場合は何もせず false を返す.
        // 拡張時は data / alive を再確保するため、data への参照は無効となる.
        private bool EnsureCapacity(long required)
        {
            if (required <= data.Length) return true;

            var max = MaxLength(start);
            if (required > max) return false;

            var doubled = Math.Max((long)data.Length * 2, MinGrowLength);
            var newLength = (int)Math.Min(Math.Max(required, doubled), max);

            Array.Resize(ref data, newLength);
            Array.Resize(ref alive, newLength);
            return true;
        }

        // 配列長の上限. 以下の小さいほうとする.
        // - 配列の最大長
        // - start + 長さ - 1 が int.MaxValue 以下となる長さ
        private static int MaxLength(int start)
        {
            return (int)Math.Min(MaxArrayLength, (long)int.MaxValue - start + 1);
        }
    }
}
