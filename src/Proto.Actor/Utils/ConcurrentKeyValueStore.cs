// -----------------------------------------------------------------------
// <copyright file="ConcurrentKeyValueStore.cs" company="Asynkron AB">
//      Copyright (C) 2015-2021 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;

namespace Proto.Utils
{
    namespace Proto.Utils
    {
        [PublicAPI]
        public abstract class ConcurrentKeyValueStore<T>
        {
            private readonly AsyncSemaphore _semaphore;

            protected ConcurrentKeyValueStore(AsyncSemaphore semaphore) => _semaphore = semaphore;

            public Task<T?> GetStateAsync(string id, CancellationToken ct) => _semaphore.WaitAsync(() => InnerGetStateAsync(id, ct));

            public Task SetStateAsync(string id, T state, CancellationToken ct) => _semaphore.WaitAsync(() => InnerSetStateAsync(id, state, ct));

            public Task ClearStateAsync(string id, CancellationToken ct) => _semaphore.WaitAsync(() => InnerClearStateAsync(id, ct));

            protected abstract Task<T?> InnerGetStateAsync(string id, CancellationToken ct);

            protected abstract Task InnerSetStateAsync(string id, T state, CancellationToken ct);

            protected abstract Task InnerClearStateAsync(string id, CancellationToken ct);
        }
    }
}