// Phones in "desktop site" mode (Chrome / Safari) ignore the viewport tag and lay the page out ~980 px wide, so everything
// shrinks and the full-height sections stretch. Show the phone layout instead: zoom the page to the phone's width and apply
// the (max-width) media rules of the stylesheets that match that width. Loaded in <head> right after style.css.
// Test on a computer with ?fit=412 (forces a 412 px phone).
(function () {
  "use strict";
  var root = document.documentElement;
  var forced = Number((location.search.match(/[?&]fit=(\d+)/) || [])[1]) || 0;
  var styleEl = null;
  var appliedWidth = 0;

  function mobileRules(width) {
    var out = [];
    for (var i = 0; i < document.styleSheets.length; i++) {
      var rules;
      try { rules = document.styleSheets[i].cssRules; } catch (e) { continue; }
      for (var j = 0; j < rules.length; j++) {
        var r = rules[j];
        var m = r.media && /^\(max-width:\s*(\d+)px\)$/.exec(r.media.mediaText.trim());
        if (!m || Number(m[1]) < width) continue;
        for (var k = 0; k < r.cssRules.length; k++) out.push(r.cssRules[k].cssText);
      }
    }
    return out.join("\n");
  }

  function fit() {
    var sw = screen.width, sh = screen.height;
    var phoneWidth = forced || (innerWidth > innerHeight ? Math.max(sw, sh) : Math.min(sw, sh));
    var touchPhone = matchMedia("(hover: none) and (pointer: coarse)").matches && Math.min(sw, sh) <= 600;
    var on = forced > 0 || (touchPhone && innerWidth > phoneWidth * 1.25);
    if (!on) {
      if (!appliedWidth) return;
      root.style.zoom = "";
      root.style.removeProperty("--vw");
      if (styleEl) styleEl.textContent = "";
      appliedWidth = 0;
      return;
    }
    root.style.zoom = String(innerWidth / phoneWidth);
    if (appliedWidth === phoneWidth) return;
    appliedWidth = phoneWidth;
    root.style.setProperty("--vw", phoneWidth / 100 + "px");
    if (!styleEl) {
      styleEl = document.createElement("style");
      styleEl.id = "phone-fit";
      document.head.appendChild(styleEl);
    }
    styleEl.textContent = mobileRules(phoneWidth);
  }

  fit();
  addEventListener("resize", fit);
  addEventListener("orientationchange", fit);
})();
