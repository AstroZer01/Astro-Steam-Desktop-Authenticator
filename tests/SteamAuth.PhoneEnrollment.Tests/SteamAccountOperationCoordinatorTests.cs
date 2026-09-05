using Steam_Desktop_Authenticator;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class SteamAccountOperationCoordinatorTests
    {
        [Fact]
        public async Task FailureTransitionRunsBeforeQueuedOperationAndDoesNotRelockReplacement()
        {
            var coordinator = new SteamAccountOperationCoordinator();
            var operationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOperation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int renewalRequired = 0;

            Task firstOperation = coordinator.RunAsync<bool>(76561198000000001UL, async () =>
            {
                operationStarted.SetResult(true);
                await releaseOperation.Task;
                throw new InvalidOperationException("The captured session is invalid.");
            }, _ => Interlocked.Exchange(ref renewalRequired, 1));

            await operationStarted.Task;

            Task queuedOperation = coordinator.RunAsync(76561198000000001UL, () =>
            {
                Assert.Equal(1, Volatile.Read(ref renewalRequired));
                return Task.FromException<bool>(new InvalidOperationException("The queued old-generation operation was rejected."));
            }, null);

            releaseOperation.SetResult(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => firstOperation);
            await Assert.ThrowsAsync<InvalidOperationException>(() => queuedOperation);

            Task<bool> replacement = coordinator.RunAsync(76561198000000001UL, () =>
            {
                Assert.Equal(1, Volatile.Read(ref renewalRequired));
                Interlocked.Exchange(ref renewalRequired, 0);
                return Task.FromResult(true);
            }, null);

            Assert.True(await replacement);
            Assert.Equal(0, Volatile.Read(ref renewalRequired));

            bool freshOperation = await coordinator.RunAsync(76561198000000001UL, () =>
                Task.FromResult(Volatile.Read(ref renewalRequired) == 0), null);
            Assert.True(freshOperation);
        }
    }
}
