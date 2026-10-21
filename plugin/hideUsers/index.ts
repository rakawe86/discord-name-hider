/*
 * HideUsers - a Vencord user plugin.
 * Hides chosen people from your own Discord client: friends list, DM list,
 * messages, typing indicators, notifications and member lists.
 * Only affects what YOU see. Nothing is sent to Discord.
 *
 * The list lives in %APPDATA%\DiscordNameHider\state.json (edited by
 * DiscordNameHider.exe) and is re-read every second, so hiding/showing
 * is live. Ctrl+Shift+H toggles hiding on/off from inside Discord.
 */

import definePlugin, { PluginNative } from "@utils/types";
import { findStoreLazy } from "@webpack";
import { ChannelStore, FluxDispatcher, MessageActions, MessageStore, RelationshipStore, showToast, UserStore } from "@webpack/common";

const Native = VencordNative.pluginHelpers.HideUsers as PluginNative<typeof import("./native")>;
const PrivateChannelSortStore = findStoreLazy("PrivateChannelSortStore");

interface Entry { value: string; label?: string; hidden: boolean; }
interface State { enabled: boolean; users: Entry[]; }

let state: State = { enabled: true, users: [] };
let lastMtime = 0;
let active = new Set<string>(); // lowercased ids/usernames currently hidden
const droppedChannels = new Set<string>();

function rebuildActive() {
    active = new Set(
        state.enabled
            ? state.users.filter(u => u.hidden).map(u => u.value.trim().toLowerCase().replace(/^@/, "")).filter(Boolean)
            : []
    );
}

function isHidden(userId?: string | null): boolean {
    if (!userId || active.size === 0) return false;
    if (active.has(userId)) return true;
    const user: any = UserStore.getUser(userId);
    if (!user) return false;
    return active.has(user.username?.toLowerCase()) || active.has(user.globalName?.toLowerCase());
}

function isHiddenDm(channelId: string): boolean {
    const channel: any = ChannelStore.getChannel(channelId);
    // type 1 = one-to-one DM. Group DMs stay; their messages are hidden.
    return channel?.type === 1 && channel.recipients?.some((id: string) => isHidden(id));
}

/* ---------- Flux interceptor: silence live events from hidden people ---------- */

function interceptor(action: any): boolean {
    switch (action.type) {
        case "MESSAGE_CREATE":
            if (isHidden(action.message?.author?.id)) {
                droppedChannels.add(action.channelId ?? action.message.channel_id);
                return true; // no message, no unread badge, no notification
            }
            return false;
        case "TYPING_START":
        case "SPEAKING":
            return isHidden(action.userId);
    }
    return false;
}

/* ---------- Store wrappers: friends list + DM list ---------- */

const restores: (() => void)[] = [];

function wrap(store: any, method: string, filter: (result: any) => any) {
    if (!store || typeof store[method] !== "function") return;
    const original = store[method];
    store[method] = function (...args: any[]) {
        const result = original.apply(this, args);
        return active.size ? filter(result) : result;
    };
    restores.push(() => { store[method] = original; });
}

function wrapStores() {
    wrap(RelationshipStore, "getFriendIDs", ids => Array.isArray(ids) ? ids.filter(id => !isHidden(id)) : ids);
    wrap(RelationshipStore, "getRelationships", rels => {
        if (!rels || typeof rels !== "object") return rels;
        const out: Record<string, any> = {};
        for (const id in rels) if (!isHidden(id)) out[id] = rels[id];
        return out;
    });
    wrap(RelationshipStore, "getMutableRelationships", rels =>
        rels instanceof Map ? new Map([...rels].filter(([id]) => !isHidden(id))) : rels);

    wrap(PrivateChannelSortStore, "getPrivateChannelIds", ids =>
        Array.isArray(ids) ? ids.filter(id => !isHiddenDm(id)) : ids);
    wrap(PrivateChannelSortStore, "getSortedChannels", list =>
        Array.isArray(list) ? list.filter((c: any) => !isHiddenDm(c?.channelId ?? c?.id)) : list);
    wrap(ChannelStore, "getSortedPrivateChannels", list =>
        Array.isArray(list) ? list.filter((c: any) => !isHiddenDm(c?.id)) : list);
}

/* ---------- DOM tagging + CSS: messages, member lists, reactions ---------- */
// Every message/member row gets data-vc-hu-uid="<authorId>". A generated
// stylesheet hides the tagged rows of hidden people, so toggling is instant
// and nothing is lost.

const STYLE_ID = "vc-hideusers-style";
let observer: MutationObserver | null = null;

function tag(root: ParentNode) {
    root.querySelectorAll<HTMLElement>("li[id^='chat-messages-']:not([data-vc-hu-uid])").forEach(li => {
        const m = li.id.match(/^chat-messages-(\d+)-(\d+)$/);
        const author = m && (MessageStore.getMessage(m[1], m[2]) as any)?.author?.id;
        if (author) li.dataset.vcHuUid = author;
    });
    root.querySelectorAll<HTMLImageElement>("img[src*='/avatars/'], img[src*='/users/']").forEach(img => {
        const m = img.src.match(/\/(?:avatars|users)\/(\d{15,21})\//);
        if (!m) return;
        const row = img.closest<HTMLElement>("[class*='member_'], [class*='voiceUser_'], [class*='reactor_'], [class*='peopleListItem_']");
        if (row && !row.dataset.vcHuUid) row.dataset.vcHuUid = m[1];
    });
}

function updateCss() {
    let style = document.getElementById(STYLE_ID);
    if (!style) {
        style = document.createElement("style");
        style.id = STYLE_ID;
        document.head.appendChild(style);
    }
    const ids = new Set<string>();
    document.querySelectorAll<HTMLElement>("[data-vc-hu-uid]").forEach(el => {
        if (isHidden(el.dataset.vcHuUid)) ids.add(el.dataset.vcHuUid!);
    });
    for (const v of active) if (/^\d{15,21}$/.test(v)) ids.add(v);
    style.textContent = [...ids].map(id => `[data-vc-hu-uid="${id}"]`).join(",") + (ids.size ? "{display:none!important}" : "");
}

function startDom() {
    tag(document);
    updateCss();
    observer = new MutationObserver(muts => {
        let added = false;
        for (const m of muts) m.addedNodes.forEach(n => {
            if (n instanceof Element) { tag(n.parentNode ?? n); added = true; }
        });
        if (added && active.size) updateCss();
    });
    observer.observe(document.body, { childList: true, subtree: true });
}

function stopDom() {
    observer?.disconnect();
    observer = null;
    document.getElementById(STYLE_ID)?.remove();
    document.querySelectorAll<HTMLElement>("[data-vc-hu-uid]").forEach(el => delete el.dataset.vcHuUid);
}

/* ---------- State sync ---------- */

function applyState(prevActive: Set<string>) {
    rebuildActive();
    updateCss();
    for (const store of [RelationshipStore, PrivateChannelSortStore, ChannelStore] as any[]) {
        try { store?.emitChange?.(); } catch { }
    }
    // People were shown again: reload chats where their live messages were skipped.
    const shownAgain = [...prevActive].some(v => !active.has(v));
    if (shownAgain && droppedChannels.size) {
        for (const channelId of droppedChannels) {
            try { (MessageActions as any).fetchMessages?.({ channelId, limit: 50 }); } catch { }
        }
        droppedChannels.clear();
    }
}

async function poll() {
    const res = await Native.readState(lastMtime).catch(() => null);
    if (!res) return;
    lastMtime = res.mtime;
    let next: State = { enabled: true, users: [] };
    try {
        const parsed = JSON.parse(res.text || "{}");
        next = { enabled: parsed.enabled !== false, users: Array.isArray(parsed.users) ? parsed.users : [] };
    } catch { return; }
    const prev = active;
    state = next;
    applyState(prev);
}

function onKey(e: KeyboardEvent) {
    if (!(e.ctrlKey && e.shiftKey && e.key.toLowerCase() === "h")) return;
    e.preventDefault();
    const prev = active;
    state.enabled = !state.enabled;
    applyState(prev);
    Native.writeState(JSON.stringify(state, null, 2)).catch(() => { });
    try { showToast({ text: state.enabled ? "Hiding ON" : "Hiding OFF - everyone visible", variant: "success" }); } catch { }
}

let timer: ReturnType<typeof setInterval> | undefined;

export default definePlugin({
    name: "HideUsers",
    description: "Hide chosen people from your friends list, DMs, messages and member lists (only on your screen). Managed by DiscordNameHider.exe; Ctrl+Shift+H toggles.",
    authors: [{ name: "rakawe86", id: 0n }],
    enabledByDefault: true,

    async start() {
        await poll();
        (FluxDispatcher as any).addInterceptor(interceptor);
        wrapStores();
        startDom();
        applyState(new Set());
        timer = setInterval(poll, 1000);
        document.addEventListener("keydown", onKey);
    },

    stop() {
        clearInterval(timer);
        document.removeEventListener("keydown", onKey);
        const list = (FluxDispatcher as any)._interceptors;
        if (Array.isArray(list)) {
            const i = list.indexOf(interceptor);
            if (i !== -1) list.splice(i, 1);
        }
        restores.splice(0).forEach(r => r());
        stopDom();
    }
});
