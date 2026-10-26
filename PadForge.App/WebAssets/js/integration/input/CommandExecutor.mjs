import {controllerView} from "../view.mjs";

export class CommandExecutor {

    constructor(gamepad) {
        this.gamepad = gamepad;
        this.currentAbortController = null;  // controller for the sequence currently being executed.
    }

    /**
     * Execute an input sequence.
     *
     * If the sequence is skipped, execution is canceled gracefully.
     *
     * @param {import("./InputSequence.mjs").InputSequence} sequence
     */
    async execute(sequence) {
        if (!sequence || sequence.isEmpty()) {
            return;
        }

        controllerView.setStatus(sequence.owner + ":  " + sequence.messageContent);
        const abortController = new AbortController();
        this.currentAbortController = abortController;

        try {
            for (const step of sequence.steps) {
                this.throwIfAborted(abortController.signal);
                await this.executeStep(step, abortController.signal);
            }
        } catch (error) {
            if (!this.isAbortError(error)) {
                throw error;
            }
        } finally {
            this.gamepad.reset(); // Always make sure no buttons remain pressed.
            if (this.currentAbortController === abortController) {
                this.currentAbortController = null;
            }
        }
    }

    /**
     * Skip the currently executing sequence.
     *
     * @returns {boolean} true if something was actually running
     */
    skipCurrent() {
        if (!this.currentAbortController) {
            return false;
        }

        this.currentAbortController.abort();
        return true;
    }

    /**
     * @param {import("./InputStep.mjs").InputStep} step
     * @param {AbortSignal} signal
     */
    async executeStep(
        step,
        signal
    ) {
        this.throwIfAborted(signal);
        if (step.actions.length === 0) {
            await this.sleep(step.delay, signal);
            return;
        }

        const pending = [];

        // Apply all initial states immediately.
        for (const action of step.actions) {
            this.throwIfAborted(signal);

            if (action.actionType === "release") {
                this.gamepad.release(action.input);

                if (action.duration !== null && action.duration > 0) {
                    pending.push(
                        this.sleep(action.duration, signal).then(() => {
                            this.gamepad.press(action.input);
                        })
                    );
                } else if (action.duration === 0) {
                    this.gamepad.press(action.input);
                }

                continue;
            }

            this.gamepad.press(action.input);

            // A press/hold with a duration is released afterward.
            if (action.duration !== null) {
                if (action.duration > 0) {
                    pending.push(
                        this.sleep(action.duration, signal).then(() => {
                            this.gamepad.release(action.input);
                        })
                    );
                } else {
                    this.gamepad.release(action.input);
                }
            }
            // No duration for "hold" => keep it pressed permanently.
        }

        await Promise.all(pending);
        this.throwIfAborted(signal);
    }

    /**
     * Abortable sleep.
     *
     * @param {number} milliseconds
     * @param {AbortSignal} signal
     */
    sleep(milliseconds, signal) {
        if (signal.aborted) {
            return Promise.reject(this.createAbortError());
        }

        return new Promise((resolve, reject) => {
            let timeoutId = null;

            const onAbort = () => {
                if (timeoutId !== null) {
                    clearTimeout(timeoutId);
                }

                signal.removeEventListener("abort", onAbort);
                reject(this.createAbortError());
            };

            signal.addEventListener("abort", onAbort, {
                once: true
            });

            timeoutId = setTimeout(() => {
                signal.removeEventListener("abort", onAbort);
                resolve();
            }, milliseconds);
        });
    }

    /**
     * @param {AbortSignal} signal
     */
    throwIfAborted(signal) {
        if (signal.aborted) {
            throw this.createAbortError();
        }
    }

    createAbortError() {
        const error = new Error("Input sequence was skipped");
        error.name = "AbortError";
        return error;
    }

    isAbortError(error) {
        return error && error.name === "AbortError";
    }
}