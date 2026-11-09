// Local bounded renderer. Text is either a topology alias or separately authorized reader-card text.
const MAX_NODES = 150;
const MAX_EDGES = 150;
const CARD_WIDTH = 232;
const CARD_HEIGHT = 88;
const CELL_WIDTH = 264;
const CELL_HEIGHT = 112;
const PANEL_PADDING = 22;
const PANEL_HEADER = 34;
const PANEL_GAP = 28;

/** Creates a bounded labelled 2D map with deterministic component layout, selection and pan/zoom controls. */
export function createMemoryGraphRenderer(canvas, onSelect) {
    const context = canvas.getContext('2d');
    if (!context) throw new Error('The local graph needs a 2D canvas.');
    let nodes = new Map();
    let edges = [];
    let panels = [];
    let bounds = { width: 1, height: 1 };
    let scale = 1;
    let offsetX = 0;
    let offsetY = 0;
    let selectedId;
    let focusId;
    let drag;
    let disposed = false;
    let fitted = true;
    let notifiedId;

    /** Reads shared theme tokens so selection and edges retain contrast in both themes. */
    function palette() {
        const style = getComputedStyle(document.documentElement);
        const token = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
        return { surface: token('--surface-raised', '#ffffff'), panel: token('--surface', '#f4f6fb'),
            text: token('--text', '#172033'), muted: token('--text-muted', '#58657c'),
            border: token('--border', '#dbe1ed'), primary: token('--primary', '#4755d6'),
            focus: token('--focus', '#0e7490') };
    }

    /** Builds connected components from edges only; grouping does not infer the meaning of any memory. */
    function layout() {
        const adjacent = new Map([...nodes.keys()].map(id => [id, new Set()]));
        for (const edge of edges) {
            adjacent.get(edge.sourceId).add(edge.targetId);
            adjacent.get(edge.targetId).add(edge.sourceId);
        }
        // A focused view is a reading path: source on the left, actual neighbors on the right.
        // These are presentation coordinates, not similarity or causality.
        if (focusId && nodes.has(focusId)) {
            const neighbors = [...nodes.keys()].filter(id => id !== focusId).sort();
            const columns = neighbors.length > 6 ? 2 : 1;
            const rows = Math.max(Math.ceil(neighbors.length / columns), 1);
            const height = rows * CELL_HEIGHT + PANEL_PADDING * 2;
            const root = nodes.get(focusId);
            root.x = PANEL_PADDING + CELL_WIDTH / 2; root.y = height / 2; root.neighbors = adjacent.get(focusId);
            for (let index = 0; index < neighbors.length; index++) {
                const node = nodes.get(neighbors[index]);
                node.x = PANEL_PADDING + CELL_WIDTH * (1.6 + index % columns) + CELL_WIDTH / 2;
                node.y = PANEL_PADDING + (Math.floor(index / columns) + 0.5) * CELL_HEIGHT;
                node.neighbors = adjacent.get(node.id);
            }
            panels = []; bounds = { width: (columns + 1.6) * CELL_WIDTH + PANEL_PADDING * 2, height };
            return;
        }
        const orderedIds = [...nodes.keys()].sort();
        const remaining = new Set(orderedIds);
        const groups = [];
        for (const seed of orderedIds) {
            if (!remaining.delete(seed)) continue;
            const group = [seed];
            for (let index = 0; index < group.length; index++) {
                for (const neighbor of [...adjacent.get(group[index])].sort())
                    if (remaining.delete(neighbor)) group.push(neighbor);
            }
            groups.push(group);
        }
        // Isolated nodes share a presentation bucket, not a claimed semantic or connected component.
        const isolated = [];
        const connected = [];
        for (const group of groups) {
            if (group.length === 1) isolated.push(group[0]);
            else connected.push(group);
        }
        groups.length = 0;
        groups.push(...connected);
        if (isolated.length) groups.push(isolated);
        // The overview packs all loaded cards in a compact reading grid. Connected ordering
        // stays deterministic; rectangles are not used to imply semantic topic clusters.
        const aspect = Math.max(canvas.clientWidth, 320) / Math.max(canvas.clientHeight, 240);
        const columns = Math.max(1, Math.ceil(Math.sqrt(nodes.size * aspect * CELL_HEIGHT / CELL_WIDTH)));
        const ordered = groups.flat();
        panels = [];
        for (let index = 0; index < ordered.length; index++) {
            const node = nodes.get(ordered[index]);
            node.x = PANEL_PADDING + (index % columns + 0.5) * CELL_WIDTH;
            node.y = PANEL_PADDING + (Math.floor(index / columns) + 0.5) * CELL_HEIGHT;
            node.neighbors = adjacent.get(node.id);
        }
        bounds = { width: Math.min(columns, Math.max(nodes.size, 1)) * CELL_WIDTH + PANEL_PADDING * 2,
            height: Math.ceil(Math.max(nodes.size, 1) / columns) * CELL_HEIGHT + PANEL_PADDING * 2 };
    }

    /** Centers the bounded snapshot at a readable scale; narrow views can pan to overflow without losing alias ordinals. */
    function fit() {
        const width = Math.max(canvas.clientWidth, 1);
        const height = Math.max(canvas.clientHeight, 1);
        scale = Math.min((width - 32) / bounds.width, (height - 32) / bounds.height, 1.4);
        // A readable minimum keeps adjacent card labels apart on narrow viewports; overflow remains pannable.
        scale = Math.max(scale, 0.48);
        offsetX = (width - bounds.width * scale) / 2;
        offsetY = (height - bounds.height * scale) / 2;
        fitted = true;
    }

    /** Converts one world coordinate to CSS pixels; drawing and hit testing use the same transform. */
    function screen(node) { return { x: node.x * scale + offsetX, y: node.y * scale + offsetY }; }

    /** Abbreviates only presentation; the inspector receives the full safe alias. */
    function labelForWidth(label, width) {
        if (context.measureText(label).width <= width) return label;
        const ordinal = /(?:\s*[№#]?\d+)$/u.exec(label)?.[0] || '';
        let prefix = label.slice(0, label.length - ordinal.length);
        while (prefix && context.measureText(`${prefix}…${ordinal}`).width > width) prefix = prefix.slice(0, -1);
        const abbreviated = `${prefix}…${ordinal}`;
        return context.measureText(abbreviated).width <= width ? abbreviated : ordinal.trim();
    }

    /** Wraps a reader title into two measured lines; the full literal title remains in the accessible list. */
    function titleLines(label, width) {
        if (context.measureText(label).width <= width) return [label];
        let end = label.length;
        while (end > 1 && context.measureText(label.slice(0, end)).width > width) end--;
        const space = label.lastIndexOf(' ', end);
        if (space > end / 2) end = space;
        return [label.slice(0, end).trim(), labelForWidth(label.slice(end).trim(), width)];
    }

    /** Draws component panels, muted links and screen-readable cards; selected neighbors retain full contrast. */
    function draw() {
        if (disposed) return;
        const ratio = Math.min(window.devicePixelRatio || 1, 2);
        const width = Math.max(canvas.clientWidth, 1);
        const height = Math.max(canvas.clientHeight, 1);
        const colors = palette();
        context.setTransform(ratio, 0, 0, ratio, 0, 0);
        context.clearRect(0, 0, width, height);
        context.fillStyle = colors.surface;
        context.fillRect(0, 0, width, height);
        context.font = '600 12px system-ui, sans-serif';
        context.textBaseline = 'middle';
        for (const panel of panels) {
            context.fillStyle = colors.panel;
            context.fillRect(panel.x * scale + offsetX, panel.y * scale + offsetY, panel.width * scale, panel.height * scale);
            context.fillStyle = colors.muted;
            context.textAlign = 'left';
            if (PANEL_HEADER * scale >= 16)
                context.fillText(panel.label, (panel.x + PANEL_PADDING) * scale + offsetX, (panel.y + PANEL_HEADER / 2) * scale + offsetY);
        }
        const selected = nodes.get(selectedId);
        const neighborhood = selected ? new Set([selected.id, ...selected.neighbors]) : undefined;
        context.lineCap = 'round';
        for (const edge of edges) {
            const highlighted = selected && (edge.sourceId === selected.id || edge.targetId === selected.id);
            const source = screen(nodes.get(edge.sourceId));
            const target = screen(nodes.get(edge.targetId));
            context.globalAlpha = selected && !highlighted ? 0.15 : highlighted ? 1 : 0.75;
            context.strokeStyle = highlighted ? colors.focus : colors.muted;
            context.lineWidth = (highlighted ? 2.5 : 1) + Math.min(Math.max(edge.weight - 1, 0), 6) * 0.25;
            const direction = source.x <= target.x ? 1 : -1;
            const startX = source.x + direction * CARD_WIDTH * scale / 2;
            const endX = target.x - direction * CARD_WIDTH * scale / 2;
            const bend = Math.max(Math.abs(endX - startX) / 2, 24);
            context.beginPath(); context.moveTo(startX, source.y);
            if (context.bezierCurveTo) context.bezierCurveTo(startX + direction * bend, source.y, endX - direction * bend, target.y, endX, target.y);
            else context.lineTo(endX, target.y);
            context.stroke();
        }
        context.textAlign = 'center';
        for (const node of nodes.values()) {
            const point = screen(node);
            const cardWidth = CARD_WIDTH * scale;
            const cardHeight = Math.max(CARD_HEIGHT * scale, 18);
            context.globalAlpha = neighborhood && !neighborhood.has(node.id) ? 0.3 : 1;
            context.fillStyle = colors.surface;
            context.fillRect(point.x - cardWidth / 2, point.y - cardHeight / 2, cardWidth, cardHeight);
            context.strokeStyle = node.id === selectedId ? colors.focus : neighborhood?.has(node.id) ? colors.primary : colors.border;
            context.lineWidth = node.id === selectedId ? 3 : neighborhood?.has(node.id) ? 2 : 1;
            context.strokeRect(point.x - cardWidth / 2, point.y - cardHeight / 2, cardWidth, cardHeight);
            context.fillStyle = node.color || colors.primary;
            context.fillRect(point.x - cardWidth / 2, point.y - cardHeight / 2, 3, cardHeight);
            context.fillStyle = colors.text;
            if (node.alias && cardHeight >= 48) {
                const fontSize = cardHeight >= 72 ? 14 : 12;
                context.font = `600 ${fontSize}px system-ui, sans-serif`;
                context.textAlign = 'left';
                const textX = point.x - cardWidth / 2 + 12;
                const available = Math.max(cardWidth - 24, 1);
                const lines = titleLines(node.label, available);
                context.fillText(lines[0], textX, point.y - 12);
                if (lines[1]) context.fillText(lines[1], textX, point.y + 3);
                context.font = '400 11px system-ui, sans-serif'; context.fillStyle = colors.muted;
                context.fillText(labelForWidth(node.subtitle || node.alias, available), textX, point.y + cardHeight / 2 - 10);
            } else {
                context.font = '600 12px system-ui, sans-serif'; context.textAlign = 'center';
                context.fillText(labelForWidth(node.label, Math.max(cardWidth - 24, 1)), point.x + 5, point.y);
            }
        }
        context.globalAlpha = 1;
        if (nodes.size === 0) {
            context.fillStyle = colors.muted;
            context.fillText('Нет узлов для отображения', width / 2, height / 2);
        }
    }

    /** Keeps the backing bitmap bounded by a 2x pixel ratio and refits only an unmodified view. */
    function resize() {
        const ratio = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.max(Math.floor(canvas.clientWidth * ratio), 1);
        canvas.height = Math.max(Math.floor(canvas.clientHeight * ratio), 1);
        if (fitted) fit();
        draw();
    }

    /** Replaces local copies while preserving valid selection and optionally retaining the camera. */
    function render(snapshot, preserveView = false) {
        if (disposed) return;
        nodes = new Map();
        for (const node of (snapshot?.nodes ?? []).slice(0, MAX_NODES))
            if (!nodes.has(node.id)) nodes.set(node.id, { ...node, x: 0, y: 0 });
        edges = (snapshot?.edges ?? []).slice(0, MAX_EDGES)
            .filter(edge => nodes.has(edge.sourceId) && nodes.has(edge.targetId) && edge.sourceId !== edge.targetId)
            .map(edge => ({ ...edge }));
        selectedId = nodes.has(selectedId) ? selectedId : undefined;
        focusId = nodes.has(snapshot?.focusId) ? snapshot.focusId : undefined;
        layout();
        if (!preserveView) fit();
        else fitted = false;
        resize();
        notifySelection();
    }

    /** Changes zoom around a fixed screen point instead of making the map jump away from the pointer. */
    function zoom(factor, x = canvas.clientWidth / 2, y = canvas.clientHeight / 2) {
        const next = Math.max(0.34, Math.min(scale * factor, 4));
        const change = next / scale;
        offsetX = x - (x - offsetX) * change;
        offsetY = y - (y - offsetY) * change;
        scale = next;
        fitted = false;
        draw();
    }

    /** Emits a safe node copy on a changed selection, including undefined when selection is cleared. */
    function notifySelection() {
        if (notifiedId === selectedId) return;
        notifiedId = selectedId;
        const selected = nodes.get(selectedId);
        if (selected) {
            const { neighbors, x, y, ...safeNode } = selected;
            onSelect?.(safeNode);
        } else onSelect?.(undefined);
    }

    /** Selects by response-local ID and reveals offscreen selections for keyboard and list users. */
    function selectNode(id) {
        selectedId = nodes.has(id) ? id : undefined;
        const node = nodes.get(selectedId);
        if (node) {
            const point = screen(node);
            if (point.x < 20 || point.x > canvas.clientWidth - 20 || point.y < 20 || point.y > canvas.clientHeight - 20) {
                offsetX = canvas.clientWidth / 2 - node.x * scale;
                offsetY = canvas.clientHeight / 2 - node.y * scale;
                fitted = false;
            }
        }
        draw(); notifySelection();
    }

    /** Restores the complete map and clears the inspector selection. */
    function reset() { selectedId = undefined; fit(); draw(); notifySelection(); }

    /** Traverses aliases with arrows, pans with Shift+arrows, and exposes fit, zoom and clear keyboard controls. */
    function onKeyDown(event) {
        const key = event.key;
        if (key === '+' || key === '=') zoom(1.2);
        else if (key === '-') zoom(1 / 1.2);
        else if (key === 'Home' || key === '0') reset();
        else if (key === 'Escape') selectNode('');
        else if (key.startsWith('Arrow')) {
            const direction = key === 'ArrowLeft' || key === 'ArrowUp' ? -1 : 1;
            if (event.shiftKey) {
                if (key === 'ArrowLeft' || key === 'ArrowRight') offsetX -= direction * 40;
                else offsetY -= direction * 40;
                fitted = false; draw();
            } else {
                const ids = [...nodes.keys()].sort();
                if (ids.length) {
                    const current = ids.indexOf(selectedId);
                    let nextIndex;
                    if (current < 0) nextIndex = direction > 0 ? 0 : ids.length - 1;
                    else nextIndex = (current + direction + ids.length) % ids.length;
                    selectNode(ids[nextIndex]);
                }
            }
        } else return;
        event.preventDefault();
    }

    /** Starts pointer capture so panning remains smooth when a drag leaves the canvas. */
    function onPointerDown(event) {
        if (event.button !== 0) return;
        canvas.focus({ preventScroll: true });
        drag = { id: event.pointerId, x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY, moved: false };
        canvas.setPointerCapture?.(event.pointerId);
    }

    /** Moves the view without moving the deterministic node positions. */
    function onPointerMove(event) {
        if (!drag || drag.id !== event.pointerId) return;
        const dx = event.clientX - drag.x;
        const dy = event.clientY - drag.y;
        if (Math.abs(event.clientX - drag.startX) + Math.abs(event.clientY - drag.startY) > 3) drag.moved = true;
        if (drag.moved) { offsetX += dx; offsetY += dy; fitted = false; draw(); }
        drag.x = event.clientX; drag.y = event.clientY;
    }

    /** Hit-tests complete cards on click; dragging or cancellation never changes selection. */
    function onPointerUp(event) {
        if (!drag || drag.id !== event.pointerId) return;
        const clicked = !drag.moved && event.type !== 'pointercancel';
        drag = undefined;
        canvas.releasePointerCapture?.(event.pointerId);
        if (!clicked) return;
        const rectangle = canvas.getBoundingClientRect();
        const x = event.clientX - rectangle.left;
        const y = event.clientY - rectangle.top;
        let hit;
        for (const node of nodes.values()) {
            const point = screen(node);
            if (Math.abs(point.x - x) <= CARD_WIDTH * scale / 2 && Math.abs(point.y - y) <= Math.max(CARD_HEIGHT * scale, 18) / 2) hit = node.id;
        }
        selectNode(hit);
    }

    /** Uses wheel input only while the map has focus, preserving ordinary page scrolling elsewhere. */
    function onWheel(event) {
        if (document.activeElement !== canvas) return;
        event.preventDefault();
        const rectangle = canvas.getBoundingClientRect();
        zoom(event.deltaY < 0 ? 1.12 : 1 / 1.12, event.clientX - rectangle.left, event.clientY - rectangle.top);
    }

    canvas.addEventListener('keydown', onKeyDown);
    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    const resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(canvas);
    const themeObserver = new MutationObserver(draw);
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class', 'data-theme', 'style'] });
    resize();
    return { render, selectNode,
        /** Projects current nodes through the same pan/zoom transform as drawing, without changing selection. */
        activityPositions(ids) {
            const rectangle = canvas.getBoundingClientRect();
            const result = [];
            for (const id of ids) {
                const node = nodes.get(id);
                if (!node) continue;
                const point = screen(node);
                if (point.x < 0 || point.y < 0 || point.x > rectangle.width || point.y > rectangle.height) continue;
                result.push({ id, x: rectangle.left + point.x, y: rectangle.top + point.y - 28 });
            }
            return result;
        }, zoomIn: () => zoom(1.2), zoomOut: () => zoom(1 / 1.2), reset,
        /** Releases listeners and snapshots; repeated disposal is safe. */
        dispose() {
            if (disposed) return;
            disposed = true;
            resizeObserver.disconnect();
            themeObserver.disconnect();
            if (drag) canvas.releasePointerCapture?.(drag.id);
            canvas.removeEventListener('keydown', onKeyDown);
            canvas.removeEventListener('pointerdown', onPointerDown);
            canvas.removeEventListener('pointermove', onPointerMove);
            canvas.removeEventListener('pointerup', onPointerUp);
            canvas.removeEventListener('pointercancel', onPointerUp);
            canvas.removeEventListener('wheel', onWheel);
            nodes.clear(); edges = []; panels = []; drag = undefined;
        } };
}
