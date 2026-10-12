using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;

namespace PadForge.Common.Input
{
    internal sealed class RemoteToneRequest
    {
        /// <summary>How long one frame keeps its tone playing, in ms. A
        /// consumer ships a frame on every tick of its stream while the tone
        /// plays, 10 ms apart (HapticToneService.DispatchToneFrame), so a tone
        /// that goes this long without a newer frame has lost its link.</summary>
        internal const int DefaultLifetimeMs = 250;

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): the life of
        /// each request made from here on, the default unless a test holds it.
        /// A test sends one frame where a link sends a stream of them, and the
        /// sink's stream thread weighs that frame only when the scheduler runs
        /// it. On a busy PC that came more than 250 ms after the frame was
        /// made, so the tone never played and the test waited for it in vain.
        /// A test holds the life past its own end and puts the default back
        /// when it ends.</summary>
        internal static int LifetimeMs = DefaultLifetimeMs;

        internal readonly UserDevice Device;
        internal readonly ISdlInputDevice Source;
        internal readonly float Hz;
        internal readonly float Amplitude;
        internal readonly long UntilMs;
        internal readonly LinkEffectTicket Ticket;
        internal RemoteToneRequest(UserDevice device, float hz, float amplitude, LinkEffectTicket ticket)
        {
            Device = device;
            Source = device.Device;
            Hz = hz;
            Amplitude = Math.Clamp(amplitude, 0f, 1f);
            UntilMs = Environment.TickCount64 + LifetimeMs;
            Ticket = ticket;
        }
        internal bool IsCurrent => Device.IsOnline && ReferenceEquals(Device.Device, Source)
            && (Ticket?.IsCurrent ?? true);
        internal bool TryPublish(Action action) => TryPublish(null, action);
        internal bool TryPublish(Func<RemoteToneRequest> latest, Action action)
        {
            lock (Device.OutputSync)
            {
                if (!IsCurrent || (latest != null && !ReferenceEquals(this, latest()))) return false;
                if (Ticket != null) return Ticket.TryCommit(action);
                action();
                return true;
            }
        }
    }

    internal sealed class DeferredRemoteToneQueue
    {
        private sealed class Pending { internal RemoteToneRequest Latest; }
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Pending> _pending = new();
        private readonly Action<RemoteToneRequest, Func<RemoteToneRequest>> _build;
        private readonly Action<Action> _schedule;
        internal DeferredRemoteToneQueue(Action<RemoteToneRequest, Func<RemoteToneRequest>> build,
            Action<Action> schedule = null)
        {
            _build = build;
            _schedule = schedule ?? (action => { _ = Task.Run(action); });
        }

        internal void Submit(RemoteToneRequest request, bool needsBuild)
        {
            Pending pending;
            Guid id = request.Device.InstanceGuid;
            lock (_gate)
            {
                if (_pending.TryGetValue(id, out pending))
                {
                    pending.Latest = request;
                    return;
                }
                if (!needsBuild) return;
                pending = new Pending { Latest = request };
                _pending.Add(id, pending);
            }
            _schedule(() =>
            {
                RemoteToneRequest Latest() { lock (_gate) return pending.Latest; }
                try
                {
                    while (true)
                    {
                        var current = Latest();
                        if (current.Amplitude > 0 && current.IsCurrent) _build(current, Latest);
                        lock (_gate)
                        {
                            if (!ReferenceEquals(current, pending.Latest)) continue;
                            _pending.Remove(id);
                            return;
                        }
                    }
                }
                finally
                {
                    lock (_gate)
                        if (_pending.TryGetValue(id, out var current) && ReferenceEquals(current, pending))
                            _pending.Remove(id);
                }
            });
        }
    }
}
