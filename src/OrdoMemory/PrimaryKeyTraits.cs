using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    // PK とする型に関する情報
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IPrimaryKeyTraits<TPrimaryKey> where TPrimaryKey : unmanaged
    {
        int Bits { get; }
        ulong ToUInt64(TPrimaryKey primaryKey);
        TPrimaryKey FromUInt64(ulong value);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct UInt32PrimaryKey : IPrimaryKeyTraits<uint>
    {
        public int Bits => 32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ToUInt64(uint primaryKey)
        {
            return primaryKey;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FromUInt64(ulong value)
        {
            return (uint)value;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct UInt64PrimaryKey : IPrimaryKeyTraits<ulong>
    {
        public int Bits => 64;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ToUInt64(ulong primaryKey)
        {
            return primaryKey;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong FromUInt64(ulong value)
        {
            return value;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IPrimaryKeyEnumerator<TPrimaryKey> where TPrimaryKey : unmanaged
    {
        TPrimaryKey Current { get; }
        bool MoveNext();
    }
}
