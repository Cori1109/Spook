﻿using System;
using System.Collections.Generic;
using LunarLabs.Parser.JSON;
using Phantasma.Blockchain;
using Phantasma.Blockchain.Contracts;
using Phantasma.Blockchain.Contracts.Native;
using Phantasma.Cryptography;
using Phantasma.Storage;
using Phantasma.Numerics;
using Phantasma.Pay.Chains;
using Phantasma.Core;

namespace Phantasma.Spook.Oracles
{
    public class NeoScanAPI
    {
        public readonly string URL;

        private readonly Address chainAddress;
        private static readonly string chainName = "NEO";

        private readonly Nexus nexus;

        public NeoScanAPI(string url, Nexus nexus, KeyPair keys)
        {
            this.URL = url;
            this.nexus = nexus;

            var key = InteropUtils.GenerateInteropKeys(keys, "NEO");
            this.chainAddress = key.Address;
        }

        public byte[] ReadOracle(string[] input)
        {
            if (input == null || input.Length != 2)
            {
                throw new OracleException("missing oracle input");
            }

            var cmd = input[0].ToLower();
            switch (cmd)
            {
                case "tx":
                    return ReadTransaction(input[1]);

                case "block":
                    return ReadBlock(input[1]);

                default:
                    throw new OracleException("unknown neo oracle");
            }
        }

        private static byte[] PackEvent(object content)
        {
            var bytes = content == null ? new byte[0] : Serialization.Serialize(content);
            return bytes;
        }

        public string GetRequestURL(string request)
        {
            Throw.If(request.StartsWith("/"), "request malformed");
            return $"https://api.{URL}/api/main_net/v1/{request}";
        }

        public byte[] ReadTransaction(string hashText)
        {
            if (hashText.StartsWith("0x"))
            {
                hashText = hashText.Substring(2);
            }

            var url = GetRequestURL($"get_transaction/{hashText}");

            string json;

            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    json = wc.DownloadString(url);
                }

                var tx = new InteropTransaction();
                tx.Platform = chainName;
                tx.Hash = Hash.Parse(hashText);

                var root = JSONReader.ReadFromString(json);

                var vins = root.GetNode("vin");
                Throw.IfNull(vins, nameof(vins));

                string inputSource = null;

                foreach (var input in vins.Children)
                {
                    var addrText = input.GetString("address_hash");
                    if (inputSource == null)
                    {
                        inputSource = addrText;
                        break;
                    }
                    else
                    if (inputSource != addrText)
                    {
                        throw new OracleException("transaction with multiple input sources, unsupported for now");
                    }
                }

                var eventList = new List<Event>();
                FillEventList(hashText, inputSource, eventList);

                if (eventList.Count <= 0)
                {
                    throw new OracleException("transaction with invalid inputs, something failed");
                }

                tx.Events = eventList.ToArray();
                return Serialization.Serialize(tx);
            }
            catch (Exception e)
            {
                throw new OracleException(e.Message);
            }
        }

        private void FillEventList(string hashText, string inputAddress, List<Event> eventList)
        {
            int page = 1;
            int maxPages = 9999;

            string json;
            while (page <= maxPages)
            {
                var url = GetRequestURL($"get_address_abstracts/{inputAddress}/{page}");

                using (var wc = new System.Net.WebClient())
                {
                    json = wc.DownloadString(url);
                }

                var root = JSONReader.ReadFromString(json);
                var entries = root.GetNode("entries");

                for (int i = 0; i < entries.ChildCount; i++)
                {
                    var entry = entries.GetNodeByIndex(i);
                    var txId = entry.GetString("txid");
                    if (hashText.Equals(txId, StringComparison.OrdinalIgnoreCase))
                    {
                        var inputAsset = entry.GetString("asset");
                        var symbol = FindSymbolFromAsset(inputAsset);

                        if (symbol == null)
                        {
                            throw new OracleException("transaction contains unknown asset: " + inputAsset);
                        }

                        var inputAmount = entry.GetDecimal("amount");

                        var sourceAddress = entry.GetString("address_from");
                        var destAddress = entry.GetString("address_to");

                        var info = nexus.GetTokenInfo(symbol);
                        var amount = UnitConversion.ToBigInteger(inputAmount, info.Decimals);

                        var sendEvt = new Event(EventKind.TokenSend, NeoWallet.EncodeAddress(sourceAddress), PackEvent(new TokenEventData() { chainAddress = chainAddress, value = amount, symbol = symbol }));
                        eventList.Add(sendEvt);

                        var receiveEvt = new Event(EventKind.TokenReceive, NeoWallet.EncodeAddress(destAddress), PackEvent(new TokenEventData() { chainAddress = chainAddress, value = amount, symbol = symbol }));
                        eventList.Add(receiveEvt);

                        return;
                    }
                }

                page++;
            }
        }

        private string FindSymbolFromAsset(string assetID)
        {
            switch (assetID)
            {
                case "ed07cffad18f1308db51920d99a2af60ac66a7b3": return "SOUL";
                case "c56f33fc6ecfcd0c225c4ab356fee59390af8560be0e930faebe74a6daff7c9b": return "NEO";
                case "602c79718b16e442de58778e148d0b1084e3b2dffd5de6b7b16cee7969282de7": return "GAS";
                default: return null;
            }
        }

        public byte[] ReadBlock(string blockText)
        {
            if (blockText.StartsWith("0x"))
            {
                blockText = blockText.Substring(2);
            }

            var url = GetRequestURL($"get_block/{blockText}");

            string json;

            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    json = wc.DownloadString(url);
                }

                var block = new InteropBlock();
                block.Platform = chainName;
                block.Hash = Hash.Parse(blockText);

                var root = JSONReader.ReadFromString(json);

                var transactions = root.GetNode("transactions");
                var hashes = new List<Hash>();

                foreach (var entry in transactions.Children)
                {
                    var hash = Hash.Parse(entry.Value);
                    hashes.Add(hash);
                }

                block.Transactions = hashes.ToArray();
                return Serialization.Serialize(block);
            }
            catch (Exception e)
            {
                throw new OracleException(e.Message);
            }
        }
    }
}
