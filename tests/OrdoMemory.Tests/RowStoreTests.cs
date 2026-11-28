using System;
using Xunit;

namespace OrdoMemory.Tests
{
    public class RowStoreTests
    {
        static RowStore<OneGenRow, uint, UInt32PrimaryKey> NewStore(int capacity = 0)
        {
            return new RowStore<OneGenRow, uint, UInt32PrimaryKey>(capacity, 22);
        }

        [Fact]
        public void StoreWithoutReserveThrows()
        {
            var store = NewStore();
            Assert.Throws<InvalidOperationException>(() => store.Store(new OneGenRow(1)));
            Assert.Equal(0, store.Count);

            Assert.True(store.TryReserve(1));
            store.Store(new OneGenRow(1));
            store.TrimExcess();
            Assert.Throws<InvalidOperationException>(() => store.Store(new OneGenRow(2)));
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void StoreReusesFreedSlotWithinReservedCapacity()
        {
            var store = NewStore();
            Assert.True(store.TryReserve(2));
            var a = store.Store(new OneGenRow(1));
            store.Store(new OneGenRow(2));
            store.Remove(store.GetIndex(a));
            var c = store.Store(new OneGenRow(3));
            Assert.True(store.TryGetIndex(c, out var index));
            Assert.Equal(3, store.GetRowRef(index).Value);
        }

        [Fact]
        public void RemoveOutOfRangeThrows()
        {
            var store = NewStore(4);
            Assert.True(store.TryReserve(1));
            store.Store(new OneGenRow(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Remove(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Remove(1));
            Assert.Equal(1, store.Count);
            store.Remove(0);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Remove(0));
        }

        [Fact]
        public void GetIndexRejectsStalePrimaryKey()
        {
            var store = NewStore(4);
            Assert.True(store.TryReserve(2));
            var a = store.Store(new OneGenRow(1));
            var b = store.Store(new OneGenRow(2));
            Assert.Equal(1, store.GetIndex(b));

            store.Remove(store.GetIndex(a));
            Assert.Throws<ArgumentException>(() => store.GetIndex(a));
            Assert.Throws<ArgumentException>(() => store.GetIndex(0));
            Assert.Equal(0, store.GetIndex(b));
        }
    }
}
