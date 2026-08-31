// LocalMediaAssets 剧照展示（详情页）
// 由插件 API 提供（/LocalMediaAssets/stillsJs），index.html 通过插件自愈补丁注入一行 script 标签加载。
(function () {
    'use strict';

    var SECTION_ID = 'lmaStillsSection';
    var GEAR_ID = 'lmaGearBtn';
    var DEBUG = location.search.indexOf('lmaDebug') !== -1;

    var lastRenderedId = null;
    var lastAttempt = {}; // itemId -> timestamp，防失败时频繁重试

    // ---------- 界面语言（zh/en） ----------
    // 初始按浏览器语言，首次获取数据后按插件配置（跟随 Jellyfin 语言）更新
    var lang = (navigator.language || 'zh').toLowerCase().indexOf('zh') === 0 ? 'zh' : 'en';
    var I18N = {
        zh: {
            title: '预览图与视频',
            gearText: '⚙ 设置',
            gearTitle: 'LocalMediaAssets 插件设置（保存后自动返回本页）',
            trailerTitle: '点击播放预告片',
            trailerFallback: '预告片',
            close: '关闭',
            remoteUnplayable: '该预告片为外部链接，无法内嵌播放。',
            openNewTab: '点击在新标签打开',
            stillTitle: '点击放大',
            lightboxClose: '✕ 关闭',
            lightboxAlt: '预览图'
        },
        en: {
            title: 'Preview & Videos',
            gearText: '⚙ Settings',
            gearTitle: 'LocalMediaAssets settings (auto return after save)',
            trailerTitle: 'Play trailer',
            trailerFallback: 'Trailer',
            close: 'Close',
            remoteUnplayable: 'This trailer is an external link and cannot be embedded.',
            openNewTab: 'Open in new tab',
            stillTitle: 'Click to enlarge',
            lightboxClose: '✕ Close',
            lightboxAlt: 'Preview'
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

    function log(msg) {
        console.log('[LocalMediaAssets] ' + msg);
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
                var t = servers[i] && (servers[i].AccessToken || servers[i].accessToken);
                if (t) return t;
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
        img.alt = '预览图';

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

    function isDetailPage() {
        return location.hash.indexOf('/details?id=') !== -1;
    }

    function removeSection() {
        var el = document.getElementById(SECTION_ID);
        if (el) el.remove();
    }

    // ---------- 皮肤兼容的定位（多级回退） ----------

    // 按标题文字找「演员/幕后」区块（兼容自定义皮肤不同的 id/class）
    function findCastSection() {
        var el = document.getElementById('castCollapsible');
        if (el) return el;

        var headers = document.querySelectorAll('h2, h3, .sectionTitle');
        for (var i = 0; i < headers.length; i++) {
            var t = (headers[i].textContent || '');
            if (/演员|幕后|演职员|cast|crew|people/i.test(t)) {
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
     * 根据位置配置决定插入点。
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

        // 默认/回退链：演员上方 → 简介上方 → 内容顶部
        var cast = findCastSection();
        if (cast) return { node: cast, mode: 'before' };
        var ov = findOverviewSection();
        if (ov) return { node: ov, mode: 'before' };
        var top = findContentTop();
        if (top) return { node: top, mode: 'before' };
        return null;
    }

    // ---------- 设置入口按钮 ----------

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

    // ---------- 渲染 ----------

    function render(itemId, force) {
        var now = Date.now();
        var last = lastAttempt[itemId] || 0;
        if (!force && now - last < 3000) return; // 同一条目失败后最多 3 秒重试一次
        lastAttempt[itemId] = now;

        debugBadge('fetching stills for ' + itemId);
        fetch('/LocalMediaAssets/Stills?itemId=' + encodeURIComponent(itemId) + '&userId=' + encodeURIComponent(getUserId()))
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                var list = data && (data.stills || data.Stills);
                var trailers = data && (data.trailers || data.Trailers) || [];
                var position = data.position || data.Position || 'AboveCast';

                // 按插件配置更新界面语言（跟随 Jellyfin 语言或自定义）
                setLang(data.lang);
                var gear = document.getElementById(GEAR_ID);
                if (gear) { gear.textContent = t('gearText'); gear.title = t('gearTitle'); }

                if ((!list || !list.length) && (!trailers || !trailers.length)) {
                    // 设置关闭剧照/无预告片无剧照：移除旧区块
                    removeSection();
                    debugBadge('no stills for ' + itemId);
                    return;
                }

                var insertion = findInsertion(position);
                if (!insertion) {
                    debugBadge('target section not found (' + position + ')');
                    log('target section not found (' + position + ')');
                    return;
                }

                removeSection();

                var section = document.createElement('div');
                section.id = SECTION_ID;
                section.className = 'verticalSection detailVerticalSection';
                section.style.cssText = 'animation:lmaFadeIn 0.4s ease;';

                var html = '<style>@keyframes lmaFadeIn{from{opacity:0}to{opacity:1}}' +
                    '@keyframes lmaSpin{to{transform:rotate(360deg)}}' +
                    '.lmaSpin{position:absolute;inset:0;margin:auto;width:34px;height:34px;' +
                    'border:3px solid #333;border-top-color:#00a4dc;border-radius:50%;' +
                    'animation:lmaSpin .8s linear infinite;}</style>';
                html += '<h2 class="sectionTitle sectionTitle-cards padded-right">' + t('title') + '</h2>';

                // 预告片（第一个位置，点击弹窗播放）
                if (trailers.length) {
                    html += '<div style="display:flex;flex-wrap:wrap;gap:10px;padding:0 2.5%;margin-bottom:14px;">';
                    trailers.forEach(function (tr) {
                        var id = tr.itemId || tr.ItemId;
                        var url = tr.url || tr.Url;
                        var nm = tr.name || tr.Name || t('trailerFallback');
                        var isRemote = tr.isRemote || tr.IsRemote;
                        html += '<div class="lmaTrailerCard" data-id="' + esc(id || '') + '" data-url="' + esc(url || '') + '" data-remote="' + (isRemote ? '1' : '0') + '" data-stream="' + esc(tr.streamUrl || tr.StreamUrl || '') + '" title="' + t('trailerTitle') + '" style="flex:1 1 240px;max-width:360px;cursor:pointer;">' +
                            '<div style="position:relative;width:100%;aspect-ratio:16/9;background:#111;border-radius:6px;display:flex;align-items:center;justify-content:center;border:1px solid #333;">' +
                            '<span style="font-size:40px;color:#fff;opacity:.85;">▶</span>' +
                            '<span style="position:absolute;left:8px;bottom:6px;right:8px;font-size:12px;color:#ccc;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">' + esc(nm) + '</span>' +
                            '</div></div>';
                    });
                    html += '</div>';
                }

                // 自适应网格：按屏幕宽度自动决定每行数量（宽屏约 5-6 张，窄屏 2-3 张）
                var stillUrls = [];
                var stillIdx = 0;
                // 唯一转圈：所有剧照加载完成（或失败）后整体隐藏，避免满屏转圈
                html += '<div id="lmaGridSpinWrap" style="display:flex;justify-content:center;padding:12px 0;"><span class="lmaSpin"></span></div>';
                html += '<div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:10px;padding:0 2.5%;">';

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

                // 预告片卡片点击 → 弹窗播放
                var cards = section.querySelectorAll('.lmaTrailerCard');
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

                // 剧照点击 → 同页灯箱浏览
                var stills = section.querySelectorAll('.lmaStill');
                for (var si = 0; si < stills.length; si++) {
                    (function (el, i) {
                        el.addEventListener('click', function () {
                            openLightbox(stillUrls, i);
                        });
                    })(stills[si], parseInt(stills[si].getAttribute('data-index'), 10) || si);
                }

                if (insertion.mode === 'append') {
                    insertion.node.appendChild(section);
                } else {
                    insertion.node.parentNode.insertBefore(section, insertion.node);
                }

                // 唯一转圈收尾：统计所有剧照图片，全部加载完成或失败后隐藏转圈
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

                debugBadge('stills rendered (' + list.length + ')');
                log('rendered ' + list.length + ' stills for ' + itemId + ' at ' + position);
            })
            .catch(function (e) {
                log('fetch failed: ' + e.message);
                debugBadge('fetch failed: ' + e.message);
            });
    }

    // ---------- 持续渲染循环 ----------

    function loop() {
        if (!isDetailPage()) {
            removeSection();
            removeGearButton();
            lastRenderedId = null;
            return;
        }

        var id = getItemId();
        if (!id) return;

        ensureGearButton();

        if (id !== lastRenderedId) {
            lastRenderedId = id;
            log('rendering stills for new item ' + id);
            render(id, true);

            // SPA 二次渲染可能把剧照区冲掉，1.5 秒后再确认一次
            setTimeout(function () {
                if (getItemId() === id && !document.getElementById(SECTION_ID)) {
                    render(id, true);
                }
            }, 1500);
            return;
        }

        if (!document.getElementById(SECTION_ID)) {
            render(id, false);
        }
    }

    setInterval(loop, 1000);
    window.addEventListener('hashchange', function () { setTimeout(loop, 100); });

    // 浏览器「返回」可能从往返缓存(bfcache)恢复旧页面：恢复后强制重新渲染，
    // 确保设置改动（位置、开关）生效
    window.addEventListener('pageshow', function (e) {
        log('pageshow, persisted=' + !!e.persisted);
        lastRenderedId = null; // 强制重绘：重新获取位置/开关配置
        setTimeout(loop, 100);
        setTimeout(loop, 500);
    });

    log('script loaded, hash=' + location.hash);
    debugBadge('script loaded');
    setTimeout(loop, 400);
})();
