const DEFAULT_CONFIRMATION_WINDOW = 30_000;
const DEFAULT_CONFIRMATION_COUNT = 2;

export class SkipControl {

    /**
     * @param {object} queue
     * @param {object} notifier
     * @param {number} confirmationWindow
     */
    constructor(
        queue,
        notifier,
        confirmationWindow = DEFAULT_CONFIRMATION_WINDOW
    ) {
        this.queue = queue;
        this.notifier = notifier;
        this.confirmationWindow = confirmationWindow;

        this.pending = {
            skip: null,
            skipAll: null
        };
    }

    /**
     * Handle !skip.
     *
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     */
    handleSkip(message) {
        this.handleRequest("skip", message);
    }

    /**
     * Handle !skipAll.
     *
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     */
    handleSkipAll(message) {
        this.handleRequest("skipAll", message);
    }

    /**
     * Handle a skip request.
     *
     * @param {"skip"|"skipAll"} type
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     */
    handleRequest(type, message) {
        if (!this.queue.isRunning) {
            this.notifier.say(`${message.user}: there is no command currently running.`);
            return;
        }

        if (message.isModerator()) {
            this.execute(type, message.user);
            return;
        }
        this.handleUserConfirmation(type, message.user);
    }

    /**
     * Handle normal-user confirmation.
     *
     * Two different users must request the same operation
     * within the confirmation window.
     *
     * @param {"skip"|"skipAll"} type
     * @param {string} username
     */
    handleUserConfirmation(type, username) {
        const now = Date.now();
        const current = this.pending[type];

        if (!current || now - current.timestamp > this.confirmationWindow) { // Start a new confirmation window.
            this.pending[type] = {
                timestamp: now,
                users: new Set([username])
            };

            this.notifier.say(`${username}: ${type} requested. Another user must confirm within 30 seconds.`);
            this.scheduleExpiration(type, now);
            return;
        }

        if (current.users.has(username)) { // Same user cannot count twice.
            this.notifier.say(`${username}: your ${type} request is already registered.`);
            return;
        }

        current.users.add(username);
        if (current.users.size >= DEFAULT_CONFIRMATION_COUNT) {
            this.execute(type, username);
        }
    }

    /**
     * Execute the requested skip operation.
     *
     * @param {"skip"|"skipAll"} type
     * @param {string} username
     */
    execute(type, username) {
        this.pending[type] = null;

        let skipped;
        if (type === "skipAll") {
            skipped = this.queue.skipAll();
        } else {
            skipped = this.queue.skipCurrent();
        }

        if (!skipped) {
            this.notifier.say(`${username}: nothing was available to skip.`);
            return;
        }

        if (type === "skipAll") {
            this.notifier.say(`${username}: current command skipped and queue cleared.`);
        } else {
            this.notifier.say(`${username}: current command skipped.`);
        }
    }

    /**
     * Expire a confirmation window after the configured timeout.
     *
     * @param {"skip"|"skipAll"} type
     * @param {number} timestamp
     */
    scheduleExpiration(type, timestamp) {
        setTimeout(() => {
            const current = this.pending[type];

            if (!current) {
                return;
            }

            if (current.timestamp !== timestamp) {
                return;
            }

            this.pending[type] = null;
        }, this.confirmationWindow);
    }

    /**
     * Clear all pending confirmations.
     */
    clear() {
        this.pending.skip = null;
        this.pending.skipAll = null;
    }
}