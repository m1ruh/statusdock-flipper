using System;
using System.Linq;

namespace StatusDockPreview
{
    internal enum ClockAlignment { Center, Left, Right }
    internal enum StatsMetric { Cpu, Ram, Gpu }

    internal sealed class StatsLayout
    {
        // Match the order indices in statusdock.c. Zero keeps the original layout.
        private static readonly StatsMetric[][] Orders =
        {
            new[] { StatsMetric.Cpu, StatsMetric.Ram, StatsMetric.Gpu },
            new[] { StatsMetric.Cpu, StatsMetric.Gpu, StatsMetric.Ram },
            new[] { StatsMetric.Ram, StatsMetric.Cpu, StatsMetric.Gpu },
            new[] { StatsMetric.Ram, StatsMetric.Gpu, StatsMetric.Cpu },
            new[] { StatsMetric.Gpu, StatsMetric.Cpu, StatsMetric.Ram },
            new[] { StatsMetric.Gpu, StatsMetric.Ram, StatsMetric.Cpu }
        };

        private int order;
        public ClockAlignment Alignment { get; set; }
        public bool NumbersOnly { get; set; }
        public int Packed { get { return (int)Alignment | (order << 2) | (NumbersOnly ? 32 : 0); } }

        public StatsLayout(int packed)
        {
            if (packed < 0 || packed > 63 || (packed & 3) > 2 || ((packed >> 2) & 7) >= Orders.Length)
                return;
            Alignment = (ClockAlignment)(packed & 3);
            order = (packed >> 2) & 7;
            NumbersOnly = (packed & 32) != 0;
        }

        public StatsMetric RowAt(int index)
        {
            return Orders[order][index];
        }

        public void MoveRow(int index, int offset)
        {
            var rows = (StatsMetric[])Orders[order].Clone();
            int target = index + offset;
            rows[index] = rows[target];
            rows[target] = RowAt(index);
            order = Array.FindIndex(Orders, candidate => candidate.SequenceEqual(rows));
        }
    }
}
