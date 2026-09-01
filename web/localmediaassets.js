// LocalMediaAssets 详情页优化（合并版）
// 功能：
//   1) 剧照区块（lmaStillsSection）+ 预告片独立区块（lmaTrailerSection），灯箱看图、弹窗播预告片
//   2) 演员卡片替换（lmaActorSection）：绿光=本地有完整信息跳详情页；红光=点击触发刷新
// 设计要点：
//   - 两个功能共用一个 1 秒调度器、一套定位/排序/工具代码，避免重复轮询与重复逻辑
//   - 冷却机制：功能被关闭/插件禁用/无素材时停止轮询，hash 变化或页面恢复时立即恢复
//   - 竞态防护：异步回调校验当前详情页 id 与请求时一致，防止快速切换影片时串页
//   - 幂等：window.__LMA_LOADED__ 防止旧版标签与新版标签并存时重复加载
(function () {
    'use strict';
    if (window.__LMA_LOADED__) return;
    window.__LMA_LOADED__ = true;

    var DEBUG = location.search.indexOf('lmaDebug') !== -1;

    // ---------- 公共工具 ----------
    var lang = (navigator.language || 'zh').toLowerCase().indexOf('zh') === 0 ? 'zh' : 'en';
    var I18N = {
        zh: {
            stillsTitle: '预览图片和剧照',
            trailerTitle: '预告片',
            trailerCardTitle: '点击播放预告片',
            gearText: '⚙ 设置',
            gearTitle: 'LocalMediaAssets 插件设置（保存后自动返回本页）',
            trailerFallback: '预告片',
            close: '关闭',
            remoteUnplayable: '该预告片为外部链接，无法内嵌播放。',
            openNewTab: '点击在新标签打开',
            stillTitle: '点击放大',
            lightboxClose: '✕ 关闭',
            lightboxAlt: '预览图',
            actorTitle: '演员',
            refreshTitle: '本地暂无演员信息，点击触发刷新',
            openTitle: '点击查看详情',
            refreshing: '刷新中…',
            done: '已更新'
        },
        en: {
            stillsTitle: 'Preview Images & Stills',
            trailerTitle: 'Trailers',
            trailerCardTitle: 'Play trailer',
            gearText: '⚙ Settings',
            gearTitle: 'LocalMediaAssets settings (auto return after save)',
            trailerFallback: 'Trailer',
            close: 'Close',
            remoteUnplayable: 'This trailer is an external link and cannot be embedded.',
            openNewTab: 'Open in new tab',
            stillTitle: 'Click to enlarge',
            lightboxClose: '✕ Close',
            lightboxAlt: 'Preview',
            actorTitle: 'Cast',
            refreshTitle: 'No local data, click to refresh',
            openTitle: 'Click for details',
            refreshing: 'Refreshing…',
            done: 'Updated'
        }
    };
    function t(key) { return (I18N[lang] || I18N.zh)[key]; }
    function setLang(l) { lang = (l === 'en') ? 'en' : 'zh'; }

    // HTML 转义：文件名/名称可能含特殊字符，防止 XSS
    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function log(msg) { console.log('[LocalMediaAssets] ' + msg); }

    function debugBadge(text) {
        if (!DEBUG) return;
        var el = document.getElementById('lmaDebugBadge');
        if (!el) {
            el = document.createElement('div');
            el.id = 'lmaDebugBadge';
            el.style.cssText = 'position:fixed;bottom:10px;right:10px;z-index:99999;background:#c00;color:#fff;font-size:12px;padding:6px 10px;border-radius:4px;max-width:70%;';
            document.body.appendChild(el);
        }
        el.textContent = text;
    }

    function getItemId() {
        var m = location.hash.match(/id=([^&]+)/);
        return m ? decodeURIComponent(m[1]) : null;
    }

    function getToken() {
        try {
            var raw = localStorage.getItem('jellyfin_credentials');
            if (!raw) return null;
            var data = JSON.parse(raw);
            var servers = data && (data.Servers || []);
            for (var i = 0; i < servers.length; i++) {
                var token = servers[i] && (servers[i].AccessToken || servers[i].accessToken);
                if (token) return token;
            }
        } catch (e) {}
        return null;
    }

    // 当前用户 ID（用于应用每用户显示偏好）
    function getUserId() {
        try {
            var raw = localStorage.getItem('jellyfin_credentials');
            if (!raw) return '';
            var data = JSON.parse(raw);
            var servers = data && (data.Servers || []);
            for (var i = 0; i < servers.length; i++) {
                var u = servers[i] && (servers[i].UserId || servers[i].userId);
                if (u) return u;
            }
        } catch (e) {}
        return '';
    }

    // 兼容 API 返回的 PascalCase / camelCase
    function prop(obj, name) {
        if (obj == null) return undefined;
        if (obj[name] !== undefined) return obj[name];
        var lower = name.charAt(0).toLowerCase() + name.slice(1);
        if (obj[lower] !== undefined) return obj[lower];
        var upper = name.charAt(0).toUpperCase() + name.slice(1);
        return obj[upper];
    }

    function isDetailPage() {
        return location.hash.indexOf('/details?id=') !== -1;
    }

    // ---------- 统一调度与冷却 ----------
    // 冷却：功能关闭/插件禁用/无素材时停止无意义轮询；hash 变化、页面恢复时立即解除
    var coolDownUntil = 0;
    function coolDown(ms) { coolDownUntil = Date.now() + ms; }
    function isCooling() { return Date.now() < coolDownUntil; }
    function resetCoolDown() {
        coolDownUntil = 0;
        lastRenderedId = null; // 强制重渲染（设置/条目可能已变化）
    }

    var lastRenderedId = null;
    var lastAttempt = {}; // itemId -> timestamp，防失败时频繁重试

    // ---------- 皮肤兼容的详情页定位（多级回退） ----------

    // 按标题文字找「演员/幕后」区块（兼容自定义皮肤不同的 id/class）
    function findCastSection() {
        var el = document.getElementById('castCollapsible');
        if (el) return el;

        var headers = document.querySelectorAll('h2, h3, .sectionTitle');
        for (var i = 0; i < headers.length; i++) {
            var text = (headers[i].textContent || '');
            if (/演员|幕后|演职员|cast|crew|people/i.test(text)) {
                var sec = headers[i].closest('.verticalSection, .detailSection, section');
                if (sec) return sec;
            }
        }
        return null;
    }

    // 找「电影信息/简介」区块
    function findOverviewSection() {
        var primary = document.querySelector('.detailPagePrimaryContent');
        if (primary) {
            var first = primary.querySelector('.detailSection');
            if (first) return first;
        }

        var overview = document.querySelector('.overview, .itemOverview');
        if (overview) {
            var sec = overview.closest('.verticalSection, .detailSection, section');
            if (sec) return sec;
        }
        return null;
    }

    // 详情内容最顶部
    function findContentTop() {
        var primary = document.querySelector('.detailPagePrimaryContent');
        if (primary) return primary.firstElementChild || primary;
        var page = document.querySelector('.mainAnimatedPage, [data-type="detail"]');
        if (page) return page.firstElementChild || page;
        return null;
    }

    // 详情内容容器（用于追加到底部）
    function findContentContainer() {
        var primary = document.querySelector('.detailPagePrimaryContent');
        if (primary) return primary;
        var page = document.querySelector('.mainAnimatedPage, [data-type="detail"]');
        if (page) return page;
        return document.body;
    }

    /**
     * 根据位置配置决定插入点（兼容旧位置字段；当前位置统一由区块排序决定）。
     * 返回 { node, mode }：mode='before' 表示插到 node 前面；mode='append' 表示追加到 node 末尾。
     */
    function findInsertion(position) {
        if (position === 'Top') {
            var t = findContentTop();
            if (t) return { node: t, mode: 'before' };
        } else if (position === 'AboveOverview') {
            var o = findOverviewSection();
            if (o) return { node: o, mode: 'before' };
        } else if (position === 'Bottom') {
            var c = findContentContainer();
            if (c) return { node: c, mode: 'append' };
        }

        // 默认/回退链：简介之后（紧跟简介，避免双栏布局下简介下方出现海报卡造成的空白）
        // → 演员卡片区上方 → 演员区上方 → 简介上方 → 内容顶部
        var ov = findOverviewSection();
        if (ov) return { node: ov, mode: 'after' };
        var lmaActors = document.getElementById('lmaActorSection');
        if (lmaActors && lmaActors.parentNode) return { node: lmaActors, mode: 'before' };
        var cast = findCastSection();
        if (cast) return { node: cast, mode: 'before' };
        var top = findContentTop();
        if (top) return { node: top, mode: 'before' };
        return null;
    }

    // ---------- 详情页区块排序（单一实现，两模块共用） ----------
    // order 形如 "Overview,Stills,Trailers,Actors"；只重排已存在的区块，放入详情页主容器。
    function applySectionOrder(order) {
        var seq = String(order || 'Overview,Stills,Trailers,Actors').split(',').map(function (s) { return s.trim(); });
        if (!seq.length) return;

        var overview = document.querySelector('.detailPagePrimaryContent .detailSection');
        var stills = document.getElementById('lmaStillsSection');
        var trailers = document.getElementById('lmaTrailerSection');
        var actors = document.getElementById('lmaActorSection');
        var blocks = { Overview: overview, Stills: stills, Trailers: trailers, Actors: actors };

        var container = null;
        if (overview && overview.parentNode) container = overview.parentNode;
        else if (stills && stills.parentNode) container = stills.parentNode;
        else if (trailers && trailers.parentNode) container = trailers.parentNode;
        else if (actors && actors.parentNode) container = actors.parentNode;
        if (!container) return;

        var anchor = null;
        for (var i = 0; i < seq.length; i++) {
            var block = blocks[seq[i]];
            if (!block || block.parentNode !== container) continue;
            if (anchor === null) {
                container.insertBefore(block, container.firstChild);
            } else {
                container.insertBefore(block, anchor.nextSibling);
            }
            anchor = block;
        }
    }

    // ================================================================
    // 模块一：剧照 + 预告片
    // ================================================================
    var SECTION_ID = 'lmaStillsSection';
    var TRAILER_SECTION_ID = 'lmaTrailerSection';
    var GEAR_ID = 'lmaGearBtn';

    // 弹窗播放预告片（不跳转新标签页）
    function openTrailerModal(trailer) {
        var modal = document.getElementById('lmaModal');
        if (modal) modal.remove();

        var body;
        if (trailer.isRemote && trailer.url) {
            // 只允许 http/https 链接（元数据中的 URL 可能被恶意写入，阻止 javascript: 等 scheme）
            var safeUrl = /^https?:\/\//i.test(trailer.url) ? trailer.url : '';
            var yid = safeUrl ? youtubeId(safeUrl) : null;
            if (yid) {
                body = '<iframe src="https://www.youtube.com/embed/' + yid + '?autoplay=1" ' +
                    'style="width:100%;aspect-ratio:16/9;border:0;border-radius:8px;background:#000;" ' +
                    'allow="autoplay; encrypted-media; picture-in-picture" allowfullscreen></iframe>';
            } else {
                body = '<div style="color:#ccc;padding:24px;text-align:center;">' + t('remoteUnplayable');
                if (safeUrl) {
                    body += '<br><br><a href="' + esc(safeUrl) + '" target="_blank" rel="noopener" style="color:#00a4dc;">' + t('openNewTab') + '</a>';
                }
                body += '</div>';
            }
        } else {
            var token = getToken();
            var src = trailer.streamUrl || trailer.StreamUrl ||
                ('/Videos/' + encodeURIComponent(trailer.itemId) + '/stream?static=true');
            if (token) src += (src.indexOf('?') >= 0 ? '&' : '?') + 'api_key=' + encodeURIComponent(token);
            body = '<video controls autoplay style="width:100%;max-height:80vh;background:#000;border-radius:8px;" src="' + esc(src) + '"></video>';
        }

        modal = document.createElement('div');
        modal.id = 'lmaModal';
        modal.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.88);z-index:100000;display:flex;align-items:center;justify-content:center;';
        modal.innerHTML = '<div style="width:min(92vw,1100px);">' + body +
            '<div style="text-align:center;margin-top:10px;">' +
            '<button id="lmaModalClose" style="padding:8px 24px;background:#333;color:#eee;border:0;border-radius:6px;cursor:pointer;">' + t('close') + '</button>' +
            '</div></div>';
        document.body.appendChild(modal);

        var video = modal.querySelector('video');
        function close() {
            if (video) video.pause();
            modal.remove();
        }
        modal.addEventListener('click', function (e) { if (e.target === modal) close(); });
        modal.querySelector('#lmaModalClose').addEventListener('click', close);
        document.addEventListener('keydown', function onEsc(e) {
            if (e.key === 'Escape' && document.getElementById('lmaModal')) {
                close();
                document.removeEventListener('keydown', onEsc);
            }
        });
    }

    // 从 YouTube 各类链接中提取视频 ID
    function youtubeId(url) {
        var m = url.match(/(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([\w-]{11})/);
        return m ? m[1] : null;
    }

    // 同页灯箱：大图浏览 + 上一张/下一张 + 键盘操作
    function openLightbox(urls, index) {
        var lb = document.getElementById('lmaLightbox');
        if (lb) lb.remove();

        lb = document.createElement('div');
        lb.id = 'lmaLightbox';
        lb.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.92);z-index:100001;display:flex;align-items:center;justify-content:center;flex-direction:column;';

        var img = document.createElement('img');
        img.style.cssText = 'max-width:92vw;max-height:80vh;border-radius:8px;box-shadow:0 0 30px rgba(0,0,0,.6);cursor:pointer;';
        img.alt = t('lightboxAlt');

        var counter = document.createElement('div');
        counter.style.cssText = 'color:#ccc;margin-top:12px;font-size:14px;';

        var prevBtn = document.createElement('button');
        prevBtn.textContent = '‹';
        prevBtn.style.cssText = 'position:fixed;left:16px;top:50%;transform:translateY(-50%);font-size:36px;background:none;border:0;color:#ccc;cursor:pointer;padding:10px;';
        var nextBtn = document.createElement('button');
        nextBtn.textContent = '›';
        nextBtn.style.cssText = 'position:fixed;right:16px;top:50%;transform:translateY(-50%);font-size:36px;background:none;border:0;color:#ccc;cursor:pointer;padding:10px;';

        var closeBtn = document.createElement('button');
        closeBtn.textContent = t('lightboxClose');
        closeBtn.style.cssText = 'position:fixed;top:14px;right:16px;background:#333;color:#eee;border:0;border-radius:6px;cursor:pointer;padding:8px 14px;';

        lb.appendChild(img);
        lb.appendChild(counter);
        lb.appendChild(prevBtn);
        lb.appendChild(nextBtn);
        lb.appendChild(closeBtn);
        document.body.appendChild(lb);

        var current = 0;
        function show(i) {
            if (!urls.length) return;
            if (i < 0) i = urls.length - 1;
            if (i >= urls.length) i = 0;
            current = i;
            img.src = urls[i];
            counter.textContent = (i + 1) + ' / ' + urls.length;
        }
        function close() {
            lb.remove();
            document.removeEventListener('keydown', onKey);
        }
        function onKey(e) {
            if (e.key === 'Escape') close();
            else if (e.key === 'ArrowLeft') show(current - 1);
            else if (e.key === 'ArrowRight') show(current + 1);
        }

        img.addEventListener('click', function () { show(current + 1); });
        prevBtn.addEventListener('click', function (e) { e.stopPropagation(); show(current - 1); });
        nextBtn.addEventListener('click', function (e) { e.stopPropagation(); show(current + 1); });
        closeBtn.addEventListener('click', close);
        lb.addEventListener('click', function (e) { if (e.target === lb) close(); });
        document.addEventListener('keydown', onKey);

        show(index);
    }

    // 设置入口按钮（右下角）
    function ensureGearButton() {
        if (document.getElementById(GEAR_ID)) return;
        var a = document.createElement('a');
        a.id = GEAR_ID;
        a.href = '/LocalMediaAssets/config';
        a.title = t('gearTitle');
        a.textContent = t('gearText');
        a.style.cssText = 'position:fixed;bottom:14px;right:14px;z-index:99999;background:rgba(0,0,0,.55);color:#eee;' +
            'font-size:13px;padding:6px 12px;border-radius:20px;text-decoration:none;opacity:.55;';
        a.style.transition = 'opacity .2s';
        a.addEventListener('mouseenter', function () { a.style.opacity = '1'; });
        a.addEventListener('mouseleave', function () { a.style.opacity = '.55'; });
        document.body.appendChild(a);
    }

    function removeGearButton() {
        var el = document.getElementById(GEAR_ID);
        if (el) el.remove();
    }

    function removeStillsSections() {
        var el = document.getElementById(SECTION_ID);
        if (el) el.remove();
        var tr = document.getElementById(TRAILER_SECTION_ID);
        if (tr) tr.remove();
    }

    // 剧照+预告片渲染；itemId 为发起请求时的条目（用于竞态校验）
    function renderStills(itemId, force) {
        var now = Date.now();
        var last = lastAttempt['s:' + itemId] || 0;
        if (!force && now - last < 3000) return; // 同一条目失败后最多 3 秒重试一次
        lastAttempt['s:' + itemId] = now;

        debugBadge('fetching stills for ' + itemId);
        fetch('/LocalMediaAssets/Stills?itemId=' + encodeURIComponent(itemId) + '&userId=' + encodeURIComponent(getUserId()))
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                // 竞态防护：请求期间用户已切到别的条目，丢弃本次结果
                if (getItemId() !== itemId) return;

                // 功能对该用户整体关闭（剧照+预告片都关）→ 冷却，不再轮询
                if (prop(data, 'enabled') === false) {
                    removeStillsSections();
                    coolDown(60000);
                    debugBadge('stills disabled');
                    return;
                }

                var list = data && (data.stills || data.Stills);
                var trailers = data && (data.trailers || data.Trailers) || [];
                var position = data.position || data.Position || 'AboveCast';

                // 按插件配置更新界面语言（跟随 Jellyfin 语言或自定义）
                setLang(prop(data, 'lang'));
                var gear = document.getElementById(GEAR_ID);
                if (gear) { gear.textContent = t('gearText'); gear.title = t('gearTitle'); }

                if ((!list || !list.length) && (!trailers || !trailers.length)) {
                    // 无素材（但功能开启）：移除旧区块并短暂冷却，避免每 3 秒空轮询
                    removeStillsSections();
                    coolDown(30000);
                    debugBadge('no stills for ' + itemId);
                    return;
                }

                var insertion = findInsertion(position);
                if (!insertion) {
                    debugBadge('target section not found (' + position + ')');
                    log('target section not found (' + position + ')');
                    return;
                }

                removeStillsSections();

                var styleBlock = '<style>@keyframes lmaFadeIn{from{opacity:0}to{opacity:1}}' +
                    '@keyframes lmaSpin{to{transform:rotate(360deg)}}' +
                    '.lmaSpin{position:absolute;inset:0;margin:auto;width:34px;height:34px;' +
                    'border:3px solid #333;border-top-color:#00a4dc;border-radius:50%;' +
                    'animation:lmaSpin .8s linear infinite;}</style>';

                // ---------- 预告片区块（独立区块，可独立排序/开关） ----------
                var trailerSection = null;
                if (trailers.length) {
                    trailerSection = document.createElement('div');
                    trailerSection.id = TRAILER_SECTION_ID;
                    trailerSection.className = 'lmaSection';
                    trailerSection.style.cssText = 'animation:lmaFadeIn 0.4s ease;margin:1.4em 0 1.2em;';

                    var tPerRow = parseInt(prop(data, 'trailersPerRow'), 10) || 0;
                    var tTemplate = tPerRow > 0
                        ? 'repeat(' + tPerRow + ',minmax(0,1fr))'
                        : 'repeat(auto-fill,minmax(240px,1fr))';
                    var thtml = styleBlock;
                    thtml += '<h2 class="sectionTitle sectionTitle-cards padded-right">' + t('trailerTitle') + '</h2>';
                    thtml += '<div style="display:grid;grid-template-columns:' + tTemplate + ';gap:10px;padding:0 2.5%;">';
                    trailers.forEach(function (tr) {
                        var id = tr.itemId || tr.ItemId;
                        var url = tr.url || tr.Url;
                        var nm = tr.name || tr.Name || t('trailerFallback');
                        var isRemote = tr.isRemote || tr.IsRemote;
                        thtml += '<div class="lmaTrailerCard" data-id="' + esc(id || '') + '" data-url="' + esc(url || '') + '" data-remote="' + (isRemote ? '1' : '0') + '" data-stream="' + esc(tr.streamUrl || tr.StreamUrl || '') + '" title="' + t('trailerCardTitle') + '" style="cursor:pointer;">' +
                            '<div style="position:relative;width:100%;aspect-ratio:16/9;background:#111;border-radius:6px;display:flex;align-items:center;justify-content:center;border:1px solid #333;">' +
                            '<span style="font-size:40px;color:#fff;opacity:.85;">▶</span>' +
                            '<span style="position:absolute;left:8px;bottom:6px;right:8px;font-size:12px;color:#ccc;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + esc(nm) + '</span>' +
                            '</div></div>';
                    });
                    thtml += '</div>';
                    trailerSection.innerHTML = thtml;

                    // 预告片卡片点击 → 弹窗播放
                    var cards = trailerSection.querySelectorAll('.lmaTrailerCard');
                    for (var ci = 0; ci < cards.length; ci++) {
                        (function (card) {
                            card.addEventListener('click', function () {
                                openTrailerModal({
                                    itemId: card.getAttribute('data-id'),
                                    url: card.getAttribute('data-url'),
                                    streamUrl: card.getAttribute('data-stream'),
                                    isRemote: card.getAttribute('data-remote') === '1'
                                });
                            });
                        })(cards[ci]);
                    }
                }

                // ---------- 剧照区块 ----------
                var section = null;
                if (list && list.length) {
                    section = document.createElement('div');
                    section.id = SECTION_ID;
                    // 自定义布局类（不用 detailVerticalSection：它带 margin-bottom:3.4em!important 会造成大空白）
                    section.className = 'lmaSection';
                    section.style.cssText = 'animation:lmaFadeIn 0.4s ease;margin:1.4em 0 1.2em;';

                    var html = styleBlock;
                    html += '<h2 class="sectionTitle sectionTitle-cards padded-right">' + t('stillsTitle') + '</h2>';

                    // 剧照网格：配置每行数量或自适应
                    var stillUrls = [];
                    var stillIdx = 0;
                    var sPerRow = parseInt(prop(data, 'stillsPerRow'), 10) || 0;
                    var sTemplate = sPerRow > 0
                        ? 'repeat(' + sPerRow + ',minmax(0,1fr))'
                        : 'repeat(auto-fill,minmax(150px,1fr))';
                    // 唯一转圈：所有剧照加载完成（或失败）后整体隐藏，避免满屏转圈
                    html += '<div id="lmaGridSpinWrap" style="display:flex;justify-content:center;padding:12px 0;"><span class="lmaSpin"></span></div>';
                    html += '<div style="display:grid;grid-template-columns:' + sTemplate + ';gap:10px;padding:0 2.5%;">';

                    (list || []).forEach(function (s) {
                        var name = s.name || s.Name;
                        if (!name) return;
                        var imgUrl = '/LocalMediaAssets/Stills/Image?itemId=' + encodeURIComponent(itemId) +
                            '&name=' + encodeURIComponent(name);
                        stillUrls.push(imgUrl);
                        html += '<div class="lmaStill" data-index="' + (stillIdx++) + '" title="' + t('stillTitle') + '" style="cursor:zoom-in;display:block;position:relative;">' +
                            '<img src="' + imgUrl + '" alt="' + esc(name) + '" loading="lazy" ' +
                            'onerror="this.style.display=\'none\';" ' +
                            'style="width:100%;aspect-ratio:16/9;object-fit:cover;border-radius:6px;display:block;background:#202020;position:relative;z-index:1;" />' +
                            '</div>';
                    });

                    html += '</div>';
                    section.innerHTML = html;

                    // 剧照点击 → 同页灯箱浏览
                    var stills = section.querySelectorAll('.lmaStill');
                    for (var si = 0; si < stills.length; si++) {
                        (function (el, i) {
                            el.addEventListener('click', function () {
                                openLightbox(stillUrls, i);
                            });
                        })(stills[si], parseInt(stills[si].getAttribute('data-index'), 10) || si);
                    }
                }

                // 插入区块：预告片 + 剧照（顺序由 applySectionOrder 最终决定）
                var blocksToInsert = [];
                if (trailerSection) blocksToInsert.push(trailerSection);
                if (section) blocksToInsert.push(section);
                for (var bi = 0; bi < blocksToInsert.length; bi++) {
                    var blk = blocksToInsert[bi];
                    if (insertion.mode === 'append') {
                        insertion.node.appendChild(blk);
                    } else if (insertion.mode === 'after') {
                        insertion.node.parentNode.insertBefore(blk, insertion.node.nextSibling);
                    } else {
                        insertion.node.parentNode.insertBefore(blk, insertion.node);
                    }
                }

                // 唯一转圈收尾：统计所有剧照图片，全部加载完成或失败后隐藏转圈
                if (section) {
                    (function () {
                        var imgs = section.querySelectorAll('.lmaStill img');
                        var total = imgs.length;
                        var doneCount = 0;
                        var hideSpin = function () {
                            var wrap = document.getElementById('lmaGridSpinWrap');
                            if (wrap) wrap.style.display = 'none';
                        };
                        if (total === 0) { hideSpin(); return; }
                        var tick = function () {
                            doneCount++;
                            if (doneCount >= total) hideSpin();
                        };
                        for (var ii = 0; ii < total; ii++) {
                            (function (im) {
                                // 缓存命中时 load 可能已触发过，complete 为 true 则直接计数
                                if (im.complete) { tick(); return; }
                                im.addEventListener('load', tick);
                                im.addEventListener('error', tick);
                            })(imgs[ii]);
                        }
                    })();
                }

                debugBadge('stills rendered (' + ((list && list.length) || 0) + ')');
                log('rendered ' + ((list && list.length) || 0) + ' stills for ' + itemId + ' at ' + position);

                // 按配置顺序重排（简介/预览图片和剧照/预告片/演员）
                applySectionOrder(prop(data, 'sectionOrder'));
            })
            .catch(function (e) {
                log('fetch failed: ' + e.message);
                debugBadge('fetch failed: ' + e.message);
            });
    }

    // ================================================================
    // 模块二：演员卡片替换
    // ================================================================
    var ACTOR_SECTION_ID = 'lmaActorSection';
    var pollers = {}; // actorName -> { timer, startedAt }

    function clearPoller(actorName) {
        var p = pollers[actorName];
        if (p && p.timer) {
            clearInterval(p.timer);
            delete pollers[actorName];
        }
    }

    function clearAllPollers() {
        Object.keys(pollers).forEach(clearPoller);
    }

    // 红光卡片点击 → 触发刷新 → 呼吸 → 轮询直到有本地信息或超时
    function startRefresh(itemId, actorName, card) {
        var statusEl = card.querySelector('.lmaActorStatus');
        if (card.classList.contains('lma-refreshing')) return; // 已在刷新

        card.classList.add('lma-refreshing');
        card.style.pointerEvents = 'none';
        if (statusEl) statusEl.textContent = t('refreshing');

        var token = getToken();
        fetch('/LocalMediaAssets/Actor/Refresh', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                ...(token ? { 'Authorization': 'MediaBrowser Token="' + token + '"' } : {})
            },
            body: JSON.stringify({ actorName: actorName })
        }).catch(function (e) {
            log('refresh request failed: ' + e.message);
        });

        // 轮询该演员状态
        var startedAt = Date.now();
        clearPoller(actorName);
        pollers[actorName] = {
            startedAt: startedAt,
            timer: setInterval(function () {
                fetch('/LocalMediaAssets/Actor/Status?itemId=' + encodeURIComponent(itemId))
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (data) {
                        if (!data) return;
                        var actor = (data.actors || data.Actors || []).find(function (a) {
                            return (prop(a, 'name') || '') === actorName;
                        });
                        if (!actor) return;

                        if (prop(actor, 'hasLocalInfo')) {
                            // 刷新完成：变绿（把 div 升级为可跳转的 <a>）
                            clearPoller(actorName);
                            card.classList.remove('lma-refreshing');
                            card.style.pointerEvents = 'auto';
                            card.setAttribute('data-has-info', '1');
                            card.title = t('openTitle');
                            if (card.tagName === 'DIV') {
                                var a = document.createElement('a');
                                a.className = card.className;
                                a.setAttribute('data-has-info', '1');
                                a.setAttribute('data-name', card.getAttribute('data-name'));
                                a.href = '/LocalMediaAssets/ActorPage?name=' + encodeURIComponent(actorName) +
                                    '&from=' + encodeURIComponent(itemId);
                                a.title = t('openTitle');
                                a.style.cssText = 'text-decoration:none;color:inherit;display:flex;flex-direction:column;align-items:center;';
                                // 移动子节点（头像/名字/状态）
                                while (card.firstChild) a.appendChild(card.firstChild);
                                card.parentNode.replaceChild(a, card);
                                card = a;
                                statusEl = a.querySelector('.lmaActorStatus');
                            }
                            if (statusEl) statusEl.textContent = t('done');
                            var avatar = card.querySelector('.lmaActorAvatar');
                            if (avatar && prop(actor, 'avatarUrl')) avatar.src = prop(actor, 'avatarUrl');
                            setTimeout(function () {
                                if (statusEl && statusEl.textContent === t('done')) statusEl.textContent = '';
                            }, 3000);
                        } else if (Date.now() - startedAt > 60000) {
                            // 超时：恢复红光，允许重试
                            clearPoller(actorName);
                            card.classList.remove('lma-refreshing');
                            card.style.pointerEvents = 'auto';
                            if (statusEl) statusEl.textContent = '';
                        }
                    })
                    .catch(function () {});
            }, 2500)
        };
    }

    // 恢复 Jellyfin 默认演员区（开关关闭/离开详情页时）
    function restoreDefaultCast() {
        var cast = findCastSection();
        if (cast && cast.getAttribute('data-lma-hidden')) {
            cast.style.display = cast.getAttribute('data-lma-display') || '';
            cast.classList.add('hide'); // 恢复原 class 状态（Jellyfin 自行控制显示）
            cast.removeAttribute('data-lma-hidden');
            cast.removeAttribute('data-lma-display');
        }
    }

    function removeActorSection() {
        var el = document.getElementById(ACTOR_SECTION_ID);
        if (el) el.remove();
    }

    // 演员卡片渲染；itemId 为发起请求时的条目（用于竞态校验）
    function renderActors(itemId, force) {
        var now = Date.now();
        var last = lastAttempt['a:' + itemId] || 0;
        if (!force && now - last < 3000) return;
        lastAttempt['a:' + itemId] = now;

        fetch('/LocalMediaAssets/Actor/Status?itemId=' + encodeURIComponent(itemId) + '&userId=' + encodeURIComponent(getUserId()))
            .then(function (r) {
                if (r.status === 503) {
                    // 插件已被禁用：恢复 Jellyfin 默认演员区，长时间冷却，停止一切轮询
                    restoreDefaultCast();
                    removeActorSection();
                    removeStillsSections();
                    clearAllPollers();
                    coolDown(120000);
                    debugBadge('plugin disabled');
                    return null;
                }
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                if (!data) return; // 插件禁用时已恢复默认
                // 竞态防护：请求期间用户已切到别的条目，丢弃本次结果
                if (getItemId() !== itemId) return;

                var enabled = !!prop(data, 'enabled');
                if (!enabled) {
                    // 开关关闭/数据库关闭：恢复默认演员区并冷却，避免每 3 秒空轮询
                    restoreDefaultCast();
                    removeActorSection();
                    coolDown(60000);
                    debugBadge('actors disabled');
                    return;
                }

                var actors = (prop(data, 'actors') || []).filter(function (a) { return prop(a, 'name'); });
                if (!actors.length) {
                    restoreDefaultCast();
                    removeActorSection();
                    coolDown(30000); // 无演员：短暂冷却，hash 变化立即恢复
                    debugBadge('no actors');
                    return;
                }

                var cast = findCastSection();
                if (!cast) {
                    debugBadge('cast section not found');
                    return;
                }

                // 配置：每排演员数量（0=自适应）；头像直径固定
                var perRow = parseInt(prop(data, 'actorCardsPerRow'), 10) || 0;
                var size = 104;
                var order = prop(data, 'sectionOrder') || 'Overview,Stills,Trailers,Actors';

                // 等 Jellyfin 把演员卡片填进 castContent（异步 chunk 加载），避免空白区
                var castContent = document.getElementById('castContent');
                var hasContent = castContent && castContent.children && castContent.children.length > 0;
                if (!hasContent && !force) {
                    debugBadge('waiting cast content');
                    return;
                }

                // 屏蔽 Jellyfin 默认演员卡片：隐藏原区块（同时去掉 hide class，防止 Jellyfin 重新显示）
                if (!cast.getAttribute('data-lma-hidden')) {
                    cast.setAttribute('data-lma-hidden', '1');
                    cast.setAttribute('data-lma-display', cast.style.display || '');
                    cast.classList.remove('hide'); // 阻止 Jellyfin 的 class 显隐与我们的 display 冲突
                    cast.style.display = 'none';
                }

                removeActorSection();
                clearAllPollers();

                var section = document.createElement('div');
                section.id = ACTOR_SECTION_ID;
                // 自定义布局类（不用 detailVerticalSection：它带 margin-bottom:3.4em!important 会造成大空白）
                section.className = 'lmaSection';
                section.style.cssText = 'animation:lmaActorsFadeIn 0.4s ease;margin:0 0 1.2em;';

                // 每排数量固定列数；0=按头像尺寸自适应（grid auto-fill）
                var rowTemplate = perRow > 0
                    ? 'repeat(' + perRow + ',minmax(0,1fr))'
                    : 'repeat(auto-fill,minmax(' + Math.max(96, size + 24) + 'px,1fr))';

                var html = '<style>' +
                    '@keyframes lmaActorsFadeIn{from{opacity:0}to{opacity:1}}' +
                    '.lmaActorRow{display:grid;grid-template-columns:' + rowTemplate + ';gap:16px;padding:12px 2.5% 16px;justify-items:center;}' +
                    '.lmaActorCard{display:flex;flex-direction:column;align-items:center;cursor:pointer;' +
                    'transition:transform 0.3s ease;min-width:0;}' +
                    '.lmaActorCard:hover{transform:translateY(-6px);}' +
                    '.lmaActorAvatarWrap{position:relative;width:' + size + 'px;height:' + size + 'px;border-radius:50%;' +
                    'background:#202020;flex-shrink:0;}' +
                    '.lmaActorAvatar{width:100%;height:100%;object-fit:cover;border-radius:50%;display:block;position:relative;z-index:1;}' +
                    // 光晕放在头像之下（z-index:0），用 box-shadow 向四周发光；
                    // 不设负偏移、不依赖溢出，避免被 overflow-x 裁剪
                    '.lmaActorGlow{position:absolute;top:0;left:0;right:0;bottom:0;border-radius:50%;' +
                    'pointer-events:none;box-shadow:0 0 18px 7px rgba(244,67,54,0.55);z-index:0;}' +
                    '.lmaActorCard[data-has-info="1"] .lmaActorGlow{' +
                    'box-shadow:0 0 18px 7px rgba(76,175,80,0.75);}' +
                    '.lmaActorCard.lma-refreshing .lmaActorGlow{' +
                    'animation:lmaActorsBreathe 1.6s ease-in-out infinite;}' +
                    '@keyframes lmaActorsBreathe{' +
                    '0%,100%{box-shadow:0 0 18px 7px rgba(244,67,54,0.55);}' +
                    '50%{box-shadow:0 0 30px 16px rgba(244,67,54,0.95);}}' +
                    '.lmaActorName{max-width:' + (size + 10) + 'px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;' +
                    'font-size:13px;color:#ccc;text-align:center;margin-top:8px;}' +
                    '.lmaActorStatus{font-size:11px;color:#888;height:14px;text-align:center;margin-top:2px;}' +
                    '</style>';
                html += '<h2 class="sectionTitle sectionTitle-cards padded-right">' + t('actorTitle') + '</h2>';
                html += '<div class="lmaActorRow">';

                actors.forEach(function (a) {
                    var hasInfo = !!prop(a, 'hasLocalInfo');
                    var avatar = prop(a, 'avatarUrl') || ('/LocalMediaAssets/Actor/Image?name=' + encodeURIComponent(prop(a, 'name')));
                    var actorName = prop(a, 'name');
                    // 绿光：真实 <a> 标签跳转（与设置页一致，浏览器原生导航最可靠）；
                    // 红光：div + 点击触发刷新
                    var tag = hasInfo ? 'a' : 'div';
                    var attrs = hasInfo
                        ? 'href="/LocalMediaAssets/ActorPage?name=' + encodeURIComponent(actorName) +
                          '&from=' + encodeURIComponent(itemId) + '" ' +
                          'data-role="lma-open-actor"'
                        : 'data-role="lma-refresh-actor"';
                    html += '<' + tag + ' class="lmaActorCard" data-has-info="' + (hasInfo ? '1' : '0') + '" ' +
                        'data-name="' + esc(actorName) + '" title="' + (hasInfo ? t('openTitle') : t('refreshTitle')) + '" ' +
                        'style="text-decoration:none;color:inherit;' + (hasInfo ? 'display:flex;flex-direction:column;align-items:center;' : '') + '" ' + attrs + '>' +
                        '<div class="lmaActorAvatarWrap">' +
                        '<img class="lmaActorAvatar" src="' + avatar + '" alt="' + esc(actorName) + '" loading="lazy" ' +
                        'onerror="this.src=\'/LocalMediaAssets/Actor/Image?name=\'+encodeURIComponent(this.closest(\'.lmaActorCard\').getAttribute(\'data-name\'));" />' +
                        '<div class="lmaActorGlow"></div>' +
                        '</div>' +
                        '<div class="lmaActorName">' + esc(actorName) + '</div>' +
                        '<div class="lmaActorStatus"></div>' +
                        '</' + tag + '>';
                });

                html += '</div>';
                section.innerHTML = html;

                // 点击事件：绿光由 <a> 原生跳转（无需 JS），红光触发刷新
                var cards = section.querySelectorAll('.lmaActorCard[data-role="lma-refresh-actor"]');
                for (var i = 0; i < cards.length; i++) {
                    (function (card) {
                        card.addEventListener('click', function () {
                            var name = card.getAttribute('data-name');
                            if (!name) return;
                            startRefresh(itemId, name, card);
                        });
                    })(cards[i]);
                }

                // 插入位置：优先在剧照区块（lmaStillsSection）之后，保证「剧照在上、演员在下」；
                // 剧照未渲染时插到 cast 前（默认演员区位置）。
                var stillsSection = document.getElementById('lmaStillsSection');
                if (stillsSection && stillsSection.parentNode) {
                    stillsSection.parentNode.insertBefore(section, stillsSection.nextSibling);
                } else {
                    cast.parentNode.insertBefore(section, cast);
                }

                // 按配置顺序重排（简介/预览图片和剧照/预告片/演员）
                applySectionOrder(order);

                debugBadge('actors rendered (' + actors.length + ')');
                log('rendered ' + actors.length + ' actor cards for ' + itemId);
            })
            .catch(function (e) {
                log('fetch failed: ' + e.message);
                debugBadge('fetch failed: ' + e.message);
            });
    }

    // ================================================================
    // 统一主循环（单一调度器）
    // ================================================================
    function loop() {
        if (isCooling()) return;

        if (!isDetailPage()) {
            removeStillsSections();
            removeGearButton();
            restoreDefaultCast();
            removeActorSection();
            clearAllPollers();
            lastRenderedId = null;
            return;
        }

        var id = getItemId();
        if (!id) return;

        ensureGearButton();

        if (id !== lastRenderedId) {
            lastRenderedId = id;
            log('rendering for new item ' + id);
            renderStills(id, true);
            renderActors(id, true);

            // SPA 二次渲染可能把区块冲掉，1.5 秒后再确认一次
            setTimeout(function () {
                if (getItemId() === id && !isCooling()) {
                    if (!document.getElementById(SECTION_ID) && !document.getElementById(TRAILER_SECTION_ID)) {
                        renderStills(id, true);
                    }
                    if (!document.getElementById(ACTOR_SECTION_ID)) {
                        renderActors(id, true);
                    }
                }
            }, 1500);
            return;
        }

        // 区块被冲掉时补渲染（带节流；被冷却的不会走到这里）
        if (!document.getElementById(SECTION_ID) && !document.getElementById(TRAILER_SECTION_ID)) {
            renderStills(id, false);
        }
        if (!document.getElementById(ACTOR_SECTION_ID)) {
            renderActors(id, false);
        }
    }

    setInterval(loop, 1000);
    window.addEventListener('hashchange', function () { resetCoolDown(); setTimeout(loop, 100); });

    // 浏览器「返回」可能从往返缓存(bfcache)恢复旧页面：恢复后强制重新渲染，
    // 确保设置改动（位置、开关）生效
    window.addEventListener('pageshow', function (e) {
        log('pageshow, persisted=' + !!e.persisted);
        resetCoolDown();
        setTimeout(loop, 100);
        setTimeout(loop, 500);
    });

    // 离开页面清理所有刷新轮询定时器
    window.addEventListener('pagehide', clearAllPollers);

    log('script loaded, hash=' + location.hash);
    debugBadge('script loaded');
    setTimeout(loop, 400);
})();
