export const ActionType = Object.freeze({
    PRESS: "press",
    HOLD: "hold",
    RELEASE: "release",
});

/**
 * @typedef {"press" | "hold" | "release"} InputActionType
 */

export class InputAction {

    /**
     * @param {string} input
     * @param {number} duration
     * @param {InputActionType} actionType
     */
    constructor(input, duration = 200, actionType = "press") {
        this.input = input;
        this.duration = duration;
        this.actionType = actionType; // press | hold | release
    }
}