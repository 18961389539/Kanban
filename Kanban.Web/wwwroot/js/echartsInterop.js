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
            if (axis.axisLabel && axis.axisLabel.formatter === '__hourOnly') {
                axis.axisLabel = Object.assign({}, axis.axisLabel, {
                    formatter: function (value) {
                        const d = new Date(value);
                        return Number.isNaN(d.getTime()) ? '' : pad2(d.getHours());
                    }
                });
                continue;
            }
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
            downloadBlob(filename, new Blob([content], { type: mimeType || 'text/plain;charset=utf-8' }));
            return true;
        },

        /** 当前页上所有 ECharts 导出为 PNG data URL，供 PDF 嵌图。 */
        exportAllPng() {
            const urls = [];
            for (const entry of charts.values()) {
                try {
                    urls.push(entry.chart.getDataURL({ type: 'png', pixelRatio: 2, backgroundColor: '#ffffff' }));
                } catch (e) {
                    // 单张图导出失败不影响其余图和正文
                }
            }
            return urls;
        },

        /**
         * 把复盘正文画成页面图片再装进 PDF。
         * 用浏览器字体画中文，避免在 WASM 里嵌一套 CJK 字体。
         * lines 里以 "§" 开头的行画成小节标题。
         */
        async downloadPdf(filename, lines, images) {
            const pages = await renderPdfPages(Array.isArray(lines) ? lines : [], Array.isArray(images) ? images : []);
            const pdf = buildImagePdf(pages, 595.28, 841.89);
            downloadBlob(filename, new Blob([pdf], { type: 'application/pdf' }));
            return true;
        },
    };

    function downloadBlob(filename, blob) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    function loadImage(src) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = () => reject(new Error('image'));
            img.src = src;
        });
    }

    function wrapText(ctx, text, maxWidth) {
        const source = text == null ? '' : String(text);
        if (!source) return [''];
        const lines = [];
        let line = '';
        for (const ch of source) {
            const next = line + ch;
            if (line && ctx.measureText(next).width > maxWidth) {
                lines.push(line);
                line = ch;
            } else {
                line = next;
            }
        }
        if (line) lines.push(line);
        return lines.length ? lines : [''];
    }

    async function renderPdfPages(lines, images) {
        const pageWidth = 595.28;
        const pageHeight = 841.89;
        const scale = 2;
        const margin = 36;
        const contentWidth = pageWidth - margin * 2;
        const pages = [];
        let canvas = null;
        let ctx = null;
        let y = margin;

        function newPage() {
            canvas = document.createElement('canvas');
            canvas.width = Math.round(pageWidth * scale);
            canvas.height = Math.round(pageHeight * scale);
            ctx = canvas.getContext('2d');
            ctx.scale(scale, scale);
            ctx.fillStyle = '#ffffff';
            ctx.fillRect(0, 0, pageWidth, pageHeight);
            ctx.fillStyle = '#1f2937';
            ctx.textBaseline = 'top';
            y = margin;
            pages.push(canvas);
        }

        function ensure(height) {
            if (!canvas || y + height > pageHeight - margin) newPage();
        }

        newPage();
        for (const raw of lines) {
            const text = raw == null ? '' : String(raw);
            const heading = text.startsWith('§');
            const body = heading ? text.slice(1) : text;
            ctx.font = heading ? 'bold 13px "Microsoft YaHei", "PingFang SC", "Noto Sans SC", sans-serif'
                : '11px "Microsoft YaHei", "PingFang SC", "Noto Sans SC", sans-serif';
            const wrapped = wrapText(ctx, body, contentWidth);
            const lineHeight = heading ? 20 : 16;
            for (const piece of wrapped) {
                ensure(lineHeight);
                ctx.fillStyle = heading ? '#111827' : '#1f2937';
                ctx.fillText(piece, margin, y);
                y += lineHeight;
            }
            if (heading) y += 4;
        }

        for (const src of images) {
            if (!src) continue;
            let img;
            try {
                img = await loadImage(src);
            } catch (e) {
                continue;
            }
            const width = contentWidth;
            const height = Math.min(280, width * (img.height / Math.max(img.width, 1)));
            ensure(height + 12);
            y += 8;
            ctx.drawImage(img, margin, y, width, height);
            y += height + 8;
        }

        return pages.map((page) => ({
            bytes: dataUrlToBytes(page.toDataURL('image/jpeg', 0.85)),
            width: page.width,
            height: page.height,
        }));
    }

    function dataUrlToBytes(dataUrl) {
        const b64 = dataUrl.slice(dataUrl.indexOf(',') + 1);
        const bin = atob(b64);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes;
    }

    /** 每页一张 JPEG，拼成可下载的 PDF。 */
    function buildImagePdf(pageJpegs, pageWidthPt, pageHeightPt) {
        const encoder = new TextEncoder();
        const chunks = [];
        let length = 0;
        function add(data) {
            const bytes = typeof data === 'string' ? encoder.encode(data) : data;
            chunks.push(bytes);
            length += bytes.length;
        }
        const xref = [];
        function beginObject(n) {
            xref[n] = length;
        }

        add('%PDF-1.4\n');
        const nPages = pageJpegs.length;
        const pageIds = [];
        for (let i = 0; i < nPages; i++) pageIds.push(3 + i * 3);

        beginObject(1);
        add('1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n');
        beginObject(2);
        add('2 0 obj\n<< /Type /Pages /Count ' + nPages + ' /Kids [' + pageIds.map((id) => id + ' 0 R').join(' ') + '] >>\nendobj\n');

        for (let i = 0; i < nPages; i++) {
            const pageId = pageIds[i];
            const contentId = pageId + 1;
            const imageId = pageId + 2;
            const jpeg = pageJpegs[i].bytes;
            const content = 'q\n' + pageWidthPt + ' 0 0 ' + pageHeightPt + ' 0 0 cm\n/Im0 Do\nQ\n';
            beginObject(pageId);
            add(pageId + ' 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ' + pageWidthPt + ' ' + pageHeightPt + '] /Resources << /XObject << /Im0 ' + imageId + ' 0 R >> >> /Contents ' + contentId + ' 0 R >>\nendobj\n');
            beginObject(contentId);
            add(contentId + ' 0 obj\n<< /Length ' + content.length + ' >>\nstream\n');
            add(content);
            add('endstream\nendobj\n');
            beginObject(imageId);
            add(imageId + ' 0 obj\n<< /Type /XObject /Subtype /Image /Width ' + pageJpegs[i].width + ' /Height ' + pageJpegs[i].height + ' /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ' + jpeg.length + ' >>\nstream\n');
            add(jpeg);
            add('\nendstream\nendobj\n');
        }

        const xrefPos = length;
        const objCount = 2 + nPages * 3;
        add('xref\n0 ' + (objCount + 1) + '\n');
        add('0000000000 65535 f \n');
        for (let i = 1; i <= objCount; i++) {
            add(String(xref[i]).padStart(10, '0') + ' 00000 n \n');
        }
        add('trailer\n<< /Size ' + (objCount + 1) + ' /Root 1 0 R >>\nstartxref\n' + xrefPos + '\n%%EOF');

        const out = new Uint8Array(length);
        let pos = 0;
        for (const chunk of chunks) {
            out.set(chunk, pos);
            pos += chunk.length;
        }
        return out;
    }
})();
