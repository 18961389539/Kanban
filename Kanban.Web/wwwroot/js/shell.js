// 壳层元数据同步：Blazor 侧语言/标题变化时推送到 DOM。
// <html lang> 影响浏览器翻译/无障碍与字体回退；<title> 显示在浏览器标签页。
(function () {
    'use strict';

    window.KanbanShell = {
        /** 同步 <html lang> 与 document.title（语言/标题任一变化时调用）。 */
        applyMetadata(lang, title) {
            if (lang) document.documentElement.lang = lang;
            if (title) document.title = title;
            return true;
        },

        copy(text) {
            if (navigator.clipboard && window.isSecureContext) {
                return navigator.clipboard.writeText(text);
            }
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.left = '-9999px';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
            return true;
        },

        getSession(key) {
            try { return sessionStorage.getItem(key); } catch { return null; }
        },

        setSession(key, value) {
            try { sessionStorage.setItem(key, value); } catch { /* ignore quota */ }
            return true;
        },
    };
})();
