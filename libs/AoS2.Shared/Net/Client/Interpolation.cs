using System;
using System.Collections.Generic;

namespace AoS2.Shared.Net.Client
{
    /// <summary>
    /// Schätzt, welcher Server-Tick gerade „jetzt“ ist. Jeder Snapshot sagt, zu welchem Tick er gehört; daraus und aus
    /// der eigenen Uhr ergibt sich ein Abstand zwischen Server- und Client-Zeit. Weil Pakete unterschiedlich lange
    /// unterwegs sind (Jitter), wird der Abstand geglättet: Früh ankommende Pakete zählen stärker als verspätete.
    /// </summary>
    public sealed class ServerClock
    {
        private readonly double _tickRate;
        private double _offset;
        private bool _initialized;

        public ServerClock(int tickRate)
        {
            _tickRate = tickRate;
        }

        public bool IsInitialized => _initialized;

        public void OnSnapshot(uint serverTick, double localTime)
        {
            var sample = serverTick / _tickRate - localTime;
            if (!_initialized || Math.Abs(sample - _offset) > 0.5)
            {
                _offset = sample;
                _initialized = true;
                return;
            }
            _offset += (sample - _offset) * (sample > _offset ? 0.2 : 0.02);
        }

        /// <summary>Geschätzter Server-Tick (mit Nachkommastellen) zur lokalen Zeit in Sekunden.</summary>
        public double ServerTick(double localTime) => (localTime + _offset) * _tickRate;
    }

    /// <summary>
    /// <b>Interpolation:</b> Speichert Zustände mit ihrem Server-Tick und liefert einen Wert für einen Zeitpunkt
    /// dazwischen. Andere Spieler werden leicht verzögert dargestellt (z. B. 100 ms): So liegen fast immer zwei
    /// Snapshots vor und hinter dem Darstellungszeitpunkt, und die Bewegung wirkt flüssig, auch wenn mal einer fehlt.
    /// </summary>
    public sealed class InterpolationBuffer<T> where T : struct
    {
        private const int Capacity = 32;
        private readonly List<uint> _ticks = new List<uint>(Capacity);
        private readonly List<T> _values = new List<T>(Capacity);

        public int Count => _ticks.Count;

        public uint NewestTick => _ticks.Count > 0 ? _ticks[_ticks.Count - 1] : 0;

        public T Newest => _values[_values.Count - 1];

        public void Add(uint tick, T value)
        {
            if (_ticks.Count > 0 && tick <= NewestTick) return; // veraltet oder doppelt
            if (_ticks.Count == Capacity)
            {
                _ticks.RemoveAt(0);
                _values.RemoveAt(0);
            }
            _ticks.Add(tick);
            _values.Add(value);
        }

        /// <summary>
        /// Wert zum Tick <paramref name="tick"/>. Vor dem ältesten Eintrag gilt der älteste, nach dem neuesten der neueste
        /// (keine Vorhersage über den letzten Snapshot hinaus). <c>false</c>, solange nichts gespeichert ist.
        /// </summary>
        public bool Sample(double tick, Func<T, T, float, T> lerp, out T value)
        {
            value = default;
            if (_ticks.Count == 0) return false;
            if (tick <= _ticks[0])
            {
                value = _values[0];
                return true;
            }
            for (var i = 1; i < _ticks.Count; i++)
            {
                if (tick > _ticks[i]) continue;
                var t = (float)((tick - _ticks[i - 1]) / (_ticks[i] - _ticks[i - 1]));
                value = lerp(_values[i - 1], _values[i], t);
                return true;
            }
            value = Newest;
            return true;
        }

        /// <summary>
        /// Wie <see cref="Sample"/>, schreibt aber nach dem neuesten Eintrag gleichmäßig mit der letzten Änderung weiter,
        /// höchstens <paramref name="maxTicks"/> Ticks. Nur für Werte, die sich stetig ändern (fallende Blöcke).
        /// </summary>
        public bool SampleExtrapolated(double tick, Func<T, T, float, T> lerp, double maxTicks, out T value)
        {
            if (!Sample(tick, lerp, out value)) return false;
            var count = _ticks.Count;
            if (count < 2 || tick <= _ticks[count - 1]) return true;
            var span = _ticks[count - 1] - _ticks[count - 2];
            var ahead = Math.Min(tick - _ticks[count - 1], maxTicks);
            value = lerp(_values[count - 2], _values[count - 1], (float)(1 + ahead / span));
            return true;
        }

        public void Clear()
        {
            _ticks.Clear();
            _values.Clear();
        }
    }
}
