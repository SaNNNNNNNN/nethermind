//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Core.Test.Blockchain
{
    public class TestBlockchain : IDisposable
    {
        public IStateReader StateReader { get; private set; }
        public IEthereumEcdsa EthereumEcdsa { get; private set; }
        public TransactionProcessor TxProcessor { get; set; }
        public IStorageProvider Storage { get; set; }
        public IReceiptStorage ReceiptStorage { get; set; }
        public ITxPool TxPool { get; set; }
        public IDb CodeDb => DbProvider.CodeDb;
        public IBlockProcessor BlockProcessor { get; set; }
        public IBlockchainProcessor BlockchainProcessor { get; set; }

        public IBlockPreprocessorStep BlockPreprocessorStep { get; set; }
        
        public IBlockProcessingQueue BlockProcessingQueue { get; set; }
        public IBlockTree BlockTree { get; set; }
        public IBlockFinder BlockFinder { get; set; }
        public IJsonSerializer JsonSerializer { get; set; }
        public IStateProvider State { get; set; }
        public IDb StateDb => DbProvider.StateDb;
        public TrieStore TrieStore { get; set; }
        public TestBlockProducer BlockProducer { get; private set; }
        public IDbProvider DbProvider { get; set; }
        public ISpecProvider SpecProvider { get; set; }

        protected TestBlockchain()
        {
        }
        
        public SealEngineType SealEngineType { get; set; }

        public static Address AccountA = TestItem.AddressA;
        public static Address AccountB = TestItem.AddressB;
        public static Address AccountC = TestItem.AddressC;
        public SemaphoreSlim _resetEvent;
        private ManualResetEvent _suggestedBlockResetEvent;
        private AutoResetEvent _oneAtATime = new AutoResetEvent(true);

        public ManualTimestamper Timestamper { get; private set; }

        public static TransactionBuilder<Transaction> BuildSimpleTransaction => Builders.Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).To(AccountB);

        protected virtual async Task<TestBlockchain> Build(ISpecProvider specProvider = null, UInt256? initialValues = null)
        {
            Timestamper = new ManualTimestamper(new DateTime(2020, 2, 15, 12, 50, 30, DateTimeKind.Utc));
            JsonSerializer = new EthereumJsonSerializer();
            SpecProvider = specProvider ?? MainnetSpecProvider.Instance;
            EthereumEcdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            ITxStorage txStorage = new InMemoryTxStorage();
            DbProvider = await TestMemDbProvider.InitAsync();
            TrieStore = new TrieStore(StateDb.Innermost, LimboLogs.Instance);
            State = new StateProvider(TrieStore, DbProvider.CodeDb, LimboLogs.Instance);
            State.CreateAccount(TestItem.AddressA, (initialValues ?? 1000.Ether()));
            State.CreateAccount(TestItem.AddressB, (initialValues ?? 1000.Ether()));
            State.CreateAccount(TestItem.AddressC, (initialValues ?? 1000.Ether()));
            byte[] code = Bytes.FromHexString("0xabcd");
            Keccak codeHash = Keccak.Compute(code);
            State.UpdateCode(code);
            State.UpdateCodeHash(TestItem.AddressA, codeHash, SpecProvider.GenesisSpec);

            Storage = new StorageProvider(TrieStore, State, LimboLogs.Instance);
            Storage.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
            Storage.Commit();

            State.Commit(SpecProvider.GenesisSpec);
            State.CommitTree(0);
            
            TxPool = CreateTxPool(txStorage);
            
            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            BlockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), SpecProvider, NullBloomStorage.Instance, LimboLogs.Instance);
            new OnChainTxWatcher(BlockTree, TxPool, SpecProvider, LimboLogs.Instance);

            ReceiptStorage = new InMemoryReceiptStorage();
            VirtualMachine virtualMachine = new VirtualMachine(State, Storage, new BlockhashProvider(BlockTree, LimboLogs.Instance), SpecProvider, LimboLogs.Instance);
            TxProcessor = new TransactionProcessor(SpecProvider, State, Storage, virtualMachine, LimboLogs.Instance);
            BlockProcessor = CreateBlockProcessor();
            BlockPreprocessorStep = new RecoverSignatures(EthereumEcdsa, TxPool, SpecProvider, LimboLogs.Instance);
            BlockchainProcessor chainProcessor = new BlockchainProcessor(BlockTree, BlockProcessor, BlockPreprocessorStep, LimboLogs.Instance, Nethermind.Blockchain.Processing.BlockchainProcessor.Options.Default);
            BlockchainProcessor = chainProcessor;
            BlockProcessingQueue = chainProcessor;
            chainProcessor.Start();

            var readOnlyTrieStore = TrieStore.AsReadOnly();
            
            StateReader = new StateReader(readOnlyTrieStore, CodeDb, LimboLogs.Instance);
            TxPoolTxSource txPoolTxSource = CreateTxPoolTxSource();
            ISealer sealer = new NethDevSealEngine(TestItem.AddressD);
            IStateProvider producerStateProvider = new StateProvider(readOnlyTrieStore, CodeDb, LimboLogs.Instance);
            BlockProducer = new TestBlockProducer(txPoolTxSource, chainProcessor, producerStateProvider, sealer, BlockTree, chainProcessor, Timestamper, LimboLogs.Instance);
            BlockProducer.Start();

            _resetEvent = new SemaphoreSlim(0);
            _suggestedBlockResetEvent = new ManualResetEvent(true);
            BlockTree.NewHeadBlock += (s, e) =>
            {
                _resetEvent.Release(1);
            };
            BlockProducer.LastProducedBlockChanged += (s, e) =>
            {
                _suggestedBlockResetEvent.Set();
            };

            var genesis = GetGenesisBlock();
            BlockTree.SuggestBlock(genesis);
            await _resetEvent.WaitAsync();
            //if (!await _resetEvent.WaitAsync(1000))
            // {
            //     throw new InvalidOperationException("Failed to process genesis in 1s.");
            // }

            await AddBlocksOnStart();
            return this;
        }

        protected virtual TxPool.TxPool CreateTxPool(ITxStorage txStorage) =>
            new TxPool.TxPool(
                txStorage,
                EthereumEcdsa,
                new FixedBlockChainHeadSpecProvider(SpecProvider),
                new TxPoolConfig(),
                State,
                new TxValidator(SpecProvider.ChainId),
                LimboLogs.Instance);

        protected virtual TxPoolTxSource CreateTxPoolTxSource()
        {
            return new TxPoolTxSource(TxPool, StateReader, LimboLogs.Instance);
        }

        public BlockBuilder GenesisBlockBuilder { get; set; }

        protected virtual Block GetGenesisBlock()
        {
            BlockBuilder genesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis;
            if (GenesisBlockBuilder != null)
            {
                genesisBlockBuilder = GenesisBlockBuilder;
            }
            
            genesisBlockBuilder.WithStateRoot(State.StateRoot);
            if (SealEngineType == SealEngineType.AuRa)
            {
                genesisBlockBuilder.WithAura(0, new byte[65]);
            }

            return genesisBlockBuilder.TestObject;
        }

        protected virtual async Task AddBlocksOnStart()
        {
            await AddBlock();
            await AddBlock(BuildSimpleTransaction.WithNonce(0).TestObject);
            await AddBlock(BuildSimpleTransaction.WithNonce(1).TestObject, BuildSimpleTransaction.WithNonce(2).TestObject);
        }

        protected virtual BlockProcessor CreateBlockProcessor() =>
            new BlockProcessor(
                SpecProvider,
                Always.Valid,
                new RewardCalculator(SpecProvider),
                TxProcessor,
                State,
                Storage,
                TxPool,
                ReceiptStorage,
                NullWitnessCollector.Instance, 
                LimboLogs.Instance);

        public async Task WaitForNewHead()
        {
            await _resetEvent.WaitAsync(CancellationToken.None);
            _suggestedBlockResetEvent.Reset();
        }

        public async Task AddBlock(params Transaction[] transactions)
        {
            await AddBlockInternal(transactions);

            await _resetEvent.WaitAsync(CancellationToken.None);
            _suggestedBlockResetEvent.Reset();
            _oneAtATime.Set();
        }

        public async Task AddBlock(bool shouldWaitForHead = true, params Transaction[] transactions)
        {
            await AddBlockInternal(transactions);

            if (shouldWaitForHead)
            {
                await _resetEvent.WaitAsync(CancellationToken.None);
            }
            else
            {
                await _suggestedBlockResetEvent.WaitOneAsync(CancellationToken.None);
            }

            _oneAtATime.Set();
        }

        private async Task AddBlockInternal(params Transaction[] transactions)
        {
            await _oneAtATime.WaitOneAsync(CancellationToken.None);
            foreach (Transaction transaction in transactions)
            {
                TxPool.AddTransaction(transaction, TxHandlingOptions.None);
            }

            Timestamper.Add(TimeSpan.FromSeconds(1));
            BlockProducer.BuildNewBlock();
        }

        public void AddTransaction(Transaction testObject)
        {
            TxPool.AddTransaction(testObject, TxHandlingOptions.None);
        }

        public virtual void Dispose()
        {
            BlockProducer?.StopAsync();
            CodeDb?.Dispose();
            StateDb?.Dispose();
        }

        /// <summary>
        /// Creates a simple transfer transaction with value defined by <paramref name="ether"/>
        /// from a rich account to <paramref name="address"/>
        /// </summary>
        /// <param name="address">Address to add funds to</param>
        /// <param name="ether">Value of ether to add to the account</param>
        /// <returns></returns>
        public async Task AddFunds(Address address, UInt256 ether)
        {
            var nonce = StateReader.GetNonce(BlockTree.Head.StateRoot, TestItem.AddressA);
            Transaction tx = Builders.Build.A.Transaction
                .SignedAndResolved(TestItem.PrivateKeyA)
                .To(address)
                .WithNonce(nonce)
                .WithValue(ether)
                .TestObject;

            await AddBlock(tx);
        }
    }
}
