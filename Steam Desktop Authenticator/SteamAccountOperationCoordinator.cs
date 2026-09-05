using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Steam_Desktop_Authenticator
{
    internal sealed class SteamAccountOperationCoordinator
    {
        private readonly ConcurrentDictionary<ulong, SemaphoreSlim> accountLocks = new ConcurrentDictionary<ulong, SemaphoreSlim>();

        public async Task<T> RunAsync<T>(ulong steamId, Func<Task<T>> operation, Action<Exception> onFailure, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            SemaphoreSlim accountLock = accountLocks.GetOrAdd(steamId, _ => new SemaphoreSlim(1, 1));
            await accountLock.WaitAsync(cancellationToken);
            try
            {
                if (onFailure == null)
                    return await operation();

                try
                {
                    return await operation();
                }
                catch (Exception exception)
                {
                    // State transitions belong to the protected operation. The
                    // next account operation must not pass the gate first.
                    onFailure(exception);
                    throw;
                }
            }
            finally
            {
                accountLock.Release();
            }
        }
    }
}
