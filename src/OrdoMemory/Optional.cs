namespace OrdoMemory
{
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
