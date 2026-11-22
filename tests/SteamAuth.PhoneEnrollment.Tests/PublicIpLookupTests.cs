using Steam_Desktop_Authenticator;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Proxy configuration")]
    public sealed class PublicIpLookupTests : IDisposable
    {
        public PublicIpLookupTests()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(ProxyService.ApplySavedConfiguration(manifest, out string error), error);
        }

        public void Dispose()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ProxyService.ApplySavedConfiguration(manifest, out _);
        }

        [Fact]
        public void AcquireActiveHttpClient_ReusesSameClientUntilProxyConfigurationChanges()
        {
            HttpClient firstClient;
            using (ProxyService.ActiveHttpClientLease lease1 = ProxyService.AcquireActiveHttpClient())
            using (ProxyService.ActiveHttpClientLease lease2 = ProxyService.AcquireActiveHttpClient())
            {
                Assert.Same(lease1.Client, lease2.Client);
                firstClient = lease1.Client;
            }

            using (ProxyService.ActiveHttpClientLease lease3 = ProxyService.AcquireActiveHttpClient())
            {
                Assert.Same(firstClient, lease3.Client);
            }

            Manifest updatedManifest = Manifest.GenerateNewManifest(false);
            updatedManifest.ProxyEnabled = true;
            updatedManifest.ProxyScheme = "http";
            updatedManifest.ProxyHost = "127.0.0.1";
            updatedManifest.ProxyPort = 8080;
            Assert.True(ProxyService.ApplySavedConfiguration(updatedManifest, out string error), error);

            using (ProxyService.ActiveHttpClientLease leaseAfterChange = ProxyService.AcquireActiveHttpClient())
            {
                Assert.NotSame(firstClient, leaseAfterChange.Client);
            }
        }

        [Fact]
        public async Task AcquireActiveHttpClient_RetainsInFlightClientAcrossProxySwapAndDisposesAfterLeaseEnds()
        {
            ProxyService.ActiveHttpClientLease heldLease = ProxyService.AcquireActiveHttpClient();
            HttpClient originalClient = heldLease.Client;
            try
            {
                Manifest updatedManifest = Manifest.GenerateNewManifest(false);
                Assert.True(ProxyService.ApplySavedConfiguration(updatedManifest, out string error), error);

                using (ProxyService.ActiveHttpClientLease replacementLease = ProxyService.AcquireActiveHttpClient())
                {
                    Assert.NotSame(originalClient, replacementLease.Client);
                }

                // Releasing the lease a second time must be idempotent and only dispose once.
                heldLease.Dispose();
                heldLease.Dispose();

                await Assert.ThrowsAsync<ObjectDisposedException>(() => originalClient.GetAsync("https://example.invalid/"));
            }
            finally
            {
                heldLease.Dispose();
            }
        }

        [Fact]
        public async Task BlockedProxyConfiguration_RejectsActiveClientAcquisitionAndReturnsNullPublicIp()
        {
            Manifest invalidManifest = Manifest.GenerateNewManifest(false);
            invalidManifest.ProxyEnabled = true;
            invalidManifest.ProxyHost = "https://invalid/path";
            invalidManifest.ProxyPort = 70000;

            Assert.False(ProxyService.ApplySavedConfiguration(invalidManifest, out _));
            Assert.Throws<InvalidOperationException>(() => ProxyService.AcquireActiveHttpClient());
            Assert.Null(await MainForm.GetCurrentPublicIpv4Async(CancellationToken.None));
        }

        [Fact]
        public async Task GetCurrentPublicIpv4Async_TrimsAndReturnsValidIpv4Address()
        {
            using (HttpClient client = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("  203.0.113.42 \r\n", Encoding.UTF8, "text/plain")
                })))
            {
                string ip = await MainForm.GetCurrentPublicIpv4Async(client, TimeSpan.FromSeconds(5), CancellationToken.None);
                Assert.Equal("203.0.113.42", ip);
            }
        }

        [Theory]
        [InlineData("2001:db8::1")]
        [InlineData("<html>error</html>")]
        [InlineData("")]
        [InlineData("999.999.999.999")]
        public async Task GetCurrentPublicIpv4Async_RejectsNonIpv4Bodies(string body)
        {
            using (HttpClient client = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/plain")
                })))
            {
                string ip = await MainForm.GetCurrentPublicIpv4Async(client, TimeSpan.FromSeconds(5), CancellationToken.None);
                Assert.Null(ip);
            }
        }

        [Fact]
        public async Task GetCurrentPublicIpv4Async_RejectsOversizedContentLengthWithoutReadingStream()
        {
            TrackingStream stream = new TrackingStream(Encoding.UTF8.GetBytes("203.0.113.42"));
            HttpContent content = new StreamContent(stream);
            content.Headers.ContentLength = 1024;

            using (HttpClient client = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content })))
            {
                string ip = await MainForm.GetCurrentPublicIpv4Async(client, TimeSpan.FromSeconds(5), CancellationToken.None);
                Assert.Null(ip);
                Assert.Equal(0, stream.ReadCalls);
            }
        }

        [Fact]
        public async Task GetCurrentPublicIpv4Async_AbortsStreamingResponseExceedingLimit()
        {
            InfiniteChunkStream stream = new InfiniteChunkStream((byte)'1', chunkSize: 100);
            using (HttpClient client = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })))
            {
                string ip = await MainForm.GetCurrentPublicIpv4Async(client, TimeSpan.FromSeconds(5), CancellationToken.None);
                Assert.Null(ip);
                Assert.True(stream.TotalBytesReturned <= 512, "Lookup must stop reading shortly after the 256-byte limit.");
            }
        }

        [Fact]
        public async Task GetCurrentPublicIpv4Async_ReturnsNullOnNonSuccessOrInvalidUtf8OrTimeout()
        {
            using (HttpClient nonSuccessClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
            {
                Assert.Null(await MainForm.GetCurrentPublicIpv4Async(nonSuccessClient, TimeSpan.FromSeconds(5), CancellationToken.None));
            }

            using (HttpClient invalidUtf8Client = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xFF, 0xFE }) })))
            {
                Assert.Null(await MainForm.GetCurrentPublicIpv4Async(invalidUtf8Client, TimeSpan.FromSeconds(5), CancellationToken.None));
            }

            using (HttpClient slowClient = new HttpClient(new StubHandler(async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("203.0.113.42") };
            })))
            {
                Assert.Null(await MainForm.GetCurrentPublicIpv4Async(slowClient, TimeSpan.FromMilliseconds(50), CancellationToken.None));
            }
        }

        [Fact]
        public async Task GetCurrentPublicIpv4Async_PropagatesCallerCancellation()
        {
            using (CancellationTokenSource cts = new CancellationTokenSource())
            using (HttpClient client = new HttpClient(new StubHandler(async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })))
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    MainForm.GetCurrentPublicIpv4Async(client, TimeSpan.FromSeconds(5), cts.Token));
            }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<CancellationToken, Task<HttpResponseMessage>> handler;

            public StubHandler(Func<CancellationToken, HttpResponseMessage> handler)
                : this(token => Task.FromResult(handler(token)))
            {
            }

            public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> handler)
            {
                this.handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return handler(cancellationToken);
            }
        }

        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream(byte[] buffer)
                : base(buffer)
            {
            }

            public int ReadCalls { get; private set; }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ReadCalls++;
                return base.ReadAsync(buffer, cancellationToken);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ReadCalls++;
                return base.ReadAsync(buffer, offset, count, cancellationToken);
            }
        }

        private sealed class InfiniteChunkStream : Stream
        {
            private readonly byte fillByte;
            private readonly int chunkSize;

            public InfiniteChunkStream(byte fillByte, int chunkSize)
            {
                this.fillByte = fillByte;
                this.chunkSize = chunkSize;
            }

            public long TotalBytesReturned { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int toWrite = Math.Min(count, chunkSize);
                for (int i = 0; i < toWrite; i++)
                    buffer[offset + i] = fillByte;
                TotalBytesReturned += toWrite;
                return toWrite;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int toWrite = Math.Min(buffer.Length, chunkSize);
                buffer.Span.Slice(0, toWrite).Fill(fillByte);
                TotalBytesReturned += toWrite;
                return ValueTask.FromResult(toWrite);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
