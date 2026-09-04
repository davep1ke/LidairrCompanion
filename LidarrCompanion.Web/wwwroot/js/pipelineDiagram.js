// Positions the SVG connector lines/arrowheads in the Settings pipeline diagram against the
// actual rendered positions of the boxes they connect. This is a direct port of the WPF
// Settings.xaml.cs geometry (BuildElementDictionary/ConnectElements/GetRectIntersectionPoint/
// SetLine) - box coloring and hover-highlight logic all stay in C#/Razor (that's ordinary
// server-rendered state), but *where pixels actually land* is fundamentally a DOM/layout
// question Blazor Server has no visibility into, so only that part lives here.

function rectOf(containerEl, containerRect, key) {
    const el = containerEl.querySelector(`[data-box-key="${CSS.escape(key)}"]`);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    const left = r.left - containerRect.left;
    const top = r.top - containerRect.top;
    return {
        left, top,
        right: left + r.width,
        bottom: top + r.height,
        centerX: left + r.width / 2,
        centerY: top + r.height / 2
    };
}

// Finds where the line from a to b crosses the edge of rect, offset a few px along the line
// (so the line/arrowhead stops just short of the box rather than overlapping its border).
function intersect(rect, a, b, preferNearA) {
    const dx1 = b.x - a.x, dy1 = b.y - a.y;
    const corners = [
        { x: rect.left, y: rect.top }, { x: rect.right, y: rect.top },
        { x: rect.right, y: rect.bottom }, { x: rect.left, y: rect.bottom }
    ];

    const candidates = [];
    for (let i = 0; i < 4; i++) {
        const p1 = corners[i], p2 = corners[(i + 1) % 4];
        const dx2 = p2.x - p1.x, dy2 = p2.y - p1.y;
        const denom = dx1 * dy2 - dy1 * dx2;
        if (Math.abs(denom) < 1e-9) continue;

        const t = ((p1.x - a.x) * dy2 - (p1.y - a.y) * dx2) / denom;
        const u = ((p1.x - a.x) * dy1 - (p1.y - a.y) * dx1) / denom;
        if (u >= -1e-9 && u <= 1 + 1e-9 && t >= -1e-9 && t <= 1 + 1e-9) {
            candidates.push({ x: a.x + t * dx1, y: a.y + t * dy1, t });
        }
    }
    if (candidates.length === 0) return null;

    const best = preferNearA
        ? candidates.filter(c => c.t >= 0).reduce((m, c) => (!m || c.t < m.t) ? c : m, null)
        : candidates.filter(c => c.t <= 1).reduce((m, c) => (!m || c.t > m.t) ? c : m, null);
    if (!best) return null;

    const vx = b.x - a.x, vy = b.y - a.y;
    const len = Math.sqrt(vx * vx + vy * vy) || 1;
    const offset = preferNearA ? 4 : -4;
    return { x: best.x + (vx / len) * offset, y: best.y + (vy / len) * offset };
}

export function positionLines(containerEl) {
    if (!containerEl) return;
    const containerRect = containerEl.getBoundingClientRect();

    containerEl.querySelectorAll('line[data-src]').forEach(line => {
        const rectA = rectOf(containerEl, containerRect, line.getAttribute('data-src'));
        const rectB = rectOf(containerEl, containerRect, line.getAttribute('data-dst'));
        if (!rectA || !rectB) return;

        const centerA = { x: rectA.centerX, y: rectA.centerY };
        const centerB = { x: rectB.centerX, y: rectB.centerY };
        const start = intersect(rectA, centerA, centerB, true) || centerA;
        const end = intersect(rectB, centerA, centerB, false) || centerB;

        line.setAttribute('x1', start.x);
        line.setAttribute('y1', start.y);
        line.setAttribute('x2', end.x);
        line.setAttribute('y2', end.y);

        const headId = line.getAttribute('data-head');
        const head = headId && containerEl.querySelector(`#${CSS.escape(headId)}`);
        if (head) {
            const angle = Math.atan2(end.y - start.y, end.x - start.x);
            const size = 10;
            const p1 = `${end.x},${end.y}`;
            const p2 = `${end.x - size * Math.cos(angle - Math.PI / 6)},${end.y - size * Math.sin(angle - Math.PI / 6)}`;
            const p3 = `${end.x - size * Math.cos(angle + Math.PI / 6)},${end.y - size * Math.sin(angle + Math.PI / 6)}`;
            head.setAttribute('points', `${p1} ${p2} ${p3}`);
        }
    });
}

const observers = new WeakMap();

// Debounced (150ms, matching the original WPF resize timer) redraw on container resize.
export function watchResize(containerEl) {
    if (!containerEl || observers.has(containerEl)) return;

    let debounceTimer = null;
    const observer = new ResizeObserver(() => {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => positionLines(containerEl), 150);
    });
    observer.observe(containerEl);
    observers.set(containerEl, observer);
}

export function unwatchResize(containerEl) {
    const observer = observers.get(containerEl);
    if (observer) {
        observer.disconnect();
        observers.delete(containerEl);
    }
}
