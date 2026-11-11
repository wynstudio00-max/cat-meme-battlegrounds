// Pass-card page: connect wallet, claim from the snapshot, list cards, open a card and watch the reveal.
// Contract: Contracts/contracts/CMBGPassCard.sol. Settings: config.js `nft`. Reading uses the public RPC (works without a
// wallet, e.g. cards.html?wallet=0x...); writing uses the connected wallet (EIP-1193, window.ethereum).
(function () {
  "use strict";
  // config.js nft: shared settings + the active network's block (nft.network picks one of nft.networks)
  var N = (window.CMBG_CONFIG || {}).nft;
  var cfg = N && N.networks ? Object.assign({}, N, N.networks[N.network] || {}) : N;
  if (cfg && N.networks) cfg.testnet = N.network !== "mainnet";
  if (cfg && !cfg.card) cfg = null;   // network without a deployed card contract yet
  var $ = function (id) { return document.getElementById(id); };
  var E = window.ethers;

  var TIERS = { 1: "COMMON", 2: "RARE", 3: "LEGENDARY" };
  var PACK_ART = { 1: "assets/card_common.webp?v=3", 2: "assets/card_rare.webp?v=3", 3: "assets/card_legendary.webp?v=3" };
  var STATE = { NONE: 0, SEALED: 1, OPENING: 2, OPENED: 3 };
  // pack-opening films (Tools/BlenderAnim/render_opening_video.py -> nft/open): one video per cat, one sealed-pack picture per tier
  var FILM = {
    dir: "nft/open/",
    cat: { 1: "gold", 2: "soldier", 3: "ninja", 4: "baby", 5: "nobi", 6: "black", 7: "white", 8: "knight" },
    pool: { 1: [1, 2, 4], 2: [3, 5, 6], 3: [7, 8] },   // characterIds per tier (CMBGPassCard pools, 2026-11-07)
    wait: { 1: "wait_common.webp", 2: "wait_rare.webp", 3: "wait_legendary.webp" }
  };
  var FILM_NAMES = { 1: "GOLDEN CAT", 2: "V.", 3: "NINJA CAT", 4: "BABY CASH CAT", 5: "NOBI", 6: "HMM CAT", 7: "CASH CAT", 8: "BUNDLE CAT" };
  var ABI = [
    "function claim(uint8 commons, uint8 rares, uint8 legendaries, bytes32[] proof)",
    "function open(uint256 tokenId)",
    "function reRequest(uint256 tokenId)",
    "function claimed(address) view returns (bool)",
    "function paused() view returns (bool)",
    "function claimDeadline() view returns (uint64)",
    "function randomSource() view returns (address)",
    "function requestedFrom(uint256) view returns (address)",
    "function cardOf(uint256) view returns (uint8 tier, uint8 character, uint8 state)",
    "function cardsOf(address) view returns (uint256[] ids, uint8[] tiers, uint8[] characters, uint8[] states)",
    "function tokenURI(uint256) view returns (string)",
    "error AlreadyClaimed()", "error BadAmount()", "error BadProof()", "error ClaimClosed()", "error NotCardOwner()",
    "error WrongState()", "error NoRandomSource()", "error NotRandomSource()", "error EnforcedPause()",
    "error ERC721NonexistentToken(uint256)"
  ];
  var ERR_TEXT = {
    AlreadyClaimed: "This wallet already claimed its cards.",
    BadProof: "This wallet is not in the snapshot (or the claim list changed).",
    BadAmount: "Wrong card amounts for this wallet.",
    ClaimClosed: "The claim window is closed.",
    EnforcedPause: "Claiming and opening are paused right now.",
    NotCardOwner: "Only the wallet holding this card can do that.",
    WrongState: "That card is not in the right state for this (refresh the page).",
    NoRandomSource: "Opening is not switched on yet."
  };

  // ---------- small helpers ----------
  var toastTimer;
  function toast(msg, ms) {
    var el = $("toast");
    el.textContent = msg;
    el.classList.add("is-on");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.classList.remove("is-on"); }, ms || 3200);
  }
  function short(a) { return a ? a.slice(0, 6) + "…" + a.slice(-4) : ""; }
  function ipfs(u) { return u && u.indexOf("ipfs://") === 0 ? cfg.ipfsGateway + u.slice(7) : u; }
  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }
  function errText(e) {
    if (e && (e.code === "ACTION_REJECTED" || e.code === 4001)) return "You cancelled it in your wallet.";
    var name = e && e.revert && e.revert.name;
    if (!name && e && e.data && read) {
      try { name = read.interface.parseError(e.data).name; } catch (_) { /* not ours */ }
    }
    if (name && ERR_TEXT[name]) return ERR_TEXT[name];
    var m = (e && (e.shortMessage || e.reason || e.message)) || String(e);
    if (/insufficient funds/i.test(m)) return "Not enough ETH on " + cfg.chainName + " for the gas fee.";
    return m.length > 140 ? m.slice(0, 140) + "…" : m;
  }
  function msg(text, kind) {
    var m = $("topMsg");
    m.hidden = !text;
    m.textContent = text || "";
    m.className = "pc-msg" + (kind ? " pc-msg--" + kind : "");
  }

  if (!cfg || !E) {
    document.querySelector("main").innerHTML = "<section class='section dark'><div class='wrap'><p class='body-lg'>Pass cards are not live yet. Check back soon.</p></div></section>";
    return;
  }

  // 2026-11-09: some phones / ISPs cannot reach the public RPC ("Failed to fetch": blocked domain or 429 rate limit), so reads
  // fail over through cfg.rpcs (public RPC, Pinax, our own read proxy). One RPC still works as before.
  var rpcUrls = (cfg.rpcs && cfg.rpcs.length ? cfg.rpcs : [cfg.rpc]);
  // (ethers' FallbackProvider does not switch on a network error, hence this small provider: it sends each request to the RPC
  // that answered last and moves on to the next one when a request cannot be delivered.)
  class MultiRpc extends E.JsonRpcProvider {
    constructor(urls, chainId) {
      super(urls[0], chainId, { staticNetwork: true, batchMaxCount: 1 });
      this.urls = urls;
      this.cur = 0;
    }
    async _send(payload) {
      var body = JSON.stringify(payload), last;
      for (var n = 0; n < this.urls.length; n++) {
        var i = (this.cur + n) % this.urls.length;
        try {
          var ctl = new AbortController(), t = setTimeout(function () { ctl.abort(); }, 8000);
          var r = await fetch(this.urls[i], { method: "POST", headers: { "Content-Type": "application/json" }, body: body, signal: ctl.signal });
          clearTimeout(t);
          if (!r.ok) throw new Error("HTTP " + r.status);
          var j = await r.json();
          this.cur = i;
          return Array.isArray(j) ? j : [j];
        } catch (e) { last = e; }
      }
      throw last;
    }
  }
  var rpc = new MultiRpc(rpcUrls, cfg.chainId);
  var read = new E.Contract(cfg.card, ABI, rpc);
  var walletProvider = null;   // BrowserProvider when connected
  var account = null;          // connected wallet (lower case)
  var shown = null;            // address on screen (connected or looked up)
  var claims = null;           // snapshot: checksummed address -> { commons, rares, legendaries, proof }
  var metaCache = {};          // tokenURI -> metadata JSON
  var busy = {};               // tokenId -> true while a tx / reveal runs
  var pollTimer = null;

  // ---------- header bits ----------
  if (cfg.testnet) {
    $("envChip").hidden = false;
    $("envChip").textContent = "TESTNET";
  }
  $("contractLine").innerHTML = "";
  $("contractLine").append("Card contract ");
  var ca = el("a", "pc-link", short(cfg.card));
  ca.href = cfg.explorer + "/address/" + cfg.card;
  ca.target = "_blank";
  ca.rel = "noopener";
  $("contractLine").append(ca, " on " + cfg.chainName + ".");

  function loadClaims() {
    if (claims) return Promise.resolve(claims);
    return fetch(cfg.claims, { cache: "no-cache" })
      .then(function (r) { if (!r.ok) throw new Error("claims " + r.status); return r.json(); })
      .then(function (j) {
        claims = {};
        Object.keys(j).forEach(function (k) { claims[k.toLowerCase()] = j[k]; });
        return claims;
      });
  }

  function metadata(tokenId) {
    return read.tokenURI(tokenId).then(function (uri) {
      if (metaCache[uri]) return metaCache[uri];
      return fetch(ipfs(uri)).then(function (r) { return r.json(); }).then(function (m) {
        metaCache[uri] = m;
        return m;
      });
    });
  }

  // ---------- wallet ----------
  function hexChain() { return "0x" + Number(cfg.chainId).toString(16); }

  function ensureChain() {
    var eth = window.ethereum;
    return eth.request({ method: "eth_chainId" }).then(function (id) {
      if (parseInt(id, 16) === Number(cfg.chainId)) return;
      return eth.request({ method: "wallet_switchEthereumChain", params: [{ chainId: hexChain() }] }).catch(function (e) {
        if (e && (e.code === 4902 || /unrecognized|not added/i.test(e.message || ""))) {
          return eth.request({
            method: "wallet_addEthereumChain",
            params: [{
              chainId: hexChain(), chainName: cfg.chainName, rpcUrls: [cfg.rpc], blockExplorerUrls: [cfg.explorer],
              nativeCurrency: { name: "Ether", symbol: "ETH", decimals: 18 }
            }]
          });
        }
        throw e;
      });
    });
  }

  function setAccount(addr) {
    account = addr ? addr.toLowerCase() : null;
    $("connectBtn").textContent = account ? short(addr) : "CONNECT WALLET";
    $("connectBtn").classList.toggle("is-connected", !!account);
    if (account) show(addr);
  }

  function connect() {
    if (!window.ethereum) {
      msg("No wallet found in this browser. Install a browser wallet (e.g. MetaMask) or open this page in your wallet app's browser.", "warn");
      return;
    }
    if (account) { show(account); return; }
    $("connectBtn").disabled = true;
    window.ethereum.request({ method: "eth_requestAccounts" })
      .then(function (accs) { return ensureChain().then(function () { return accs; }); })
      .then(function (accs) {
        walletProvider = new E.BrowserProvider(window.ethereum);
        setAccount(accs[0]);
      })
      .catch(function (e) { toast(errText(e)); })
      .then(function () { $("connectBtn").disabled = false; });
  }

  function signerContract() {
    return ensureChain().then(function () {
      walletProvider = new E.BrowserProvider(window.ethereum);
      return walletProvider.getSigner();
    }).then(function (s) { return new E.Contract(cfg.card, ABI, s); });
  }

  if (window.ethereum) {
    window.ethereum.request({ method: "eth_accounts" }).then(function (accs) {
      if (accs && accs[0] && !new URLSearchParams(location.search).get("wallet")) {
        walletProvider = new E.BrowserProvider(window.ethereum);
        setAccount(accs[0]);
      }
    }).catch(function () {});
    if (window.ethereum.on) {
      window.ethereum.on("accountsChanged", function (accs) { setAccount(accs && accs[0]); if (!accs || !accs[0]) msg(""); });
      window.ethereum.on("chainChanged", function () { if (shown) show(shown); });
    }
  }

  // ---------- page ----------
  function isMine() { return account && shown && account === shown.toLowerCase(); }

  function show(addr) {
    shown = addr;
    $("walletBox").hidden = false;
    $("walletAddr").textContent = addr;
    $("walletMode").textContent = isMine() ? "CONNECTED" : "VIEW ONLY";
    msg("");
    return Promise.all([
      read.claimed(addr), read.cardsOf(addr), read.paused(), read.claimDeadline(),
      loadClaims().catch(function () { return null; })
    ]).then(function (r) {
      renderClaim(addr, r[0], r[2], Number(r[3]));
      renderCards(addr, r[1]);
    }).catch(function (e) {
      msg("Could not read the chain: " + errText(e), "warn");
    });
  }

  function renderClaim(addr, claimed, paused, deadline) {
    var entry = claims && claims[addr.toLowerCase()];
    var packs = $("claimPacks");
    packs.innerHTML = "";
    $("claimSection").hidden = false;
    var btn = $("claimBtn");
    btn.hidden = true;
    if (!entry) {
      $("claimTitle").textContent = "NOT IN THE SNAPSHOT.";
      $("claimText").textContent = claims ? "This wallet has no cards to claim." : "The claim list could not be loaded.";
      return;
    }
    var list = [];
    for (var i = 0; i < entry.legendaries; i++) list.push(3);
    for (i = 0; i < entry.rares; i++) list.push(2);
    for (i = 0; i < entry.commons; i++) list.push(1);
    list.forEach(function (t) {
      var f = el("figure", "pc-pack pc-pack--" + t);
      var img = el("img");
      img.src = PACK_ART[t];
      img.alt = TIERS[t] + " pack";
      f.append(img, el("figcaption", null, TIERS[t]));
      packs.append(f);
    });
    var names = list.map(function (t) { return TIERS[t]; }).join(" + ");
    if (claimed) {
      $("claimTitle").textContent = "CLAIMED ✓";
      $("claimText").textContent = names + " — already in this wallet (see below).";
      packs.classList.add("is-claimed");
      return;
    }
    packs.classList.remove("is-claimed");
    $("claimTitle").textContent = names;
    var closed = deadline && Date.now() / 1000 > deadline;
    if (closed) { $("claimText").textContent = "The claim window closed."; return; }
    if (paused) { $("claimText").textContent = "Claiming is paused right now. Try again later."; return; }
    if (!isMine()) { $("claimText").textContent = "Connect this wallet to claim. You only pay the network gas."; return; }
    $("claimText").textContent = "Ready to claim. You only pay the network gas (ETH on " + cfg.chainName + ").";
    btn.hidden = false;
    btn.onclick = function () { doClaim(entry); };
  }

  function doClaim(entry) {
    var btn = $("claimBtn");
    btn.disabled = true;
    btn.textContent = "CONFIRM IN WALLET…";
    signerContract()
      .then(function (c) { return c.claim(entry.commons, entry.rares, entry.legendaries, entry.proof); })
      .then(function (tx) { btn.textContent = "CLAIMING…"; return tx.wait(); })
      .then(function () { toast("Cards claimed! Open them below."); return show(shown); })
      .catch(function (e) { toast(errText(e), 4500); })
      .then(function () { btn.disabled = false; btn.textContent = "CLAIM CARDS"; });
  }

  function renderCards(addr, res) {
    var ids = res[0], tiers = res[1], chars = res[2], states = res[3];
    var grid = $("cardGrid");
    grid.innerHTML = "";
    $("cardsSection").hidden = false;
    $("cardsTitle").textContent = ids.length ? ids.length + (ids.length === 1 ? " CARD." : " CARDS.") : "NO CARDS YET.";
    var waiting = false;
    read.randomSource().then(function (src) {
      for (var i = 0; i < ids.length; i++) grid.append(cardTile(Number(ids[i]), Number(tiers[i]), Number(states[i]), src));
    });
    for (var i = 0; i < ids.length; i++) if (Number(states[i]) === STATE.OPENING) waiting = true;
    clearTimeout(pollTimer);
    if (waiting) pollTimer = setTimeout(function () { if (shown === addr) show(addr); }, cfg.pollSeconds * 1000 * 2);
  }

  function cardTile(id, tier, state, randomSource) {
    var t = el("article", "pc-card pc-card--" + tier + (state === STATE.OPENED ? " is-open" : ""));
    var art = el("div", "pc-card__art");
    var img = el("img");
    img.alt = "";
    img.src = PACK_ART[tier];
    art.append(img);
    var body = el("div", "pc-card__body");
    var row = el("div", "pc-card__row");
    row.append(el("span", "pc-tier pc-tier--" + tier, TIERS[tier]), el("span", "pc-id", "#" + id));
    var name = el("p", "display pc-card__name", state === STATE.OPENED ? "…" : "SEALED");
    var actions = el("div", "pc-card__actions");
    body.append(row, name, actions);
    t.append(art, body);

    if (state === STATE.OPENED) {
      metadata(id).then(function (m) {
        img.src = ipfs(m.image);
        img.alt = m.name;
        var ch = (m.attributes || []).filter(function (a) { return a.trait_type === "Character"; })[0];
        name.textContent = ch ? ch.value : m.name;
      }).catch(function () { name.textContent = "OPENED"; });
    } else if (state === STATE.OPENING) {
      name.textContent = "REVEALING…";
      actions.append(el("span", "pc-wait", "Waiting for the reveal. This usually takes seconds."));
      read.requestedFrom(id).then(function (from) {
        if (from.toLowerCase() !== randomSource.toLowerCase() && isMine()) {
          var again = el("button", "btn btn--outline-lime btn--xs", "ASK AGAIN");
          again.onclick = function () { reRequest(id, again); };
          actions.innerHTML = "";
          actions.append(el("span", "pc-wait", "The reveal service changed. Ask again to get your cat."), again);
        }
      });
    } else if (state === STATE.SEALED && isMine()) {
      var b = el("button", "btn btn--lime btn--xs", "OPEN");
      b.onclick = function () { openCard(id, tier); };
      actions.append(b);
    }
    var link = el("a", "pc-link pc-small", "explorer ↗");
    link.href = cfg.explorer + "/token/" + cfg.card + "/instance/" + id;
    link.target = "_blank";
    link.rel = "noopener";
    actions.append(link);
    return t;
  }

  function reRequest(id, btn) {
    btn.disabled = true;
    signerContract()
      .then(function (c) { return c.reRequest(id); })
      .then(function (tx) { btn.textContent = "SENDING…"; return tx.wait(); })
      .then(function () { poke(); toast("Asked again. Your cat is on the way."); return show(shown); })
      .catch(function (e) { toast(errText(e), 4500); btn.disabled = false; });
  }

  // ---------- opening + reveal ----------
  var dlg = $("revealDlg");
  var filmMode = false;
  function rvStage(stage, opts) {
    var rv = $("rv");
    rv.className = "rv rv--" + stage + (opts && opts.tier ? " rv--t" + opts.tier : "") + (filmMode ? " rv--film" : "");
  }

  // warm the browser cache with every film of this tier while the reveal is pending (2-3 files)
  function prefetchFilms(tier) {
    (FILM.pool[tier] || []).forEach(function (c) { fetch(FILM.dir + FILM.cat[c] + ".mp4", { cache: "force-cache" }).catch(function () {}); });
  }

  // plays the opening film of character c over the waiting picture; resolves when it ends (or is skipped), rejects if it cannot play
  function playFilm(c) {
    var v = $("rvVideo"), film = $("rvFilm"), skip = $("rvSkip");
    return new Promise(function (resolve, reject) {
      var done = false;
      function finish(ok) {
        if (done) return; done = true;
        clearTimeout(timer); skip.hidden = true;
        v.onended = v.onerror = v.oncanplay = v.onplaying = null;
        if (ok) resolve(); else reject(new Error("film"));
      }
      var timer = setTimeout(function () { finish(false); }, 15000);   // too slow to load: fall back to the picture reveal
      v.onerror = function () { finish(false); };
      v.oncanplay = function () {
        v.oncanplay = null;
        var p = v.play();
        if (p && p.catch) p.catch(function () { finish(false); });
        film.classList.add("is-playing");
        clearTimeout(timer);
        timer = setTimeout(function () { finish(true); }, (v.duration || 12) * 1000 + 2000);
        skip.hidden = false;
      };
      v.onended = function () { finish(true); };
      // once frames really move, the waiting picture underneath is hidden (no double pack during a slow start)
      v.onplaying = function () { setTimeout(function () { $("rvWait").style.visibility = "hidden"; }, 250); };
      skip.onclick = function () { try { v.currentTime = Math.max(0, (v.duration || 11) - 0.05); } catch (e) {} };
      v.src = FILM.dir + FILM.cat[c] + ".mp4";
      v.load();
    });
  }

  function openCard(id, tier) {
    if (busy[id]) return;
    busy[id] = true;
    $("rvPack").src = PACK_ART[tier];
    $("rvCard").removeAttribute("src");
    $("rvKicker").textContent = TIERS[tier] + " CARD #" + id;
    $("rvName").textContent = "";
    $("rvStatus").textContent = "Confirm in your wallet…";
    $("rvClose").hidden = true;
    filmMode = cfg.openFilm !== false && !!FILM.wait[tier];
    $("rvFilm").hidden = !filmMode;
    $("rvFilm").classList.remove("is-playing");
    $("rvSkip").hidden = true;
    if (filmMode) { $("rvWait").src = FILM.dir + FILM.wait[tier]; $("rvWait").style.visibility = ""; $("rvVideo").removeAttribute("src"); }
    rvStage("wait", { tier: tier });
    if (dlg.showModal) dlg.showModal(); else dlg.setAttribute("open", "");

    signerContract()
      .then(function (c) { return c.open(id); })
      .then(function (tx) { $("rvStatus").textContent = "Opening the pack…"; rvStage("shake", { tier: tier }); if (filmMode) prefetchFilms(tier); return tx.wait(); })
      .then(function () { $("rvStatus").textContent = "Shuffling the cats…"; poke(); return waitOpened(id, 180); })
      .then(function (opened) {
        if (!opened) {
          $("rvStatus").textContent = "The reveal is taking longer than usual. Your card is safe: it opens as soon as the reveal arrives. You can close this.";
          $("rvClose").hidden = false;
          $("rvClose").textContent = "CLOSE";
          return;
        }
        // the film: the character's own opening video from the sealed pack to the hologram, then its name
        var film = filmMode ? read.cardOf(id).then(function (c) {
          var who = Number(c[1]);
          if (!FILM.cat[who]) throw new Error("no film");
          $("rvStatus").textContent = "";
          rvStage("filmplay", { tier: tier });
          return playFilm(who);
        }).then(function () { return true; }, function () { filmMode = false; $("rvFilm").hidden = true; return false; }) : Promise.resolve(false);
        return film.then(function (played) {
          return metadata(id).then(function (m) { return { m: m, played: played }; }, function (e) {
            if (!played) throw e;   // the film already showed the cat: a slow IPFS gateway only costs the name line
            return read.cardOf(id).then(function (c) { return { m: { name: FILM_NAMES[Number(c[1])] || "YOUR CAT", attributes: [] }, played: true }; });
          });
        }).then(function (r) {
          var m = r.m;
          var ch = (m.attributes || []).filter(function (a) { return a.trait_type === "Character"; })[0];
          if (r.played) {
            rvStage("filmdone", { tier: tier });
            $("rvName").textContent = ch ? ch.value : m.name;
            $("rvStatus").textContent = "is yours. Play it in the game with this wallet.";
            $("rvClose").hidden = false;
            $("rvClose").textContent = "NICE";
            return;
          }
          var pic = new Image();
          pic.src = ipfs(m.image);
          return (pic.decode ? pic.decode() : Promise.resolve()).catch(function () {}).then(function () {
            $("rvCard").src = pic.src;
            $("rvCard").alt = m.name;
            rvStage("burst", { tier: tier });
            return sleep(650);
          }).then(function () {
            rvStage("show", { tier: tier });
            $("rvName").textContent = ch ? ch.value : m.name;
            $("rvStatus").textContent = "is yours. Play it in the game with this wallet.";
            $("rvClose").hidden = false;
            $("rvClose").textContent = "NICE";
          });
        });
      })
      .catch(function (e) {
        $("rvStatus").textContent = errText(e);
        $("rvClose").hidden = false;
        $("rvClose").textContent = "CLOSE";
        rvStage("wait", { tier: tier });
      })
      .then(function () { busy[id] = false; });
  }

  // asks the auto-reveal Worker (Backend/reveal) to reveal right away instead of on its next minute; harmless if it fails
  function poke() {
    if (!cfg.revealApi) return;
    fetch(cfg.revealApi.replace(/\/$/, "") + "/poke", { method: "POST" }).catch(function () {});
  }

  function waitOpened(id, seconds) {
    var until = Date.now() + seconds * 1000;
    function tick() {
      return read.cardOf(id).then(function (c) {
        if (Number(c[2]) === STATE.OPENED) return true;
        if (Date.now() > until) return false;
        return sleep(cfg.pollSeconds * 1000).then(tick);
      });
    }
    return tick();
  }

  $("rvClose").onclick = function () { dlg.close ? dlg.close() : dlg.removeAttribute("open"); if (shown) show(shown); };
  dlg.addEventListener("cancel", function (e) { if ($("rvClose").hidden) e.preventDefault(); });

  // ---------- controls ----------
  $("connectBtn").onclick = connect;
  $("refreshBtn").onclick = function () { if (shown) show(shown); };
  $("viewForm").onsubmit = function (e) {
    e.preventDefault();
    var v = $("viewInput").value.trim();
    if (!E.isAddress(v)) { toast("That is not a wallet address."); return; }
    show(E.getAddress(v));
  };

  // preview without a wallet: cards.html?filmtest=<tier>:<characterId>  (e.g. 3:7 = Legendary pack -> Cash Cat)
  var ft = new URLSearchParams(location.search).get("filmtest");
  if (ft) {
    var fp = ft.split(":"), ftTier = Number(fp[0]) || 1, ftCat = Number(fp[1]) || FILM.pool[ftTier][0];
    filmMode = true;
    $("rvKicker").textContent = TIERS[ftTier] + " CARD · PREVIEW";
    $("rvName").textContent = ""; $("rvClose").hidden = true; $("rvSkip").hidden = true;
    $("rvFilm").hidden = false; $("rvFilm").classList.remove("is-playing");
    $("rvWait").src = FILM.dir + FILM.wait[ftTier]; $("rvWait").style.visibility = "";
    $("rvStatus").textContent = "Confirm in your wallet…";
    rvStage("wait", { tier: ftTier });
    if (dlg.showModal) dlg.showModal(); else dlg.setAttribute("open", "");
    sleep(1500).then(function () {
      $("rvStatus").textContent = "Shuffling the cats…"; rvStage("shake", { tier: ftTier }); prefetchFilms(ftTier);
      return sleep(3500);
    }).then(function () {
      $("rvStatus").textContent = ""; rvStage("filmplay", { tier: ftTier });
      return playFilm(ftCat);
    }).then(function () {
      rvStage("filmdone", { tier: ftTier });
      $("rvName").textContent = FILM_NAMES[ftCat];
      $("rvStatus").textContent = "is yours. Play it in the game with this wallet.";
      $("rvClose").hidden = false; $("rvClose").textContent = "NICE";
    }).catch(function () { $("rvStatus").textContent = "The film could not play (missing file?)."; $("rvClose").hidden = false; });
  }

  var q = new URLSearchParams(location.search).get("wallet");
  if (q && E.isAddress(q)) { $("viewInput").value = q; show(E.getAddress(q)); }
})();
