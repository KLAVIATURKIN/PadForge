// src/main/input/CommandParser.js

import { InputAction } from "./InputAction.mjs";
import { InputStep } from "./InputStep.mjs";
import { InputSequence } from "./InputSequence.mjs";

const DEFAULT_DURATION = 200;

export class CommandParser {

    constructor(validInputs = [
        "a",
        "b",
        "select",
        "start",
        "up",
        "down",
        "left",
        "right"
    ]) {
        this.validInputs = new Set(validInputs);
    }

    parse(message) {
        if (!message) {
            throw new Error("Invalid command message");
        }

        const messageContent = message.content.trim();
        if (messageContent.length > 4096) throw new Error("Command is too long.");
        this.remainingSteps = 4096;
        if (!messageContent) {
            return new InputSequence();
        }

        const tokens = this.tokenize(messageContent);
        const inputSequence = this.parseTokens(tokens);
        inputSequence.setOwner(message.user);
        inputSequence.setOwnerColor(message.getUserColor());
        inputSequence.setMessageContent(messageContent);
        return inputSequence;
    }

    tokenize(input) {
        const tokens = [];

        let current = "";
        let depth = 0;

        for (let i = 0; i < input.length; i++) {
            const char = input[i];

            if (char === "[") {
                depth++;
                if (depth > 16) throw new Error("Groups are nested too deeply.");
                current += char;
                continue;
            }

            if (char === "]") {
                depth--;

                if (depth < 0) {
                    throw new Error("Unexpected ']'");
                }

                current += char;
                continue;
            }

            if (/\s/.test(char) && depth === 0) {
                if (current.length > 0) {
                    tokens.push(current);
                    current = "";
                }

                continue;
            }

            current += char;
        }

        if (depth !== 0) {
            throw new Error("Unclosed '['");
        }

        if (current.length > 0) {
            tokens.push(current);
        }

        return tokens;
    }

    parseTokens(tokens, groupDuration = null) {
        const sequence = new InputSequence();

        for (const token of tokens) {
            if (token.startsWith("[") && token.includes("]")) {
                this.parseGroup(token, sequence);
                continue;
            }

            const step = this.parseToken(token, groupDuration);

            if (step) {
                this.appendStep(sequence, step);
            }
        }

        return sequence;
    }

    parseGroup(token, sequence) {
        const closingIndex = this.findClosingBracket(token);

        const content = token.substring(1, closingIndex);
        const suffix = token.substring(closingIndex + 1);

        const repeatMatch = suffix.match(/^\*(\d+)$/);

        if (repeatMatch) {
            const count = Number(repeatMatch[1]);

            if (!Number.isSafeInteger(count) || count <= 0 || count > 1000) {
                throw new Error(`Invalid repeat count: ${token}`);
            }

            const innerTokens = this.tokenize(content);

            for (let i = 0; i < count; i++) {
                const innerSequence = this.parseTokens(innerTokens);

                for (const step of innerSequence.steps) {
                    this.appendStep(sequence, step);
                }
            }

            return;
        }

        let groupDuration = null;

        if (suffix) {
            const duration = this.parseDuration(suffix);

            if (duration === null) {
                throw new Error(`Invalid group suffix: ${token}`);
            }

            groupDuration = duration;
        }

        const innerTokens = this.tokenize(content);

        const innerSequence = this.parseTokens(
            innerTokens,
            groupDuration
        );

        for (const step of innerSequence.steps) {
            this.appendStep(sequence, step);
        }
    }

    findClosingBracket(token) {
        let depth = 0;

        for (let i = 0; i < token.length; i++) {
            if (token[i] === "[") {
                depth++;
            }

            if (token[i] === "]") {
                depth--;

                if (depth === 0) {
                    return i;
                }
            }
        }

        return -1;
    }

    parseToken(token, groupDuration = null) {
        if (!token) {
            return null;
        }

        if (token.startsWith("#")) {
            const duration = this.parseDuration(
                token.substring(1)
            );

            if (duration === null) {
                throw new Error(`Invalid delay: ${token}`);
            }

            return new InputStep([], duration);
        }

        const parts = this.splitSimultaneous(token);
        const actions = [];

        for (const part of parts) {
            const action = this.parseAction(
                part,
                groupDuration
            );

            actions.push(action);
        }

        return new InputStep(actions);
    }

    splitSimultaneous(token) {
        const parts = [];
        let current = "";

        for (const char of token) {
            if (char === "+") {
                if (!current) {
                    throw new Error(
                        `Invalid simultaneous input: ${token}`
                    );
                }

                parts.push(current);
                current = "";
                continue;
            }

            current += char;
        }

        if (!current) {
            throw new Error(
                `Invalid simultaneous input: ${token}`
            );
        }

        parts.push(current);

        return parts;
    }

    parseAction(token, groupDuration = null) {
        let type = "press";
        let input = token;

        if (input.startsWith("_")) {
            type = "hold";
            input = input.substring(1);
        } else if (input.startsWith("-")) {
            type = "release";
            input = input.substring(1);
        }

        if (!input) {
            throw new Error(`Invalid input: ${token}`);
        }

        // "_" / "-" without a duration are permanent state changes.
        let duration = null;

        const durationMatch = input.match(
            /(\d+(?:\.\d+)?)(ms|s)$/
        );

        if (durationMatch) {
            duration = this.parseDuration(durationMatch[0]);

            input = input.substring(
                0,
                durationMatch.index
            );
        } else if (groupDuration !== null) {
            duration = groupDuration;
        }

        input = input.toLowerCase();

        if (!this.validInputs.has(input)) {
            throw new Error(`Unknown input: ${input}`);
        }

        // Normal press keeps the default duration unless overridden.
        if (type === "press" && duration === null) {
            duration = DEFAULT_DURATION;
        }

        return new InputAction(
            input,
            duration,
            type
        );
    }

    appendStep(sequence, step) {
        if (--this.remainingSteps < 0) throw new Error("Command expands to too many steps.");
        sequence.addStep(step);
    }

    parseDuration(value) {
        if (!value) {
            return null;
        }

        const match = value.match(
            /^(\d+(?:\.\d+)?)(ms|s)$/
        );

        if (!match) {
            return null;
        }

        const amount = Number(match[1]);
        const unit = match[2];

        return unit === "s"
            ? amount * 1000
            : amount;
    }
}