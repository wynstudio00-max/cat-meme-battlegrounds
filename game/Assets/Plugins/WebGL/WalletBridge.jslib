// Browser side of WalletSession / WalletBridge (Assets/Scripts/Web3/WalletSession.cs).
// The wallet itself is handled by window.cmbgWallet in the WebGL template (Assets/WebGLTemplates/CMBG/index.html),
// which sends the address back with unityInstance.SendMessage("WalletBridge", "OnWallet", address).
mergeInto(LibraryManager.library, {
  CMBG_WalletReady: function () {
    if (window.cmbgWallet && window.cmbgWallet.ready) window.cmbgWallet.ready();
  },
  CMBG_WalletConnect: function () {
    if (window.cmbgWallet && window.cmbgWallet.connect) window.cmbgWallet.connect();
    else if (window.unityInstance) window.unityInstance.SendMessage("WalletBridge", "OnWalletError", "No wallet support on this page.");
  },
  CMBG_WalletDisconnect: function () {
    if (window.cmbgWallet && window.cmbgWallet.disconnect) window.cmbgWallet.disconnect();
  },
  CMBG_OpenUrl: function (url) {
    window.open(UTF8ToString(url), "_blank", "noopener");
  }
});
