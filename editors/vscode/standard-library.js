"use strict";

/** Une seule proposition par session, même si plusieurs documents/frappes signalent le manque. */
function standardLibraryRecovery({ prompt, restore, restart, reportError }) {
    let offered = false;
    return async function recover() {
        if (offered) return;
        offered = true;
        if (await prompt() !== "Restaurer la std") return;
        try {
            const result = await restore();
            if (!result.available) throw new Error(`Impossible d’écrire la std dans ${result.path}. Vérifiez les droits d’accès.`);
            await restart();
        } catch (error) {
            await reportError(error.message ?? String(error));
        }
    };
}

module.exports = { standardLibraryRecovery };
