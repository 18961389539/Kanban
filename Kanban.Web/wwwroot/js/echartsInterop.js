// ECharts × Blazor WASM 互操作层。
// 设计要点：
// - 图表实例表存于 JS 侧（WASM 侧无跨渲染周期的对象引用；元素 id 为键）；
// - init 时挂 ResizeObserver 自动适配容器尺寸（卡片/窗口缩放无需手动 resize）；
// - setOption 默认 notMerge=true 全量替换——查询页每次查询重建 option，
//   避免增量合并残留旧轴/旧系列（语言切换后系列名也会变）。
(function () {
    'use strict';

    const charts = new Map(); // id -> { chart, observer }

    function pad2(n) {
        return String(n).padStart(2, '0');
    }

    /** ECharts time 轴默认跟浏览器 locale，en-US 会显示 12 小时 + AM/PM；强制工厂墙钟 24 小时。 */
    function formatAxisTime(value) {
        const d = new Date(value);
        if (Number.isNaN(d.getTime())) return '';
        return pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
    }

    function collectAxes(axis, into) {
        if (!axis) return;
        if (Array.isArray(axis)) {
            axis.forEach((item) => collectAxes(item, into));
            return;
        }
        into.push(axis);
    }

    function apply24HourTimeFormat(option) {
        if (!option || typeof option !== 'object') return option;
        const axes = [];
        collectAxes(option.xAxis, axes);
        collectAxes(option.yAxis, axes);
        let hasTimeAxis = false;
        for (const axis of axes) {
            if (axis.type !== 'time') continue;
            hasTimeAxis = true;
            axis.axisLabel = Object.assign({}, axis.axisLabel || {}, { formatter: formatAxisTime });
        }
        if (hasTimeAxis) {
            option.tooltip = Object.assign({}, option.tooltip || {});
            option.tooltip.axisPointer = Object.assign({}, option.tooltip.axisPointer || {});
            option.tooltip.axisPointer.label = Object.assign({}, option.tooltip.axisPointer.label || {}, {
                formatter: (p) => formatAxisTime(p && p.value)
            });
        }
        return option;
    }

    // ─── 函数标记解析 ───
    // Blazor JSON 序列化无法携带 JS 函数：C# 侧用约定字符串占位，推送前在此替换为真实函数。
    // 甘特图（custom series）：series.renderItem = '__gantt'，tooltip.formatter = '__ganttTooltip'，
    // 状态名数组经 option.__ganttStateNames 传入（tooltip 显示用，解析后从 option 删除）。
    const GANTT_RENDER_ITEM = '__gantt';
    const GANTT_TOOLTIP = '__ganttTooltip';

    /** 甘特段渲染：数据项 value = [startMs, endMs, categoryIndex]，按类目行高画区间矩形。 */
    function ganttRenderItem(params, api) {
        const start = api.coord([api.value(0), api.value(2)]);
        const end = api.coord([api.value(1), api.value(2)]);
        const bandHeight = api.size([0, 1])[1];
        const height = Math.max(bandHeight * 0.55, 4);
        const width = Math.max(end[0] - start[0], 1.5); // 短段保底 1.5px，否则不可见
        return {
            type: 'rect',
            shape: { x: start[0], y: start[1] - height / 2, width: width, height: height },
            style: api.style()
        };
    }

    function resolveFunctionMarkers(option) {
        if (!option || typeof option !== 'object') return option;
        const stateNames = option.__ganttStateNames;
        if (stateNames) delete option.__ganttStateNames;
        const seriesList = Array.isArray(option.series) ? option.series : (option.series ? [option.series] : []);
        for (const s of seriesList) {
            if (s && s.renderItem === GANTT_RENDER_ITEM) s.renderItem = ganttRenderItem;
        }
        if (option.tooltip && option.tooltip.formatter === GANTT_TOOLTIP) {
            option.tooltip.formatter = function (p) {
                const v = (p && p.value) || [];
                const name = stateNames && stateNames[v[2]] != null ? stateNames[v[2]] : '';
                const hours = (v[1] - v[0]) / 3600000;
                return name + '<br/>' + formatAxisTime(v[0]) + ' ~ ' + formatAxisTime(v[1]) +
                    '<br/>' + hours.toFixed(2) + ' h';
            };
        }
        return option;
    }

    function prepareOption(option) {
        return resolveFunctionMarkers(apply24HourTimeFormat(option));
    }

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
            chart.setOption(prepareOption(option));
            const observer = new ResizeObserver(() => chart.resize());
            observer.observe(el);
            charts.set(id, { chart, observer });
            return true;
        },

        /** 全量替换图表配置。 */
        setOption(id, option) {
            const entry = charts.get(id);
            if (!entry) return false;
            entry.chart.setOption(prepareOption(option), { notMerge: true, lazyUpdate: false });
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
