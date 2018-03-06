﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

[assembly: InternalsVisibleTo("Nethermind.Mining.Test")]

namespace Nethermind.Mining
{
    public class Ethash : IEthash
    {
        private readonly ConcurrentDictionary<ulong, IEthashDataSet<byte[]>> _cacheCache = new ConcurrentDictionary<ulong, IEthashDataSet<byte[]>>();

        public const int WordBytes = 4; // bytes in word
        public static ulong DatasetBytesInit = (ulong)BigInteger.Pow(2, 30); // bytes in dataset at genesis
        public static ulong DatasetBytesGrowth = (ulong)BigInteger.Pow(2, 23); // dataset growth per epoch
        public static uint CacheBytesInit = (uint)BigInteger.Pow(2, 24); // bytes in cache at genesis
        public static uint CacheBytesGrowth = (uint)BigInteger.Pow(2, 17); // cache growth per epoch
        public const int CacheMultiplier = 1024; // Size of the DAG relative to the cache
        public const ulong EpochLength = 30000; // blocks per epoch
        public const uint MixBytes = 128; // width of mix
        public const uint HashBytes = 64; // hash length in bytes
        public const uint DatasetParents = 256; // number of parents of each dataset element
        public const int CacheRounds = 3; // number of rounds in cache production
        public const int Accesses = 64; // number of accesses in hashimoto loop

        public static ulong GetEpoch(BigInteger blockNumber)
        {
            return (ulong)blockNumber / EpochLength;
        }

        public static ulong GetDataSize(BigInteger blockNumber)
        {
            ulong size = DatasetBytesInit + DatasetBytesGrowth * GetEpoch(blockNumber);
            size -= MixBytes;
            while (!IsPrime(size / MixBytes))
            {
                size -= 2 * MixBytes;
            }

            return size;
        }

        public static uint GetCacheSize(BigInteger blockNumber)
        {
            uint size = CacheBytesInit + CacheBytesGrowth * (uint)GetEpoch(blockNumber);
            size -= HashBytes;
            while (!IsPrime(size / HashBytes))
            {
                size -= 2 * HashBytes;
            }

            return size;
        }

        public static bool IsPrime(ulong number)
        {
            if (number == 1)
            {
                return false;
            }

            if (number == 2 || number == 3)
            {
                return true;
            }

            if (number % 2 == 0 || number % 3 == 0)
            {
                return false;
            }

            ulong w = 2;
            ulong i = 5;
            while (i * i <= number)
            {
                if (number % i == 0)
                {
                    return false;
                }

                i += w;
                w = 6 - w;
            }

            return true;
        }

        public static Keccak GetSeedHash(BigInteger blockNumber)
        {
            byte[] seed = new byte[32];
            for (int i = 0; i < blockNumber / EpochLength; i++)
            {
                seed = Keccak.Compute(seed).Bytes; // TODO: optimize
            }

            return new Keccak(seed);
        }

        private readonly BigInteger _2To256 = BigInteger.Pow(2, 256);

        private static readonly Random Random = new Random();

        private static ulong GetRandomNonce()
        {
            byte[] buffer = new byte[8];
            Random.NextBytes(buffer);
            return BitConverter.ToUInt64(buffer, 0);
        }

        private bool IsGreaterThanTarget(byte[] result, byte[] target)
        {
            throw new NotImplementedException();
        }

        public ulong Mine(ulong fullSize, IEthashDataSet<byte[]> dataSet, BlockHeader header, BigInteger difficulty)
        {
            ulong nonce = GetRandomNonce();
            byte[] target = BigInteger.Divide(_2To256, difficulty).ToBigEndianByteArray();
            (byte[] _, byte[] result) = Hashimoto(fullSize, dataSet, header, nonce);
            while (IsGreaterThanTarget(result, target))
            {
                unchecked
                {
                    nonce += 1;
                }
            }

            return nonce;
        }

        internal const uint FnvPrime = 0x01000193;

        internal static void Fnv(byte[] b1, byte[] b2)
        {
            Debug.Assert(b1.Length == b2.Length, "FNV expecting same length arrays");
            Debug.Assert(b1.Length % 4 == 0, "FNV expecting length to be a multiple of 4");

            uint[] b1Ints = new uint[b1.Length / 4];
            uint[] b2Ints = new uint[b1.Length / 4];
            Buffer.BlockCopy(b1, 0, b1Ints, 0, b1.Length);
            Buffer.BlockCopy(b2, 0, b2Ints, 0, b2.Length);

            // TODO: check this thing (in place calc)
            for (uint i = 0; i < b1Ints.Length; i++)
            {
                b1Ints[i] = Fnv(b1Ints[i], b2Ints[i]);
            }

            Buffer.BlockCopy(b1Ints, 0, b1, 0, b1.Length);
        }

        internal static void Fnv(uint[] b1, uint[] b2)
        {
            for (uint i = 0; i < b1.Length; i++)
            {
                b1[i] = Fnv(b1[i], b2[i]);
            }
        }

        internal static uint Fnv(uint v1, uint v2)
        {
            return (v1 * FnvPrime) ^ v2;
        }

//        internal static void SetUInt(byte[] bytes, uint offset, uint value)
//        {
//            byte[] valueBytes = value.ToByteArray(Bytes.Endianness.Little);
//            Buffer.BlockCopy(valueBytes, 0, bytes, (int)offset * 4, 4);
//        }

        internal static uint GetUInt(byte[] bytes, uint offset)
        {
            return BitConverter.ToUInt32(BitConverter.IsLittleEndian ? bytes : Bytes.Reverse(bytes), (int)offset * 4);
        }

        private const int CacheCacheSizeLimit = 6;

        public bool Validate(BlockHeader header)
        {
            ulong epoch = GetEpoch(header.Number);

            ulong? epochToRemove = null;
            IEthashDataSet<byte[]> cache = _cacheCache.GetOrAdd(epoch, e =>
            {
                uint cacheSize = GetCacheSize(header.Number);
                Keccak seed = GetSeedHash(header.Number);

                // naive
                if (_cacheCache.Count > CacheCacheSizeLimit)
                {
                    int indextToRemove = Random.Next(CacheCacheSizeLimit);
                    {
                        int index = 0;
                        foreach (ulong epochInCache in _cacheCache.Keys)
                        {
                            if (index == indextToRemove)
                            {
                                epochToRemove = epochInCache;
                            }
                        }
                    }
                }

                Console.WriteLine($"Building cache for epoch {epoch}");
                return new EthashBytesCache(cacheSize, seed.Bytes);
            });

            if (epochToRemove.HasValue)
            {
                Console.WriteLine($"Removing cache for epoch {epochToRemove}");
                _cacheCache.TryRemove(epochToRemove.Value, out IEthashDataSet<byte[]> removedItem);
            }

            ulong fullSize = GetDataSize(header.Number);
            (byte[] _, byte[] result) = Hashimoto(fullSize, cache, header, header.Nonce);

            BigInteger threshold = BigInteger.Divide(BigInteger.Pow(2, 256), header.Difficulty);
            BigInteger resultAsInteger = result.ToUnsignedBigInteger();
            return resultAsInteger < threshold;
        }

        public (byte[], byte[]) Hashimoto(ulong fullSize, IEthashDataSet<byte[]> dataSet, BlockHeader header, ulong nonce)
        {
            uint hashesInFull = (uint)(fullSize / HashBytes);
            uint wordsInMix = MixBytes / WordBytes;
            uint hashesInMix = MixBytes / HashBytes;
            byte[] headerHashed = Keccak.Compute(Rlp.Encode(header, false)).Bytes; // sic! Keccak here not Keccak512  // this tests fine
            byte[] headerAndNonceHashed = Keccak512.Compute(Bytes.Concat(headerHashed, nonce.ToByteArray(Bytes.Endianness.Little))).Bytes; // this tests fine
            uint[] mixInts = new uint[MixBytes / WordBytes];

            for (int i = 0; i < hashesInMix; i++)
            {
                Buffer.BlockCopy(headerAndNonceHashed, 0, mixInts, i * headerAndNonceHashed.Length, headerAndNonceHashed.Length);
            }

            for (uint i = 0; i < Accesses; i++)
            {
                uint p = Fnv(i ^ GetUInt(headerAndNonceHashed, 0), mixInts[i % wordsInMix]) % (hashesInFull / hashesInMix) * hashesInMix; // since we take 'hashesInMix' consecutive blocks we want only starting indices of such blocks
                uint[] newData = new uint[MixBytes / WordBytes];
                for (uint j = 0; j < hashesInMix; j++)
                {
                    byte[] item = dataSet.CalcDataSetItem(p + j);
                    Buffer.BlockCopy(item, 0, newData, (int)(j * item.Length), item.Length);
                }

                Fnv(mixInts, newData);
            }
            
            byte[] cmix = new byte[MixBytes / WordBytes];
            uint[] cmixInts = new uint[MixBytes / WordBytes / 4];
            
            for (uint i = 0; i < mixInts.Length; i += 4)
            {
                cmixInts[i / 4] = Fnv(Fnv(Fnv(mixInts[i], mixInts[i+1]), mixInts[i + 2]), mixInts[i + 3]);
            }
            
            Buffer.BlockCopy(cmixInts, 0, cmix, 0, cmix.Length);

            if (header.MixHash != Keccak.Zero && !Bytes.UnsafeCompare(cmix, header.MixHash.Bytes))
            {
                // TODO: handle properly
                throw new InvalidOperationException();
            }

            return (cmix, Keccak.Compute(Bytes.Concat(headerAndNonceHashed, cmix)).Bytes); // this tests fine
        }
    }
}