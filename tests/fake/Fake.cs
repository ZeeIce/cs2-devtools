using System.Collections.Generic;

// Stand-ins shaped like Presidente's state: static auto-property, private nested class list, struct in array, dictionary.
namespace HomelandSecurity.Immigration
{
    public struct Point { public float X; public int Y; }
    public enum Mode { Off, Gentle, Full }

    public sealed class StandingSystem
    {
        public static float Approval { get; private set; } = 50f;
        private float m_PublicApproval = 46.9f;
        private readonly float[] m_Mood = new float[29];
        private readonly List<Ev> m_Events = new List<Ev> { new Ev { A = 3, V = 2f, K = "x" } };
        private readonly Point[] m_Points = new Point[2];
        private Point m_Single;
        private readonly Dictionary<string, bool> m_On = new Dictionary<string, bool> { ["tariff"] = true, ["deposit"] = false };
        private Mode m_Mode = Mode.Gentle;
        private string m_Name = "Fort Johnson";
        private Inner m_Inner = new Inner();
        public bool m_Moved;

        private sealed class Ev { public byte A; public float V; public int M; public string K; }
        public sealed class Inner { public int Isolation = 30; public Point P; }

        private bool TryFound(string name, out string message) { message = "founded " + name; m_Moved = true; return true; }
        public int Month() => 24343;
        private static int Twice(int x) => x * 2;
    }

    public static class BoomLaws { private static int s_TariffPct = 10; }
}
