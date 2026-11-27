namespace System.Runtime.CompilerServices
{
    // 補完文字列ハンドラを使用したいが netstandard2.0 には存在しないため自前で定義する
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }
}
