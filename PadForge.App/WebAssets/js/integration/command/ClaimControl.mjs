const DEFAULT_CONTROL_DURATION_MS = 5 * 60 * 1000;
const DEFAULT_VOTE_DURATION_MS = 60 * 1000;
const DEFAULT_REQUIRED_VOTES = 2;
const DEFAULT_MAX_PENDING_REQUESTS = 5;

export class ClaimControl {
    constructor(
        notifier,
        controlDurationMs = DEFAULT_CONTROL_DURATION_MS,
        voteDurationMs = DEFAULT_VOTE_DURATION_MS,
        requiredVotes = DEFAULT_REQUIRED_VOTES,
        maxPendingRequests = DEFAULT_MAX_PENDING_REQUESTS
    ) {
        this.controlDurationMs = controlDurationMs;
        this.voteDurationMs = voteDurationMs;
        this.requiredVotes = requiredVotes;
        this.maxPendingRequests = maxPendingRequests;
        this.notifier = notifier;

        this.owner = null;
        this.expiresAt = null;
        this.claimRequests = new Map();
    }

    /**
     * Handle !claim Supported forms:
     *
     * !claim
     *     -> request exclusive control
     *
     * !claim status
     *     -> show status
     *
     * !claim @Alice
     *     -> moderator: force Alice to become owner
     *     -> normal user: vote for Alice's request
     *
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     */
    handleClaim(message) {
        this.#cleanupExpiredRequests();
        const claimCommand = message.getCommandArgs()?.[0];

        if (!claimCommand) {
            this.#request(message);
            return;
        }

        if (claimCommand.toLowerCase() === "status") {
            this.#handleStatus(message);
            return;
        }

        const targetUser = this.normalize(claimCommand);
        if (!claimCommand.startsWith("@") || !targetUser) {
            this.notifier.say(`@${message.user}: invalid claim target: ${claimCommand}`);
            return;
        }
        if (message.isModerator()) {
            this.#forceClaim(targetUser, message.user);
            return;
        }

        this.#handleVote(message, targetUser);
    }

    /**
     * Handle !accept @username
     *
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     */
    handleAccept(message) {
        const acceptCommand = message.getCommandArgs()?.[0];
        if (!acceptCommand) {
            this.notifier.say(`@${message.user}: use !accept @username`);
            return;
        }

        const targetUser = this.normalize(acceptCommand);
        if (!targetUser) {
            this.notifier.say(`@${message.user}: invalid accept target: ${acceptCommand}.`);
            return;
        }

        this.#handleVote(message, targetUser);
    }

    /**
     * !release
     *     -> only the current owner can release control.
     *
     * !release @username
     *     -> moderator can force-release that user's claim
     *
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     */
    handleRelease(message) {
        const releaseCommand = message.getCommandArgs()?.[0];
        const owner = this.getOwner();

        if (!owner) {
            this.notifier.say(`@${message.user}: there is currently no active claim.` );
            return;
        }

        /* * Moderator-only forced release. */
        if (releaseCommand) {
            if (!message.isModerator()) {
                this.notifier.say(`@${message.user}: moderators only can release another user's claim.` );
                return;
            }

            const targetUser = this.normalize(releaseCommand);
            if (!targetUser) {
                this.notifier.say(`@${message.user}: invalid release target: ${releaseCommand}.`);
                return;
            }

            if (targetUser !== this.normalize(owner)) {
                this.notifier.say(`@${message.user}: @${targetUser} not own the current claim.`);
                return;
            }

            this.release();
            this.notifier.say(`@${message.user}: force-released @${owner}'s exclusive control.`);
            return;
        }

        if (
            this.normalize(owner) !==
            this.normalize(message.user)
        ) {
            this.notifier.say(`@${message.user}: only @${owner} or mods can release claim.`);
            return;
        }

        this.release();
        this.notifier.say(`@${message.user}: released exclusive control.`);
    }

    /**
     * Handle: !claim status.
     *
     * @param {import("../model/ChatMessage.mjs").ChatMessage} message
     */
    #handleStatus(message) {
        this.#cleanupExpiredRequests();

        const owner = this.getOwner();
        if (owner) {
            const seconds = Math.ceil(
                this.#getRemainingControlTime() / 1000
            );

            this.notifier.say(`@${message.user}: Control belongs to @${owner}. ${seconds}s remaining.`);
            return;
        }

        const requests = this.#getClaimRequests();
        if (requests.length === 0) {
            this.notifier.say(`@${message.user}: There is currently no active claim or claim request.`);
            return;
        }

        const status = requests.map(request => {
            const seconds = Math.ceil(
                Math.max(0, request.expiresAt - Date.now()) / 1000
            );

            return `@${request.user} ` +
                `(${request.votes}/${request.requiredVotes} votes, ` +
                `${seconds}s)`;
        }).join(" | ");

        this.notifier.say(`@${message.user}: Pending claims: ${status}`);
    }

    /**
     * Handle: !claim
     */
    #request(message) {
        const result = this.#requestClaim(message.user);
        if (!result.success) {
            this.notifier.say(`@${message.user}: ${result.reason}`);
            return;
        }

        this.notifier.say(
            `@${message.user} requested exclusive control. ` +
            `${this.requiredVotes} other users must approve with ` +
            `!accept @${message.user} or ` +
            `!claim @${message.user} within ` +
            `${Math.ceil(this.voteDurationMs / 1000)}s.`
        );
    }

    #requestClaim(user) {
        this.#cleanupExpiredRequests();

        if (this.isActive()) {
            return {
                success: false,
                reason: `control already belongs to ${this.owner}`
            };
        }

        const normalized = this.normalize(user);

        if (!normalized) {
            return {
                success: false,
                reason: "invalid username"
            };
        }

        if (this.claimRequests.has(normalized)) {
            return {
                success: false,
                reason: "you already have a pending claim request"
            };
        }

        if (this.claimRequests.size >= this.maxPendingRequests) {
            return {
                success: false,
                reason:
                    `maximum of ${this.maxPendingRequests} ` +
                    "active claim requests reached"
            };
        }

        this.claimRequests.set(normalized, {
            user: this.cleanUsername(user),
            expiresAt: Date.now() + this.voteDurationMs,
            voters: new Set()
        });

        return {
            success: true
        };
    }

    /**
     * Handle:
     *     !claim @username
     *     !accept @username
     */
    #handleVote(message, target) {
        if (this.isActive()) {
            this.notifier.say(`@${message.user}: control currently belongs to ${this.owner}.`);
            return;
        }

        const request = this.claimRequests.get(target);
        if (!request) {
            this.notifier.say(`@${message.user}: @${target} does not have an active claim request.`);
            return;
        }

        const voter = this.normalize(message.user);
        if (voter === target) {
            this.notifier.say(`@${message.user}: you cannot vote for your own claim request.`);
            return;
        }

        if (request.voters.has(voter)) {
            this.notifier.say(`@${message.user}: you have already accepted @${request.user}'s claim request.`);
            return;
        }

        request.voters.add(voter);
        const votes = request.voters.size;

        if (votes >= this.requiredVotes) {
            this.#grantControl(request.user);

            this.notifier.say(
                `@${request.user} now has exclusive control for ` +
                `${Math.ceil(this.controlDurationMs / 60000)} minutes.`
            );
            return;
        }

        const remainingVotes =
            this.requiredVotes - votes;

        const seconds = Math.ceil(
            Math.max(0, request.expiresAt - Date.now()) / 1000
        );

        this.notifier.say(
            `@${message.user}: accepted @${request.user}'s claim. ` +
            `${votes}/${this.requiredVotes} votes, ` +
            `${remainingVotes} more needed, ${seconds}s remaining.`
        );
    }

    #forceClaim(target, moderator) {
        const requestUser = this.getClaimRequestUser(target);
        const user = requestUser || target;

        this.#grantControl(user);
        this.notifier.say(
            `@${moderator}: granted exclusive control to ` +
            `@${user} for ` +
            `${Math.ceil(this.controlDurationMs / 60000)} minutes.`
        );
    }

    #grantControl(user) {
        this.#clearClaimRequests();
        this.owner = this.cleanUsername(user);
        this.expiresAt = Date.now() + this.controlDurationMs;
    }

    release() {
        this.owner = null;
        this.expiresAt = null;
    }

    canExecute(message) {
        if (!this.isActive()) {
            return true;
        }

        if (message.isModerator()) {
            return true;
        }

        return this.normalize(message.user) === this.normalize(this.getOwner());
    }

    isActive() {
        if (
            this.owner === null ||
            this.expiresAt === null
        ) {
            return false;
        }

        if (Date.now() >= this.expiresAt) {
            this.release();
            return false;
        }

        return true;
    }

    getOwner() {
        return this.isActive()
            ? this.owner
            : null;
    }

    #getClaimRequests() {
        this.#cleanupExpiredRequests();

        return [...this.claimRequests.values()].map(request => ({
            user: request.user,
            votes: request.voters.size,
            requiredVotes: this.requiredVotes,
            expiresAt: request.expiresAt
        }));
    }

    #getRemainingControlTime() {
        return this.isActive()
            ? Math.max(0, this.expiresAt - Date.now())
            : 0;
    }

    getRemainingVoteTime(user) {
        const key = this.normalize(user);
        const request = this.claimRequests.get(key);

        if (!request) {
            return 0;
        }

        if (Date.now() >= request.expiresAt) {
            this.claimRequests.delete(key);
            return 0;
        }

        return Math.max(
            0,
            request.expiresAt - Date.now()
        );
    }

    #clearClaimRequests() {
        this.claimRequests.clear();
    }

    #cleanupExpiredRequests() {
        const now = Date.now();

        for (const [username, request] of this.claimRequests) {
            if (now >= request.expiresAt) {
                this.claimRequests.delete(username);
            }
        }
    }

    getClaimRequestUser(username) {
        const request = this.claimRequests.get(
            this.normalize(username)
        );

        return request?.user || null;
    }

    cleanUsername(username) {
        return String(username)
            .trim()
            .replace(/^@/, "");
    }

    normalize(username) {
        const cleaned = this.cleanUsername(username);
        return cleaned
            ? cleaned.toLowerCase()
            : null;
    }
}