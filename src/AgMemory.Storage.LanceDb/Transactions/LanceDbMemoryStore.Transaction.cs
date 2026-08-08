using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using AgMemory.Contracts;
using lancedb;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private sealed class Transaction(LanceDbMemoryStore owner) : IMemoryStoreTransaction
    {
        private readonly Dictionary<MemoryId, MemoryRecord?> _recordChanges = [];
        private readonly Dictionary<string, SessionHotMemory> _hotChanges = [];
        private readonly Dictionary<string, IdempotencyReceipt> _receiptChanges = [];
        private readonly List<OutboxMessage> _outboxChanges = [];
        private readonly Dictionary<MemoryId, MemoryRecord?> _recordSnapshots = [];
        private readonly Dictionary<string, SessionHotMemory?> _hotSnapshots = [];
        private bool _committed;
        private bool _disposed;

        public async Task<IdempotencyReceipt?> FindReceiptAsync(
            AuthorizedScopeSet authorizedScopes,
            string commandKind,
            ScopeSelector scope,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            if (!authorizedScopes.Contains(scope.Scope)) return null;
            var key = ReceiptKey(commandKind, scope, idempotencyKey);
            return _receiptChanges.TryGetValue(key, out var changed)
                ? changed
                : await owner.ReadReceiptAsync(authorizedScopes, commandKind, scope, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }

        public async Task<MemoryRecord?> FindByDeduplicationKeyAsync(
            AuthorizedScopeSet authorizedScopes,
            ScopeSelector scope,
            string deduplicationKey,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            if (!authorizedScopes.Contains(scope.Scope)) return null;
            var candidates = new List<MemoryRecord>();
            var predicate = $"({BuildSelectorPredicate(scope)}) AND deduplication_key = {Literal(deduplicationKey)}";
            candidates.AddRange(await owner.ReadRecordsAsync(predicate, cancellationToken).ConfigureAwait(false));
            candidates.AddRange(_recordChanges.Values.Where(record => record is not null).Cast<MemoryRecord>()
                .Where(record => record.Scope == scope.Scope && record.DeduplicationKey == deduplicationKey));
            return candidates.Where(record => authorizedScopes.Contains(record.Scope)).OrderBy(record => record.Id.Value, StringComparer.Ordinal).FirstOrDefault();
        }

        public async Task<MemoryRecord?> FindRecordAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            var record = await CurrentRecordAsync(id, cancellationToken).ConfigureAwait(false);
            return record is not null && scopes.Contains(record.Scope) ? record : null;
        }

        public async Task<SessionHotMemory?> FindHotMemoryAsync(
            AuthorizedScopeSet scopes,
            MemoryScope exactScope,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            if (!scopes.Contains(exactScope)) return null;
            var key = ScopeKey(exactScope);
            var memory = await CurrentHotMemoryAsync(exactScope, cancellationToken).ConfigureAwait(false);
            return memory is not null && scopes.Contains(memory.Scope) && _hotChanges.ContainsKey(key)
                ? memory
                : memory is not null && scopes.Contains(memory.Scope) ? memory : null;
        }

        public async Task<ConditionalWriteResult> WriteRecordAsync(
            AuthorizedScopeSet authorizedScopes,
            MemoryRecord record,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(record);
            ValidateMemoryRecordForSchema(record);
            if (!authorizedScopes.Contains(record.Scope)) return new(false, null);
            var existing = await CurrentRecordAsync(record.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!authorizedScopes.Contains(existing.Scope) || expectedVersion == 0 || existing.Version != expectedVersion)
                    return new(false, authorizedScopes.Contains(existing.Scope) ? existing.Version : null);
            }
            else if (expectedVersion != 0)
            {
                return new(false, null);
            }

            _recordChanges[record.Id] = record;
            return new(true, record.Version);
        }

        public async Task<IReadOnlyList<ConditionalWriteResult>> WriteRecordsAsync(
            AuthorizedScopeSet authorizedScopes,
            IReadOnlyList<ConditionalRecordWrite> writes,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(writes);
            var results = new List<ConditionalWriteResult>(writes.Count);
            foreach (var write in writes)
                results.Add(await WriteRecordAsync(authorizedScopes, write.Record, write.ExpectedVersion, cancellationToken).ConfigureAwait(false));
            return results;
        }

        public async Task<ConditionalWriteResult> DeleteRecordAsync(
            AuthorizedScopeSet authorizedScopes,
            MemoryRecord record,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(record);
            var existing = await CurrentRecordAsync(record.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null) return new(false, null);
            if (!authorizedScopes.Contains(record.Scope) || !authorizedScopes.Contains(existing.Scope)) return new(false, null);
            if (existing.Version != expectedVersion) return new(false, existing.Version);
            _recordChanges[record.Id] = null;
            return new(true, null);
        }

        public async Task<ConditionalWriteResult> WriteHotMemoryAsync(
            AuthorizedScopeSet authorizedScopes,
            SessionHotMemory memory,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(memory);
            memory.Validate();
            if (!authorizedScopes.Contains(memory.Scope)) return new(false, null);
            var existing = await CurrentHotMemoryAsync(memory.Scope, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!authorizedScopes.Contains(existing.Scope) || expectedVersion == 0 || existing.Version != expectedVersion)
                    return new(false, authorizedScopes.Contains(existing.Scope) ? existing.Version : null);
            }
            else if (expectedVersion != 0)
            {
                return new(false, null);
            }

            _hotChanges[ScopeKey(memory.Scope)] = memory;
            return new(true, memory.Version);
        }

        public Task SaveReceiptAsync(AuthorizedScopeSet authorizedScopes, IdempotencyReceipt receipt, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(receipt);
            cancellationToken.ThrowIfCancellationRequested();
            RequireAuthorized(authorizedScopes, receipt.Scope);
            _receiptChanges[ReceiptKey(receipt.CommandKind, receipt.Scope, receipt.IdempotencyKey)] = receipt;
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(AuthorizedScopeSet authorizedScopes, OutboxMessage message, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            RequireAuthorized(authorizedScopes, message.Scope);
            _outboxChanges.Add(message);
            return Task.CompletedTask;
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            foreach (var change in _recordChanges)
            {
                var previous = _recordSnapshots.GetValueOrDefault(change.Key);
                if (change.Value is null) await owner.DeleteRecordAsync(previous!, cancellationToken).ConfigureAwait(false);
                else await owner.PersistRecordAsync(change.Value, previous, cancellationToken).ConfigureAwait(false);
            }
            foreach (var memory in _hotChanges.Values)
                await owner.PersistHotMemoryAsync(memory, cancellationToken).ConfigureAwait(false);
            foreach (var receipt in _receiptChanges.Values)
                await owner.PersistReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            foreach (var message in _outboxChanges)
                await owner.PersistOutboxMessageAsync(message, cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            owner._gate.Release();
            return ValueTask.CompletedTask;
        }

        private async Task<MemoryRecord?> CurrentRecordAsync(MemoryId id, CancellationToken cancellationToken)
        {
            if (_recordChanges.TryGetValue(id, out var changed)) return changed;
            if (_recordSnapshots.TryGetValue(id, out var snapshot)) return snapshot;
            var loaded = await owner.ReadRecordUnscopedAsync(id, cancellationToken).ConfigureAwait(false);
            _recordSnapshots[id] = loaded;
            return loaded;
        }

        private async Task<SessionHotMemory?> CurrentHotMemoryAsync(MemoryScope scope, CancellationToken cancellationToken)
        {
            var key = ScopeKey(scope);
            if (_hotChanges.TryGetValue(key, out var changed)) return changed;
            if (_hotSnapshots.TryGetValue(key, out var snapshot)) return snapshot;
            var allScopes = new AuthorizedScopeSet([new ScopeSelector(scope)]);
            var loaded = await owner.ReadHotMemoryAsync(scope, allScopes, cancellationToken).ConfigureAwait(false);
            _hotSnapshots[key] = loaded;
            return loaded;
        }

        private void ThrowIfUnavailable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Transaction));
            if (_committed) throw new InvalidOperationException("A LanceDB memory transaction cannot be reused after commit.");
        }

        private static void RequireAuthorized(AuthorizedScopeSet authorizedScopes, ScopeSelector scope)
        {
            if (!authorizedScopes.Contains(scope.Scope))
                throw new UnauthorizedAccessException("A storage transaction cannot write outside its authorized exact selectors.");
        }
    }
}
