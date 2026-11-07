/*
 * HideUsers native side: reads/writes the shared state file that
 * DiscordNameHider.exe edits, so changes apply live without a restart.
 */

import { execFile } from "child_process";
import { app, shell } from "electron";
import { mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from "fs";
import { dirname, join } from "path";

const STATE_FILE = join(app.getPath("appData"), "DiscordNameHider", "state.json");

/** Returns the file content if it changed since `lastMtime`, otherwise null. */
export function readState(_: unknown, lastMtime: number) {
    try {
        const mtime = statSync(STATE_FILE).mtimeMs;
        if (mtime === lastMtime) return null;
        return { mtime, text: readFileSync(STATE_FILE, "utf8") };
    } catch {
        return null; // locked or mid-replace: keep the current list, retry next poll
    }
}

export function writeState(_: unknown, text: string) {
    mkdirSync(dirname(STATE_FILE), { recursive: true });
    // Atomic: the tool never sees a half-written list.
    const tmp = STATE_FILE + ".plugin.tmp";
    writeFileSync(tmp, text);
    renameSync(tmp, STATE_FILE);
}

const REPO = "rakawe86/discord-name-hider";
const VERSION_RE = /^\d+(\.\d+)*$/;

/** True when version a is newer than b ("1.4.10" > "1.4.9"). */
function isNewer(a: string, b: string) {
    const x = a.split(".").map(Number), y = b.split(".").map(Number);
    for (let i = 0; i < Math.max(x.length, y.length); i++)
        if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) > (y[i] || 0);
    return false;
}

/** The newer version on GitHub, or null. Only talks to GitHub, never to Discord. */
export async function checkUpdate() {
    try {
        const installed = readFileSync(join(process.env.LOCALAPPDATA ?? "", "DiscordNameHider", "dist", "version.txt"), "utf8").trim();
        const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { "User-Agent": "DiscordNameHider" } });
        if (!res.ok) return null;
        const latest = String((await res.json())?.tag_name ?? "").replace(/^v/, "");
        return VERSION_RE.test(latest) && VERSION_RE.test(installed) && isNewer(latest, installed) ? latest : null;
    } catch {
        return null; // offline or GitHub down: try again later
    }
}

export function openDownload() {
    shell.openExternal(`https://github.com/${REPO}/releases/latest`);
}


// Downloads the release exe from this project's GitHub and lets it refresh the installed files.
// Discord keeps running; the new version is used from the next Discord start.
async function downloadAndApply(version: string) {
    if (!VERSION_RE.test(version)) return false;
    try {
        const res = await fetch(`https://github.com/${REPO}/releases/download/v${version}/DiscordNameHider.exe`);
        if (!res.ok) return false;
        const exe = Buffer.from(await res.arrayBuffer());
        if (exe.length < 100_000 || exe[0] !== 0x4d || exe[1] !== 0x5a) return false; // must be a Windows program
        const dir = join(process.env.LOCALAPPDATA ?? "", "DiscordNameHider", "updates");
        mkdirSync(dir, { recursive: true });
        const file = join(dir, `DiscordNameHider-v${version}.exe`);
        writeFileSync(file + ".tmp", exe);
        renameSync(file + ".tmp", file);
        return await new Promise<boolean>(done =>
            execFile(file, ["--update-files"], { timeout: 120_000, windowsHide: true }, err => done(!err)));
    } catch {
        return false;
    }
}

export async function installUpdate(_: unknown, version: string) {
    return downloadAndApply(String(version));
}

export function restartDiscord() {
    app.relaunch();
    app.exit(0);
}

/** "Update automatically" mode. Runs in Discord's main process, so it still works when Discord's window hangs. */
export async function runAutoUpdate() {
    try {
        if (JSON.parse(readFileSync(STATE_FILE, "utf8"))?.autoUpdate !== true) return false;
        const version = await checkUpdate();
        return version ? await downloadAndApply(version) : false;
    } catch {
        return false;
    }
}

function autoUpdateLoop() {
    runAutoUpdate().finally(() => setTimeout(autoUpdateLoop, 6 * 60 * 60 * 1000));
}
setTimeout(autoUpdateLoop, 60_000);
