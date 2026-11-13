namespace OrdoMemory.Sample
{
    // Update の部分更新用. default は「指定なし」、値を渡すと null も含めて「その値に更新」を表す.
    // 構造体のためアロケーションは発生しない.
    public readonly struct Optional<T>
    {
        public readonly bool HasValue;
        public readonly T Value;

        public Optional(T value)
        {
            HasValue = true;
            Value = value;
        }

        public static implicit operator Optional<T>(T value)
        {
            return new Optional<T>(value);
        }

        public T OrElse(T fallback)
        {
            return HasValue ? Value : fallback;
        }
    }
}
