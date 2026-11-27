using System.ComponentModel;

namespace OrdoMemory
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IRow<out TRow, TPrimaryKey> where TPrimaryKey : unmanaged
    {
        TPrimaryKey PrimaryKey { get; }
        TRow WithPrimaryKey(TPrimaryKey primaryKey);
    }
}
