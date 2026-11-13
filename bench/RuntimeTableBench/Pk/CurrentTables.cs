using System;
using System.Collections.Generic;

namespace OrdoMemory.Bench.Pk
{
    // A: PR #10 の SampleTable をそのまま移したもの (二次索引を除く).
    // class の Record, keys の二分探索, tombstone, 閾値 0.3 の自動コンパクション, yield による列挙.
    public sealed class CurrentClassTable : IPkTable
    {
        const double Threshold = 0.3;

        readonly int start;
        RowObj[] data;
        int[] keys;
        bool[] alive;
        int used;
        int deletedCount;
        long removedTotal;
        long nextKey;

        public CurrentClassTable(int start = 0, int capacity = 16)
        {
            this.start = start;
            data = new RowObj[capacity];
            keys = new int[capacity];
            alive = new bool[capacity];
            nextKey = start;
        }

        public string Name => "A  Current(class)";
        public int Count => used - deletedCount;
        public bool SupportsRange => true;

        public bool Insert(in Row row, out int key)
        {
            key = 0;
            if (nextKey > int.MaxValue) return false;
            EnsureCapacity(used + 1);

            // 現行案の API では、呼び出し側が Record のインスタンスを作って渡す.
            var record = new RowObj
            {
                Score = row.Score,
                Category = row.Category,
                Name = row.Name,
                Description = row.Description,
            };

            key = (int)nextKey++;
            record.Id = key;
            data[used] = record;
            keys[used] = key;
            alive[used] = true;
            used++;
            return true;
        }

        public int GetScore(int key)
        {
            return TryGetAliveIndex(key, out var index) ? data[index].Score : -1;
        }

        public bool UpdateScore(int key, int score)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;
            data[index].Score = score;
            return true;
        }

        public bool Remove(int key)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;
            alive[index] = false;
            deletedCount++;
            removedTotal++;
            if ((double)deletedCount / used >= Threshold) Compact();
            return true;
        }

        public int Compact()
        {
            if (deletedCount == 0) return 0;
            var write = 0;
            for (var read = 0; read < used; read++)
            {
                if (!alive[read]) continue;
                if (write != read)
                {
                    data[write] = data[read];
                    keys[write] = keys[read];
                    alive[write] = true;
                }

                write++;
            }

            Array.Clear(data, write, used - write);
            Array.Clear(keys, write, used - write);
            Array.Clear(alive, write, used - write);
            var reclaimed = used - write;
            used = write;
            deletedCount = 0;
            return reclaimed;
        }

        public long SumAll()
        {
            long sum = 0;
            foreach (var r in All()) sum += r.Score;
            return sum;
        }

        public long SumRange(int min, int max)
        {
            long sum = 0;
            foreach (var r in FindRange(min, max)) sum += r.Score;
            return sum;
        }

        public IEnumerable<RowObj> All()
        {
            for (var index = 0; index < used; index++)
            {
                if (alive[index]) yield return data[index];
            }
        }

        public IEnumerable<RowObj> FindRange(int min, int max)
        {
            for (var index = LowerBound(min); index < used && keys[index] <= max; index++)
            {
                if (alive[index]) yield return data[index];
            }
        }

        bool TryGetAliveIndex(int key, out int index)
        {
            return TryFindIndex(key, out index) && alive[index];
        }

        bool TryFindIndex(int key, out int index)
        {
            var offset = (long)key - start;
            if (offset < 0 || offset >= nextKey - start || used == 0)
            {
                index = -1;
                return false;
            }

            var upper = (int)Math.Min(offset, used - 1);
            var lower = (int)Math.Max(0L, offset - removedTotal);
            if (lower > upper)
            {
                index = -1;
                return false;
            }

            var found = Array.BinarySearch(keys, lower, upper - lower + 1, key);
            index = found >= 0 ? found : -1;
            return found >= 0;
        }

        int LowerBound(int key)
        {
            var found = Array.BinarySearch(keys, 0, used, key);
            return found >= 0 ? found : ~found;
        }

        void EnsureCapacity(int required)
        {
            if (required <= data.Length) return;
            var newLength = Math.Max(required, Math.Max(data.Length * 2, 4));
            Array.Resize(ref data, newLength);
            Array.Resize(ref keys, newLength);
            Array.Resize(ref alive, newLength);
        }
    }

    // A2 / A3: 現行案のアルゴリズムで Record を struct にしたもの. 列挙は yield を使わない.
    // tightBound = true (A3) は探索範囲を「コンパクションで詰めた数」と末尾 Key からの距離で絞る.
    public sealed class CurrentStructTable : IPkTable
    {
        const double Threshold = 0.3;

        readonly int start;
        readonly bool tightBound;
        Row[] data;
        int[] keys;
        bool[] alive;
        int used;
        int deletedCount;
        long removedTotal;
        long reclaimedTotal;
        long nextKey;

        public CurrentStructTable(bool tightBound, int start = 0, int capacity = 16)
        {
            this.tightBound = tightBound;
            this.start = start;
            data = new Row[capacity];
            keys = new int[capacity];
            alive = new bool[capacity];
            nextKey = start;
        }

        public string Name => tightBound ? "A3 Current(struct,bound)" : "A2 Current(struct)";
        public int Count => used - deletedCount;
        public bool SupportsRange => true;

        public bool Insert(in Row row, out int key)
        {
            key = 0;
            if (nextKey > int.MaxValue) return false;
            EnsureCapacity(used + 1);
            key = (int)nextKey++;
            data[used] = row;
            data[used].Id = key;
            keys[used] = key;
            alive[used] = true;
            used++;
            return true;
        }

        public int GetScore(int key)
        {
            return TryGetAliveIndex(key, out var index) ? data[index].Score : -1;
        }

        public bool UpdateScore(int key, int score)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;
            data[index].Score = score;
            return true;
        }

        public bool Remove(int key)
        {
            if (!TryGetAliveIndex(key, out var index)) return false;
            alive[index] = false;
            deletedCount++;
            removedTotal++;
            if ((double)deletedCount / used >= Threshold) Compact();
            return true;
        }

        public int Compact()
        {
            if (deletedCount == 0) return 0;
            var write = 0;
            for (var read = 0; read < used; read++)
            {
                if (!alive[read]) continue;
                if (write != read)
                {
                    data[write] = data[read];
                    keys[write] = keys[read];
                    alive[write] = true;
                }

                write++;
            }

            Array.Clear(data, write, used - write);
            Array.Clear(keys, write, used - write);
            Array.Clear(alive, write, used - write);
            var reclaimed = used - write;
            reclaimedTotal += reclaimed;
            used = write;
            deletedCount = 0;
            return reclaimed;
        }

        public long SumAll()
        {
            long sum = 0;
            var d = data;
            var a = alive;
            for (var i = 0; i < used; i++)
            {
                if (a[i]) sum += d[i].Score;
            }

            return sum;
        }

        public long SumRange(int min, int max)
        {
            long sum = 0;
            for (var i = LowerBound(min); i < used && keys[i] <= max; i++)
            {
                if (alive[i]) sum += data[i].Score;
            }

            return sum;
        }

        bool TryGetAliveIndex(int key, out int index)
        {
            return TryFindIndex(key, out index) && alive[index];
        }

        bool TryFindIndex(int key, out int index)
        {
            var offset = (long)key - start;
            if (offset < 0 || offset >= nextKey - start || used == 0)
            {
                index = -1;
                return false;
            }

            long upper = Math.Min(offset, used - 1);
            long lower;
            if (tightBound)
            {
                // tombstone は添字をずらさないため、欠けている Key はコンパクションで詰めた分だけ.
                lower = Math.Max(0L, offset - reclaimedTotal);
                // 先頭 / 末尾の Key からの距離でも絞る (古い順の削除ではほぼ 1 点になる).
                upper = Math.Min(upper, (long)key - keys[0]);
                lower = Math.Max(lower, used - 1 - ((long)keys[used - 1] - key));
            }
            else
            {
                lower = Math.Max(0L, offset - removedTotal);
            }

            if (lower > upper)
            {
                index = -1;
                return false;
            }

            var found = Array.BinarySearch(keys, (int)lower, (int)(upper - lower + 1), key);
            index = found >= 0 ? found : -1;
            return found >= 0;
        }

        int LowerBound(int key)
        {
            var found = Array.BinarySearch(keys, 0, used, key);
            return found >= 0 ? found : ~found;
        }

        void EnsureCapacity(int required)
        {
            if (required <= data.Length) return;
            var newLength = Math.Max(required, Math.Max(data.Length * 2, 4));
            Array.Resize(ref data, newLength);
            Array.Resize(ref keys, newLength);
            Array.Resize(ref alive, newLength);
        }
    }
}
