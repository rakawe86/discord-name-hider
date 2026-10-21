/*
 * HideUsers native side: reads/writes the shared state file that
 * DiscordNameHider.exe edits, so changes apply live without a restart.
 */

import { app } from "electron";
import { mkdirSync, readFileSync, statSync, writeFileSync } from "fs";
import { dirname, join } from "path";

const STATE_FILE = join(app.getPath("appData"), "DiscordNameHider", "state.json");

/** Returns the file content if it changed since `lastMtime`, otherwise null. */
export function readState(_: unknown, lastMtime: number) {
    try {
        const mtime = statSync(STATE_FILE).mtimeMs;
        if (mtime === lastMtime) return null;
        return { mtime, text: readFileSync(STATE_FILE, "utf8") };
    } catch {
        return lastMtime === -1 ? null : { mtime: -1, text: "" };
    }
}

export function writeState(_: unknown, text: string) {
    mkdirSync(dirname(STATE_FILE), { recursive: true });
    writeFileSync(STATE_FILE, text);
}
