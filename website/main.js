(function () {
  "use strict";
  var cfg = window.CMBG_CONFIG || { links: {} };
  var links = cfg.links || {};

  // ---------- toast ----------
  var toastEl = document.getElementById("toast");
  var toastTimer;
  function toast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.add("is-on");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove("is-on"); }, 2200);
  }

  // ---------- locked buttons (padlock pop + shake) ----------
  var lockedKeys = cfg.locked || [];
  function isLocked(key) { return lockedKeys.indexOf(key) >= 0; }
  var lockSvg = '<svg viewBox="0 0 40 48" aria-hidden="true"><path d="M11 21v-7a9 9 0 0 1 18 0v7" fill="none" stroke="#0b0b0b" stroke-width="8"/>' +
    '<path d="M11 21v-7a9 9 0 0 1 18 0v7" fill="none" stroke="#d4ff00" stroke-width="3.5"/>' +
    '<rect x="3" y="20" width="34" height="26" rx="4" fill="#d4ff00" stroke="#0b0b0b" stroke-width="3"/>' +
    '<circle cx="20" cy="31" r="4" fill="#0b0b0b"/><rect x="18" y="32" width="4" height="8" rx="1" fill="#0b0b0b"/></svg>';
  function lockPop(btn, name) {
    toast(name + " · LOCKED · COMING SOON");
    var old = btn.querySelector(".lock-pop");
    if (old) old.remove();
    var pad = document.createElement("span");
    pad.className = "lock-pop";
    pad.innerHTML = lockSvg;
    btn.appendChild(pad);
    btn.classList.remove("is-shaking");
    void btn.offsetWidth;
    btn.classList.add("is-shaking");
    clearTimeout(btn._lockTimer);
    btn._lockTimer = setTimeout(function () {
      pad.remove();
      btn.classList.remove("is-shaking");
    }, 1400);
  }

  // ---------- links from config ----------
  var soon = { buy: "BUY CMBG", claim: "NFT CLAIM", play: "WEB BETA", x: "X", telegram: "TELEGRAM", dexscreener: "DEXSCREENER" };
  document.querySelectorAll("[data-link]").forEach(function (a) {
    var key = a.getAttribute("data-link");
    var url = links[key];
    if (isLocked(key)) {
      a.addEventListener("click", function (e) {
        e.preventDefault();
        lockPop(a, soon[key] || "THIS");
      });
    } else if (url) {
      a.href = url;
      a.target = "_blank";
      a.rel = "noopener";
    } else {
      a.addEventListener("click", function (e) {
        e.preventDefault();
        toast((soon[key] || "THIS") + " · COMING SOON");
      });
    }
  });

  // ---------- contract address ----------
  var net = cfg.network || {};
  if (cfg.ca) {
    document.querySelectorAll(".ca-value").forEach(function (el) { el.textContent = cfg.ca; });
    if (net.explorerUrl) {
      document.querySelectorAll("[data-explorer-ca]").forEach(function (a) {
        a.href = net.explorerUrl.replace(/\/$/, "") + "/token/" + cfg.ca;
        a.hidden = false;
      });
    }
  }
  document.querySelectorAll("[data-copy-ca]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      if (!cfg.ca) { toast("CA DROPS AT LAUNCH · FAKE CAs BITE"); return; }
      copyText(cfg.ca).then(function () {
        var old = btn.textContent;
        btn.textContent = "COPIED ✓";
        setTimeout(function () { btn.textContent = old; }, 1400);
        toast("CA COPIED");
      }, function () { toast("COPY FAILED · SELECT IT BY HAND"); });
    });
  });
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
    return new Promise(function (resolve, reject) {
      var ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand("copy") ? resolve() : reject(); } catch (err) { reject(err); }
      document.body.removeChild(ta);
    });
  }

  // ---------- burn count ----------
  if (cfg.burned !== "" && cfg.burned != null) {
    document.getElementById("burnCount").textContent = Number(cfg.burned).toLocaleString("en-US");
  }

  // ---------- art: swap placeholders for real files when they exist ----------
  document.querySelectorAll("[data-art]").forEach(function (box) {
    var video = box.getAttribute("data-video");
    var src = box.getAttribute("data-art");
    function tryImage() {
      var img = new Image();
      img.onload = function () {
        img.alt = "";
        box.appendChild(img);
        box.classList.add("has-media");
      };
      img.src = src;
    }
    if (!video) { tryImage(); return; }
    var v = document.createElement("video");
    v.muted = true; v.loop = true; v.autoplay = true; v.playsInline = true;
    v.setAttribute("playsinline", "");
    v.oncanplay = function () {
      if (box.classList.contains("has-media")) return;
      box.appendChild(v);
      box.classList.add("has-media");
      v.play().catch(function () {});
    };
    v.onerror = tryImage;
    v.src = video;
  });

  // ---------- video player (trailer, pack opening) ----------
  var videos = {
    trailer: { src: "assets/trailer.mp4", poster: "assets/trailer_poster.jpg", caption: "CMBG TEASER", muted: false, loop: false },
    // 2026-11-08: the real opening film of a Legendary pack (Bundle Cat, card_pack/opening_card_NFT, also used by cards.html)
    pack: { src: "nft/open/knight.mp4", poster: "nft/open/wait_legendary.webp", caption: "PACK OPENING · LEGENDARY", muted: true, loop: true }
  };
  var player = document.getElementById("player");
  var pv = document.getElementById("playerVideo");
  function closePlayer() {
    pv.pause();
    pv.removeAttribute("src");
    pv.load();
    if (player.open) player.close();
    document.body.classList.remove("is-locked");
  }
  document.querySelectorAll("[data-video-open]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var key = btn.getAttribute("data-video-open");
      if (isLocked(key)) { lockPop(btn, key === "pack" ? "PACK OPENING" : key.toUpperCase()); return; }
      // 2026-11-08: a key with a link in config.links (e.g. trailer -> the teaser post on X) opens that page instead of the player
      if (links[key]) { window.open(links[key], "_blank", "noopener"); return; }
      var v = videos[key];
      if (!v) return;
      player.classList.toggle("player--square", v.loop);
      pv.poster = v.poster;
      pv.muted = v.muted;
      pv.loop = v.loop;
      pv.src = v.src;
      document.getElementById("playerCaption").textContent = v.caption;
      if (typeof player.showModal === "function") player.showModal(); else player.setAttribute("open", "");
      document.body.classList.add("is-locked");
      pv.play().catch(function () {});
    });
  });
  document.getElementById("playerClose").addEventListener("click", closePlayer);
  player.addEventListener("click", function (e) { if (e.target === player) closePlayer(); });
  player.addEventListener("cancel", function (e) { e.preventDefault(); closePlayer(); });

  // ---------- fighters roster (section hidden for now, see index.html) ----------
  var roster = window.CMBG_ROSTER || [];
  var rosterEl = document.getElementById("roster");
  if (rosterEl) {
  var tierLabel = { original: "FREE", common: "COMMON", rare: "RARE", legendary: "LEGENDARY" };
  var maxOf = { dmg: 72, rate: 14, range: 220 };
  function bar(label, value, key, unit) {
    var pct = Math.max(6, Math.round(value / maxOf[key] * 100));
    return '<div class="stat"><span class="stat__k">' + label + '</span><span class="stat__bar"><i style="--w:' + pct + '%"></i></span><span class="stat__v">' + value + unit + "</span></div>";
  }
  rosterEl.innerHTML = roster.map(function (c) {
    return '<article class="fighter fighter--' + c.tier + '" data-tier="' + c.tier + '">' +
      '<div class="fighter__art"><span class="fighter__tier">' + tierLabel[c.tier] + "</span>" +
      '<img src="assets/cats/' + c.id + '.webp" alt="' + c.name + '" loading="lazy" width="370" height="560"></div>' +
      '<div class="fighter__body"><h3 class="display fighter__name">' + c.name + "</h3>" +
      '<p class="fighter__weapon">' + c.weapon.toUpperCase() + "</p>" +
      '<p class="fighter__role">' + c.role + "</p>" +
      '<div class="fighter__stats">' + bar("DMG", c.dmg, "dmg", "") + bar("RATE", c.rate, "rate", "/s") + bar("RANGE", c.range, "range", "m") + "</div></div></article>";
  }).join("");
  document.querySelectorAll(".filter").forEach(function (f) {
    f.addEventListener("click", function () {
      var want = f.getAttribute("data-filter");
      document.querySelectorAll(".filter").forEach(function (o) {
        o.classList.toggle("is-on", o === f);
        o.setAttribute("aria-selected", o === f ? "true" : "false");
      });
      rosterEl.querySelectorAll(".fighter").forEach(function (card) {
        card.hidden = want !== "all" && card.getAttribute("data-tier") !== want;
      });
    });
  });
  }

  // ---------- live leaderboard (GET /v1/leaderboard of the game's API, polled while the tab is visible) ----------
  var lbCfg = cfg.leaderboard || {};
  var lbEl = document.getElementById("leaderboard");
  if (lbEl && lbCfg.api && lbCfg.api.length) {
    var lbApis = lbCfg.api.slice();
    var lbRows = lbCfg.rows || 20;
    var lbEvery = Math.max(10, lbCfg.refreshSeconds || 20) * 1000;
    var lbState = { mode: "br", period: "week" };
    var lbCache = {};      // "br|week" -> last answer
    var lbPrev = {};       // "br|week" -> { name: { rank, score } } for the change highlights
    var lbShown = "";
    var lbTimer = null, lbBusy = false, lbFails = 0, lbClockSkew = 0, lbWeekEnd = 0, lbLastOk = 0;
    var lbPodium = document.getElementById("lbPodium");
    var lbTable = document.getElementById("lbTable");
    var lbEmpty = document.getElementById("lbEmpty");
    var lbNote = document.getElementById("lbNote");
    var lbLive = document.getElementById("lbLive");
    var lbUpdated = document.getElementById("lbUpdated");
    var lbReset = document.getElementById("lbReset");
    var lbTitle = document.getElementById("lbTitle");

    var lbFetch = function (path, i) {
      i = i || 0;
      var ctrl = "AbortController" in window ? new AbortController() : null;
      var t = setTimeout(function () { if (ctrl) ctrl.abort(); }, 8000);
      return fetch(lbApis[i].replace(/\/$/, "") + path, { cache: "no-store", signal: ctrl ? ctrl.signal : undefined })
        .then(function (r) { return r.json(); })
        .then(function (d) {
          clearTimeout(t);
          if (!d || d.ok !== true) throw new Error("bad answer");
          if (i > 0) lbApis.unshift(lbApis.splice(i, 1)[0]);   // keep the address that answered first
          return d;
        }, function (err) {
          clearTimeout(t);
          throw err;
        })
        .catch(function (err) {
          if (i + 1 < lbApis.length) return lbFetch(path, i + 1);
          throw err;
        });
    };

    var lbEl2 = function (tag, cls, text) {
      var e = document.createElement(tag);
      if (cls) e.className = cls;
      if (text != null) e.textContent = text;
      return e;
    };
    var lbNum = function (n) { return Number(n || 0).toLocaleString("en-US"); };

    var lbRender = function (d, key) {
      var zombies = d.mode === "zombies";
      var rows = d.rows || [];
      var sig = key + JSON.stringify(rows);
      if (sig === lbShown) return;      // nothing changed: keep the DOM (no replayed animations)
      lbShown = sig;
      var prev = lbPrev[key] || null;
      var now = {};
      // one row per wallet (2026-11-08): the wallet is the identity, the name can change
      var idOf = function (r) { return r.wallet || r.name; };
      rows.forEach(function (r) { now[idOf(r)] = { rank: r.rank, score: r.score }; });
      lbPrev[key] = now;

      var change = function (r) {
        if (!prev) return "";
        var p = prev[idOf(r)];
        if (!p) return "new";
        if (r.score > p.score) return r.rank < p.rank ? "up" : "score";
        return "";
      };
      var stats = function (r) {
        return zombies
          ? [["SCORE", lbNum(r.score)], ["WAVE", lbNum(r.kills)], ["RUNS", lbNum(r.matches)]]
          : [["POINTS", lbNum(r.score)], ["KILLS", lbNum(r.kills)], ["MATCHES", lbNum(r.matches)]];
      };

      lbPodium.textContent = "";
      lbTable.textContent = "";
      lbEmpty.hidden = rows.length > 0;
      lbPodium.hidden = rows.length === 0;
      lbTable.hidden = rows.length <= 3;

      rows.slice(0, 3).forEach(function (r, i) {
        var card = lbEl2("article", "lb__card lb__card--" + (i + 1));
        var c = change(r);
        if (c) card.classList.add("is-" + c);
        var top = lbEl2("div", "lb__card-top");
        top.appendChild(lbEl2("span", "display lb__place", "#" + r.rank));
        if (i === 0) top.appendChild(lbEl2("span", "lb__crown", "CROWN"));
        card.appendChild(top);
        card.appendChild(lbEl2("h3", "display lb__name", r.name));
        if (r.wallet) card.appendChild(lbEl2("p", "lb__wallet", r.wallet));
        var dl = lbEl2("dl", "lb__stats");
        stats(r).forEach(function (s) {
          var box = lbEl2("div");
          box.appendChild(lbEl2("dt", null, s[1]));
          box.appendChild(lbEl2("dd", null, s[0]));
          dl.appendChild(box);
        });
        card.appendChild(dl);
        lbPodium.appendChild(card);
      });

      if (rows.length > 3) {
        var head = lbEl2("div", "lb__row lb__row--head");
        head.setAttribute("role", "row");
        ["#", "PLAYER"].concat(stats(rows[0]).map(function (s) { return s[0]; })).forEach(function (h, i) {
          var c = lbEl2("span", "lb__c" + i, h);
          c.setAttribute("role", "columnheader");
          head.appendChild(c);
        });
        lbTable.appendChild(head);
        rows.slice(3).forEach(function (r) {
          var row = lbEl2("div", "lb__row");
          row.setAttribute("role", "row");
          var c = change(r);
          if (c) row.classList.add("is-" + c);
          var cells = [String(r.rank), r.name].concat(stats(r).map(function (s) { return s[1]; }));
          cells.forEach(function (v, i) {
            var cell = lbEl2("span", "lb__c" + i, v);
            cell.setAttribute("role", "cell");
            if (i === 1 && c === "up") cell.appendChild(lbEl2("i", "lb__up", "▲"));
            if (i === 1 && r.wallet) cell.appendChild(lbEl2("small", "lb__wallet", r.wallet));
            row.appendChild(cell);
          });
          lbTable.appendChild(row);
        });
      }

      lbNote.textContent = (zombies
        ? "Zombie Survival: your best run counts. Score = waves x 1000 + kills."
        : (d.period === "week"
          ? "Battle Royale: your 10 best matches of the week count. Placement points + 10 per kill."
          : "Battle Royale: every match counts. Placement points + 10 per kill."))
        + " Only matches played with a connected wallet are ranked.";
    };

    var lbShow = function () {
      var key = lbState.mode + "|" + lbState.period;
      lbTitle.textContent = lbState.period === "week" ? "THIS WEEK." : "OF ALL TIME.";
      lbReset.style.visibility = lbState.period === "week" ? "visible" : "hidden";
      if (lbCache[key]) lbRender(lbCache[key], key);
      else { lbShown = ""; lbPodium.textContent = ""; lbTable.textContent = ""; lbTable.hidden = true; lbEmpty.hidden = true; lbUpdated.textContent = "LOADING…"; }
    };

    var lbLoad = function () {
      if (lbBusy) return;
      lbBusy = true;
      var mode = lbState.mode, period = lbState.period, key = mode + "|" + period;
      lbFetch("/v1/leaderboard?mode=" + mode + "&period=" + period + "&limit=" + lbRows)
        .then(function (d) {
          lbFails = 0;
          lbLastOk = Date.now();
          lbClockSkew = (d.serverTime || Date.now()) - Date.now();
          lbWeekEnd = d.weekEnd || 0;
          lbCache[key] = d;
          lbLive.classList.remove("is-off");
          if (mode === lbState.mode && period === lbState.period) lbRender(d, key);
        })
        .catch(function () {
          lbFails++;
          lbLive.classList.add("is-off");
          if (!lbCache[key] && mode === lbState.mode && period === lbState.period) {
            lbPodium.textContent = ""; lbTable.textContent = ""; lbTable.hidden = true; lbEmpty.hidden = true;
          }
        })
        .then(function () { lbBusy = false; lbTick(); });
    };

    var lbTick = function () {
      if (lbFails > 0) {
        lbUpdated.textContent = lbLastOk ? "OFFLINE · RETRYING · LAST UPDATE " + lbAgo(lbLastOk) : "LEADERBOARD OFFLINE · RETRYING";
      } else if (lbLastOk) {
        lbUpdated.textContent = "UPDATED " + lbAgo(lbLastOk);
      }
      if (lbWeekEnd) {
        var left = Math.max(0, lbWeekEnd - (Date.now() + lbClockSkew));
        var dd = Math.floor(left / 864e5), hh = Math.floor(left / 36e5) % 24, mm = Math.floor(left / 6e4) % 60, ss = Math.floor(left / 1e3) % 60;
        var two = function (n) { return (n < 10 ? "0" : "") + n; };
        lbReset.textContent = "WEEK RESETS IN " + (dd ? dd + "D " : "") + two(hh) + ":" + two(mm) + ":" + two(ss);
      }
    };
    var lbAgo = function (t) {
      var s = Math.max(0, Math.round((Date.now() - t) / 1000));
      return s < 5 ? "JUST NOW" : s < 60 ? s + "S AGO" : Math.floor(s / 60) + " MIN AGO";
    };

    var lbStart = function () {
      clearInterval(lbTimer);
      lbLoad();
      lbTimer = setInterval(function () {
        // back off to once a minute while the API is down
        if (lbFails > 2 && Date.now() - lbLastTry < 60000) return;
        lbLastTry = Date.now();
        lbLoad();
      }, lbEvery);
    };
    var lbLastTry = 0;
    var lbStop = function () { clearInterval(lbTimer); lbTimer = null; };

    document.querySelectorAll("[data-lb-mode], [data-lb-period]").forEach(function (b) {
      b.addEventListener("click", function () {
        var isMode = b.hasAttribute("data-lb-mode");
        var attr = isMode ? "data-lb-mode" : "data-lb-period";
        lbState[isMode ? "mode" : "period"] = b.getAttribute(attr);
        document.querySelectorAll("[" + attr + "]").forEach(function (o) {
          o.classList.toggle("is-on", o === b);
          o.setAttribute("aria-selected", o === b ? "true" : "false");
        });
        lbShow();
        lbLoad();
      });
    });

    setInterval(lbTick, 1000);
    lbShow();
    // poll only while the section is near the screen and the tab is visible
    var lbNear = !("IntersectionObserver" in window);
    var lbSync = function () {
      if (lbNear && !document.hidden) { if (!lbTimer) lbStart(); } else lbStop();
    };
    if (!lbNear) {
      new IntersectionObserver(function (es) { lbNear = es[0].isIntersecting; lbSync(); }, { rootMargin: "400px 0px" }).observe(lbEl);
    }
    document.addEventListener("visibilitychange", lbSync);
    lbLoad();          // one load right away, so the board is ready before the visitor scrolls to it
    lbSync();
  }

  // ---------- nav ----------
  var nav = document.querySelector(".nav");
  var burger = document.getElementById("burger");
  burger.addEventListener("click", function () {
    var open = nav.classList.toggle("is-open");
    burger.setAttribute("aria-expanded", open ? "true" : "false");
  });
  document.querySelectorAll("#navLinks a").forEach(function (a) {
    a.addEventListener("click", function () {
      nav.classList.remove("is-open");
      burger.setAttribute("aria-expanded", "false");
    });
  });
  function onScroll() { nav.classList.toggle("is-scrolled", window.scrollY > 8); }
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  // active section in the nav
  var navMap = {};
  document.querySelectorAll('#navLinks a[href^="#"]').forEach(function (a) { navMap[a.getAttribute("href").slice(1)] = a; });
  if ("IntersectionObserver" in window) {
    var spy = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        var a = navMap[e.target.id];
        if (a) a.classList.toggle("is-active", e.isIntersecting);
      });
    }, { rootMargin: "-45% 0px -50% 0px" });
    Object.keys(navMap).forEach(function (id) {
      var s = document.getElementById(id);
      if (s) spy.observe(s);
    });

    // reveal on scroll
    var rev = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) { e.target.classList.add("is-in"); rev.unobserve(e.target); }
      });
    }, { threshold: 0.15 });
    document.querySelectorAll(".reveal").forEach(function (el) { rev.observe(el); });
  } else {
    document.querySelectorAll(".reveal").forEach(function (el) { el.classList.add("is-in"); });
  }

  // ---------- count-up for big numbers ----------
  document.querySelectorAll("[data-count]").forEach(function (el) {
    var target = Number(el.getAttribute("data-count"));
    var started = false;
    function run() {
      if (started) return;
      started = true;
      var t0 = performance.now(), dur = 1400;
      (function step(now) {
        var k = Math.min(1, (now - t0) / dur);
        k = 1 - Math.pow(1 - k, 3);
        el.textContent = Math.round(target * k).toLocaleString("en-US");
        if (k < 1) requestAnimationFrame(step);
      })(t0);
    }
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches || !("IntersectionObserver" in window)) return;
    var o = new IntersectionObserver(function (es) { if (es[0].isIntersecting) { run(); o.disconnect(); } }, { threshold: 0.6 });
    o.observe(el);
  });
})();
