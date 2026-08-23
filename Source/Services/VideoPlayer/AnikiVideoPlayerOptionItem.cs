using System;

namespace AnikiHelper.Services.VideoPlayer
{
    public sealed class AnikiVideoPlayerOptionItem
    {
        public AnikiVideoPlayerOptionItem(
            string id,
            string name,
            string secondaryText = null,
            bool isSelected = false,
            bool isEnabled = true,
            int intValue = 0,
            long longValue = 0,
            double doubleValue = 0.0)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            SecondaryText = secondaryText ?? string.Empty;
            IsSelected = isSelected;
            IsEnabled = isEnabled;
            IntValue = intValue;
            LongValue = longValue;
            DoubleValue = doubleValue;
        }

        public string Id { get; }
        public string Name { get; }
        public string SecondaryText { get; }
        public bool IsSelected { get; }
        public bool IsEnabled { get; }
        public int IntValue { get; }
        public long LongValue { get; }
        public double DoubleValue { get; }
    }
}
