using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using PadForge.Common.Input.Telemetry;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>One entry in a Custom Expression variable's telemetry picker
    /// (issue #531): a standard channel, or a custom variable by name.</summary>
    public sealed class TelemetryChannelOption : INotifyPropertyChanged
    {
        public TelemetryChannelOption(TelemetryChannel channel, string variable)
        {
            Channel = channel;
            Variable = channel == TelemetryChannel.Variable ? variable ?? string.Empty : string.Empty;
        }

        public TelemetryChannel Channel { get; }
        public string Variable { get; }
        public string Display => TelemetryChannelNames.DisplayName(Channel, Variable);

        public bool Matches(TelemetryChannel channel, string variable)
            => Channel == channel
               && (channel != TelemetryChannel.Variable
                   || string.Equals(Variable, variable, StringComparison.OrdinalIgnoreCase));

        public event PropertyChangedEventHandler PropertyChanged;
        internal void RaiseDisplayChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        public override string ToString() => Display;
    }

    /// <summary>
    /// The telemetry picker's entries, shared by every variable row: the
    /// standard channels, then the custom variables of every custom format.
    /// Rebuilt on the UI thread when the formats change, and named again on
    /// a language change.
    /// </summary>
    public static class TelemetryChannelOptions
    {
        public static ObservableCollection<TelemetryChannelOption> Items { get; } = Build();

        private static ObservableCollection<TelemetryChannelOption> Build()
        {
            var items = new ObservableCollection<TelemetryChannelOption>();
            Fill(items);
            Strings.Instance.PropertyChanged += (_, _) =>
            {
                foreach (var item in items) item.RaiseDisplayChanged();
            };
            return items;
        }

        private static void Fill(ObservableCollection<TelemetryChannelOption> items)
        {
            foreach (TelemetryChannel channel in Enum.GetValues(typeof(TelemetryChannel)))
                if (channel is not (TelemetryChannel.None or TelemetryChannel.Variable))
                    items.Add(new TelemetryChannelOption(channel, null));
            foreach (var variable in GameTelemetryRuntime.Variables)
                items.Add(new TelemetryChannelOption(TelemetryChannel.Variable, variable.Name));
        }

        /// <summary>Brings the list up to date after the custom formats
        /// changed. Entries that stay keep their place and their instance, so
        /// a picker showing one keeps its selection: clearing the list would
        /// blank every open picker. A variable a row names that no format
        /// defines any more reads 0, and the row's summary still names it.</summary>
        public static void Refresh()
        {
            var wanted = new System.Collections.Generic.List<TelemetryChannelOption>();
            foreach (TelemetryChannel channel in Enum.GetValues(typeof(TelemetryChannel)))
                if (channel is not (TelemetryChannel.None or TelemetryChannel.Variable))
                    wanted.Add(Find(channel, null) ?? new TelemetryChannelOption(channel, null));
            foreach (var variable in GameTelemetryRuntime.Variables)
                wanted.Add(Find(TelemetryChannel.Variable, variable.Name)
                           ?? new TelemetryChannelOption(TelemetryChannel.Variable, variable.Name));
            for (int i = Items.Count - 1; i >= 0; i--)
                if (!wanted.Contains(Items[i])) Items.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++)
            {
                if (i < Items.Count && ReferenceEquals(Items[i], wanted[i])) continue;
                int at = Items.IndexOf(wanted[i]);
                if (at >= 0) Items.Move(at, i);
                else Items.Insert(i, wanted[i]);
            }
        }

        public static TelemetryChannelOption Find(TelemetryChannel channel, string variable)
        {
            foreach (var item in Items)
                if (item.Matches(channel, variable)) return item;
            return null;
        }
    }

    /// <summary>The words for a telemetry channel.</summary>
    public static class TelemetryChannelNames
    {
        public static string DisplayName(TelemetryChannel channel, string variable)
        {
            var s = Strings.Instance;
            return channel switch
            {
                TelemetryChannel.SpeedKmh => s.Telemetry_Target_Speed,
                TelemetryChannel.SpeedMph => s.Telemetry_Channel_SpeedMph,
                TelemetryChannel.Rpm => s.Telemetry_Target_Rpm,
                TelemetryChannel.MaxRpm => s.Telemetry_Target_MaxRpm,
                TelemetryChannel.RpmPercent => s.Telemetry_Channel_RpmPercent,
                TelemetryChannel.Gear => s.Telemetry_Channel_Gear,
                TelemetryChannel.ThrottlePercent => s.Telemetry_Channel_Throttle,
                TelemetryChannel.BrakePercent => s.Telemetry_Channel_Brake,
                TelemetryChannel.ClutchPercent => s.Telemetry_Channel_Clutch,
                TelemetryChannel.HandbrakePercent => s.Telemetry_Channel_Handbrake,
                TelemetryChannel.SteeringPercent => s.Telemetry_Channel_Steering,
                TelemetryChannel.Driving => s.Telemetry_Channel_Driving,
                TelemetryChannel.Variable => variable ?? string.Empty,
                _ => s.Macro_NotSet,
            };
        }
    }
}
