namespace System.Runtime.CompilerServices
{
    // netstandard2.0 には存在しないため自前で定義する
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }
}
