// import { ClaimControl } from "./ClaimControl.mjs";
import { ClaimControl } from "./ClaimControl.mjs";
import { SkipControl } from "./SkipControl.mjs";

class CommandHandler {
    /**
     * @param {ClaimControl} claimControl
     * @param {SkipControl} skipControl
     */
    constructor(
        claimControl,
        skipControl
    ) {
        this.claimControl = claimControl;
        this.skipControl = skipControl;

        this.commands = new Map();
        this.registerCommands();
    }

    registerCommands() {
        this.register("!skip", this.skipControl.handleSkip.bind(this.skipControl));
        this.register("!skipAll", this.skipControl.handleSkipAll.bind(this.skipControl));

        this.register("!claim", this.claimControl.handleClaim.bind(this.claimControl));
        this.register("!accept", this.claimControl.handleAccept.bind(this.claimControl));
        this.register("!release", this.claimControl.handleRelease.bind(this.claimControl));
    }

    /**
     * Register a command.
     *
     * @param {string} name
     * @param {(message: import("../model/ChatMessage.mjs").ChatMessage, args: string[]) => void} handler
     */
    register(
        name,
        handler
    ) {
        console.log(`Registering: ${name}`);
        this.commands.set(
            name.toLowerCase(),
            handler
        );
    }

    /**
     * Handle a ChatMessage.
     *
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     * @returns {boolean}
     */
    handle(message) {
        const commandName = message.command;
        const args = message.args;

        const handler = this.commands.get(commandName);
        if (!handler) {
            return false;
        }

        handler(message, args);
        return true;
    }
}

/**
 * Factory function keeps object construction outside
 * the actual application logic.
 *
 * @param {object} queue
 * @param {object} notifier
 */
function createCommandHandler(
    queue,
    notifier
) {
    const claimControl = new ClaimControl(
        notifier
    );
    const skipControl = new SkipControl(
        queue,
        notifier
    );
    const commandHandler = new CommandHandler(
        claimControl,
        skipControl
    );

    return {
        commandHandler,
        claimControl,
        skipControl
    };
}

export {
    CommandHandler,
    createCommandHandler
};