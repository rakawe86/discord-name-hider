/*
 * HideUsers - a Vencord user plugin.
 * Hides chosen people from your own Discord client: friends list, DM list,
 * messages, typing indicators, notifications and (best effort) member lists.
 * Only affects what YOU see. Nothing is sent to Discord.
 */

import { definePluginSettings } from "@api/Settings";
import definePlugin, { OptionType } from "@utils/types";
import { findStoreLazy } from "@webpack";
import { ChannelStore, FluxDispatcher, RelationshipStore, UserStore } from "@webpack/common";

const PrivateChannelSortStore = findStoreLazy("PrivateChannelSortStore");

const settings = definePluginSettings({
    users: {
        type: OptionType.STRING,
        description: "People to hide: user IDs or usernames, separated by commas (e.g. 123456789012345678, someusername)",
        default: "",
        onChange: () => refresh()
    }
});

let hidden = new Set<string>();

function parseList() {
    hidden = new Set(
        settings.store.users
            .split(/[,\n]/)
            .map(s => s.trim().toLowerCase().replace(/^@/, ""))
            .filter(Boolean)
    );
}

function isHidden(userId?: string | null): boolean {
    if (!userId || hidden.size === 0) return false;
    if (hidden.has(userId)) return true;
    const user: any = UserStore.getUser(userId);
    if (!user) return false;
    return hidden.has(user.username?.toLowerCase())
        || hidden.has(user.globalName?.toLowerCase());
}

function isHiddenDm(channelId: string): boolean {
    const channel: any = ChannelStore.getChannel(channelId);
    // type 1 = one-to-one DM. Group DMs stay visible; their messages are filtered.
    return channel?.type === 1 && channel.recipients?.some((id: string) => isHidden(id));
}

/* ---------- Flux interceptor: drop/filter incoming events ---------- */

function interceptor(action: any): boolean {
    switch (action.type) {
        case "MESSAGE_CREATE":
        case "MESSAGE_UPDATE":
            return isHidden(action.message?.author?.id);
        case "LOAD_MESSAGES_SUCCESS":
        case "LOAD_MESSAGES_AROUND_SUCCESS":
        case "LOAD_RECENT_MENTIONS_SUCCESS":
        case "LOAD_PINNED_MESSAGES_SUCCESS":
            if (Array.isArray(action.messages))
                action.messages = action.messages.filter((m: any) => !isHidden(m?.author?.id));
            return false;
        case "SEARCH_FINISH":
            if (Array.isArray(action.messages))
                action.messages = action.messages.filter((group: any[]) => !group?.some(m => isHidden(m?.author?.id)));
            return false;
        case "TYPING_START":
            return isHidden(action.userId);
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
        return filter(original.apply(this, args));
    };
    restores.push(() => { store[method] = original; });
}

function filterIdArray(ids: any) {
    return Array.isArray(ids) ? ids.filter(id => !isHidden(id)) : ids;
}

function wrapStores() {
    // Friends list
    wrap(RelationshipStore, "getFriendIDs", filterIdArray);
    wrap(RelationshipStore, "getRelationships", rels => {
        if (!rels || typeof rels !== "object") return rels;
        const out: Record<string, any> = {};
        for (const id in rels) if (!isHidden(id)) out[id] = rels[id];
        return out;
    });
    wrap(RelationshipStore, "getMutableRelationships", rels => {
        if (!(rels instanceof Map)) return rels;
        return new Map([...rels].filter(([id]) => !isHidden(id)));
    });

    // DM list
    wrap(PrivateChannelSortStore, "getPrivateChannelIds", ids =>
        Array.isArray(ids) ? ids.filter(id => !isHiddenDm(id)) : ids);
    wrap(PrivateChannelSortStore, "getSortedChannels", list =>
        Array.isArray(list) ? list.filter((c: any) => !isHiddenDm(c?.channelId ?? c?.id)) : list);
    wrap(ChannelStore, "getSortedPrivateChannels", list =>
        Array.isArray(list) ? list.filter((c: any) => !isHiddenDm(c?.id)) : list);
}

/* ---------- DOM sweeper: member lists, reactions, anything left over ---------- */

const STYLE_ID = "vc-hideusers-style";
let observer: MutationObserver | null = null;

function sweep(root: ParentNode = document) {
    if (hidden.size === 0) return;
    root.querySelectorAll<HTMLImageElement>("img[src*='/avatars/'], img[src*='/users/']").forEach(img => {
        const m = img.src.match(/\/(?:avatars|users)\/(\d{15,21})\//);
        if (!m || !isHidden(m[1])) return;
        const row = img.closest("[class*='member_'], [class*='voiceUser_'], [class*='reactor_'], [class*='friendsRow'], li");
        if (row) (row as HTMLElement).dataset.vcHideusers = "1";
    });
}

function startDom() {
    if (!document.getElementById(STYLE_ID)) {
        const style = document.createElement("style");
        style.id = STYLE_ID;
        style.textContent = "[data-vc-hideusers='1']{display:none!important}";
        document.head.appendChild(style);
    }
    sweep();
    observer = new MutationObserver(muts => {
        for (const m of muts) m.addedNodes.forEach(n => n instanceof Element && sweep(n));
    });
    observer.observe(document.body, { childList: true, subtree: true });
}

function stopDom() {
    observer?.disconnect();
    observer = null;
    document.getElementById(STYLE_ID)?.remove();
    document.querySelectorAll<HTMLElement>("[data-vc-hideusers]").forEach(el => delete el.dataset.vcHideusers);
}

function refresh() {
    parseList();
    document.querySelectorAll<HTMLElement>("[data-vc-hideusers]").forEach(el => delete el.dataset.vcHideusers);
    sweep();
    for (const store of [RelationshipStore, PrivateChannelSortStore, ChannelStore] as any[]) {
        try { store?.emitChange?.(); } catch { }
    }
}

export default definePlugin({
    name: "HideUsers",
    description: "Completely hide chosen people from your friends list, DMs, messages and member lists (only on your screen).",
    authors: [{ name: "you", id: 0n }],
    settings,

    start() {
        parseList();
        (FluxDispatcher as any).addInterceptor(interceptor);
        wrapStores();
        startDom();
        refresh();
    },

    stop() {
        const list = (FluxDispatcher as any)._interceptors;
        if (Array.isArray(list)) {
            const i = list.indexOf(interceptor);
            if (i !== -1) list.splice(i, 1);
        }
        restores.splice(0).forEach(r => r());
        stopDom();
    }
});
