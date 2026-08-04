using System.Buffers;

namespace PictTag.PgfCodec;

/// <summary>
/// pgf-codec-allocation-and-simd.md Stage 2: explicit owner for codec work-buffer rents. It is
/// intentionally internal until the decode/encode APIs can accept it without changing their
/// existing ownership contracts. A workspace is single-threaded, grows by retaining each rent for
/// its lifetime, and returns every array exactly once on <see cref="Dispose"/>.
///
/// Every rent is exposed as a <see cref="Memory{T}"/> sliced to its requested logical length,
/// never as the pool's physical array. <see cref="ArrayPool{T}"/> may over-rent, so treating the
/// backing array's length as encoded data length is forbidden by construction at this boundary.
/// Coefficient/image data is not cleared when returned: it is not secret material and clearing
/// multi-megabyte buffers would erase the allocation-reduction benefit. Callers must overwrite all
/// logical data they consume.
/// </summary>
internal sealed class PgfWorkspace : IDisposable
{
    private readonly List<int[]> int32Rents = [];
    private readonly List<uint[]> uint32Rents = [];
    private readonly List<bool[]> booleanRents = [];
    private readonly List<byte[]> byteRents = [];
    private bool disposed;

    public Memory<int> RentInt32(int length) => Rent(ArrayPool<int>.Shared, int32Rents, length);

    public Memory<uint> RentUInt32(int length) => Rent(ArrayPool<uint>.Shared, uint32Rents, length);

    public Memory<bool> RentBoolean(int length) => Rent(ArrayPool<bool>.Shared, booleanRents, length);

    public Memory<byte> RentByte(int length) => Rent(ArrayPool<byte>.Shared, byteRents, length);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Return(ArrayPool<int>.Shared, int32Rents);
        Return(ArrayPool<uint>.Shared, uint32Rents);
        Return(ArrayPool<bool>.Shared, booleanRents);
        Return(ArrayPool<byte>.Shared, byteRents);
    }

    private Memory<T> Rent<T>(ArrayPool<T> pool, List<T[]> rents, int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        T[] rented = pool.Rent(length);
        rents.Add(rented);
        return rented.AsMemory(0, length);
    }

    private static void Return<T>(ArrayPool<T> pool, List<T[]> rents)
    {
        foreach (T[] rented in rents)
        {
            pool.Return(rented, clearArray: false);
        }

        rents.Clear();
    }
}
