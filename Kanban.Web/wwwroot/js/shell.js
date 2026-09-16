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
    };
})();
