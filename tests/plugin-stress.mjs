// Tries to break the HideUsers plugin against a fake Discord (no real Discord involved).
// Run: node tests/plugin-stress.mjs [pluginDir]   (needs the Vencord checkout from build-exe.ps1 for esbuild)
import { createRequire } from "module";
import { mkdtempSync, renameSync, rmSync, writeFileSync, readFileSync } from "fs";
import { homedir, tmpdir } from "os";
import { join, resolve } from "path";
import { pathToFileURL } from "url";

const here = new URL(".", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const pluginDir = resolve(process.argv[2] ?? join(here, "../plugin/hideUsers"));
const esbuild = createRequire(join(homedir(), "Vencord/package.json"))("esbuild");
const box = mkdtempSync(join(tmpdir(), "dnh-plugin-"));
const stateFile = join(box, "DiscordNameHider", "state.json");

// ---------- fake Discord ----------
const ME = "100000000000000001", HIDDEN = "200000000000000002", OTHER = "300000000000000003";
const users = { [ME]: { username: "me" }, [HIDDEN]: { username: "hiddenguy" }, [OTHER]: { username: "other" } };
const channels = { dm1: { type: 1, recipients: [HIDDEN] }, dm2: { type: 1, recipients: [OTHER] }, group: { type: 3, recipients: [HIDDEN, OTHER] }, srv: { type: 0 } };
globalThis.__notices = [];
let interceptors, fetches, breakUsers = false, selected = "srv", voice = {};
const plainStore = extra => ({ emitChange() { }, ...extra });
globalThis.__fake = {
    FluxDispatcher: { get _interceptors() { return interceptors; }, addInterceptor(f) { interceptors.push(f); }, dispatch() { } },
    UserStore: { getUser(id) { if (breakUsers) throw new Error("store exploded"); return users[id]; }, getCurrentUser: () => ({ id: ME }) },
    ChannelStore: plainStore({ getChannel: id => channels[id], getSortedPrivateChannels: () => [] }),
    RelationshipStore: plainStore({ getFriendIDs: () => [HIDDEN, OTHER], getRelationships: () => ({ [HIDDEN]: 1, [OTHER]: 1 }), getMutableRelationships: () => new Map([[HIDDEN, 1], [OTHER, 1]]) }),
    MessageStore: { getMessage: () => null },
    MessageActions: { fetchMessages: a => fetches.push(a.channelId) },
    SelectedChannelStore: { getChannelId: () => selected },
    VoiceStateStore: { getVoiceStatesForChannel: id => voice[id] ?? {} },
    sortStore: plainStore({ getPrivateChannelIds: () => ["dm1", "dm2"] }),
    showToast() { }
};
globalThis.document = { querySelectorAll: () => [], getElementById: () => null, createElement: () => ({ textContent: "" }), head: { appendChild() { } }, body: {}, addEventListener(t, f) { if (t === "keydown") globalThis.__key = f; }, removeEventListener() { } };
globalThis.MutationObserver = class { observe() { } disconnect() { } };
let ticker; globalThis.setInterval = f => { ticker = f; return 1; }; globalThis.clearInterval = () => { };

const stubs = {
    "@utils/types": "export default p => p;",
    "@webpack": "export const findStoreLazy = () => globalThis.__fake.sortStore;",
    "@webpack/common": Object.keys(globalThis.__fake).filter(k => k !== "sortStore").map(k => `export const ${k} = globalThis.__fake.${k};`).join(""),
    electron: `export const app = { getPath: () => ${JSON.stringify(box)} }; export const shell = { openExternal: u => globalThis.__opened = u };`,
    "@api/Notices": "export const showNotice = (m, b, f) => globalThis.__notices.push({ m, f }); export const popNotice = () => {};"
};
const plugin = { name: "stubs", setup(b) {
    b.onResolve({ filter: /^(@utils\/types|@webpack|@webpack\/common|@api\/Notices|electron)$/ }, a => ({ path: a.path, namespace: "stub" }));
    b.onLoad({ filter: /.*/, namespace: "stub" }, a => ({ contents: stubs[a.path], loader: "js" }));
} };
async function load(file, name) {
    const out = join(box, name);
    await esbuild.build({ entryPoints: [join(pluginDir, file)], bundle: true, format: "esm", platform: "node", outfile: out, plugins: [plugin], logLevel: "silent" });
    return import(pathToFileURL(out).href);
}
const native = await load("native.ts", "native.mjs");
globalThis.VencordNative = { pluginHelpers: { HideUsers: { readState: async m => native.readState(null, m), writeState: async t => native.writeState(null, t) } } };
const hide = (await load("index.ts", "index.mjs")).default;

// ---------- helpers ----------
let fails = 0;
const check = (name, ok, detail = "") => { if (!ok) fails++; console.log(`${ok ? "PASS" : "FAIL"}  ${name}${ok ? "" : "  " + detail}`); };
const tick = async () => { await new Promise(r => setTimeout(r, 15)); await ticker(); };
async function setList(users_, enabled = true) { native.writeState(null, JSON.stringify({ enabled, users: users_ })); await tick(); }
const blocked = a => { try { return interceptors.some(f => f(a)); } catch (e) { return "THROW:" + e.message; } };
const msg = (id, channelId, username, mid = "900") => ({ type: "MESSAGE_CREATE", channelId, message: { id: mid, channel_id: channelId, author: { id, username } } });
const person = (value, hidden = true) => ({ value, label: "", hidden });
async function fresh(list = [person(HIDDEN)]) { interceptors = []; fetches = []; breakUsers = false; selected = "srv"; voice = {}; hide.stop(); await hide.start(); await setList(list); fetches = []; }

// ---------- tests ----------
await fresh();
check("hidden person's message dropped", blocked(msg(HIDDEN, "srv")) === true);
check("visible person's message kept", blocked(msg(OTHER, "srv")) === false);
check("typing from hidden person dropped", blocked({ type: "TYPING_START", userId: HIDDEN }) === true);

await fresh([person(ME), person("me")]);
check("putting your OWN name on the list never eats your messages", blocked(msg(ME, "srv", "me")) === false);

await fresh([person("stranger")]);
check("hidden by username, person not cached yet: still dropped", blocked(msg("400000000000000004", "srv", "Stranger")) === true);

await fresh();
for (const a of [{}, { type: "MESSAGE_CREATE" }, { type: "MESSAGE_CREATE", message: {} }, { type: "VOICE_STATE_UPDATES" }, { type: "VOICE_STATE_UPDATES", voiceStates: [null, {}] }, { type: "CALL_CREATE" }, { type: "CALL_DELETE" }, { type: "TYPING_START" }])
    check(`malformed event ${JSON.stringify(a)} doesn't break Discord`, blocked(a) === false, String(blocked(a)));
breakUsers = true;
check("internal error never blocks Discord's event system", blocked(msg(OTHER, "srv")) === false, String(blocked(msg(OTHER, "srv"))));
breakUsers = false;

await fresh();
for (const bad of ['{"enabled":true,"users":[null,{"value":null},{"value":5},{"hidden":true},{"value":"' + HIDDEN + '","hidden":true}]}']) {
    native.writeState(null, bad); let threw = null; try { await tick(); } catch (e) { threw = e.message; }
    check("bad entries in the list are skipped, good ones still hide", !threw && blocked(msg(HIDDEN, "srv")) === true, threw ?? "person not hidden");
}

await fresh();
const good = readFileSync(stateFile, "utf8");
writeFileSync(stateFile, good.slice(0, 10)); await tick();
check("half-written file keeps people hidden", blocked(msg(HIDDEN, "srv")) === true);
writeFileSync(stateFile, good); await tick();
rmSync(stateFile); await tick();
check("file briefly missing (being replaced) keeps people hidden", blocked(msg(HIDDEN, "srv")) === true);
writeFileSync(stateFile, good); await tick();

// Burst: person was active in 1000 channels. Showing them must not hammer Discord's servers.
await fresh();
for (let i = 0; i < 1000; i++) blocked(msg(HIDDEN, "chan" + i));
blocked(msg(HIDDEN, "srv"));
await setList([person(HIDDEN, false)]);
check("show someone active in 1000 channels: max 1 request now", fetches.length <= 1, `${fetches.length} requests`);
fetches = []; blocked({ type: "CHANNEL_SELECT", channelId: "chan7" }); await new Promise(r => setTimeout(r, 15));
check("their messages reload when you open that chat", fetches.join() === "chan7", fetches.join());
fetches = []; blocked({ type: "CHANNEL_SELECT", channelId: "chan7" }); await new Promise(r => setTimeout(r, 15));
check("...and only once", fetches.length === 0, fetches.join());

// Two hidden people in one chat; show only one.
await fresh([person(HIDDEN), person(OTHER)]);
blocked(msg(HIDDEN, "srv")); blocked(msg(OTHER, "srv"));
await setList([person(HIDDEN), person(OTHER, false)]);
check("two hidden in a chat, show one: their messages come back", fetches.includes("srv"), "no reload");

// Calls
await fresh();
check("DM call from hidden person dropped", blocked({ type: "CALL_CREATE", channelId: "dm1", ringing: [ME] }) === true);
check("DM call ring update dropped", blocked({ type: "CALL_UPDATE", channelId: "dm1", ringing: [ME] }) === true);
check("DM call from visible person rings", blocked({ type: "CALL_CREATE", channelId: "dm2", ringing: [ME] }) === false);
let vs = { type: "VOICE_STATE_UPDATES", voiceStates: [{ userId: HIDDEN, channelId: "group" }] }; blocked(vs);
check("group call started by hidden person dropped", blocked({ type: "CALL_CREATE", channelId: "group", ringing: [ME] }) === true);
voice.group = { [OTHER]: {} };
check("group call with a visible person shows", blocked({ type: "CALL_UPDATE", channelId: "group", ringing: [ME] }) === false);
await fresh();
check("server channel unaffected by call logic", blocked({ type: "CALL_CREATE", channelId: "srv" }) === false);
await setList([person(HIDDEN)], false);
check("Hide everyone OFF: call from them rings", blocked({ type: "CALL_CREATE", channelId: "dm1", ringing: [ME] }) === false);

// Friends / toggle
await fresh();
check("friends list filtered", globalThis.__fake.RelationshipStore.getFriendIDs().join() === OTHER);
check("DM list filtered", globalThis.__fake.sortStore.getPrivateChannelIds().join() === "dm2");
let writes = 0; const realWrite = VencordNative.pluginHelpers.HideUsers.writeState;
VencordNative.pluginHelpers.HideUsers.writeState = async t => { writes++; return realWrite(t); };
const key = { ctrlKey: true, shiftKey: true, key: "H", preventDefault() { } };
for (let i = 0; i < 30; i++) globalThis.__key({ ...key, repeat: i > 0 });
check("holding Ctrl+Shift+H toggles once, not 30 times", writes === 1, `${writes} toggles`);
VencordNative.pluginHelpers.HideUsers.writeState = realWrite;

hide.stop();
const now = globalThis.__fake.RelationshipStore.getFriendIDs();
check("stop() restores everything", now.length === 2 && interceptors.length === 0);

// Update check (GitHub only)
process.env.LOCALAPPDATA = box;
const verFile = join(box, "DiscordNameHider", "dist", "version.txt");
(await import("fs")).mkdirSync(join(box, "DiscordNameHider", "dist"), { recursive: true });
const answer = (status, body) => { globalThis.fetch = async u => { globalThis.__asked = u; return { ok: status === 200, json: async () => body }; }; };
writeFileSync(verFile, "1.4.1");
answer(200, { tag_name: "v1.4.2" });
check("newer release on GitHub is found", await native.checkUpdate() === "1.4.2" && globalThis.__asked.startsWith("https://api.github.com/repos/rakawe86/discord-name-hider/"));
answer(200, { tag_name: "v1.4.1" }); check("same version: no prompt", await native.checkUpdate() === null);
answer(200, { tag_name: "v1.3.9" }); check("older release: no prompt", await native.checkUpdate() === null);
answer(200, { tag_name: "v1.10.0" }); check("1.10 counts as newer than 1.4 (not text order)", await native.checkUpdate() === "1.10.0");
answer(200, { tag_name: "<script>" }); check("junk tag ignored", await native.checkUpdate() === null);
answer(200, null); check("empty reply ignored", await native.checkUpdate() === null);
answer(403, {}); check("GitHub rate limit or error: no prompt, no crash", await native.checkUpdate() === null);
globalThis.fetch = async () => { throw new Error("offline"); }; check("offline: no crash", await native.checkUpdate() === null);
rmSync(verFile); answer(200, { tag_name: "v9.9.9" }); check("version file missing: no prompt", await native.checkUpdate() === null);
native.openDownload(); check("Download opens the project's release page only", globalThis.__opened === "https://github.com/rakawe86/discord-name-hider/releases/latest");

// Native write must be atomic: no moment where a reader sees a partial file.
const src = readFileSync(join(pluginDir, "native.ts"), "utf8");
check("save from Discord is atomic", /renameSync/.test(src));

rmSync(box, { recursive: true, force: true });
console.log(fails ? `\n${fails} test(s) FAILED` : "\nALL PASSED");
process.exit(fails ? 1 : 0);
