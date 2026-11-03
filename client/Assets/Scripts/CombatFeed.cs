using System.Collections.Generic;
using AoS2.Shared.Maps;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Merkt sich Kampf-Ereignisse für das HUD: Kill-Anzeige, Trefferanzeige (eigener Treffer vom Server bestätigt),
    /// erlittener Schaden mit Richtung, wer einen zuletzt getötet hat, Meldungen zur Teamwahl.
    /// </summary>
    public sealed class CombatFeed
    {
        public const int MaxKills = 6;

        public struct KillEntry
        {
            public string Killer;
            public string Victim;
            public Team KillerTeam;
            public Team VictimTeam;
            public string Weapon;
            public bool Headshot;
            public bool Explosion;
            public bool Mine;
            public float Time;
        }

        private readonly List<KillEntry> _kills = new List<KillEntry>();

        public IReadOnlyList<KillEntry> Kills => _kills;

        /// <summary>Zeitpunkt (Time.time) des letzten bestätigten eigenen Treffers; ob er tödlich war.</summary>
        public float LastHitTime { get; private set; } = -100;
        public bool LastHitKilled { get; private set; }
        public bool LastHitHeadshot { get; private set; }

        /// <summary>Vom Server bestätigte eigene Treffer (für Tests).</summary>
        public int HitsConfirmed { get; private set; }

        public float LastDamageTime { get; private set; } = -100;
        public int LastDamageAmount { get; private set; }

        /// <summary>Wo der Angreifer beim letzten erlittenen Treffer stand (falls bekannt).</summary>
        public Vector3? DamageFrom { get; private set; }

        public string KilledBy { get; private set; }
        public string KilledWith { get; private set; }

        /// <summary>Zuletzt das Team gewechselt (der Tod danach ist kein Kill, der Todesbildschirm sagt es so).</summary>
        public bool SwitchedTeam { get; set; }

        /// <summary>Nummer des laufenden Matches (0 = noch keins gesehen).</summary>
        public int MatchNumber { get; set; }

        /// <summary>Meldung zur Teamwahl (z. B. abgelehnt) und wann sie kam.</summary>
        public string TeamMessage { get; private set; }
        public float TeamMessageTime { get; private set; } = -100;

        public void ShowTeamMessage(string message)
        {
            TeamMessage = message;
            TeamMessageTime = Time.time;
        }

        public void Clear()
        {
            _kills.Clear();
            LastHitTime = LastDamageTime = -100;
            HitsConfirmed = 0;
            KilledBy = KilledWith = null;
            SwitchedTeam = false;
            TeamMessage = null;
            MatchNumber = 0;
        }

        public void OnDamaged(WorldEvent e, byte me, Vector3? attackerPosition)
        {
            if (e.Attacker == me && e.PlayerId != me)
            {
                LastHitTime = Time.time;
                LastHitKilled = e.Health == 0;
                LastHitHeadshot = e.Zone == (byte)HitZone.Head;
                HitsConfirmed++;
            }
            if (e.PlayerId == me)
            {
                LastDamageTime = Time.time;
                LastDamageAmount = e.Amount;
                DamageFrom = e.Attacker != me ? attackerPosition : null;
            }
        }

        public void OnKilled(WorldEvent e, byte me, string victim, string killer, string weapon, Team victimTeam, Team killerTeam)
        {
            _kills.Add(new KillEntry
            {
                Killer = killer,
                Victim = victim,
                Weapon = weapon,
                VictimTeam = victimTeam,
                KillerTeam = killerTeam,
                Headshot = e.Zone == (byte)HitZone.Head,
                Explosion = e.Zone == WorldEvent.ExplosionZone,
                Mine = e.PlayerId == me || e.Attacker == me,
                Time = Time.time,
            });
            if (_kills.Count > MaxKills) _kills.RemoveAt(0);
            if (e.PlayerId == me)
            {
                SwitchedTeam = false;
                KilledBy = e.Attacker == me ? null : killer;
                KilledWith = weapon;
            }
        }
    }
}
