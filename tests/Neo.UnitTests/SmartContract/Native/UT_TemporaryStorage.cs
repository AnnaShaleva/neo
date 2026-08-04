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

using Neo.Extensions.VM;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract;
using Neo.SmartContract.Iterators;
using Neo.SmartContract.Native;
using Neo.VM;
using Neo.VM.Types;
using System.Numerics;

namespace Neo.UnitTests.SmartContract.Native;

[TestClass]
public class UT_TemporaryStorage
{
    private const long TestGas = 1_000_000_000_000_000;
    private const ulong MaxTtl = 7ul * 24 * 60 * 60 * 1000;
    private DataCache _snapshotCache = null!;

    [TestInitialize]
    public void TestSetup()
    {
        _snapshotCache = TestBlockchain.GetTestSnapshotCache();
    }

    [TestMethod]
    public void TestPutGetRenewDelete()
    {
        var snapshot = _snapshotCache.CloneCache();
        var caller = NativeContract.TokenManagement.Hash;

        ulong now = GetCurrentTimestamp(snapshot);
        ulong validTill = now + MaxTtl - 1_000;
        ulong renewedTill = validTill + 500;
        byte[] key = [0xAA, 0x01];
        byte[] value = [0x01, 0x02, 0x03];

        Assert.IsInstanceOfType<Null>(CallAsContract(snapshot, caller, "put",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key },
            new ContractParameter(ContractParameterType.ByteArray) { Value = value },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill })!);

        var ret = CallAsContract(snapshot, caller, "get",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.IsInstanceOfType<ByteString>(ret);
        CollectionAssert.AreEqual(value, ret.GetSpan().ToArray());

        ret = CallAsContract(snapshot, caller, "get",
            new ContractParameter(ContractParameterType.Hash160) { Value = caller },
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.IsInstanceOfType<ByteString>(ret);
        CollectionAssert.AreEqual(value, ret.GetSpan().ToArray());

        ret = CallAsContract(snapshot, caller, "getExpiration",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.AreEqual(new BigInteger(validTill), ret?.GetInteger());

        ret = CallAsContract(snapshot, caller, "find",
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0xAA } },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(byte)FindOptions.ValuesOnly });
        Assert.IsInstanceOfType<InteropInterface>(ret);
        var iter = ret.GetInterface<StorageIterator>()!;
        Assert.IsTrue(iter.Next());
        CollectionAssert.AreEqual(value, iter.Value(new ReferenceCounter()).GetSpan().ToArray());
        Assert.IsFalse(iter.Next());

        Assert.IsInstanceOfType<Null>(CallAsContract(snapshot, caller, "renew",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)renewedTill })!);

        ret = CallAsContract(snapshot, caller, "getExpiration",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.AreEqual(new BigInteger(renewedTill), ret?.GetInteger());

        Assert.IsInstanceOfType<Null>(CallAsContract(snapshot, caller, "delete",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key })!);

        ret = CallAsContract(snapshot, caller, "get",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.IsInstanceOfType<Null>(ret);

        ret = CallAsContract(snapshot, caller, "getExpiration",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.AreEqual(BigInteger.Zero, ret?.GetInteger());
    }

    [TestMethod]
    public void TestGetByHashAndFindByHash()
    {
        var snapshot = _snapshotCache.CloneCache();
        var caller1 = NativeContract.TokenManagement.Hash;
        var caller2 = NativeContract.Governance.Hash;
        ulong validTill = GetCurrentTimestamp(snapshot) + MaxTtl - 1_000;

        byte[] key1 = [0x10, 0x01];
        byte[] value1 = [0x01];
        byte[] key2 = [0x10, 0x02];
        byte[] value2 = [0x02];

        CallAsContract(snapshot, caller1, "put",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key1 },
            new ContractParameter(ContractParameterType.ByteArray) { Value = value1 },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill });

        CallAsContract(snapshot, caller2, "put",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key2 },
            new ContractParameter(ContractParameterType.ByteArray) { Value = value2 },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill });

        var ret = CallAsContract(snapshot, caller1, "get",
            new ContractParameter(ContractParameterType.Hash160) { Value = caller1 },
            new ContractParameter(ContractParameterType.ByteArray) { Value = key1 });
        Assert.IsInstanceOfType<ByteString>(ret);
        CollectionAssert.AreEqual(value1, ret.GetSpan().ToArray());

        ret = CallAsContract(snapshot, caller1, "get",
            new ContractParameter(ContractParameterType.Hash160) { Value = caller2 },
            new ContractParameter(ContractParameterType.ByteArray) { Value = key2 });
        Assert.IsInstanceOfType<ByteString>(ret);
        CollectionAssert.AreEqual(value2, ret.GetSpan().ToArray());

        ret = CallAsContract(snapshot, caller1, "find",
            new ContractParameter(ContractParameterType.Hash160) { Value = caller1 },
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x10 } },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(byte)FindOptions.ValuesOnly });
        Assert.IsInstanceOfType<InteropInterface>(ret);
        var iter = ret.GetInterface<StorageIterator>()!;
        Assert.IsTrue(iter.Next());
        CollectionAssert.AreEqual(value1, iter.Value(new ReferenceCounter()).GetSpan().ToArray());
        Assert.IsFalse(iter.Next());
    }

    [TestMethod]
    public void TestValidationAndExpiration()
    {
        var snapshot = _snapshotCache.CloneCache();
        var caller = NativeContract.TokenManagement.Hash;
        ulong now = GetCurrentTimestamp(snapshot);
        ulong minValidTill = now + 2ul * TestProtocolSettings.Default.MillisecondsPerBlock;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CallAsContract(snapshot, caller, "put",
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x01 } },
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x02 } },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(minValidTill - 1) }));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CallAsContract(snapshot, caller, "put",
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x01 } },
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x02 } },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)(now + MaxTtl + 1) }));

        ulong validTill = minValidTill;
        byte[] key = [0x20];
        CallAsContract(snapshot, caller, "put",
            new ContractParameter(ContractParameterType.ByteArray) { Value = key },
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x01 } },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill });

        var futureBlock = CreatePersistingBlock(snapshot, validTill + 1);
        var ret = CallAsContract(snapshot, caller, "get", futureBlock,
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.IsInstanceOfType<Null>(ret);

        ret = CallAsContract(snapshot, caller, "getExpiration", futureBlock,
            new ContractParameter(ContractParameterType.ByteArray) { Value = key });
        Assert.AreEqual(BigInteger.Zero, ret?.GetInteger());

        Assert.ThrowsExactly<InvalidOperationException>(() => CallAsContract(snapshot, caller, "renew",
            new ContractParameter(ContractParameterType.ByteArray) { Value = new byte[] { 0x30 } },
            new ContractParameter(ContractParameterType.Integer) { Value = (BigInteger)validTill }));
    }

    private static StackItem? CallAsContract(DataCache snapshot, UInt160 caller, string method, params ContractParameter[] args)
    {
        return CallAsContract(snapshot, caller, method, null, args);
    }

    private static StackItem? CallAsContract(DataCache snapshot, UInt160 caller, string method, Block? persistingBlock, params ContractParameter[] args)
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

        return engine.ResultStack.Count > 0 ? engine.ResultStack.Pop() : null;
    }

    private static ulong GetCurrentTimestamp(DataCache snapshot)
    {
        UInt256 hash = NativeContract.Ledger.CurrentHash(snapshot);
        Block currentBlock = NativeContract.Ledger.GetBlock(snapshot, hash)!;
        return currentBlock.Timestamp + TestProtocolSettings.Default.MillisecondsPerBlock;
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
