"use strict";
const assert = require("node:assert/strict");
const { standardLibraryRecovery } = require("../standard-library");

(async () => {
    const events = [];
    const recover = standardLibraryRecovery({
        prompt: async () => { events.push("prompt"); return "Restaurer la std"; },
        restore: async () => { events.push("restore"); return { available: true }; },
        restart: async () => { events.push("restart"); },
        reportError: async () => { throw new Error("unexpected error"); },
    });
    await Promise.all([recover(), recover(), recover()]);
    assert.deepEqual(events, ["prompt", "restore", "restart"]);

    let restored = false;
    const declined = standardLibraryRecovery({
        prompt: async () => undefined,
        restore: async () => { restored = true; },
    });
    await declined(); await declined();
    assert.equal(restored, false);

    let error;
    const failed = standardLibraryRecovery({
        prompt: async () => "Restaurer la std",
        restore: async () => ({ available: false, path: "/cache/std" }),
        restart: async () => { throw new Error("must not restart"); },
        reportError: async message => { error = message; },
    });
    await failed(); await failed();
    assert.match(error, /droits d’accès/);
    console.log("VS Code : restauration unique de la std, annulation et échec vérifiés.");
})().catch(error => { console.error(error); process.exitCode = 1; });
