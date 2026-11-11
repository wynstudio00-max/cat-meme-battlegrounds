using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CatBattleground
{
    /// <summary>
    /// Reads a wallet's pass cards straight from CMBGPassCard with one JSON-RPC eth_call of
    /// cardsOf(address) -> (uint256[] ids, uint8[] tiers, uint8[] characters, uint8[] states). Read-only, no keys,
    /// works in WebGL (the Robinhood RPC allows any origin), the editor and a dedicated server.
    /// </summary>
    public static class ChainCards
    {
        const string CardsOfSelector = "c7e93f2c";   // keccak256("cardsOf(address)")[0..4]
        const int StateOpened = 3;

        [Serializable] class RpcError { public int code; public string message; }
        [Serializable] class RpcResponse { public string result; public RpcError error; }

        public static bool IsAddress(string a)
        {
            if (string.IsNullOrEmpty(a) || a.Length != 42 || !a.StartsWith("0x")) return false;
            for (int i = 2; i < 42; i++) if (Uri.IsHexDigit(a[i]) == false) return false;
            return true;
        }

        /// <summary>done(cards, null) on success, done(null, error) on failure. Callback on the main thread.</summary>
        public static void Fetch(string wallet, Action<List<PassCard>, string> done)
        {
            if (!IsAddress(wallet)) { done(null, "Not a wallet address."); return; }
            if (!IsAddress(ChainConfig.CardContract)) { done(null, "Pass cards are not live yet."); return; }
            WalletBridge.Runner.StartCoroutine(FetchRoutine(wallet.ToLowerInvariant(), done));
        }

        /// <summary>Endpoint that answered last (index into Endpoints); tried first next time.</summary>
        static int working;
        static string[] endpoints;
        static string[] Endpoints
        {
            get
            {
                if (endpoints == null)
                {
                    endpoints = new string[1 + ChainConfig.ProxyRpcs.Length];
                    endpoints[0] = ChainConfig.Rpc;
                    ChainConfig.ProxyRpcs.CopyTo(endpoints, 1);
                }
                return endpoints;
            }
        }

        static IEnumerator FetchRoutine(string wallet, Action<List<PassCard>, string> done)
        {
            string data = "0x" + CardsOfSelector + wallet.Substring(2).PadLeft(64, '0');
            string result = null, error = null;
            yield return EthCall(ChainConfig.CardContract, data, (r, e) => { result = r; error = e; });
            if (error != null) { done(null, error); yield break; }
            List<PassCard> cards;
            try { cards = Decode(result); }
            catch (Exception e) { done(null, "Could not read the cards (" + e.Message + ")."); yield break; }
            done(cards, null);
        }

        // ---------------------------------------------------------------- $CMBG balance (2026-11-10, lobby coin)
        const string BalanceOfSelector = "70a08231";   // keccak256("balanceOf(address)")[0..4]

        /// <summary>done(whole CMBG, null) or done(-1, error). Read-only balanceOf on the CMBG token (18 decimals).</summary>
        public static void FetchCmbg(string wallet, Action<double, string> done)
        {
            if (!IsAddress(wallet)) { done(-1, "Not a wallet address."); return; }
            WalletBridge.Runner.StartCoroutine(CmbgRoutine(wallet.ToLowerInvariant(), done));
        }

        static IEnumerator CmbgRoutine(string wallet, Action<double, string> done)
        {
            string result = null, error = null;
            yield return EthCall(ChainConfig.CmbgToken, "0x" + BalanceOfSelector + wallet.Substring(2).PadLeft(64, '0'), (r, e) => { result = r; error = e; });
            if (error != null) { done(-1, error); yield break; }
            double tokens;
            try { tokens = TokenAmount(result, 18); }
            catch (Exception e) { done(-1, "Could not read the balance (" + e.Message + ")."); yield break; }
            done(tokens, null);
        }

        /// <summary>uint256 hex word -> whole tokens (a double is plenty for display; 1B supply).</summary>
        public static double TokenAmount(string hex, int decimals)
        {
            string h = (hex ?? "").StartsWith("0x") ? hex.Substring(2) : hex ?? "";
            if (h.Length == 0) throw new FormatException("empty result");
            var raw = System.Numerics.BigInteger.Parse("0" + h, System.Globalization.NumberStyles.HexNumber);
            var unit = System.Numerics.BigInteger.Pow(10, decimals);
            var whole = System.Numerics.BigInteger.DivRem(raw, unit, out var rest);
            return (double)whole + (double)rest / (double)unit;
        }

        /// <summary>One eth_call against the public RPC, then our read proxies; done(resultHex, null) or done(null, error).</summary>
        static IEnumerator EthCall(string to, string data, Action<string, string> done)
        {
            string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_call\",\"params\":[{\"to\":\"" + to
                          + "\",\"data\":\"" + data + "\"},\"latest\"]}";
            // the public RPC first, then our own read proxy (2026-11-07: some ISPs block *.robinhood.com and workers.dev)
            RpcResponse res = null;
            string lastError = null;
            var list = Endpoints;
            for (int k = 0; k < list.Length && res == null; k++)
            {
                int index = (working + k) % list.Length;
                using (var req = new UnityWebRequest(list[index], "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = 12;
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success) { lastError = req.error; continue; }
                    RpcResponse r = null;
                    try { r = JsonUtility.FromJson<RpcResponse>(req.downloadHandler.text); } catch (Exception) { /* block page / error page */ }
                    if (r == null || (string.IsNullOrEmpty(r.result) && (r.error == null || string.IsNullOrEmpty(r.error.message))))
                    { lastError = "bad answer"; continue; }
                    res = r;
                    working = index;
                }
            }
            if (res == null) { done(null, "Could not reach the chain (" + lastError + ")."); yield break; }
            if (res.error != null && !string.IsNullOrEmpty(res.error.message)) { done(null, "Chain error: " + res.error.message); yield break; }
            done(res.result, null);
        }

        /// <summary>ABI-decodes the cardsOf return value (four dynamic arrays of 32-byte words).</summary>
        public static List<PassCard> Decode(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 2 + 64 * 4) throw new FormatException("short result");
            string h = hex.StartsWith("0x") ? hex.Substring(2) : hex;
            ulong Word(int byteOffset) => Convert.ToUInt64(h.Substring(byteOffset * 2 + 48, 16), 16);   // low 8 bytes of a word
            ulong[] Array(int headIndex)
            {
                int off = (int)Word(headIndex * 32);
                int n = (int)Word(off);
                if (n < 0 || n > 10000) throw new FormatException("bad length");
                var a = new ulong[n];
                for (int i = 0; i < n; i++) a[i] = Word(off + 32 + i * 32);
                return a;
            }
            ulong[] ids = Array(0), tiers = Array(1), chars = Array(2), states = Array(3);
            var list = new List<PassCard>(ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                ChainConfig.Characters.TryGetValue((int)chars[i], out var cid);
                bool opened = (int)states[i] == StateOpened;
                list.Add(new PassCard
                {
                    tokenId = ids[i].ToString(),
                    tier = (CharacterTier)Mathf.Clamp((int)tiers[i], 0, 3),
                    characterId = opened ? cid ?? "" : "",
                    opened = opened,
                });
            }
            return list;
        }
    }
}
