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
// channelId -> authors whose live messages were held back while hidden
const dropped = new Map<string, Set<string>>();
// the held-back MESSAGE_CREATE events, replayed if a channel can't be re-downloaded
const held: any[] = [];

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
                const channelId = action.channelId ?? action.message.channel_id;
                if (!dropped.has(channelId)) dropped.set(channelId, new Set());
                dropped.get(channelId)!.add(action.message.author.id);
                held.push(action);
                if (held.length > 500) held.shift();
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

const ID_RE = /^\d{15,21}$/;

// Rows that show one person, and which React props carry that person's ID.
const ROWS: [selector: string, pick: (p: any) => any][] = [
    ["[class*='member_']", p => p.user?.id],                   // server member list, group DM members
    ["[class*='voiceUser_']", p => p.user?.id],                // voice channel users
    ["[class*='reactor_']", p => p.user?.id],                  // reaction popout
    ["[class*='peopleListItem_']", p => p.user?.id],           // friends list rows
    ["[class*='itemCard_']", p => p.user?.id ?? p.party?.priorityMembers?.[0]?.user?.id], // Active Now
    ["[class~='mention']", p => p.userId],                     // @mentions inside messages
    ["[class*='searchResult_']", p => p.message?.author?.id],  // search results
    ["[class*='messageGroupWrapper_']", p => p.message?.author?.id] // pins / inbox
];

function reactFiber(el: Element): any {
    for (const k in el) if (k.startsWith("__reactFiber$")) return (el as any)[k];
    return null;
}

function idFromFiber(el: Element, pick: (p: any) => any): string | undefined {
    let f = reactFiber(el);
    for (let i = 0; f && i < 15; i++, f = f.return) {
        if (!f.memoizedProps) continue;
        let id: any;
        try { id = pick(f.memoizedProps); } catch { }
        if (typeof id === "string" && ID_RE.test(id)) return id;
    }
}

function setTag(el: HTMLElement, id: string | undefined) {
    if (id) { if (el.dataset.vcHuUid !== id) el.dataset.vcHuUid = id; }
    else if (el.dataset.vcHuUid) delete el.dataset.vcHuUid;
}

function tag(root: ParentNode) {
    root.querySelectorAll<HTMLElement>("li[id^='chat-messages-']").forEach(li => {
        const m = li.id.match(/^chat-messages-(\d+)-(\d+)$/);
        const msg: any = m && MessageStore.getMessage(m[1], m[2]);
        setTag(li, msg?.author?.id);
        // "Replying to <hidden person>" header on someone else's message
        const reply = li.querySelector<HTMLElement>("[class*='repliedMessage_']");
        if (reply) {
            const ref = msg?.messageReference;
            const refMsg: any = ref && MessageStore.getMessage(ref.channel_id, ref.message_id);
            setTag(reply, refMsg?.author?.id ?? idFromFiber(reply, p => p.referencedMessage?.message?.author?.id));
        }
    });
    for (const [selector, pick] of ROWS)
        root.querySelectorAll<HTMLElement>(selector).forEach(el => setTag(el, idFromFiber(el, pick)));
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
    tag(document);
    updateCss();
    for (const store of [RelationshipStore, PrivateChannelSortStore, ChannelStore] as any[]) {
        try { store?.emitChange?.(); } catch { }
    }
    // People were shown again: re-download chats where their live messages were held back,
    // so those messages appear in the right place.
    const shownAgain = [...prevActive].some(v => !active.has(v));
    if (shownAgain) restoreHeldMessages();
}

function restoreHeldMessages() {
    const fetchMessages = (MessageActions as any)?.fetchMessages;
    const replay: any[] = [];

    for (const [channelId, authors] of dropped) {
        for (const a of [...authors]) if (!isHidden(a)) authors.delete(a);
        if (authors.size) continue; // someone in this chat is still hidden
        dropped.delete(channelId);

        const mine = held.filter(a => (a.channelId ?? a.message.channel_id) === channelId);
        for (const a of mine) held.splice(held.indexOf(a), 1);

        let reloaded = false;
        if (typeof fetchMessages === "function") {
            try { fetchMessages.call(MessageActions, { channelId, limit: 50 }); reloaded = true; } catch { }
        }
        if (!reloaded) replay.push(...mine);
    }

    // Fallback: put the held messages back, oldest first (IDs are time-ordered).
    replay.sort((a, b) => (BigInt(a.message.id) < BigInt(b.message.id) ? -1 : 1));
    for (const action of replay) {
        try { FluxDispatcher.dispatch(action); } catch { }
    }
}

async function poll() {
    // Re-check visible rows: Discord recycles list elements while scrolling.
    if (active.size) { tag(document); updateCss(); }

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
