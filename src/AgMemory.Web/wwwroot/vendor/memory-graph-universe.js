import * as THREE from './three/three.module.js';

const DEFAULT_VIEW = Object.freeze({ yaw: Math.PI * 0.25, pitch: 0.48, distance: 62 });
const MAX_DISTANCE = 220;
const MIN_DISTANCE = 12;
const TYPE_APPEARANCES = Object.freeze([
    { color: '#6172f3', shape: 'sphere' },
    { color: '#12a594', shape: 'octahedron' },
    { color: '#b66be5', shape: 'box' },
    { color: '#e09243', shape: 'dodecahedron' }
]);

function hash(value) {
    let result = 2166136261;
    for (let index = 0; index < value.length; index++) result = Math.imul(result ^ value.charCodeAt(index), 16777619);
    return result >>> 0;
}

function unit(value, salt) {
    return hash(`${value}:${salt}`) / 0xffffffff;
}

function clamp(value, minimum, maximum) {
    return Math.min(Math.max(value, minimum), maximum);
}

function appearanceFor(type) {
    return TYPE_APPEARANCES[hash(type) % TYPE_APPEARANCES.length];
}

function geometryFor(shape, radius) {
    switch (shape) {
        case 'octahedron': return new THREE.OctahedronGeometry(radius, 1);
        case 'box': return new THREE.BoxGeometry(radius * 1.55, radius * 1.55, radius * 1.55);
        case 'dodecahedron': return new THREE.DodecahedronGeometry(radius, 0);
        default: return new THREE.SphereGeometry(radius, 18, 14);
    }
}

function sortIds(ids, layoutKeys) {
    return [...ids].sort((first, second) => layoutKeys.get(first).localeCompare(layoutKeys.get(second)));
}

function componentGroups(ids, adjacent, layoutKeys) {
    const remaining = new Set(ids);
    const components = [];
    while (remaining.size > 0) {
        const seed = sortIds(remaining, layoutKeys)[0];
        const queue = [seed];
        const component = [];
        remaining.delete(seed);
        for (let index = 0; index < queue.length; index++) {
            const id = queue[index];
            component.push(id);
            for (const neighbor of sortIds(adjacent.get(id) ?? [], layoutKeys)) {
                if (remaining.delete(neighbor)) queue.push(neighbor);
            }
        }
        components.push(sortIds(component, layoutKeys));
    }
    return components.sort((first, second) => layoutKeys.get(first[0]).localeCompare(layoutKeys.get(second[0])));
}

// This bounded, deterministic label pass uses generic response-local labels and safe topology. It is view layout,
// not a semantic clustering or a claim about similarity between memory records.
function localCommunities(component, adjacent, layoutKeys) {
    let labels = new Map(component.map(id => [id, layoutKeys.get(id)]));
    for (let round = 0; round < 5; round++) {
        const next = new Map();
        for (const id of component) {
            const votes = new Map([[labels.get(id), 1]]);
            for (const neighbor of adjacent.get(id) ?? []) {
                const label = labels.get(neighbor);
                votes.set(label, (votes.get(label) ?? 0) + 1);
            }
            next.set(id, [...votes.entries()]
                .sort((first, second) => second[1] - first[1] || first[0].localeCompare(second[0]))[0][0]);
        }
        labels = next;
    }
    const groups = new Map();
    for (const id of component) {
        const label = labels.get(id);
        const group = groups.get(label) ?? [];
        group.push(id);
        groups.set(label, group);
    }
    return [...groups.entries()]
        .map(([label, ids]) => ({ label, ids: sortIds(ids, layoutKeys) }))
        .sort((first, second) => first.label.localeCompare(second.label));
}

function structuralLayout(snapshot) {
    const layoutKeys = new Map(snapshot.nodes.map(node => [node.id, node.layoutKey ?? `${node.type}:${node.label}`]));
    const ids = sortIds(snapshot.nodes.map(node => node.id), layoutKeys);
    const adjacent = new Map(ids.map(id => [id, new Set()]));
    for (const edge of snapshot.edges) {
        adjacent.get(edge.sourceId)?.add(edge.targetId);
        adjacent.get(edge.targetId)?.add(edge.sourceId);
    }

    const components = componentGroups(ids, adjacent, layoutKeys);
    const positions = new Map();
    const side = Math.max(1, Math.ceil(Math.cbrt(components.length)));
    components.forEach((component, componentIndex) => {
        const layer = Math.floor(componentIndex / (side * side));
        const row = Math.floor(componentIndex / side) % side;
        const column = componentIndex % side;
        const componentAnchor = new THREE.Vector3(
            (column - (side - 1) / 2) * 38,
            (row - (side - 1) / 2) * 32,
            (layer - (side - 1) / 2) * 38);
        const communities = localCommunities(component, adjacent, layoutKeys);
        communities.forEach((community, communityIndex) => {
            const angle = (Math.PI * 2 * communityIndex) / Math.max(communities.length, 1);
            const anchorRadius = 7 + Math.sqrt(community.ids.length) * 2;
            const communityAnchor = componentAnchor.clone().add(new THREE.Vector3(
                Math.cos(angle) * anchorRadius,
                Math.sin(angle * 2.1) * anchorRadius * 0.58,
                Math.sin(angle) * anchorRadius));
            community.ids.forEach((id, nodeIndex) => {
                const layoutKey = layoutKeys.get(id);
                const radius = 1.8 + (nodeIndex % 4) * 0.85 + unit(layoutKey, 'radius') * 1.5;
                const theta = unit(layoutKey, 'theta') * Math.PI * 2;
                const phi = Math.acos(1 - unit(layoutKey, 'phi') * 2);
                positions.set(id, communityAnchor.clone().add(new THREE.Vector3(
                    Math.sin(phi) * Math.cos(theta) * radius,
                    Math.cos(phi) * radius,
                    Math.sin(phi) * Math.sin(theta) * radius)));
            });
        });
    });
    return { adjacent, positions };
}

function disposeObject(object) {
    object.traverse(item => {
        item.geometry?.dispose();
        const materials = Array.isArray(item.material) ? item.material : [item.material];
        for (const material of materials) material?.dispose?.();
    });
}

function pixelRatio() {
    const mobile = window.matchMedia('(max-width: 52rem), (pointer: coarse)').matches;
    return Math.min(window.devicePixelRatio || 1, mobile ? 1.5 : 2);
}

/**
 * Creates the primary, local-only Three.js renderer. The caller owns fallback selection when this
 * throws or calls onUnavailable; this module never fetches, imports remotely, or handles data I/O.
 */
export function createMemoryUniverse(canvas, selectionSummary, onUnavailable) {
    if (!canvas || typeof WebGL2RenderingContext === 'undefined') {
        throw new Error('WebGL2 is unavailable.');
    }

    let disposed = false;
    let raf;
    let resizeObserver;
    let selectedId;
    let snapshot = { nodes: [], edges: [] };
    let currentExtent = 24;
    let drag;
    let pinchDistance;
    const pointers = new Map();
    const nodeRecords = new Map();
    const edgeRecords = [];
    const sceneObjects = [];
    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(42, 1, 0.1, 500);
    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    const target = new THREE.Vector3();
    const view = { ...DEFAULT_VIEW };
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: 'low-power' });

    renderer.setPixelRatio(pixelRatio());
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.setClearColor(new THREE.Color('#101827'), 1);
    scene.add(new THREE.HemisphereLight(0xdce7ff, 0x111a2b, 1.6));
    const keyLight = new THREE.DirectionalLight(0xffffff, 1.8);
    keyLight.position.set(24, 38, 28);
    scene.add(keyLight);

    function scheduleRender() {
        if (disposed || raf) return;
        raf = requestAnimationFrame(() => {
            raf = undefined;
            renderer.render(scene, camera);
        });
    }

    function present() {
        if (disposed) return;
        if (reducedMotion) {
            if (raf) cancelAnimationFrame(raf);
            raf = undefined;
            renderer.render(scene, camera);
            return;
        }
        scheduleRender();
    }

    function applyCamera() {
        const cosine = Math.cos(view.pitch);
        camera.position.set(
            target.x + view.distance * cosine * Math.cos(view.yaw),
            target.y + view.distance * Math.sin(view.pitch),
            target.z + view.distance * cosine * Math.sin(view.yaw));
        camera.lookAt(target);
    }

    function announceSelection(node) {
        if (!selectionSummary) return;
        if (!node) {
            selectionSummary.textContent = 'Нет выбранного узла. Выберите узел, чтобы увидеть его безопасные агрегированные свойства и ближайшее окружение.';
            return;
        }
        const neighborCount = [...(nodeRecords.get(node.id)?.neighbors ?? [])].length;
        selectionSummary.replaceChildren(document.createTextNode(`Выбран ${node.label}: тип ${node.type}, степень ${node.degree}, важность ${node.importanceBand} из 5, уверенность ${node.confidenceBand} из 5. Ближайших соседей: ${neighborCount}. `));
        if (typeof node.href === 'string' && node.href.startsWith('/memory-reader/')) {
            const link = document.createElement('a');
            link.href = node.href;
            link.textContent = 'Открыть запись';
            selectionSummary.append(link);
        }
    }

    function updateSelection() {
        const selected = selectedId ? nodeRecords.get(selectedId) : undefined;
        const neighborhood = selected ? new Set([selectedId, ...selected.neighbors]) : undefined;
        for (const record of nodeRecords.values()) {
            const inNeighborhood = !neighborhood || neighborhood.has(record.node.id);
            record.group.scale.setScalar(record.node.id === selectedId ? 1.24 : inNeighborhood ? 1 : 0.8);
            record.material.opacity = record.baseOpacity * (inNeighborhood ? 1 : 0.18);
            record.ringMaterial.opacity = record.ringOpacity * (inNeighborhood ? 1 : 0.16);
        }
        for (const edge of edgeRecords) {
            const isNeighborEdge = selected && (edge.sourceId === selectedId || edge.targetId === selectedId);
            edge.material.opacity = selected ? (isNeighborEdge ? 1 : 0.16) : edge.baseOpacity;
            edge.material.linewidth = selected && isNeighborEdge ? 3 : 1;
            edge.material.color.setHex(selected && isNeighborEdge ? 0xffffff : edge.baseColor);
        }
        announceSelection(selected?.node);
        present();
    }

    function clearScene() {
        for (const object of sceneObjects) {
            scene.remove(object);
            disposeObject(object);
        }
        sceneObjects.length = 0;
        nodeRecords.clear();
        edgeRecords.length = 0;
    }

    function addNode(node, position, neighbors) {
        const radius = clamp(0.95 + Math.sqrt(node.degree) * 0.24, 0.95, 2.05);
        const appearance = appearanceFor(node.type);
        const color = new THREE.Color(appearance.color);
        const group = new THREE.Group();
        group.position.copy(position);
        group.userData.nodeId = node.id;
        const material = new THREE.MeshStandardMaterial({
            color,
            emissive: color.clone().multiplyScalar(0.15 + node.confidenceBand * 0.075),
            emissiveIntensity: 0.6,
            roughness: 0.36,
            metalness: 0.16,
            transparent: true,
            opacity: 0.42 + node.confidenceBand * 0.105
        });
        const outer = new THREE.Mesh(geometryFor(appearance.shape, radius), material);
        const inner = new THREE.Mesh(
            new THREE.SphereGeometry(radius * (0.24 + node.importanceBand * 0.065), 12, 10),
            new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.1 + node.importanceBand * 0.07 }));
        const ringMaterial = new THREE.MeshBasicMaterial({
            color,
            transparent: true,
            opacity: 0.14 + node.confidenceBand * 0.12,
            side: THREE.DoubleSide
        });
        const ring = new THREE.Mesh(new THREE.TorusGeometry(radius * 1.24, 0.045 + node.confidenceBand * 0.012, 7, 28), ringMaterial);
        ring.rotation.x = Math.PI * 0.5;
        group.add(outer, inner, ring);
        scene.add(group);
        sceneObjects.push(group);
        nodeRecords.set(node.id, {
            node,
            group,
            material,
            ringMaterial,
            baseOpacity: material.opacity,
            ringOpacity: ringMaterial.opacity,
            neighbors
        });
    }

    function addEdge(edge, positions) {
        const source = positions.get(edge.sourceId);
        const targetPosition = positions.get(edge.targetId);
        if (!source || !targetPosition) return;
        const normalizedWeight = clamp(Math.log2(edge.weight + 1) / 7, 0, 1);
        const material = new THREE.LineBasicMaterial({
            color: 0xa8b7d1,
            transparent: true,
            opacity: 0.48 + normalizedWeight * 0.32
        });
        const geometry = new THREE.BufferGeometry().setFromPoints([source, targetPosition]);
        const line = new THREE.Line(geometry, material);
        line.frustumCulled = false;
        scene.add(line);
        sceneObjects.push(line);
        edgeRecords.push({ ...edge, material, baseOpacity: material.opacity, baseColor: 0xa8b7d1 });
    }

    function resetView() {
        selectedId = undefined;
        target.set(0, 0, 0);
        view.yaw = DEFAULT_VIEW.yaw;
        view.pitch = DEFAULT_VIEW.pitch;
        view.distance = clamp(Math.max(DEFAULT_VIEW.distance, currentExtent * 2.3), MIN_DISTANCE, MAX_DISTANCE);
        // There is no camera tween: reduced-motion interactions paint synchronously in present().
        applyCamera();
        updateSelection();
    }

    function render(nextSnapshot, preserveView = false) {
        const priorView = preserveView ? { yaw: view.yaw, pitch: view.pitch, distance: view.distance, target: target.clone() } : undefined;
        const priorSelection = selectedId;
        snapshot = nextSnapshot;
        clearScene();
        const { adjacent, positions } = structuralLayout(snapshot);
        currentExtent = 24;
        for (const position of positions.values()) currentExtent = Math.max(currentExtent, position.length());
        for (const node of snapshot.nodes) addNode(node, positions.get(node.id), adjacent.get(node.id) ?? new Set());
        for (const edge of snapshot.edges) addEdge(edge, positions);
        if (!priorView) {
            resetView();
            return;
        }
        view.yaw = priorView.yaw;
        view.pitch = priorView.pitch;
        view.distance = clamp(priorView.distance, MIN_DISTANCE, MAX_DISTANCE);
        target.copy(priorView.target);
        selectedId = priorSelection && nodeRecords.has(priorSelection) ? priorSelection : undefined;
        applyCamera();
        updateSelection();
        present();
    }

    function zoom(multiplier) {
        view.distance = clamp(view.distance * multiplier, MIN_DISTANCE, MAX_DISTANCE);
        applyCamera();
        present();
    }

    function orbit(horizontal, vertical) {
        view.yaw += horizontal;
        view.pitch = clamp(view.pitch + vertical, -1.15, 1.15);
        applyCamera();
        present();
    }

    function pan(horizontal, vertical) {
        const forward = new THREE.Vector3();
        camera.getWorldDirection(forward);
        const right = new THREE.Vector3().crossVectors(forward, camera.up).normalize();
        const up = new THREE.Vector3().crossVectors(right, forward).normalize();
        target.addScaledVector(right, horizontal);
        target.addScaledVector(up, vertical);
        applyCamera();
        present();
    }

    function pick(clientX, clientY) {
        const rectangle = canvas.getBoundingClientRect();
        if (rectangle.width === 0 || rectangle.height === 0) return;
        pointer.set(
            ((clientX - rectangle.left) / rectangle.width) * 2 - 1,
            -((clientY - rectangle.top) / rectangle.height) * 2 + 1);
        raycaster.setFromCamera(pointer, camera);
        const matches = raycaster.intersectObjects([...nodeRecords.values()].map(record => record.group), true);
        let match = matches[0]?.object;
        while (match && !match.userData.nodeId) match = match.parent;
        selectedId = match?.userData.nodeId;
        updateSelection();
    }

    function onPointerDown(event) {
        canvas.focus({ preventScroll: true });
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        drag = { x: event.clientX, y: event.clientY, moved: false, pan: event.button !== 0 };
        canvas.setPointerCapture?.(event.pointerId);
        if (pointers.size === 2) {
            const [first, second] = [...pointers.values()];
            pinchDistance = Math.hypot(first.x - second.x, first.y - second.y);
        }
    }

    function onPointerMove(event) {
        if (!pointers.has(event.pointerId) || !drag) return;
        const previous = pointers.get(event.pointerId);
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        if (pointers.size === 2) {
            const [first, second] = [...pointers.values()];
            const distance = Math.hypot(first.x - second.x, first.y - second.y);
            if (pinchDistance) zoom(clamp(pinchDistance / distance, 0.92, 1.08));
            pinchDistance = distance;
            drag.moved = true;
            return;
        }
        const dx = event.clientX - previous.x;
        const dy = event.clientY - previous.y;
        if (Math.abs(event.clientX - drag.x) + Math.abs(event.clientY - drag.y) > 4) drag.moved = true;
        if (drag.pan) pan(-dx * 0.035, dy * 0.035);
        else orbit(-dx * 0.008, -dy * 0.008);
    }

    function onPointerUp(event) {
        const wasClick = drag && !drag.moved && pointers.size === 1;
        pointers.delete(event.pointerId);
        pinchDistance = undefined;
        canvas.releasePointerCapture?.(event.pointerId);
        if (wasClick) pick(event.clientX, event.clientY);
        drag = undefined;
    }

    function onWheel(event) {
        event.preventDefault();
        zoom(event.deltaY > 0 ? 1.12 : 0.88);
    }

    function onKeyDown(event) {
        const key = event.key.toLowerCase();
        if (key === '+' || key === '=') zoom(0.85);
        else if (key === '-') zoom(1.18);
        else if (key === 'home' || key === '0') resetView();
        else if (key === 'arrowleft') orbit(-0.13, 0);
        else if (key === 'arrowright') orbit(0.13, 0);
        else if (key === 'arrowup') orbit(0, 0.1);
        else if (key === 'arrowdown') orbit(0, -0.1);
        else if (key === 'q') pan(-2.4, 0);
        else if (key === 'e') pan(2.4, 0);
        else if (key === 'escape') {
            selectedId = undefined;
            updateSelection();
        } else return;
        event.preventDefault();
    }

    function resize() {
        const width = Math.max(Math.floor(canvas.clientWidth), 1);
        const height = Math.max(Math.floor(canvas.clientHeight), 1);
        renderer.setPixelRatio(pixelRatio());
        renderer.setSize(width, height, false);
        camera.aspect = width / height;
        camera.updateProjectionMatrix();
        applyCamera();
        present();
    }

    function onContextLost(event) {
        event.preventDefault();
        if (!disposed) onUnavailable?.('context-lost');
    }

    function onContextMenu(event) {
        event.preventDefault();
    }

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('keydown', onKeyDown);
    canvas.addEventListener('contextmenu', onContextMenu);
    canvas.addEventListener('webglcontextlost', onContextLost, false);
    resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(canvas);
    resize();

    return {
        render,
        zoomIn: () => zoom(0.85),
        zoomOut: () => zoom(1.18),
        reset: resetView,
        selectNode(nodeId) {
            selectedId = nodeRecords.has(nodeId) ? nodeId : undefined;
            updateSelection();
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            if (raf) cancelAnimationFrame(raf);
            resizeObserver?.disconnect();
            canvas.removeEventListener('pointerdown', onPointerDown);
            canvas.removeEventListener('pointermove', onPointerMove);
            canvas.removeEventListener('pointerup', onPointerUp);
            canvas.removeEventListener('pointercancel', onPointerUp);
            canvas.removeEventListener('wheel', onWheel);
            canvas.removeEventListener('keydown', onKeyDown);
            canvas.removeEventListener('contextmenu', onContextMenu);
            canvas.removeEventListener('webglcontextlost', onContextLost);
            clearScene();
            renderer.dispose();
            renderer.forceContextLoss?.();
        }
    };
}
