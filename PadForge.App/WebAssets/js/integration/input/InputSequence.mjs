export class InputSequence {
    constructor(steps = []) {
        this.steps = steps;
        this.owner = "";
        this.ownerColor = "";
        this.messageContent = "";
    }

    addStep(step) {
        this.steps.push(step);
    }

    setOwner(owner) {
        this.owner = owner;
    }

    setOwnerColor(ownerColor) {
        this.ownerColor = ownerColor;
    }

    setMessageContent(content) {
        this.messageContent = content;
    }

    isEmpty() {
        return this.steps.length === 0;
    }
}