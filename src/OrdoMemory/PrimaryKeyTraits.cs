using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    public interface IPrimaryKeyTraits<TPrimaryKey> where TPrimaryKey : unmanaged
    {
        int Bits { get; }
        ulong ToUInt64(TPrimaryKey primaryKey);
        TPrimaryKey FromUInt64(ulong value);
    }

    public readonly struct UInt32PrimaryKey : IPrimaryKeyTraits<uint>
    {
        public int Bits => 32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ToUInt64(uint primaryKey) => primaryKey;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FromUInt64(ulong value) => (uint)value;
    }

    public readonly struct UInt64PrimaryKey : IPrimaryKeyTraits<ulong>
    {
        public int Bits => 64;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ToUInt64(ulong primaryKey) => primaryKey;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong FromUInt64(ulong value) => value;
    }

    public interface IPrimaryKeyEnumerator<TPrimaryKey> where TPrimaryKey : unmanaged
    {
        TPrimaryKey Current { get; }
        bool MoveNext();
    }
}
