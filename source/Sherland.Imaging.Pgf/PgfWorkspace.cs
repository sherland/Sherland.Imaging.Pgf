using System.Buffers;

namespace Sherland.Imaging.Pgf;

/// <summary>
/// pgf-codec-allocation-and-simd.md Stage 2: explicit owner for codec work-buffer rents. It is
/// intentionally internal until the decode/encode APIs can accept it without changing their
/// existing ownership contracts. A workspace is single-threaded, grows by retaining each rent for
/// its lifetime, and returns every array exactly once on <see cref="Dispose"/>.
///
/// Every rent is exposed as a <see cref="Memory{T}"/> sliced to its requested logical length,
/// never as the pool's physical array. <see cref="ArrayPool{T}"/> may over-rent, so treating the
/// backing array's length as encoded data length is forbidden by construction at this boundary.
/// Backing arrays are returned uncleared, but the requested logical range is cleared before a
/// retained array is handed to the codec again. That preserves the zero-initialized-array contract
/// of the original allocation-based decoder: entropy decoding deliberately leaves some coefficient
/// positions untouched. Pool capacity beyond the requested logical range remains uncleared.
/// </summary>
public sealed class PgfWorkspace : IDisposable
{
    private readonly List<int[]> int32Rents = [];
    private readonly List<int[]> availableInt32 = [];
    private readonly List<uint[]> uint32Rents = [];
    private readonly List<uint[]> availableUInt32 = [];
    private readonly List<bool[]> booleanRents = [];
    private readonly List<bool[]> availableBoolean = [];
    private readonly List<byte[]> byteRents = [];
    private readonly List<byte[]> availableByte = [];
    private bool disposed;

    /// <summary>Rents a logical <paramref name="length"/>-element coefficient span.</summary>
    public Memory<int> RentInt32(int length) => RentInt32Backing(length).AsMemory(0, length);

    /// <summary>Rents a logical <paramref name="length"/>-element code-word span.</summary>
    public Memory<uint> RentUInt32(int length) => RentUInt32Backing(length).AsMemory(0, length);

    /// <summary>Rents a logical <paramref name="length"/>-element flag span.</summary>
    public Memory<bool> RentBoolean(int length) => RentBooleanBacking(length).AsMemory(0, length);

    /// <summary>Rents a logical <paramref name="length"/>-byte span.</summary>
    public Memory<byte> RentByte(int length) => RentByteBacking(length).AsMemory(0, length);

    internal int[] RentInt32Backing(int length) => RentBacking(ArrayPool<int>.Shared, int32Rents, length);

    internal uint[] RentUInt32Backing(int length) => RentBacking(ArrayPool<uint>.Shared, uint32Rents, length);

    internal bool[] RentBooleanBacking(int length) => RentBacking(ArrayPool<bool>.Shared, booleanRents, length);

    internal byte[] RentByteBacking(int length) => RentBacking(ArrayPool<byte>.Shared, byteRents, length);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Return(ArrayPool<int>.Shared, int32Rents);
        Return(ArrayPool<int>.Shared, availableInt32);
        Return(ArrayPool<uint>.Shared, uint32Rents);
        Return(ArrayPool<uint>.Shared, availableUInt32);
        Return(ArrayPool<bool>.Shared, booleanRents);
        Return(ArrayPool<bool>.Shared, availableBoolean);
        Return(ArrayPool<byte>.Shared, byteRents);
        Return(ArrayPool<byte>.Shared, availableByte);
    }

    /// <summary>
    /// Makes buffers used by a completed operation available to the next operation without returning
    /// them to the shared pool. The caller must not use any decoder/session opened with this workspace
    /// after calling Reset; it invalidates that operation's internal working storage.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Reset(default);
    }

    /// <summary>Captures the first rent of each backing-array type that belongs to a long-lived
    /// owner. A reusable decode session keeps its entropy macroblock arrays before this mark while
    /// recycling only subsequently-rented wavelet working arrays between passes.</summary>
    internal PgfWorkspaceMark MarkPersistent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new PgfWorkspaceMark(int32Rents.Count, uint32Rents.Count, booleanRents.Count, byteRents.Count);
    }

    /// <summary>Recycles only arrays rented after <paramref name="mark"/>. This keeps long-lived
    /// decoder state from being handed out as scratch storage during its own next decode pass.</summary>
    internal void Reset(PgfWorkspaceMark mark)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReuseFrom(int32Rents, availableInt32, mark.Int32Count);
        ReuseFrom(uint32Rents, availableUInt32, mark.UInt32Count);
        ReuseFrom(booleanRents, availableBoolean, mark.BooleanCount);
        ReuseFrom(byteRents, availableByte, mark.ByteCount);
    }

    private T[] RentBacking<T>(ArrayPool<T> pool, List<T[]> rents, int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        List<T[]> available = typeof(T) == typeof(int) ? (List<T[]>)(object)availableInt32
            : typeof(T) == typeof(uint) ? (List<T[]>)(object)availableUInt32
            : typeof(T) == typeof(bool) ? (List<T[]>)(object)availableBoolean
            : (List<T[]>)(object)availableByte;
        int reusableIndex = -1;
        for (int i = 0; i < available.Count; i++)
        {
            if (available[i].Length >= length)
            {
                reusableIndex = i;
                break;
            }
        }
        T[] rented = reusableIndex >= 0 ? available[reusableIndex] : pool.Rent(length);
        if (reusableIndex >= 0)
        {
            available.RemoveAt(reusableIndex);
            Array.Clear(rented, 0, length);
        }
        rents.Add(rented);
        return rented;
    }

    private static void ReuseFrom<T>(List<T[]> rents, List<T[]> available, int retainedCount)
    {
        for (int i = retainedCount; i < rents.Count; i++)
        {
            available.Add(rents[i]);
        }

        rents.RemoveRange(retainedCount, rents.Count - retainedCount);
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

/// <summary>Opaque per-array-type retention boundary captured by <see cref="PgfWorkspace"/> for a
/// long-lived internal owner.</summary>
internal readonly record struct PgfWorkspaceMark(int Int32Count, int UInt32Count, int BooleanCount, int ByteCount);
