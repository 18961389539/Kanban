// ECharts × Blazor WASM 互操作层。
// 设计要点：
// - 图表实例表存于 JS 侧（WASM 侧无跨渲染周期的对象引用；元素 id 为键）；
// - init 时挂 ResizeObserver 自动适配容器尺寸（卡片/窗口缩放无需手动 resize）；
// - setOption 默认 notMerge=true 全量替换——查询页每次查询重建 option，
//   避免增量合并残留旧轴/旧系列（语言切换后系列名也会变）。
(function () {
    'use strict';

    const charts = new Map(); // id -> { chart, observer }

    window.KanbanECharts = {
        /** 初始化图表（已存在则先销毁重建）。option 为 JSON 对象。 */
        init(id, option) {
            const el = document.getElementById(id);
            if (!el) return false;
            const prev = charts.get(id);
            if (prev) {
                prev.observer.disconnect();
                prev.chart.dispose();
            }
            const chart = echarts.init(el, null, { renderer: 'canvas' });
            chart.setOption(option);
            const observer = new ResizeObserver(() => chart.resize());
            observer.observe(el);
            charts.set(id, { chart, observer });
            return true;
        },

        /** 全量替换图表配置。 */
        setOption(id, option) {
            const entry = charts.get(id);
            if (!entry) return false;
            entry.chart.setOption(option, { notMerge: true, lazyUpdate: false });
            return true;
        },

        /** 手动触发重算尺寸（ResizeObserver 覆盖不到的场景兜底）。 */
        resize(id) {
            const entry = charts.get(id);
            if (!entry) return false;
            entry.chart.resize();
            return true;
        },

        /** 销毁图表并解绑观察器（组件 Dispose 时调用，防泄漏）。 */
        dispose(id) {
            const entry = charts.get(id);
            if (!entry) return;
            entry.observer.disconnect();
            entry.chart.dispose();
            charts.delete(id);
        },

        /** 浏览器下载（CSV 导出）：Blob + a[download]，不依赖服务端。 */
        download(filename, content, mimeType) {
            const blob = new Blob([content], { type: mimeType || 'text/plain;charset=utf-8' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
            return true;
        },
    };
})();
