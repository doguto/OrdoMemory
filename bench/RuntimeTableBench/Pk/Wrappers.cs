namespace OrdoMemory.Bench.Pk
{
    // ジェネリクスを値型で具象化させ、計測ループからの呼び出しを直接呼び出し (インライン化可能) にするためのラッパー.
    // 参照型の型引数では共有コードになり、インターフェース呼び出しのコストが計測に混ざる.

    public struct CurrentClassW : IPkTable
    {
        public CurrentClassTable T;
        public string Name => T.Name;
        public int Count => T.Count;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(in Row row, out int key) => T.Insert(row, out key);
        public int GetScore(int key) => T.GetScore(key);
        public bool UpdateScore(int key, int score) => T.UpdateScore(key, score);
        public bool Remove(int key) => T.Remove(key);
        public long SumAll() => T.SumAll();
        public long SumRange(int min, int max) => T.SumRange(min, max);
    }

    public struct CurrentStructW : IPkTable
    {
        public CurrentStructTable T;
        public string Name => T.Name;
        public int Count => T.Count;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(in Row row, out int key) => T.Insert(row, out key);
        public int GetScore(int key) => T.GetScore(key);
        public bool UpdateScore(int key, int score) => T.UpdateScore(key, score);
        public bool Remove(int key) => T.Remove(key);
        public long SumAll() => T.SumAll();
        public long SumRange(int min, int max) => T.SumRange(min, max);
    }

    public struct RankW : IPkTable
    {
        public RankTable T;
        public string Name => T.Name;
        public int Count => T.Count;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(in Row row, out int key) => T.Insert(row, out key);
        public int GetScore(int key) => T.GetScore(key);
        public bool UpdateScore(int key, int score) => T.UpdateScore(key, score);
        public bool Remove(int key) => T.Remove(key);
        public long SumAll() => T.SumAll();
        public long SumRange(int min, int max) => T.SumRange(min, max);
    }

    public struct BlockW : IPkTable
    {
        public BlockTable T;
        public string Name => T.Name;
        public int Count => T.Count;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(in Row row, out int key) => T.Insert(row, out key);
        public int GetScore(int key) => T.GetScore(key);
        public bool UpdateScore(int key, int score) => T.UpdateScore(key, score);
        public bool Remove(int key) => T.Remove(key);
        public long SumAll() => T.SumAll();
        public long SumRange(int min, int max) => T.SumRange(min, max);
    }

    public struct SlotMapW : IPkTable
    {
        public SlotMapTable T;
        public string Name => T.Name;
        public int Count => T.Count;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(in Row row, out int key) => T.Insert(row, out key);
        public int GetScore(int key) => T.GetScore(key);
        public bool UpdateScore(int key, int score) => T.UpdateScore(key, score);
        public bool Remove(int key) => T.Remove(key);
        public long SumAll() => T.SumAll();
        public long SumRange(int min, int max) => T.SumRange(min, max);
    }

    public struct DictionaryW : IPkTable
    {
        public DictionaryTable T;
        public string Name => T.Name;
        public int Count => T.Count;
        public bool SupportsRange => T.SupportsRange;
        public bool Insert(in Row row, out int key) => T.Insert(row, out key);
        public int GetScore(int key) => T.GetScore(key);
        public bool UpdateScore(int key, int score) => T.UpdateScore(key, score);
        public bool Remove(int key) => T.Remove(key);
        public long SumAll() => T.SumAll();
        public long SumRange(int min, int max) => T.SumRange(min, max);
    }
}
