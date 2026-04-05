using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NfsSharp.Protocol;

namespace NfsSharp.Tests
{
    public class NfsStreamTests
    {
        // ── Hand-written mock ─────────────────────────────────────────────────

        private sealed class MockProtocolClient : INfsProtocolClient
        {
            // Data to serve from ReadAsync
            public byte[] ReadData { get; set; } = Array.Empty<byte>();
            public bool ReadEof { get; set; }

            // Tracking calls
            public int WriteCallCount { get; private set; }
            public int CommitCallCount { get; private set; }
            public int SetAttrCallCount { get; private set; }
            public NfsSetAttributes? LastSetAttr { get; private set; }

            // Write returns how many bytes were written
            public int WriteBytesPerCall { get; set; } = int.MaxValue; // default: write all

            public Task<NfsReadResult> ReadAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
            {
                int available = (int)Math.Min(count, Math.Max(0, ReadData.LongLength - offset));
                var segment = new byte[available];
                if (available > 0)
                    Array.Copy(ReadData, offset, segment, 0, available);
                bool eof = (offset + available) >= ReadData.Length;
                return Task.FromResult(new NfsReadResult(segment, eof));
            }

            public Task<int> WriteAsync(NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct)
            {
                WriteCallCount++;
                int written = Math.Min(count, WriteBytesPerCall);
                return Task.FromResult(written);
            }

            public Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
            {
                CommitCallCount++;
                return Task.CompletedTask;
            }

            public Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
            {
                var attrs = new NfsFileAttributes
                {
                    Size = (ulong)ReadData.Length,
                    Type = NfsFileType.Regular
                };
                return Task.FromResult(attrs);
            }

            public Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct)
            {
                SetAttrCallCount++;
                LastSetAttr = attrs;
                return Task.CompletedTask;
            }

            public Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(NfsFileHandle dir, string name, CancellationToken ct)
                => throw new NotImplementedException();

            public Task<NfsFileHandle> CreateFileAsync(NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
                => throw new NotImplementedException();

            public Task<NfsFileHandle> MkDirAsync(NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
                => throw new NotImplementedException();

            public Task SymLinkAsync(NfsFileHandle dir, string name, string linkTarget, NfsSetAttributes attrs, CancellationToken ct)
                => throw new NotImplementedException();

            public Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(NfsFileHandle dir, CancellationToken ct)
                => throw new NotImplementedException();

            public IAsyncEnumerable<NfsDirectoryEntry> EnumerateDirAsync(NfsFileHandle dir, CancellationToken ct)
                => throw new NotImplementedException();

            public Task<string> ReadLinkAsync(NfsFileHandle handle, CancellationToken ct)
                => throw new NotImplementedException();

            public Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct)
                => throw new NotImplementedException();

            public Task RmDirAsync(NfsFileHandle dir, string name, CancellationToken ct)
                => throw new NotImplementedException();

            public Task RenameAsync(NfsFileHandle fromDir, string fromName, NfsFileHandle toDir, string toName, CancellationToken ct)
                => throw new NotImplementedException();

            public Task LinkAsync(NfsFileHandle file, NfsFileHandle linkDir, string linkName, CancellationToken ct)
                => throw new NotImplementedException();

            public Task<NfsFsStat> FsStatAsync(NfsFileHandle handle, CancellationToken ct)
                => throw new NotImplementedException();

            public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

            public void Dispose() { }
        }

        private static NfsFileHandle MakeHandle() => new NfsFileHandle(new byte[] { 1, 2, 3, 4 });

        private static NfsStream MakeStream(MockProtocolClient ops, long knownLength = 0,
            bool readable = true, bool writable = true)
            => new NfsStream(ops, MakeHandle(), knownLength, readable, writable);

        // ── Tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Read_AssemblesBytesCorrectly()
        {
            var ops = new MockProtocolClient { ReadData = new byte[] { 1, 2, 3, 4, 5 } };
            using var stream = MakeStream(ops, knownLength: 5);
            var buf = new byte[5];
            int n = await stream.ReadAsync(buf, 0, 5, CancellationToken.None);
            Assert.Equal(5, n);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buf);
        }

        [Fact]
        public async Task Read_AdvancesPosition()
        {
            var ops = new MockProtocolClient { ReadData = new byte[] { 10, 20, 30 } };
            using var stream = MakeStream(ops, knownLength: 3);
            var buf = new byte[2];
            await stream.ReadAsync(buf, 0, 2, CancellationToken.None);
            Assert.Equal(2, stream.Position);
        }

        [Fact]
        public async Task Write_CallsWriteAsyncOnMockAndAdvancesPosition()
        {
            var ops = new MockProtocolClient();
            using var stream = MakeStream(ops, writable: true);
            var data = new byte[] { 9, 8, 7 };
            await stream.WriteAsync(data, 0, 3, CancellationToken.None);
            Assert.Equal(1, ops.WriteCallCount);
            Assert.Equal(3, stream.Position);
        }

        [Fact]
        public async Task Flush_CallsCommit_WhenUncommittedWritesPending()
        {
            var ops = new MockProtocolClient();
            using var stream = MakeStream(ops, writable: true);
            await stream.WriteAsync(new byte[] { 1 }, 0, 1, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
            Assert.Equal(1, ops.CommitCallCount);
        }

        [Fact]
        public async Task Flush_DoesNotCallCommit_WhenNoUncommittedWrites()
        {
            var ops = new MockProtocolClient();
            using var stream = MakeStream(ops);
            await stream.FlushAsync(CancellationToken.None);
            Assert.Equal(0, ops.CommitCallCount);
        }

        [Fact]
        public void Seek_Begin()
        {
            var ops = new MockProtocolClient { ReadData = new byte[] { 1, 2, 3, 4, 5 } };
            using var stream = MakeStream(ops, knownLength: 5);
            long pos = stream.Seek(3, SeekOrigin.Begin);
            Assert.Equal(3, pos);
            Assert.Equal(3, stream.Position);
        }

        [Fact]
        public void Seek_Current()
        {
            var ops = new MockProtocolClient { ReadData = new byte[] { 1, 2, 3, 4, 5 } };
            using var stream = MakeStream(ops, knownLength: 5);
            stream.Position = 2;
            long pos = stream.Seek(1, SeekOrigin.Current);
            Assert.Equal(3, pos);
        }

        [Fact]
        public void Seek_End()
        {
            var ops = new MockProtocolClient { ReadData = new byte[] { 1, 2, 3, 4, 5 } };
            using var stream = MakeStream(ops, knownLength: 5);
            long pos = stream.Seek(-2, SeekOrigin.End);
            Assert.Equal(3, pos);
        }

        [Fact]
        public async Task SetLengthAsync_CallsSetAttr_WithCorrectSize()
        {
            var ops = new MockProtocolClient();
            using var stream = MakeStream(ops, writable: true);
            await stream.SetLengthAsync(42);
            Assert.Equal(1, ops.SetAttrCallCount);
            Assert.NotNull(ops.LastSetAttr?.Size);
            Assert.Equal(42ul, ops.LastSetAttr!.Size!.Value);
        }

        [Fact]
        public void CanRead_ReflectsFlag()
        {
            var ops = new MockProtocolClient();
            using var s1 = MakeStream(ops, readable: true, writable: false);
            using var s2 = MakeStream(ops, readable: false, writable: true);
            Assert.True(s1.CanRead);
            Assert.False(s2.CanRead);
        }

        [Fact]
        public void CanWrite_ReflectsFlag()
        {
            var ops = new MockProtocolClient();
            using var s1 = MakeStream(ops, readable: true, writable: true);
            using var s2 = MakeStream(ops, readable: true, writable: false);
            Assert.True(s1.CanWrite);
            Assert.False(s2.CanWrite);
        }

        [Fact]
        public void CanSeek_TrueWhileOpen()
        {
            var ops = new MockProtocolClient();
            using var stream = MakeStream(ops);
            Assert.True(stream.CanSeek);
        }

        [Fact]
        public void Read_OnWriteOnlyStream_ThrowsNotSupportedException()
        {
            var ops = new MockProtocolClient();
            using var stream = MakeStream(ops, readable: false, writable: true);
            var buf = new byte[4];
            Assert.Throws<NotSupportedException>(() => stream.Read(buf, 0, 4));
        }

        [Fact]
        public void Write_OnReadOnlyStream_ThrowsNotSupportedException()
        {
            var ops = new MockProtocolClient();
            using var stream = MakeStream(ops, readable: true, writable: false);
            Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 1 }, 0, 1));
        }

        [Fact]
        public async Task Dispose_CommitsUnstableWrites()
        {
            var ops = new MockProtocolClient();
            var stream = MakeStream(ops, writable: true);
            await stream.WriteAsync(new byte[] { 42 }, 0, 1, CancellationToken.None);
            stream.Dispose();
            Assert.Equal(1, ops.CommitCallCount);
        }

        [Fact]
        public void DoubleDispose_IsSafe()
        {
            var ops = new MockProtocolClient();
            var stream = MakeStream(ops);
            stream.Dispose();
            stream.Dispose(); // should not throw
        }

        [Fact]
        public void AfterDispose_CanRead_IsFalse()
        {
            var ops = new MockProtocolClient();
            var stream = MakeStream(ops, readable: true, writable: true);
            stream.Dispose();
            Assert.False(stream.CanRead);
            Assert.False(stream.CanWrite);
            Assert.False(stream.CanSeek);
        }
    }
}
