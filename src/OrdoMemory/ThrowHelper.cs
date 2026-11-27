using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace OrdoMemory
{
    // 例外の生成を呼び出し元から切り離し、検査を入れたメソッドのインライン化を妨げないようにする
    static class ThrowHelper
    {
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowArgumentOutOfRange(string paramName)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowInvalidPrimaryKey(string paramName)
        {
            throw new ArgumentException("The primary key does not refer to a stored row.", paramName);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowNotReserved()
        {
            throw new InvalidOperationException("No reserved capacity. Call TryReserve before Store.");
        }
    }
}
