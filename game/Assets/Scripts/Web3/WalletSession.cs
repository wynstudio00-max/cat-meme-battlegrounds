using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CatBattleground
{
    /// <summary>
    /// The player's connected wallet and its pass cards (read from the chain by <see cref="ChainCards"/>).
    /// Browser: the page connects the wallet (loading screen "Connect Wallet", or the lobby WALLET button through
    /// WalletBridge.jslib) and sends the address to <see cref="WalletBridge"/>. Editor / development builds: the
    /// NetworkBootstrap "Dev Wallet" field or the command line -wallet 0x... (read-only, any address, testing only).
    /// </summary>
    public static class WalletSession
    {
        public static string Address { get; private set; } = "";
        public static bool Connected => Address.Length == 42;
        public static List<PassCard> Cards { get; private set; } = new List<PassCard>();
        public static bool Loading { get; private set; }
        public static string Error { get; private set; } = "";
        /// <summary>Whole $CMBG held by the wallet; -1 = not read (yet) or the read failed (2026-11-10, lobby coin).</summary>
        public static double CmbgBalance { get; private set; } = -1;
        /// <summary>Bumped on every change (address, cards, loading, error): UIs rebuild when it moves.</summary>
        public static int Version { get; private set; }
        public static event Action Changed;

        public static string Short => Connected ? Address.Substring(0, 6) + "…" + Address.Substring(38) : "";

        static void Bump() { Version++; Changed?.Invoke(); }

        public static void SetAddress(string address)
        {
            address = (address ?? "").Trim().ToLowerInvariant();
            if (address.Length > 0 && !ChainCards.IsAddress(address)) { Error = "Not a wallet address."; Bump(); return; }
            if (address == Address) { if (Connected) Refresh(); return; }
            Address = address;
            Cards = new List<PassCard>();
            CmbgBalance = -1;
            Error = "";
            Bump();
            Refresh();
        }

        public static void Refresh()
        {
            if (!Connected) return;
            string asked = Address;
            Loading = true;
            Bump();
            ChainCards.Fetch(asked, (cards, error) =>
            {
                if (asked != Address) return;   // the wallet changed meanwhile
                Loading = false;
                Error = error ?? "";
                if (cards != null) Cards = cards;
                Bump();
            });
            RefreshCmbg();
        }

        /// <summary>Reads the wallet's $CMBG balance (lobby coin). A failed read keeps the last known value.</summary>
        public static void RefreshCmbg()
        {
            if (!Connected) return;
            string asked = Address;
            ChainCards.FetchCmbg(asked, (tokens, error) =>
            {
                if (asked != Address || error != null) return;
                CmbgBalance = tokens;
                Bump();
            });
        }

        /// <summary>Lobby WALLET button: browser = ask the page's wallet; editor = the dev wallet.</summary>
        public static void Connect()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WalletBridge.JsConnect();
#else
            var dev = NetworkBootstrap.Instance != null ? NetworkBootstrap.Instance.DevWallet : "";
            if (ChainCards.IsAddress(dev)) SetAddress(dev);
            else { Error = "Editor: put a wallet address into NetworkBootstrap > Dev Wallet."; Bump(); }
#endif
        }

        public static void Disconnect()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WalletBridge.JsDisconnect();
#endif
            SetAddress("");
        }

        internal static void SetError(string error) { Error = error ?? ""; Bump(); }
    }

    /// <summary>
    /// GameObject "WalletBridge" (always present, DontDestroyOnLoad): target of the page's SendMessage calls and the
    /// coroutine runner for chain requests (also on a dedicated server, which checks cards in connection approval).
    /// </summary>
    public class WalletBridge : MonoBehaviour
    {
        static WalletBridge runner;
        public static MonoBehaviour Runner
        {
            get
            {
                if (runner == null)
                {
                    var go = new GameObject("WalletBridge");
                    DontDestroyOnLoad(go);
                    runner = go.AddComponent<WalletBridge>();
                }
                return runner;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var r = Runner;   // create it
#if UNITY_WEBGL && !UNITY_EDITOR
            CMBG_WalletReady();   // the page sends the wallet it already has (loading screen choice)
#endif
        }

        // ---- called by the page (unityInstance.SendMessage("WalletBridge", ...))
        public void OnWallet(string address) => WalletSession.SetAddress(address);
        public void OnWalletError(string message) => WalletSession.SetError(message);

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void CMBG_WalletReady();
        [DllImport("__Internal")] static extern void CMBG_WalletConnect();
        [DllImport("__Internal")] static extern void CMBG_WalletDisconnect();
        [DllImport("__Internal")] static extern void CMBG_OpenUrl(string url);
        public static void JsConnect() => CMBG_WalletConnect();
        public static void JsDisconnect() => CMBG_WalletDisconnect();
        public static void OpenUrl(string url) => CMBG_OpenUrl(url);
#else
        public static void OpenUrl(string url) => Application.OpenURL(url);
#endif
    }
}
