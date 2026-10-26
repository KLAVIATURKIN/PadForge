// src/main/input/InputCommandQueue.js
export class InputCommandQueue {

    constructor(executor) {
        this.executor = executor;

        this.queue = [];
        this.running = false;
    }

    /**
     * Add a sequence to the queue.
     *
     * @param {import("./InputSequence.mjs").InputSequence} sequence
     */
    enqueue(sequence) {
        if (!sequence || sequence.isEmpty()) {
            return;
        }

        this.queue.push(sequence);

        void this.process();
    }

    /**
     * Process queued sequences sequentially.
     */
    async process() {
        if (this.running) {
            return;
        }

        this.running = true;

        try {
            while (this.queue.length > 0) {
                const sequence = this.queue.shift();

                try {
                    await this.executor.execute(sequence);
                } catch (error) {
                    console.error("[QUEUE] Failed to execute sequence:", error);
                }
            }
        } finally {
            this.running = false;
        }
    }

    /**
     * Skip the sequence currently being executed.
     * The remaining queue is preserved.
     *
     * @returns {boolean}
     */
    skipCurrent() {
        return this.executor.skipCurrent();
    }

    /**
     * Skip the currently running sequence
     * and clear all sequences waiting in the queue.
     *
     * @returns {boolean}
     */
    skipAll() {
        const skipped = this.executor.skipCurrent();
        this.clear();
        return skipped;
    }

    /**
     * Remove all sequences waiting in the queue.
     */
    clear() {
        this.queue.length = 0;
    }

    get size() {
        return this.queue.length;
    }

    get isRunning() {
        return this.running;
    }
}