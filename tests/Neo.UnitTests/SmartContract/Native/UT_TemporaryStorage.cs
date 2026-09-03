// Copyright (C) 2015-2026 The Neo Project.
//
// UT_TemporaryStorage.cs file belongs to the neo project and is free
// software distributed under the MIT software license, see the
// accompanying file LICENSE in the main directory of the
// repository or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Neo.Cryptography.ECC;
using Neo.Extensions;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Iterators;
using Neo.SmartContract.Native;
using Neo.UnitTests.Extensions;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;

namespace Neo.UnitTests.SmartContract.Native
{
    [TestClass]
    public class UT_TemporaryStorage
    {
        private const long TestGas = 1_000_000_000_000_000;
        private const byte PrefixRecord = 0x01;
        private const byte PrefixValidTill = 0x02;
        private const int MaxCleanupBatchSize = 10_000;
        private DataCache _snapshotCache = null!;

        [TestInitialize]
        public void TestSetup()
        {
            _snapshotCache = TestBlockchain.GetTestSnapshotCache();
        }

        [TestMethod]
        public void Check_Name()
        {
            Assert.AreEqual(nameof(TemporaryStorage), NativeContract.TemporaryStorage.Name);
        }

        [TestMethod]
        public void Test_Put_ChargesGas_AndSupports64ByteKey()
        {
            var snapshot = _snapshotCache.CloneCache();
            var caller = NativeContract.GAS.Hash;

            var timePerBlock = (ulong)NativeContract.Policy.Call(snapshot, "getMillisecondsPerBlock").GetInteger();
            ulong now = 1;
            var persistingBlock = CreatePersistingBlock(snapshot, now);
            ulong validTill = now + 3 * timePerBlock;
            byte[] key = Enumerable.Range(1, ApplicationEngine.MaxStorageKeySize).Select(static i => (byte)i).ToArray();
            byte[] value = [0x7B];

            var (ret, gasConsumed) = CallFromContractWithGas(snapshot, persistingBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill });

            Assert.IsInstanceOfType<Null>(ret);
            Assert.IsGreaterThan(gasConsumed, 0L);

            ret = CallFromContract(snapshot, persistingBlock, caller, "get",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key });
            Assert.IsInstanceOfType<ByteString>(ret);
            Assert.AreSequenceEqual(value, ret.GetSpan().ToArray());
        }

        [TestMethod]
        public void Test_Find_Options_HideExpired()
        {
            var snapshot = _snapshotCache.CloneCache();
            var caller = NativeContract.GAS.Hash;

            var timePerBlock = (ulong)NativeContract.Policy.Call(snapshot, "getMillisecondsPerBlock").GetInteger();
            ulong now = 1;
            var putBlock = CreatePersistingBlock(snapshot, now);
            byte[] key1 = [0xAA, 0x01];
            byte[] key2 = [0xAA, 0x02];
            byte[] key3 = [0xAA, 0x03];
            byte[] value1 = [0x11];
            byte[] value2 = [0x12];
            byte[] value3 = [0x13];

            _ = CallFromContract(snapshot, putBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value1 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(now + 2 * timePerBlock) });
            _ = CallFromContract(snapshot, putBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key2 },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value2 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(now + 5 * timePerBlock) });
            _ = CallFromContract(snapshot, putBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key3 },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value3 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(now + 6 * timePerBlock) });

            var queryBlock = CreatePersistingBlock(snapshot, now + 3 * timePerBlock);
            var values = FindByteStrings(snapshot, queryBlock, caller, [0xAA], FindOptions.ValuesOnly);
            Assert.AreEqual(2, values.Count);
            Assert.AreSequenceEqual(value2, values[0]);
            Assert.AreSequenceEqual(value3, values[1]);

            var keys = FindByteStrings(snapshot, queryBlock, caller, [0xAA],
                FindOptions.KeysOnly | FindOptions.RemovePrefix | FindOptions.Backwards);
            Assert.AreEqual(2, keys.Count);
            Assert.AreSequenceEqual(new byte[] { 0x03 }, keys[0]);
            Assert.AreSequenceEqual(new byte[] { 0x02 }, keys[1]);
        }

        [TestMethod]
        public void Test_DeleteAndOverwrite_RemoveValidTillIndex()
        {
            var snapshot = _snapshotCache.CloneCache();
            var caller = NativeContract.GAS.Hash;

            var timePerBlock = (ulong)NativeContract.Policy.Call(snapshot, "getMillisecondsPerBlock").GetInteger();
            ulong now = 1;
            var persistingBlock = CreatePersistingBlock(snapshot, now);
            byte[] key = [0xAB, 0xCD];
            byte[] value1 = [0x01];
            byte[] value2 = [0x02];
            ulong validTill1 = now + 4 * timePerBlock;
            ulong validTill2 = now + 5 * timePerBlock;

            _ = CallFromContract(snapshot, persistingBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value1 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill1 });
            Assert.IsNotNull(snapshot.TryGet(MakeValidTillStorageKey(validTill1, key)));

            _ = CallFromContract(snapshot, persistingBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value2 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill2 });
            Assert.IsNull(snapshot.TryGet(MakeValidTillStorageKey(validTill1, key)));
            Assert.IsNotNull(snapshot.TryGet(MakeValidTillStorageKey(validTill2, key)));

            _ = CallFromContract(snapshot, persistingBlock, caller, "delete",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key });
            Assert.IsNull(snapshot.TryGet(MakeRecordStorageKey(key)));
            Assert.IsNull(snapshot.TryGet(MakeValidTillStorageKey(validTill2, key)));
        }

        [TestMethod]
        public void Test_PutGetRenewDelete()
        {
            var snapshot = _snapshotCache.CloneCache();
            var caller = NativeContract.GAS.Hash;

            var timePerBlock = (ulong)NativeContract.Policy.Call(snapshot, "getMillisecondsPerBlock").GetInteger();
            var maxTTL = (ulong)NativeContract.Policy.Call(snapshot, "getTemporaryStorageMaxTTL").GetInteger();
            ulong now = 1;
            var persistingBlock = CreatePersistingBlock(snapshot, now);
            ulong validTill1 = now + 4 * timePerBlock;
            ulong validTill2 = now + 5 * timePerBlock;
            ulong validTillRenewed = validTill1 + 2 * timePerBlock;
            ulong validTillStale = now + 2 * timePerBlock;
            byte[] key1 = [0xAA, 0x01];
            byte[] value1 = [0x01];
            byte[] key2 = [0xAA, 0x02];
            byte[] value2 = [0x02];
            byte[] key3 = [0xBB, 0x01];
            byte[] value3 = [0x03];
            byte[] unknownKey = [0xCC];
            byte[] staleKey = [0xDD];

            // put: validTill is too low.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                CallFromContract(snapshot, persistingBlock, caller, "put",
                    new ContractParameter(ContractParameterType.ByteArray) { Value = key1 },
                    new ContractParameter(ContractParameterType.ByteArray) { Value = value1 },
                    new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)2 * timePerBlock - 1 });
            });

            // put: validTill exceeds MaxTTL
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                CallFromContract(snapshot, persistingBlock, caller, "put",
                    new ContractParameter(ContractParameterType.ByteArray) { Value = key1 },
                    new ContractParameter(ContractParameterType.ByteArray) { Value = value1 },
                    new ContractParameter(ContractParameterType.Integer) { Value = now + maxTTL + 1 });
            });

            // put: good.
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value1 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill1 }));
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key2 },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value2 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill2 }));
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key3 },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value3 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill2 })); // same validTill2.
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "put",
                new ContractParameter(ContractParameterType.ByteArray) { Value = staleKey },
                new ContractParameter(ContractParameterType.ByteArray) { Value = value1 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTillStale }));

            // get AA: good.
            var ret = CallFromContract(snapshot, persistingBlock, caller, "get",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 });
            Assert.IsInstanceOfType<ByteString>(ret);
            Assert.AreSequenceEqual(value1, ret.GetSpan().ToArray());

            // get AA by hash: good.
            ret = CallFromContract(snapshot, persistingBlock, caller, "get",
                new ContractParameter(ContractParameterType.Hash160) { Value = caller },
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 });
            Assert.IsInstanceOfType<ByteString>(ret);
            Assert.AreSequenceEqual(value1, ret.GetSpan().ToArray());

            // getExpiration: unknown item.
            ret = CallFromContract(snapshot, persistingBlock, caller, "getExpiration",
                new ContractParameter(ContractParameterType.ByteArray) { Value = unknownKey });
            Assert.AreEqual(new BigInteger(0), ret?.GetInteger());

            // getExpiration: known item.
            ret = CallFromContract(snapshot, persistingBlock, caller, "getExpiration",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 });
            Assert.AreEqual(new BigInteger(validTill1), ret?.GetInteger());

            // getExpiration: outdated item.
            persistingBlock = CreatePersistingBlock(snapshot, validTillStale + 1);
            ret = CallFromContract(snapshot, persistingBlock, caller, "getExpiration",
                new ContractParameter(ContractParameterType.ByteArray) { Value = staleKey });
            Assert.AreEqual(new BigInteger(0), ret?.GetInteger());

            // find AA-prefixed values: OK.
            using var sb = new ScriptBuilder().EmitDynamicCall(NativeContract.TemporaryStorage.Hash, "find",
                new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0xAA } },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(byte)FindOptions.ValuesOnly });
            using var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot, persistingBlock, TestProtocolSettings.Default, TestGas);
            engine.LoadScript(sb.ToArray());
            var state = engine.CurrentContext!.GetState<ExecutionContextState>();
            state.NativeCallingScriptHash = caller;
            state.ScriptHash = caller;
            engine.Execute();
            Assert.IsInstanceOfType<InteropInterface>(engine.ResultStack[0]);
            var iter = engine.ResultStack[0].GetInterface<object>() as StorageIterator;
            Assert.IsTrue(iter.Next());
            Assert.AreSequenceEqual(value1, iter.Value().GetSpan().ToArray());
            Assert.IsTrue(iter.Next());
            Assert.AreSequenceEqual(value2, iter.Value().GetSpan().ToArray());
            Assert.IsFalse(iter.Next());

            // renew: good.
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "renew",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTillRenewed })!);

            // getExpiration: should return an updated value.
            ret = CallFromContract(snapshot, persistingBlock, caller, "getExpiration",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 });
            Assert.AreEqual(new BigInteger(validTillRenewed), ret?.GetInteger());

            // delete: good.
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "delete",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 })!);

            // get: retrieve removed entry should return null.
            ret = CallFromContract(snapshot, persistingBlock, caller, "get",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 });
            Assert.IsInstanceOfType<Null>(ret);

            // delete: unknown entry.
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "delete",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 })!);

            // getExpiration: unknown entry.
            ret = CallFromContract(snapshot, persistingBlock, caller, "getExpiration",
                new ContractParameter(ContractParameterType.ByteArray) { Value = key1 });
            Assert.AreEqual(BigInteger.Zero, ret?.GetInteger());
        }

        [TestMethod]
        public void Test_TempStoragePostPersist()
        {
            const int count = 10;
            var snapshot = _snapshotCache.CloneCache();
            var caller = NativeContract.GAS.Hash;

            var timePerBlock = (ulong)NativeContract.Policy.Call(snapshot, "getMillisecondsPerBlock").GetInteger();
            ulong now = 1;
            var persistingBlock = CreatePersistingBlock(snapshot, now);
            byte[] value = [0x01];

            using (var sb = new ScriptBuilder())
            {
                for (int i = 0; i < 3 * count; i++)
                {
                    sb.EmitDynamicCall(NativeContract.TemporaryStorage.Hash, "put",
                        new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { (byte)i } },
                        new ContractParameter(ContractParameterType.ByteArray) { Value = value },
                        new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)now + 2 * timePerBlock + i });
                }

                using var appEngine = ApplicationEngine.Create(TriggerType.Application, null, snapshot, persistingBlock, TestProtocolSettings.Default, TestGas);
                appEngine.LoadScript(sb.ToArray());
                var appState = appEngine.CurrentContext!.GetState<ExecutionContextState>();
                appState.NativeCallingScriptHash = caller;
                appState.ScriptHash = caller;
                Assert.AreEqual(VMState.HALT, appEngine.Execute(), appEngine.FaultException?.ToString());
                appEngine.SnapshotCache.Commit();
                snapshot.Commit();
            }

            // Check all values are retrievable at the current timestamp.
            for (int i = 0; i < 3 * count; i++)
            {
                var ret = CallFromContract(snapshot, persistingBlock, caller, "get",
                    new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { (byte)i } });
                Assert.IsInstanceOfType<ByteString>(ret);
                Assert.AreSequenceEqual(value, ret.GetSpan().ToArray());
            }

            // Run persist at (now + 2 * TimePerBlock + 2 * maxCleanupBatchSize). 2/3 of all items should be marked as stale and removed.
            var script = new ScriptBuilder();
            script.EmitSysCall(ApplicationEngine.System_Contract_NativePostPersist);

            persistingBlock = CreatePersistingBlock(snapshot, now + 2 * timePerBlock + 2 * count);
            using var engine = ApplicationEngine.Create(TriggerType.PostPersist, null, snapshot, persistingBlock, TestProtocolSettings.Default, TestGas);
            engine.LoadScript(script.ToArray());
            Assert.AreEqual(VMState.HALT, engine.Execute(), engine.FaultException?.ToString());
            engine.SnapshotCache.Commit();
            snapshot.Commit();

            // Ensure stale items are not retrievable via contract API anymore.
            for (int i = 0; i < 2 * count; i++)
            {
                Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, persistingBlock, caller, "get",
                    new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { (byte)i } }));

                // Ensure the item is removed from the storage.
                Assert.IsNull(snapshot.TryGet(MakeRecordStorageKey(new byte[] { (byte)i })));
                Assert.IsNull(snapshot.TryGet(MakeValidTillStorageKey((ulong)now + 2 * timePerBlock + (ulong)i, new byte[] { (byte)i })));
            }
            // Ensure the rest 1/3 of items are still retrievable.
            for (int i = 2 * count; i < 3 * count; i++)
            {
                var ret = CallFromContract(snapshot, persistingBlock, caller, "get",
                    new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { (byte)i } });
                Assert.IsInstanceOfType<ByteString>(ret);
                Assert.AreSequenceEqual(value, ret.GetSpan().ToArray());
                Assert.IsNotNull(snapshot.TryGet(MakeValidTillStorageKey((ulong)now + 2 * timePerBlock + (ulong)i, new byte[] { (byte)i })));
            }
        }

        [TestMethod]
        public void Test_PostPersist_CleanupBatchCap_AndIndexCleanup()
        {
            const int staleCount = MaxCleanupBatchSize + 10;
            const int freshCount = 5;
            var snapshot = _snapshotCache.CloneCache();
            var caller = NativeContract.GAS.Hash;
            var timePerBlock = (ulong)NativeContract.Policy.Call(snapshot, "getMillisecondsPerBlock").GetInteger();
            ulong now = 1;
            ulong staleValidTill = now + 2 * timePerBlock;
            ulong freshValidTill = now + 6 * timePerBlock;
            byte[] value = [0x01];

            for (int i = 0; i < staleCount; i++)
                PutRawRecord(snapshot, BuildStaleKey(i), value, staleValidTill);
            for (int i = 0; i < freshCount; i++)
                PutRawRecord(snapshot, BuildFreshKey(i), value, freshValidTill);
            snapshot.Commit();

            using var script = new ScriptBuilder();
            script.EmitSysCall(ApplicationEngine.System_Contract_NativePostPersist);
            var postPersistBlock = CreatePersistingBlock(snapshot, staleValidTill + 1);
            using var engine = ApplicationEngine.Create(TriggerType.PostPersist, null, snapshot, postPersistBlock, TestProtocolSettings.Default, TestGas);
            engine.LoadScript(script.ToArray());
            Assert.AreEqual(VMState.HALT, engine.Execute(), engine.FaultException?.ToString());
            engine.SnapshotCache.Commit();
            snapshot.Commit();

            for (int i = 0; i < MaxCleanupBatchSize; i++)
            {
                var key = BuildStaleKey(i);
                Assert.IsNull(snapshot.TryGet(MakeRecordStorageKey(key)));
                Assert.IsNull(snapshot.TryGet(MakeValidTillStorageKey(staleValidTill, key)));
            }
            for (int i = MaxCleanupBatchSize; i < staleCount; i++)
            {
                var key = BuildStaleKey(i);
                Assert.IsNotNull(snapshot.TryGet(MakeRecordStorageKey(key)));
                Assert.IsNotNull(snapshot.TryGet(MakeValidTillStorageKey(staleValidTill, key)));
            }
            for (int i = 0; i < freshCount; i++)
            {
                var key = BuildFreshKey(i);
                Assert.IsNotNull(snapshot.TryGet(MakeRecordStorageKey(key)));
                Assert.IsNotNull(snapshot.TryGet(MakeValidTillStorageKey(freshValidTill, key)));
            }

            var sampleExpiredKey = BuildStaleKey(staleCount - 1);
            Assert.IsInstanceOfType<Null>(CallFromContract(snapshot, postPersistBlock, caller, "get",
                new ContractParameter(ContractParameterType.ByteArray) { Value = sampleExpiredKey }));

            var values = FindByteStrings(snapshot, postPersistBlock, caller, [0xBA], FindOptions.ValuesOnly);
            Assert.AreEqual(freshCount, values.Count);
        }

        private static StackItem CallFromContract(DataCache snapshot, Block persistingBlock, UInt160 caller, string method, params ContractParameter[] args)
        {
            return CallFromContractWithGas(snapshot, persistingBlock, caller, method, args).Result;
        }

        private static (StackItem Result, long GasConsumed) CallFromContractWithGas(DataCache snapshot, Block persistingBlock, UInt160 caller, string method, params ContractParameter[] args)
        {
            using var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot, persistingBlock, TestProtocolSettings.Default, TestGas);
            using var sb = new ScriptBuilder();
            sb.EmitDynamicCall(NativeContract.TemporaryStorage.Hash, method, args);
            engine.LoadScript(sb.ToArray());

            var state = engine.CurrentContext!.GetState<ExecutionContextState>();
            state.NativeCallingScriptHash = caller;
            state.ScriptHash = caller;

            if (engine.Execute() != VMState.HALT)
            {
                Exception exception = engine.FaultException!;
                while (exception.InnerException is not null)
                    exception = exception.InnerException;
                throw exception;
            }

            engine.SnapshotCache.Commit();
            snapshot.Commit(); // for further tests that rely on the same snapshot state.
            return (engine.ResultStack.Count > 0 ? engine.ResultStack.Pop() : StackItem.Null, engine.GasConsumed);
        }

        private static List<byte[]> FindByteStrings(DataCache snapshot, Block persistingBlock, UInt160 caller, byte[] prefix, FindOptions options)
        {
            using var sb = new ScriptBuilder().EmitDynamicCall(NativeContract.TemporaryStorage.Hash, "find",
                new ContractParameter(ContractParameterType.ByteArray) { Value = prefix },
                new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(byte)options });
            using var engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot, persistingBlock, TestProtocolSettings.Default, TestGas);
            engine.LoadScript(sb.ToArray());
            var state = engine.CurrentContext!.GetState<ExecutionContextState>();
            state.NativeCallingScriptHash = caller;
            state.ScriptHash = caller;
            Assert.AreEqual(VMState.HALT, engine.Execute(), engine.FaultException?.ToString());

            Assert.IsInstanceOfType<InteropInterface>(engine.ResultStack[0]);
            var iter = engine.ResultStack[0].GetInterface<object>() as StorageIterator;
            Assert.IsNotNull(iter);

            List<byte[]> values = [];
            while (iter.Next())
                values.Add(iter.Value().GetSpan().ToArray());

            return values;
        }

        private static StorageKey MakeRecordStorageKey(byte[] key)
        {
            return new KeyBuilder(NativeContract.TemporaryStorage.Id, PrefixRecord).AddLittleEndian(NativeContract.GAS.Id).Add(key);
        }

        private static StorageKey MakeValidTillStorageKey(ulong validTill, byte[] key)
        {
            return new KeyBuilder(NativeContract.TemporaryStorage.Id, PrefixValidTill).AddBigEndian(validTill).AddLittleEndian(NativeContract.GAS.Id).Add(key);
        }

        private static void PutRawRecord(DataCache snapshot, byte[] key, byte[] value, ulong validTill)
        {
            var recordValue = new byte[8 + value.Length];
            BinaryPrimitives.WriteUInt64BigEndian(recordValue, validTill);
            value.AsSpan().CopyTo(recordValue.AsSpan(8));

            snapshot.GetAndChange(MakeRecordStorageKey(key), () => new StorageItem())!.Value = recordValue;
            snapshot.GetAndChange(MakeValidTillStorageKey(validTill, key), () => new StorageItem([]));
        }

        private static byte[] BuildStaleKey(int index)
        {
            return [(byte)0xAA, (byte)(index >> 8), (byte)index];
        }

        private static byte[] BuildFreshKey(int index)
        {
            return [(byte)0xBA, (byte)index];
        }

        private static Block CreatePersistingBlock(DataCache snapshot, ulong timestamp)
        {
            UInt256 hash = NativeContract.Ledger.CurrentHash(snapshot);
            Block currentBlock = NativeContract.Ledger.GetBlock(snapshot, hash)!;
            return new Block
            {
                Header = new Header
                {
                    PrevHash = hash,
                    MerkleRoot = UInt256.Zero,
                    Index = currentBlock.Index + 1,
                    Timestamp = timestamp,
                    NextConsensus = currentBlock.NextConsensus,
                    Witness = Witness.Empty
                },
                Transactions = []
            };
        }
    }
}
