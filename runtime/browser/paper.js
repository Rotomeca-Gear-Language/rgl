(() => {
    "use strict";

    const ranges = new Map();

    function scan(root = document) {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_COMMENT);
        const starts = new Map();
        while (walker.nextNode()) {
            const text = walker.currentNode.data.trim();
            let match = /^rgl:f(\d+)$/.exec(text);
            if (match) starts.set(Number(match[1]), walker.currentNode);
            match = /^rgl:\/f(\d+)$/.exec(text);
            if (match && starts.has(Number(match[1])))
                ranges.set(Number(match[1]), { start: starts.get(Number(match[1])), end: walker.currentNode });
        }
    }

    function patch(id, html) {
        const range = ranges.get(id);
        if (!range) return false;
        let node = range.start.nextSibling;
        while (node && node !== range.end) {
            const next = node.nextSibling;
            node.remove();
            node = next;
        }
        const template = document.createElement("template");
        template.innerHTML = html;
        range.end.parentNode.insertBefore(template.content, range.end);
        scan(range.end.parentNode);
        return true;
    }

    const loaded = new Set();

    function applyHead(head) {
        if (typeof head.title === "string") document.title = head.title;
        if (head.env) window.RGL_ENV = Object.assign(window.RGL_ENV || {}, head.env);
        for (const url of head.scripts || []) {
            if (loaded.has(url)) continue;
            loaded.add(url);
            const script = document.createElement("script");
            script.src = url;
            document.body.appendChild(script);
        }
    }

    for (const script of document.querySelectorAll("script[src]")) loaded.add(script.getAttribute("src"));

    let activeSocket = null;

    function connect(url) {
        const socket = new WebSocket(url);
        activeSocket = socket;
        socket.addEventListener("message", event => {
            const message = JSON.parse(event.data);
            if (message.type === "fragment") patch(message.id, message.html);
            if (message.type === "fragments")
                for (const fragment of message.items) patch(fragment.id, fragment.html);
            if (message.head) applyHead(message.head);
            if (message.type === "reload") location.reload();
        });
        return socket;
    }

    for (const type of ["click", "input", "change", "submit", "keydown", "keyup"]) {
        document.addEventListener(type, event => {
            const target = event.target?.closest?.(`[data-rgl-on-${type}]`);
            if (!target || !activeSocket || activeSocket.readyState !== WebSocket.OPEN) return;
            const clean = value => String(value ?? "").replaceAll("\r", " ").replaceAll("\n", " ");
            activeSocket.send([
                target.getAttribute(`data-rgl-on-${type}`), type, target.id || "",
                "value" in target ? target.value : "", "key" in event ? event.key : ""
            ].map(clean).join("\n"));
        }, true);
    }

    const wasmReady = WebAssembly.instantiateStreaming(fetch("./rgl-paper.wasm"), {})
        .then(result => result.instance.exports)
        .catch(() => null);

    scan();
    window.RglPaper = Object.freeze({ scan, patch, connect, wasmReady });
})();
