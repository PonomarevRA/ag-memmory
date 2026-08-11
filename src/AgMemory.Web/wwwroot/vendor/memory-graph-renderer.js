// Local, dependency-free canvas renderer. It is intentionally vendored with the host rather than loaded from a CDN.
export function createMemoryGraphRenderer(canvas, onSelect) {
    const context = canvas.getContext('2d');
    const nodes = new Map();
    let edges = [];
    let scale = 1;
    let selectedId;
    let resizeObserver;

    function palette() {
        const style = getComputedStyle(document.documentElement);
        return {
            surface: style.getPropertyValue('--surface-raised').trim() || '#ffffff',
            muted: style.getPropertyValue('--text-muted').trim() || '#58657c',
            text: style.getPropertyValue('--text').trim() || '#172033',
            border: style.getPropertyValue('--border').trim() || '#dbe1ed',
            primary: style.getPropertyValue('--primary').trim() || '#4755d6',
            focus: style.getPropertyValue('--focus').trim() || '#0e7490'
        };
    }

    function hash(value) {
        let result = 2166136261;
        for (let index = 0; index < value.length; index++) result = Math.imul(result ^ value.charCodeAt(index), 16777619);
        return result >>> 0;
    }

    function resetPositions() {
        const width = Math.max(canvas.clientWidth, 1);
        const height = Math.max(canvas.clientHeight, 1);
        const radius = Math.min(width, height) * 0.3;
        const values = [...nodes.values()];
        values.forEach((node, index) => {
            const angle = (Math.PI * 2 * index) / Math.max(values.length, 1) + ((hash(node.id) % 360) * Math.PI / 1800);
            node.x = width / 2 + Math.cos(angle) * radius;
            node.y = height / 2 + Math.sin(angle) * radius;
        });
        relax();
    }

    function relax() {
        const values = [...nodes.values()];
        const width = Math.max(canvas.clientWidth, 1);
        const height = Math.max(canvas.clientHeight, 1);
        for (let iteration = 0; iteration < 80; iteration++) {
            for (let first = 0; first < values.length; first++) {
                for (let second = first + 1; second < values.length; second++) {
                    const a = values[first];
                    const b = values[second];
                    const dx = b.x - a.x;
                    const dy = b.y - a.y;
                    const distance = Math.max(Math.hypot(dx, dy), 1);
                    const force = 600 / (distance * distance);
                    a.x -= (dx / distance) * force;
                    a.y -= (dy / distance) * force;
                    b.x += (dx / distance) * force;
                    b.y += (dy / distance) * force;
                }
            }
            for (const edge of edges) {
                const source = nodes.get(edge.sourceId);
                const target = nodes.get(edge.targetId);
                if (!source || !target) continue;
                const dx = target.x - source.x;
                const dy = target.y - source.y;
                const distance = Math.max(Math.hypot(dx, dy), 1);
                const force = (distance - 100) * 0.012;
                source.x += (dx / distance) * force;
                source.y += (dy / distance) * force;
                target.x -= (dx / distance) * force;
                target.y -= (dy / distance) * force;
            }
            for (const node of values) {
                node.x += (width / 2 - node.x) * 0.015;
                node.y += (height / 2 - node.y) * 0.015;
            }
        }
    }

    function resize() {
        const ratio = window.devicePixelRatio || 1;
        const width = Math.max(Math.floor(canvas.clientWidth * ratio), 1);
        const height = Math.max(Math.floor(canvas.clientHeight * ratio), 1);
        if (canvas.width !== width || canvas.height !== height) {
            canvas.width = width;
            canvas.height = height;
        }
        draw();
    }

    function draw() {
        const ratio = window.devicePixelRatio || 1;
        const colors = palette();
        const width = canvas.width / ratio;
        const height = canvas.height / ratio;
        context.setTransform(ratio, 0, 0, ratio, 0, 0);
        context.clearRect(0, 0, width, height);
        context.fillStyle = colors.surface;
        context.fillRect(0, 0, width, height);
        context.save();
        context.translate(width / 2, height / 2);
        context.scale(scale, scale);
        context.translate(-width / 2, -height / 2);
        context.lineCap = 'round';
        for (const edge of edges) {
            const source = nodes.get(edge.sourceId);
            const target = nodes.get(edge.targetId);
            if (!source || !target) continue;
            const selected = selectedId && (edge.sourceId === selectedId || edge.targetId === selectedId);
            context.strokeStyle = selected ? '#ffffff' : '#A8B7D1';
            context.lineWidth = Math.min((selected ? 2 : 1) + edge.weight * 0.55, selected ? 6 : 4);
            context.beginPath();
            context.moveTo(source.x, source.y);
            context.lineTo(target.x, target.y);
            context.stroke();
        }
        for (const node of nodes.values()) {
            const radius = Math.min(10 + node.degree * 1.4, 22);
            context.fillStyle = colors.primary;
            context.beginPath();
            context.arc(node.x, node.y, radius, 0, Math.PI * 2);
            context.fill();
            context.strokeStyle = colors.focus;
            context.lineWidth = node.id === selectedId ? 4 : 1;
            context.stroke();
            context.fillStyle = colors.text;
            context.font = '600 12px system-ui, sans-serif';
            context.textAlign = 'center';
            context.fillText(node.label, node.x, node.y + radius + 15);
        }
        context.restore();
        if (nodes.size === 0) {
            context.fillStyle = colors.muted;
            context.font = '600 14px system-ui, sans-serif';
            context.textAlign = 'center';
            context.fillText('Нет узлов для отображения', width / 2, height / 2);
        }
    }

    function render(snapshot, preserveView = false) {
        const previousSelection = selectedId;
        nodes.clear();
        for (const node of snapshot?.nodes ?? []) nodes.set(node.id, { ...node, x: 0, y: 0 });
        edges = Array.isArray(snapshot?.edges) ? snapshot.edges : [];
        selectedId = previousSelection && nodes.has(previousSelection) ? previousSelection : undefined;
        if (!preserveView) scale = 1;
        resetPositions();
        resize();
    }

    function zoomIn() {
        scale = Math.min(scale * 1.2, 2.4);
        draw();
    }

    function zoomOut() {
        scale = Math.max(scale / 1.2, 0.55);
        draw();
    }

    function reset() {
        scale = 1;
        resetPositions();
        draw();
    }

    function onKeyDown(event) {
        if (event.key === '+' || event.key === '=') {
            event.preventDefault();
            zoomIn();
        } else if (event.key === '-') {
            event.preventDefault();
            zoomOut();
        } else if (event.key === '0' || event.key === 'Home') {
            event.preventDefault();
            reset();
        }
    }

    function onPointerUp(event) {
        const rectangle = canvas.getBoundingClientRect();
        const x = (event.clientX - rectangle.left - rectangle.width / 2) / scale + rectangle.width / 2;
        const y = (event.clientY - rectangle.top - rectangle.height / 2) / scale + rectangle.height / 2;
        let selected;
        let nearest = Number.POSITIVE_INFINITY;
        for (const node of nodes.values()) {
            const distance = Math.hypot(node.x - x, node.y - y);
            if (distance <= Math.min(10 + node.degree * 1.4, 22) + 10 && distance < nearest) { selected = node; nearest = distance; }
        }
        selectedId = selected?.id;
        draw();
        if (selected) onSelect?.(selected);
    }

    canvas.addEventListener('keydown', onKeyDown);
    canvas.addEventListener('pointerup', onPointerUp);
    resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(canvas);
    resize();

    return {
        render,
        zoomIn,
        zoomOut,
        reset,
        selectNode(nodeId) {
            selectedId = nodes.has(nodeId) ? nodeId : undefined;
            draw();
            const selected = selectedId ? nodes.get(selectedId) : undefined;
            if (selected) onSelect?.(selected);
        },
        dispose() {
            resizeObserver?.disconnect();
            canvas.removeEventListener('keydown', onKeyDown);
            canvas.removeEventListener('pointerup', onPointerUp);
            nodes.clear();
            edges = [];
        }
    };
}
