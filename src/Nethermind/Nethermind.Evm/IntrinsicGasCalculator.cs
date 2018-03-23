﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public class IntrinsicGasCalculator
    {
        private readonly IReleaseSpec _releaseSpec;

        public IntrinsicGasCalculator(IReleaseSpec releaseSpec)
        {
            _releaseSpec = releaseSpec;
        }
        
        public long Calculate(Transaction transaction)
        {
            long result = GasCostOf.Transaction;

            if (transaction.Data != null)
            {
                for (int i = 0; i < transaction.Data.Length; i++)
                {
                    result += transaction.Data[i] == 0 ? GasCostOf.TxDataZero : GasCostOf.TxDataNonZero;
                }
            }
            else if (transaction.Init != null)
            {
                for (int i = 0; i < transaction.Init.Length; i++)
                {
                    result += transaction.Init[i] == 0 ? GasCostOf.TxDataZero : GasCostOf.TxDataNonZero;
                }
            }

            if (transaction.IsContractCreation && _releaseSpec.IsEip2Enabled)
            {
                result += GasCostOf.TxCreate;
            }

            return result;
        }
    }
}