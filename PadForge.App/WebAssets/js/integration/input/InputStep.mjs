export class InputStep {
    constructor(actions = [], delay = 0) {
        this.actions = actions;
        this.delay = delay;
    }

    add(action) {
        this.actions.push(action);
    }
}