namespace ConsoleOverlay.Overlay;

/// <summary>
/// The overlay's own style sheet for Steam's web pages, added by our browser to the page it shows (like a user style
/// in Edge). It changes only how the page looks on this PC: the overlay's font, bigger rounded controls, white pill
/// buttons, frosted panels on the overlay's dark glass, and no clutter a controller can't use. Nothing in Steam or
/// your account changes.
///
/// Steam's web chat names its classes differently from build to build, so the chat rules match parts of class names
/// and, for the page's big background panels, look at the page itself (any panel larger than a tenth of the screen
/// with a solid background becomes see-through). Rules that find nothing simply do nothing.
/// </summary>
internal static class SteamWebStyle
{
    public const string Script = """
(() => {
  if (window.__coStyle) return;
  window.__coStyle = true;
  const host = location.hostname, path = location.pathname;
  const chat = host === 'steamcommunity.com' && path.startsWith('/chat');
  const store = host === 'store.steampowered.com' || host === 'checkout.steampowered.com';

  const common = `
    html, body { background: transparent !important; color: #e9eef4 !important; }
    body, body input, body textarea, body button, body select, body a, body div, body span, body p, body h1, body h2,
    body h3, body h4, body li, body td {
      font-family: "Segoe UI Variable Text", "Segoe UI", Arial, sans-serif !important; }
    ::selection { background: rgba(255,255,255,.85); color: #12161c; }
    ::-webkit-scrollbar { width: 8px; height: 8px; background: transparent; }
    ::-webkit-scrollbar-thumb { background: rgba(255,255,255,.22); border-radius: 8px; }
    ::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }
    .__co-ring { border-radius: 14px !important; }
    a { color: #a9d8ff; }
  `;

  const chatCss = `
    body { font-size: 16px !important; }
    [class*="titleBar" i], [class*="TitleBar"], [class*="windowControls" i], [class*="title-area-highlight" i],
    #global_header, .responsive_header { display: none !important; }
    /* The message box: a rounded frosted field, like the overlay's own. */
    textarea, [contenteditable="true"] {
      background: rgba(255,255,255,.10) !important; color: #fff !important; caret-color: #fff !important;
      border: 1px solid rgba(255,255,255,.20) !important; border-radius: 22px !important;
      padding: 12px 18px !important; font-size: 17px !important; line-height: 1.35 !important; outline: none !important; }
    textarea::placeholder { color: rgba(255,255,255,.55) !important; }
    /* Messages: bigger, lighter text with more room between lines. */
    [class*="msg" i], [class*="ChatMessage" i], [class*="chatMessage" i] { font-size: 17px !important; line-height: 1.45 !important; }
    [class*="speakerName" i], [class*="playerName" i], [class*="persona" i] { font-weight: 600 !important; }
    [class*="timestamp" i], [class*="Timestamp"] { color: rgba(233,238,244,.55) !important; }
    /* Buttons and the send arrow: white pills. */
    button, [class*="submit" i], [class*="Button" i][role="button"] { border-radius: 999px !important; }
    [class*="chatSubmit" i], button[type="submit"] { background: #fff !important; color: #12161c !important; border: none !important; }
    [class*="chatSubmit" i] svg, button[type="submit"] svg { fill: #12161c !important; color: #12161c !important; }
    /* Avatars round like the overlay's tiles. */
    img[class*="avatar" i], [class*="avatar" i] img { border-radius: 12px !important; }
    /* Menus and pop-ups keep a solid background so they stay readable. */
    [class*="contextMenu" i], [class*="ContextMenu"], [role="menu"], [class*="popup" i], [class*="Popup"],
    [class*="modal" i], [class*="Modal"], [class*="tooltip" i] {
      background: #1d232d !important; border-radius: 16px !important; border: 1px solid rgba(255,255,255,.14) !important; }
  `;

  const storeCss = `
    body { font-size: 15px !important; }
    /* Backgrounds: the overlay's glass instead of Steam's blue. */
    .page_background_holder, .page_background, .home_page_body_ctn, .page_content_ctn, .responsive_page_frame,
    .responsive_page_content, .responsive_page_template_content, #store_header, .store_header, .home_page_content,
    .game_page_background, .game_background_glow, .page_content, .store_main_capsule, .home_ctn, .tab_content_ctn,
    #global_header, #global_header .content, .blur_background { background: transparent !important; background-image: none !important; box-shadow: none !important; }
    /* Clutter a controller can't use: footer, Install Steam, the language menu. */
    #footer, #footer_spacer, .header_installsteam_btn, #language_pulldown, #language_dropdown,
    .responsive_store_nav_ctn_spacer, #global_header .supernav_container, .store_nav_bg .store_nav_left_spacer,
    .home_page_sign_in_ctn, .home_rightcol .home_ctn_spacer, #ignoreBtn { display: none !important; }
    /* The Store menu bar: a frosted pill with bigger items. */
    .store_nav_bg, #store_nav_area .store_nav_bg { background: rgba(255,255,255,.08) !important; border: none !important;
      border-radius: 999px !important; box-shadow: none !important; padding: 4px 10px !important; }
    .store_nav .tab, .store_nav .tab > span { font-size: 16px !important; background: transparent !important; border-radius: 999px !important; }
    .store_nav .tab:hover, .store_nav .tab.focus { background: rgba(255,255,255,.14) !important; }
    #store_search, .searchbox, #store_search .searchbox { background: rgba(255,255,255,.92) !important; border: none !important;
      border-radius: 999px !important; box-shadow: none !important; }
    #store_nav_search_term, .searchbox input { color: #12161c !important; font-size: 16px !important; background: transparent !important; }
    /* Game pictures: rounded like the overlay's tiles. */
    .store_capsule, .store_main_capsule, .tab_item, .tab_item_cap, .tab_item_cap_img, .capsule, .carousel_items > a,
    .home_area_spotlight, .spotlight_img, .special, .store_capsule img, .capsule img, .game_header_image_full,
    .highlight_player_item, .highlight_strip_item, .search_result_row, .wishlist_row, .dailydeal_ctn, .home_marketing_message
    { border-radius: 16px !important; overflow: hidden !important; }
    .tab_item, .search_result_row, .wishlist_row { background: rgba(255,255,255,.06) !important; margin-bottom: 8px !important; border: none !important; }
    /* Main buttons (Add to Cart, Purchase, Continue): the overlay's white pill. */
    .btn_green_steamui, .btn_green_white_innerfade, .btn_addtocart .btn_green_steamui, .btn_blue_steamui {
      background: #fff !important; border-radius: 999px !important; border: none !important; box-shadow: none !important; }
    .btn_green_steamui > span, .btn_green_white_innerfade > span, .btn_blue_steamui > span {
      background: transparent !important; color: #12161c !important; font-weight: 600 !important; font-size: 17px !important;
      padding: 0 24px !important; line-height: 46px !important; border-radius: 999px !important; }
    /* Other buttons: frosted pills. */
    .btnv6_blue_hoverfade, .btnv6_white_transparent, .btn_grey_steamui, .btn_darkblue_white_innerfade, .btn_grey_black,
    .btn_grey_white_innerfade, .queue_btn_inactive, .queue_btn_active {
      background: rgba(255,255,255,.12) !important; border-radius: 999px !important; border: none !important; box-shadow: none !important; }
    .btnv6_blue_hoverfade > span, .btnv6_white_transparent > span, .btn_grey_steamui > span, .btn_grey_black > span,
    .btn_grey_white_innerfade > span, .btn_darkblue_white_innerfade > span {
      background: transparent !important; color: #fff !important; font-size: 16px !important; line-height: 40px !important; padding: 0 18px !important; }
    /* Prices: white discount badge, like the overlay's. */
    .discount_pct, .bundle_base_discount, [class*="StoreSaleDiscountBox"], [class*="DiscountBox" i] { background: #fff !important; color: #12161c !important; border-radius: 10px !important; font-weight: 700 !important; }
    .discount_prices, .discount_block, .game_purchase_action_bg, .game_purchase_price, .game_purchase_discount { background: transparent !important; }
    .discount_final_price, .game_purchase_price, .price { color: #fff !important; font-size: 17px !important; }
    /* Panels (the buy box, the review summary, side boxes): frosted glass. */
    .game_area_purchase_game, .game_area_purchase_game_wrapper .game_area_purchase_game, .block_content, .glance_ctn,
    .game_meta_data .block, .responsive_apppage_details_right, .checkout_content_box, .checkout_tab, .cart_area_body,
    .cart_row, .page_area_ctn, .rightcol .block, .home_tabs_content { background: rgba(255,255,255,.06) !important;
      border-radius: 22px !important; border: none !important; box-shadow: none !important; }
    .game_area_purchase_game h1 { font-size: 22px !important; }
    h2, .home_page_content h2, .home_section_title { font-size: 22px !important; font-weight: 600 !important; letter-spacing: 0 !important;
      text-transform: none !important; color: #fff !important; }
    .apphub_AppName { font-size: 40px !important; font-weight: 700 !important; }
    .game_description_snippet, .game_area_description { font-size: 17px !important; line-height: 1.5 !important; color: #dfe6ee !important; }
    input[type="text"], input[type="email"], input[type="password"], input[type="tel"], select, textarea {
      border-radius: 14px !important; background: rgba(255,255,255,.10) !important; color: #fff !important;
      border: 1px solid rgba(255,255,255,.2) !important; font-size: 16px !important; padding: 8px 12px !important; }
    .carousel_thumbs > div, .carousel_thumbs div { border-radius: 999px !important; }
  `;

  const add = () => {
    if (document.getElementById('__co-theme')) return;
    const style = document.createElement('style');
    style.id = '__co-theme';
    style.textContent = common + (chat ? chatCss : '') + (store ? storeCss : '');
    (document.head || document.documentElement).appendChild(style);
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', add); else add();

  if (!chat) return;
  // The chat app draws its big panels with solid colours that differ between builds: every large panel becomes
  // see-through so the overlay's glass shows, while small things (messages, buttons, menus) keep theirs.
  const solid = c => { const m = /rgba?\(([^)]+)\)/.exec(c); if (!m) return false; const p = m[1].split(','); return p.length < 4 || parseFloat(p[3]) > 0.05; };
  const flatten = () => {
    const area = innerWidth * innerHeight * 0.10;
    for (const el of document.querySelectorAll('body div, body section, body main')) {
      if (el.dataset.coFlat) continue;
      if (el.closest('[class*="menu" i], [class*="popup" i], [class*="modal" i], [role="menu"], [role="dialog"]')) continue;
      const r = el.getBoundingClientRect();
      if (r.width * r.height < area) continue;
      const s = getComputedStyle(el);
      if (!solid(s.backgroundColor) && s.backgroundImage === 'none') continue;
      el.dataset.coFlat = '1';
      el.style.setProperty('background', 'transparent', 'important');
      el.style.setProperty('box-shadow', 'none', 'important');
    }
  };
  let pending = 0;
  const later = () => { if (pending) return; pending = setTimeout(() => { pending = 0; flatten(); }, 600); };
  const start = () => {
    flatten();
    new MutationObserver(later).observe(document.body, { childList: true, subtree: true });
    addEventListener('resize', later);
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
""";
}
