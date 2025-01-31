﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class ConsensusModuleTests
    {
        [Test]
        public void consensus_assembleBlock_should_return_expected_results()
        {
            IConsensusRpcModule consensusRpcModule = CreateConsensusModule();
            Assert.Throws<NotImplementedException> (() => consensusRpcModule.consensus_assembleBlock(new AssembleBlockRequest()));
        }

        private IConsensusRpcModule CreateConsensusModule()
        {
            return new ConsensusRpcModule();
        }
    }
}
