namespace ConsoleOverlay.Overlay;

/// <summary>
/// The script added to every page the overlay's browser opens. It lets a controller use an ordinary web page: the
/// D-pad moves a white ring to the nearest link or button in that direction (the way a TV browser does), A clicks it,
/// and text typed on the on-screen keyboard goes into the field the ring is on. It only reads the page's layout and
/// sends the same clicks and typing a person would; it never reads or sends anything else.
/// </summary>
internal static class WebControllerScript
{
    public const string Source = """
(() => {
  if (window.__co) return;
  const RING = '__co-ring';
  const addStyle = () => {
    if (document.getElementById('__co-style')) return;
    const style = document.createElement('style');
    style.id = '__co-style';
    style.textContent = '.' + RING + '{outline:3px solid #fff !important;outline-offset:3px !important;' +
      'box-shadow:0 0 0 9px rgba(255,255,255,.28) !important;border-radius:8px;transition:box-shadow .12s;}';
    (document.head || document.documentElement).appendChild(style);
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', addStyle); else addStyle();

  const BASE = 'a[href],button,input:not([type=hidden]),select,textarea,summary,[role=button],[role=link],[role=tab],' +
    '[role=menuitem],[role=option],[role=checkbox],[role=radio],[tabindex]:not([tabindex="-1"]),[onclick],[contenteditable=""],[contenteditable=true]';
  // The main button of a Steam page: Add to Cart, Purchase for myself, Continue to payment, Install, Play.
  const PRIMARY = '.btn_addtocart a, .btn_addtocart .btn_green_steamui, #purchase_button_bottom, .btn_green_steamui, .btn_green_white_innerfade, .btn_blue_steamui';
  let cur = null, text = null;

  const visible = el => {
    const r = el.getBoundingClientRect();
    if (r.width < 4 || r.height < 4) return false;
    if (r.bottom < -innerHeight * 2 || r.top > innerHeight * 3) return false;
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none' && parseFloat(s.opacity) > 0.05;
  };
  const isText = el => !!el && (el.tagName === 'TEXTAREA' || el.isContentEditable ||
    (el.tagName === 'INPUT' && !['button','submit','reset','checkbox','radio','image','file','range','color'].includes((el.type || '').toLowerCase())));

  // Links and buttons, plus things web apps make clickable from script (cursor: pointer), outermost only.
  const candidates = () => {
    const found = new Set();
    for (const el of document.querySelectorAll(BASE)) if (!el.disabled && visible(el)) found.add(el);
    for (const el of document.querySelectorAll('div,span,li,img,svg')) {
      if (found.has(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 8 || r.height < 8 || r.bottom < -innerHeight || r.top > innerHeight * 2) continue;
      if (getComputedStyle(el).cursor !== 'pointer') continue;
      const parent = el.parentElement;
      if (parent && getComputedStyle(parent).cursor === 'pointer') continue;
      if (visible(el)) found.add(el);
    }
    return [...found].filter(el => {
      for (let p = el.parentElement; p; p = p.parentElement) if (found.has(p) && !isText(el)) return false;
      return !el.closest('[aria-hidden="true"]');
    });
  };

  const set = el => {
    if (cur) cur.classList.remove(RING);
    cur = el;
    if (!el) return;
    el.classList.add(RING);
    el.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
    try { el.focus({ preventScroll: true }); } catch (e) {}
  };

  const scroller = el => {
    for (let p = el && el.parentElement; p && p !== document.body; p = p.parentElement) {
      const s = getComputedStyle(p);
      if (/(auto|scroll)/.test(s.overflowY) && p.scrollHeight > p.clientHeight + 20) return p;
    }
    return null;
  };

  const scroll = dir => {
    const target = scroller(cur);
    const by = dir * (target ? target.clientHeight : innerHeight) * 0.7;
    (target || window).scrollBy({ top: by, behavior: 'smooth' });
  };

  const first = list => {
    const onScreen = list.filter(el => { const r = el.getBoundingClientRect(); return r.top >= 0 && r.top < innerHeight; });
    onScreen.sort((a, b) => { const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect(); return (ra.top - rb.top) || (ra.left - rb.left); });
    return onScreen[0] || list[0] || null;
  };

  const move = dir => {
    if (cur && cur.tagName === 'SELECT' && (dir === 'left' || dir === 'right')) {
      const i = Math.max(0, Math.min(cur.options.length - 1, cur.selectedIndex + (dir === 'right' ? 1 : -1)));
      if (i !== cur.selectedIndex) {
        cur.selectedIndex = i;
        cur.dispatchEvent(new Event('input', { bubbles: true }));
        cur.dispatchEvent(new Event('change', { bubbles: true }));
      }
      return 'select';
    }
    const list = candidates();
    if (!cur || !document.contains(cur) || !visible(cur)) {
      const el = first(list);
      if (el) { set(el); return 'moved'; }
      if (dir === 'up' || dir === 'down') scroll(dir === 'down' ? 1 : -1);
      return 'none';
    }
    const a = cur.getBoundingClientRect();
    const ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    let best = null, bestScore = Infinity;
    for (const el of list) {
      if (el === cur || el.contains(cur) || cur.contains(el)) continue;
      const b = el.getBoundingClientRect();
      const bx = b.left + b.width / 2, by = b.top + b.height / 2;
      let along, across;
      if (dir === 'down')  { if (by <= ay + 2 || b.bottom <= a.bottom - 2) continue; along = Math.max(0, b.top - a.bottom); across = Math.max(0, Math.max(a.left, b.left) - Math.min(a.right, b.right)); }
      if (dir === 'up')    { if (by >= ay - 2 || b.top >= a.top + 2) continue; along = Math.max(0, a.top - b.bottom); across = Math.max(0, Math.max(a.left, b.left) - Math.min(a.right, b.right)); }
      if (dir === 'right') { if (bx <= ax + 2 || b.right <= a.right - 2) continue; along = Math.max(0, b.left - a.right); across = Math.max(0, Math.max(a.top, b.top) - Math.min(a.bottom, b.bottom)); }
      if (dir === 'left')  { if (bx >= ax - 2 || b.left >= a.left + 2) continue; along = Math.max(0, a.left - b.right); across = Math.max(0, Math.max(a.top, b.top) - Math.min(a.bottom, b.bottom)); }
      const centre = (dir === 'up' || dir === 'down') ? Math.abs(bx - ax) : Math.abs(by - ay);
      // Steam's main buttons (Add to Cart, Purchase, Continue) are easier to reach from the side.
      const weight = el.matches(PRIMARY) ? 1 : 3;
      const score = along + across * weight + centre * 0.1;
      if (score < bestScore) { bestScore = score; best = el; }
    }
    // Far below the screen: scroll a bit first, so the page can be read on the way down.
    if (best && (dir === 'up' || dir === 'down')) {
      const b = best.getBoundingClientRect();
      if ((dir === 'down' && b.top > innerHeight * 1.4) || (dir === 'up' && b.bottom < -innerHeight * 0.4)) {
        scroll(dir === 'down' ? 1 : -1);
        return 'scrolled';
      }
    }
    if (!best) {
      if (dir === 'up' || dir === 'down') scroll(dir === 'down' ? 1 : -1);
      return 'none';
    }
    set(best);
    return 'moved';
  };

  const activate = () => {
    if (!cur || !document.contains(cur)) { const el = first(candidates()); if (el) set(el); return 'moved'; }
    if (isText(cur)) { text = cur; cur.focus(); return 'text'; }
    if (cur.tagName === 'SELECT') return 'select';
    cur.click();
    return 'clicked';
  };

  const textTarget = () => {
    if (text && document.contains(text) && visible(text)) return text;
    const active = document.activeElement;
    if (isText(active)) return text = active;
    if (isText(cur)) return text = cur;
    const fields = [...document.querySelectorAll('textarea,input,[contenteditable=""],[contenteditable=true]')].filter(el => isText(el) && visible(el));
    const any = fields.find(el => el.tagName === 'TEXTAREA') || fields[0];
    if (any) { set(any); text = any; }
    return text || null;
  };

  const setValue = (el, value) => {
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  };

  const key = (el, name, code) => {
    const make = type => {
      const ev = new KeyboardEvent(type, { key: name, code: name, bubbles: true, cancelable: true });
      Object.defineProperty(ev, 'keyCode', { get: () => code });
      Object.defineProperty(ev, 'which', { get: () => code });
      return ev;
    };
    const go = el.dispatchEvent(make('keydown'));
    el.dispatchEvent(make('keypress'));
    el.dispatchEvent(make('keyup'));
    return go;
  };

  const type = t => {
    const el = textTarget();
    if (!el) return 'none';
    el.focus();
    if (el.isContentEditable) document.execCommand('insertText', false, t);
    else setValue(el, (el.value || '') + t);
    return 'typed';
  };

  const back = () => {
    const el = textTarget();
    if (!el) return 'none';
    el.focus();
    if (el.isContentEditable) document.execCommand('delete', false);
    else setValue(el, (el.value || '').slice(0, -1));
    return 'typed';
  };

  const enter = () => {
    const el = textTarget();
    if (!el) return 'none';
    el.focus();
    if (key(el, 'Enter', 13) && el.form) {
      if (el.form.requestSubmit) el.form.requestSubmit(); else el.form.submit();
    }
    return 'entered';
  };

  // On a store page the ring starts on its main button, so A buys (or plays) straight away.
  const home = () => {
    if (cur && document.contains(cur)) return 'kept';
    const el = [...document.querySelectorAll(PRIMARY)].find(e => visible(e) && !e.closest('[aria-hidden="true"]'));
    if (!el) return 'none';
    cur = el;
    el.classList.add(RING);
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    try { el.focus({ preventScroll: true }); } catch (e) {}
    return 'home';
  };

  // Opens a conversation in Steam's web chat: clicks the friend whose name is shown in the list. An exact match in the
  // friends list wins; a name that starts the same (a nickname shown with the name) comes next.
  const openByName = name => {
    const want = (name || '').trim().toLowerCase();
    if (!want) return 'none';
    let best = null, bestRank = 9;
    for (const el of document.querySelectorAll('div,span,a')) {
      if (el.children.length > 2) continue;
      const t = (el.textContent || '').trim().toLowerCase();
      if (!t || t.length > want.length + 40) continue;
      let rank = t === want ? 0 : (want.length >= 3 && t.startsWith(want)) ? 2 : 9;
      if (rank === 9 || !visible(el)) continue;
      if (!el.closest('[class*="friend" i]')) rank += 1;
      if (rank < bestRank) { best = el; bestRank = rank; }
    }
    if (!best) return 'none';
    let target = best;
    for (let p = best; p && p !== document.body; p = p.parentElement)
      if (getComputedStyle(p).cursor === 'pointer' || p.tagName === 'A') { target = p; break; }
    target.click();
    return 'opened';
  };

  // The chat's message box (the biggest visible text box near the bottom of the page).
  const messageBox = () => {
    const boxes = [...document.querySelectorAll('textarea,[contenteditable=""],[contenteditable=true]')].filter(el => visible(el));
    boxes.sort((a, b) => b.getBoundingClientRect().bottom - a.getBoundingClientRect().bottom);
    return boxes[0] || null;
  };

  const focusMessage = () => {
    const box = messageBox();
    if (!box) return 'none';
    set(box);
    text = box;
    return 'text';
  };

  // Y in the Store: the field the ring is on, else the Store's search box.
  const focusSearch = () => {
    if (isText(cur) && document.contains(cur) && visible(cur)) { text = cur; return 'text'; }
    const box = document.querySelector('#store_nav_search_term, input[name="term"], #term, input[type="search"]');
    if (!box || !visible(box)) { const any = textTarget(); return any ? 'text' : 'none'; }
    set(box);
    text = box;
    return 'text';
  };

  // Only the conversation: the friends list column is hidden (the overlay has its own list). Found by its search
  // box / class name and the column around it; put back if hiding it would also hide the conversation.
  const chatMode = () => {
    if (document.querySelector('[data-co-hidden]')) return 'kept';
    const box = messageBox();
    if (!box) return 'no-message-box';
    const seed = document.querySelector('[class*="friendlist" i], [class*="FriendsList" i], [class*="friendsList" i]');
    if (!seed) return 'no-list';
    let column = seed;
    for (let p = seed.parentElement; p && p !== document.body; p = p.parentElement) {
      if (p.contains(box)) break;
      column = p;
    }
    const r = column.getBoundingClientRect();
    if (r.width > innerWidth * 0.6 || column.contains(box)) return 'unsafe';
    column.setAttribute('data-co-hidden', '1');
    column.style.setProperty('display', 'none', 'important');
    // Also the drag handle between the list and the chat, when it is a thin sibling.
    const next = column.nextElementSibling;
    if (next && !next.contains(box) && next.getBoundingClientRect().width < 16) next.style.setProperty('display', 'none', 'important');
    if (!visible(box)) { column.style.removeProperty('display'); column.removeAttribute('data-co-hidden'); return 'restored'; }
    dispatchEvent(new Event('resize'));
    return 'list-hidden';
  };

  // Esc on a real keyboard goes to the overlay (B), since the page has the keyboard focus.
  addEventListener('keydown', e => {
    if (e.key === 'Escape' && e.isTrusted && window.chrome && chrome.webview) {
      e.preventDefault();
      chrome.webview.postMessage('escape');
    }
  }, true);

  window.__co = { move, activate, home, scroll, type, back, enter, openByName, focusMessage, focusSearch, chatMode, isTextFocused: () => isText(document.activeElement) };
})();
""";
}
