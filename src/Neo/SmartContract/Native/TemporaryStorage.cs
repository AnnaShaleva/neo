// Copyright (C) 2015-2026 The Neo Project.
//
// TemporaryStorage.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

#pragma warning disable IDE0051

using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract.Iterators;
using Neo.SmartContract.Manifest;
using System.Buffers.Binary;

namespace Neo.SmartContract.Native;

/// <summary>
/// A native contract for temporary key-value storage.
/// </summary>
public sealed class TemporaryStorage : NativeContract
{
    private const byte Prefix_TempStorage = 0x01;
    private const byte Prefix_ValidTill = 0x02;
    private const int ExpirationLength = sizeof(ulong);
    private const int MaxCleanupBatchSize = 10_000;
    private const ulong MaxTTL = 7ul * 24 * 60 * 60 * 1000;
    private const long MsPerYear = 365L * 24 * 60 * 60 * 1000;

    internal TemporaryStorage() : base(-14) { }

    internal override ContractTask OnPersistAsync(ApplicationEngine engine)
    {
        return ContractTask.CompletedTask;
    }

    internal override ContractTask PostPersistAsync(ApplicationEngine engine)
    {
        int count = 0;
        var timestamp = engine.PersistingBlock!.Timestamp;
        foreach (var (key, _) in engine.SnapshotCache.Find(CreateStorageKey(Prefix_ValidTill), SeekDirection.Forward))
        {
            var keySpan = key.Key.Span;
            if (keySpan.Length < 1 + ExpirationLength)
                continue;

            ulong validTill = BinaryPrimitives.ReadUInt64BigEndian(keySpan.Slice(1, ExpirationLength));
            if (validTill >= timestamp)
                break;

            engine.SnapshotCache.Delete(key);

            byte[] recordKey = new byte[1 + keySpan.Length - 1 - ExpirationLength];
            recordKey[0] = Prefix_TempStorage;
            keySpan[(1 + ExpirationLength)..].CopyTo(recordKey.AsSpan(1));
            engine.SnapshotCache.Delete(new StorageKey { Id = Id, Key = recordKey });

            count++;
            if (count >= MaxCleanupBatchSize)
                break;
        }

        return ContractTask.CompletedTask;
    }

    [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.WriteStates)]
    private void Put(ApplicationEngine engine, byte[] key, byte[] value, ulong validTill)
    {
        ValidateKeyLength(key);
        ValidateValueLength(value);

        ulong timestamp = GetCurrentTimestamp(engine);
        ValidateValidTill(engine, validTill, timestamp);

        ContractState callingContract = GetContractState(engine, engine.CallingScriptHash);
        StorageKey recordKey = MakeRecordStorageKey(callingContract.Id, key);
        long lifetime = checked((long)(validTill - timestamp));
        engine.AddFee(CalculateStoragePrice(engine, recordKey, value.Length, lifetime));

        PutRecord(engine.SnapshotCache, recordKey, value, validTill);
    }

    [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.ReadStates)]
    private byte[]? Get(ApplicationEngine engine, byte[] key)
    {
        ValidateKeyLength(key);
        return GetInternal(engine, engine.CallingScriptHash, key).Value;
    }

    [ContractMethod(Name = "get", CpuFee = 1 << 15, RequiredCallFlags = CallFlags.ReadStates)]
    private byte[]? GetByHash(ApplicationEngine engine, UInt160 hash, byte[] key)
    {
        ValidateKeyLength(key);
        return GetInternal(engine, hash, key).Value;
    }

    [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.ReadStates)]
    private ulong GetExpiration(ApplicationEngine engine, byte[] key)
    {
        ValidateKeyLength(key);
        return GetInternal(engine, engine.CallingScriptHash, key).ValidTill;
    }

    [ContractMethod(Name = "getExpiration", CpuFee = 1 << 15, RequiredCallFlags = CallFlags.ReadStates)]
    private ulong GetExpirationByHash(ApplicationEngine engine, UInt160 hash, byte[] key)
    {
        ValidateKeyLength(key);
        return GetInternal(engine, hash, key).ValidTill;
    }

    [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.WriteStates)]
    private void Delete(ApplicationEngine engine, byte[] key)
    {
        ValidateKeyLength(key);

        ContractState callingContract = GetContractState(engine, engine.CallingScriptHash);
        StorageKey recordKey = MakeRecordStorageKey(callingContract.Id, key);
        if (!engine.SnapshotCache.TryGet(recordKey, out var record))
            return;

        engine.SnapshotCache.Delete(recordKey);

        if (!TryReadValidTill(record.Value.Span, out var validTill))
            return;

        engine.SnapshotCache.Delete(MakeValidTillStorageKey(validTill, recordKey.Key.Span));
    }

    [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.ReadStates)]
    private StorageIterator Find(ApplicationEngine engine, byte[] prefix, FindOptions options)
    {
        ValidateKeyLength(prefix);
        return FindInternal(engine, engine.CallingScriptHash, prefix, options);
    }

    [ContractMethod(Name = "find", CpuFee = 1 << 15, RequiredCallFlags = CallFlags.ReadStates)]
    private StorageIterator FindByHash(ApplicationEngine engine, UInt160 hash, byte[] prefix, FindOptions options)
    {
        ValidateKeyLength(prefix);
        return FindInternal(engine, hash, prefix, options);
    }

    [ContractMethod(CpuFee = 1 << 15, RequiredCallFlags = CallFlags.WriteStates)]
    private void Renew(ApplicationEngine engine, byte[] key, ulong validTill)
    {
        ValidateKeyLength(key);

        ulong timestamp = GetCurrentTimestamp(engine);
        ValidateValidTill(engine, validTill, timestamp);

        ContractState callingContract = GetContractState(engine, engine.CallingScriptHash);
        StorageKey recordKey = MakeRecordStorageKey(callingContract.Id, key);
        var oldRecord = engine.SnapshotCache.TryGet(recordKey) ?? throw new InvalidOperationException("old record not found");

        if (!TryReadValidTill(oldRecord.Value.Span, out ulong oldValidTill) || oldValidTill < timestamp)
            throw new InvalidOperationException("old record is expired");
        if (validTill <= oldValidTill)
            throw new ArgumentOutOfRangeException(nameof(validTill), $"new expiration point should be newer than the old one: {validTill} vs {oldValidTill}");

        byte[] value = oldRecord.Value[ExpirationLength..].ToArray();
        long lifetime = checked((long)(validTill - oldValidTill));
        engine.AddFee(CalculateStoragePrice(engine, recordKey, value.Length, lifetime));

        engine.SnapshotCache.Delete(MakeValidTillStorageKey(oldValidTill, recordKey.Key.Span));
        PutRecord(engine.SnapshotCache, recordKey, value, validTill);
    }

    private StorageIterator FindInternal(ApplicationEngine engine, UInt160? hash, byte[] prefix, FindOptions options)
    {
        ValidateFindOptions(options);
        var direction = options.HasFlag(FindOptions.Backwards) ? SeekDirection.Backward : SeekDirection.Forward;

        ContractState contract = GetContractState(engine, hash);
        byte[] recordPrefix = MakeRecordKey(contract.Id, prefix);
        ulong timestamp = GetCurrentTimestamp(engine);
        var enumerator = engine.SnapshotCache
            .Find(new StorageKey { Id = Id, Key = recordPrefix }, direction)
            .Where(kvp => TryReadValidTill(kvp.Value.Value.Span, out var validTill) && validTill >= timestamp)
            .Select(kvp => (kvp.Key, new StorageItem(kvp.Value.Value[ExpirationLength..].ToArray())))
            .GetEnumerator();

        return new StorageIterator(enumerator, recordPrefix.Length, options);
    }

    private (byte[]? Value, ulong ValidTill) GetInternal(ApplicationEngine engine, UInt160? hash, byte[] key)
    {
        ContractState contract = GetContractState(engine, hash);
        StorageKey recordKey = MakeRecordStorageKey(contract.Id, key);
        if (!engine.SnapshotCache.TryGet(recordKey, out var record))
            return (null, 0);

        if (!TryReadValidTill(record.Value.Span, out var validTill))
            return (null, 0);
        if (validTill < GetCurrentTimestamp(engine))
            return (null, 0);

        return (record.Value[ExpirationLength..].ToArray(), validTill);
    }

    private void PutRecord(DataCache snapshot, StorageKey recordKey, ReadOnlySpan<byte> value, ulong validTill)
    {
        byte[] recordValue = new byte[ExpirationLength + value.Length];
        BinaryPrimitives.WriteUInt64BigEndian(recordValue.AsSpan(0, ExpirationLength), validTill);
        value.CopyTo(recordValue.AsSpan(ExpirationLength));

        snapshot.GetAndChange(recordKey, () => new StorageItem())!.Value = recordValue;
        snapshot.GetAndChange(MakeValidTillStorageKey(validTill, recordKey.Key.Span), () => new StorageItem())!.Value = Array.Empty<byte>();
    }

    private StorageKey MakeRecordStorageKey(int contractId, ReadOnlySpan<byte> key)
    {
        return new StorageKey { Id = Id, Key = MakeRecordKey(contractId, key) };
    }

    private static byte[] MakeRecordKey(int contractId, ReadOnlySpan<byte> key)
    {
        byte[] buffer = new byte[1 + sizeof(uint) + key.Length];
        buffer[0] = Prefix_TempStorage;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, sizeof(uint)), unchecked((uint)contractId));
        key.CopyTo(buffer.AsSpan(1 + sizeof(uint)));
        return buffer;
    }

    private StorageKey MakeValidTillStorageKey(ulong validTill, ReadOnlySpan<byte> recordKey)
    {
        byte[] buffer = new byte[1 + ExpirationLength + recordKey.Length - 1];
        buffer[0] = Prefix_ValidTill;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(1, ExpirationLength), validTill);
        recordKey[1..].CopyTo(buffer.AsSpan(1 + ExpirationLength));
        return new StorageKey { Id = Id, Key = buffer };
    }

    private static ulong GetCurrentTimestamp(ApplicationEngine engine)
    {
        if (engine.PersistingBlock is not null)
            return engine.PersistingBlock.Timestamp;

        UInt256 hash = Ledger.CurrentHash(engine.SnapshotCache);
        Block currentBlock = Ledger.GetBlock(engine.SnapshotCache, hash) ?? throw new InvalidOperationException("current block not found");
        return currentBlock.Timestamp + engine.ProtocolSettings.MillisecondsPerBlock;
    }

    private static ContractState GetContractState(ApplicationEngine engine, UInt160? hash)
    {
        if (hash is null)
            throw new InvalidOperationException("calling contract not found");
        return ContractManagement.GetContract(engine.SnapshotCache, hash) ?? throw new InvalidOperationException($"contract not found: {hash}");
    }

    private static bool TryReadValidTill(ReadOnlySpan<byte> value, out ulong validTill)
    {
        if (value.Length < ExpirationLength)
        {
            validTill = 0;
            return false;
        }

        validTill = BinaryPrimitives.ReadUInt64BigEndian(value.Slice(0, ExpirationLength));
        return true;
    }

    private static long CalculateStoragePrice(ApplicationEngine engine, StorageKey key, int valueLength, long lifetime)
    {
        var item = engine.SnapshotCache.TryGet(key);
        int sizeInc = valueLength;
        if (item is null)
        {
            sizeInc = key.Key.Length + valueLength;
        }
        else
        {
            if (valueLength == 0)
                sizeInc = 0;
            else if (valueLength <= item.Value.Length)
                sizeInc = (valueLength - 1) / 4 + 1;
            else if (item.Value.Length == 0)
                sizeInc = valueLength;
            else
                sizeInc = (item.Value.Length - 1) / 4 + 1 + valueLength - item.Value.Length;
        }

        long permanentPrice = checked(sizeInc * (long)engine.StoragePrice);
        return permanentPrice / Math.Min(lifetime, MsPerYear) * MsPerYear;
    }

    private static void ValidateFindOptions(FindOptions options)
    {
        if ((options & ~FindOptions.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(options), $"Invalid find options: {options}");

        if (options.HasFlag(FindOptions.KeysOnly) &&
            (options.HasFlag(FindOptions.ValuesOnly) ||
             options.HasFlag(FindOptions.DeserializeValues) ||
             options.HasFlag(FindOptions.PickField0) ||
             options.HasFlag(FindOptions.PickField1)))
        {
            throw new ArgumentException("KeysOnly cannot be used with ValuesOnly, DeserializeValues, PickField0, or PickField1", nameof(options));
        }

        if (options.HasFlag(FindOptions.ValuesOnly) && (options.HasFlag(FindOptions.KeysOnly) || options.HasFlag(FindOptions.RemovePrefix)))
            throw new ArgumentException("ValuesOnly cannot be used with KeysOnly or RemovePrefix", nameof(options));

        if (options.HasFlag(FindOptions.PickField0) && options.HasFlag(FindOptions.PickField1))
            throw new ArgumentException("PickField0 and PickField1 cannot be used together", nameof(options));

        if ((options.HasFlag(FindOptions.PickField0) || options.HasFlag(FindOptions.PickField1)) && !options.HasFlag(FindOptions.DeserializeValues))
            throw new ArgumentException("PickField0 or PickField1 requires DeserializeValues", nameof(options));
    }

    private static void ValidateValidTill(ApplicationEngine engine, ulong validTill, ulong timestamp)
    {
        ulong maxValidTill = checked(timestamp + MaxTTL);
        if (validTill > maxValidTill)
            throw new ArgumentOutOfRangeException(nameof(validTill), $"validTill exceeds max limit: {validTill} vs {maxValidTill}");

        ulong minValidTill = checked(timestamp + 2ul * engine.ProtocolSettings.MillisecondsPerBlock);
        if (validTill < minValidTill)
            throw new ArgumentOutOfRangeException(nameof(validTill), $"item is valid for less than 2*msPerBlock: {validTill} vs {minValidTill}");
    }

    private static void ValidateKeyLength(byte[] key)
    {
        if (key.Length > ApplicationEngine.MaxStorageKeySize)
            throw new ArgumentException($"Key length {key.Length} exceeds maximum allowed size of {ApplicationEngine.MaxStorageKeySize} bytes.", nameof(key));
    }

    private static void ValidateValueLength(byte[] value)
    {
        if (value.Length > ApplicationEngine.MaxStorageValueSize)
            throw new ArgumentException($"Value length {value.Length} exceeds maximum allowed size of {ApplicationEngine.MaxStorageValueSize} bytes.", nameof(value));
    }
}
