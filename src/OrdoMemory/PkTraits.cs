using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    // struct で渡し、JIT に定数畳み込みとインライン化をさせる.
    public interface IPkTraits<TPk> where TPk : unmanaged
    {
        int Bits { get; }
        ulong ToUInt64(TPk pk);
        TPk FromUInt64(ulong value);
    }

    public readonly struct UInt32Pk : IPkTraits<uint>
    {
        public int Bits => 32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ToUInt64(uint pk) => pk;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FromUInt64(ulong value) => (uint)value;
    }

    public readonly struct UInt64Pk : IPkTraits<ulong>
    {
        public int Bits => 64;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ToUInt64(ulong pk) => pk;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong FromUInt64(ulong value) => value;
    }

    public interface IPkEnumerator<TPk> where TPk : unmanaged
    {
        TPk Current { get; }
        bool MoveNext();
    }
}
