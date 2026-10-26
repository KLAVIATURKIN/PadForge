export class ChatMessage {
    constructor(user, messageContent, command, flags, extra, commandContent) {
        this.user = user;
        this.content = messageContent;
        this.command = command ? "!" + command : "";
        this.flags = flags;
        this.extra = extra;
        this.commandContent = commandContent;
    }

    isCommand() {
        return this.content.trim().startsWith("!");
    }

    /**
     * Determine whether a user has moderator/admin privileges.
     *
     * ComfyJS normally exposes:
     *   flags.mod
     *   flags.broadcaster
     */
    isModerator() {
        return Boolean(
            this.flags.mod ||
            this.flags.moderator ||
            this.flags.broadcaster
        );
    }

    getCommandArgs() {
        return this.commandContent.trim().split(/\s+/);
    }

    getUserColor() {
        return this.extra.userColor ? this.extra.userColor : "#590000";
    }
}