using System.Runtime.CompilerServices;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class PipelineOwnershipTests
{
    private sealed class Lifetime
    {
        public int Releases;
    }

    private sealed class NativeOwner(Lifetime lifetime)
    {
        ~NativeOwner() => Interlocked.Increment(ref lifetime.Releases);
    }

    [Fact]
    public void BorrowedSubobjectDataKeepsItsOwnerAliveUntilMaterialized()
    {
        var lifetime = new Lifetime();
        Assert.Equal("decoded input layout", ReadBorrowedData(lifetime, fail: false));
        Collect();
        Assert.Equal(1, lifetime.Releases);
    }

    [Fact]
    public void NativeOwnerIsRetainedThroughFailingDecodingAndReleasedAfterward()
    {
        var lifetime = new Lifetime();
        Assert.Throws<InvalidOperationException>(() => ReadBorrowedData(lifetime, fail: true));
        Collect();
        Assert.Equal(1, lifetime.Releases);
    }

    // Keep the owner out of the caller's locals so its lifetime comes only from
    // ReadWithNativeOwner, just as GetSubobjects leaves borrowed native pointers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadBorrowedData(Lifetime lifetime, bool fail)
        => PipelineTools.ReadWithNativeOwner(new NativeOwner(lifetime), () =>
        {
            Collect();
            Assert.Equal(0, lifetime.Releases);
            if (fail) throw new InvalidOperationException("Native descriptor decoding failed.");
            return "decoded input layout";
        });

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
