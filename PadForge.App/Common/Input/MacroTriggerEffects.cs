using System;
using PadForge.ViewModels;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One slot's adaptive trigger programs played by Adaptive Trigger Effect
    /// macro actions (issue #531): an 11-byte block per side, with when it
    /// ends. Step 4b writes it on the poll thread. The effect dispatcher reads
    /// it on its own thread for the Sony pass, and the poll thread reads it
    /// for the Xbox pads that translate trigger vibrations, so every access
    /// takes the lock.
    ///
    /// <para>An effect ends at its time, when the macro that owns it lets its
    /// trigger go (the until-release hold), at a Stop Adaptive Trigger Effect,
    /// or with the engine, which drops the whole store, the macro rumble's
    /// rule.</para>
    /// </summary>
    public sealed class MacroTriggerEffectState
    {
        private sealed class Entry
        {
            public byte[] Block;
            /// <summary>Environment.TickCount64 at which it ends, long.MaxValue
            /// for the until-release and until-stopped holds.</summary>
            public long UntilMs;
            /// <summary>The macro whose trigger release ends it, or null.</summary>
            public object Owner;
        }

        private readonly object _lock = new();
        private readonly int _slot;
        private Entry _left, _right;
        private volatile bool _any;
        private volatile bool _owned;
        private volatile bool _timed;

        public MacroTriggerEffectState(int slot) => _slot = slot;

        /// <summary>Raised after every change, with the slot. The engine points
        /// it at the dispatcher's wake-up.</summary>
        internal static Action<int> Changed;

        /// <summary>True while either side holds an effect.</summary>
        public bool Any => _any;

        /// <summary>True while an effect waits on its macro's trigger.</summary>
        public bool AnyOwned => _owned;

        /// <summary>True while an effect ends at a time, which keeps the
        /// dispatcher's writer running to write its end.</summary>
        public bool AnyTimed => _timed;

        /// <summary>Plays a block on one side or both. <paramref name="untilMs"/>
        /// is long.MaxValue for a hold with no time. <paramref name="owner"/>
        /// is the macro for the until-release hold.</summary>
        public void Play(byte[] block, MacroTriggerEffectSide side, long untilMs, object owner)
        {
            if (block == null || block.Length < 11) return;
            bool changed = false;
            lock (_lock)
            {
                // A macro that repeats while held plays the same program on
                // every poll. Only a different program wakes the dispatcher:
                // the firmware holds what was written.
                if (side != MacroTriggerEffectSide.Left)
                    changed |= Set(ref _right, block, untilMs, owner);
                if (side != MacroTriggerEffectSide.Right)
                    changed |= Set(ref _left, block, untilMs, owner);
                Recount();
            }
            if (changed) Changed?.Invoke(_slot);
        }

        // Caller holds the lock. True when the side's program changed.
        private static bool Set(ref Entry entry, byte[] block, long untilMs, object owner)
        {
            if (entry != null && entry.Owner == owner && block.AsSpan(0, 11).SequenceEqual(entry.Block.AsSpan(0, 11)))
            {
                entry.UntilMs = untilMs;
                return false;
            }
            entry = new Entry { Block = (byte[])block.Clone(), UntilMs = untilMs, Owner = owner };
            return true;
        }

        /// <summary>Ends what one side holds, or both.</summary>
        public void Stop(MacroTriggerEffectSide side)
        {
            bool changed;
            lock (_lock)
            {
                changed = (side != MacroTriggerEffectSide.Left && _right != null)
                          || (side != MacroTriggerEffectSide.Right && _left != null);
                if (side != MacroTriggerEffectSide.Left) _right = null;
                if (side != MacroTriggerEffectSide.Right) _left = null;
                Recount();
            }
            if (changed) Changed?.Invoke(_slot);
        }

        /// <summary>Ends the effects a macro holds until its trigger releases.</summary>
        public void ReleaseOwner(object owner)
        {
            if (owner == null || !_owned) return;
            bool changed = false;
            lock (_lock)
            {
                if (_right?.Owner == owner) { _right = null; changed = true; }
                if (_left?.Owner == owner) { _left = null; changed = true; }
                if (changed) Recount();
            }
            if (changed) Changed?.Invoke(_slot);
        }

        /// <summary>Ends the effects whose macro is no longer one the slot
        /// runs: deleted, turned off, or swapped out by a profile.</summary>
        public void ReleaseOwnersNotIn(Func<object, bool> stillRuns)
        {
            if (!_owned || stillRuns == null) return;
            bool changed = false;
            lock (_lock)
            {
                if (_right?.Owner != null && !stillRuns(_right.Owner)) { _right = null; changed = true; }
                if (_left?.Owner != null && !stillRuns(_left.Owner)) { _left = null; changed = true; }
                if (changed) Recount();
            }
            if (changed) Changed?.Invoke(_slot);
        }

        public void Clear() => Stop(MacroTriggerEffectSide.Both);

        /// <summary>Copies of the blocks live at <paramref name="nowMs"/>,
        /// null for a side with none. An effect past its time ends here.</summary>
        public void Read(long nowMs, out byte[] left, out byte[] right)
        {
            left = right = null;
            if (!_any) return;
            bool expired = false;
            lock (_lock)
            {
                if (_right != null && nowMs >= _right.UntilMs) { _right = null; expired = true; }
                if (_left != null && nowMs >= _left.UntilMs) { _left = null; expired = true; }
                if (expired) Recount();
                if (_right != null) right = (byte[])_right.Block.Clone();
                if (_left != null) left = (byte[])_left.Block.Clone();
            }
            if (expired) Changed?.Invoke(_slot);
        }

        // Caller holds the lock.
        private void Recount()
        {
            _any = _left != null || _right != null;
            _owned = _left?.Owner != null || _right?.Owner != null;
            _timed = (_left != null && _left.UntilMs != long.MaxValue)
                     || (_right != null && _right.UntilMs != long.MaxValue);
        }
    }
}
