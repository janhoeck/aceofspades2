using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;
using Microsoft.Extensions.Logging;

namespace AoS2.Server;

/// <summary>Ergebnis eines Hitscan-Schusses (für Tests und Protokoll).</summary>
public readonly record struct ShotReport(byte Shooter, byte? Victim, HitZone Zone, double ViewTick, Vec3 From, Vec3 To);

/// <summary>Ein fliegendes Geschoss auf dem Server und der Tick, in dem es abgefeuert wurde.</summary>
internal sealed class ServerProjectile
{
    public Projectile Projectile;
    public uint SpawnTick;
}

/// <summary>
/// Kampf: Leben, Tod und Respawn, Hitscan-Schüsse mit Lag-Ausgleich, Geschosse und Explosionen.
/// <para>
/// <b>Lag-Ausgleich (Lag Compensation), Schritt für Schritt:</b>
/// <list type="number">
/// <item>Der Client zeigt andere Spieler etwas in der Vergangenheit (Interpolation, 100 ms). Wer schießt, zielt also auf
///       eine Position, an der der Gegner auf dem Server schon nicht mehr steht.</item>
/// <item>Darum schickt der Client mit der Feuern-Eingabe den Zeitpunkt mit, den er gerade gezeigt hat (<see cref="PlayerInput.ViewTick"/>).</item>
/// <item>Der Server merkt sich für jeden Spieler pro Tick, was die Snapshots von ihm zeigen: Position, Blick, Haltung und
///       Animationszustand (<see cref="ServerPlayer.RecordView"/>).</item>
/// <item>Beim Schuss rechnet er daraus genau das Bild nach, das der Schütze gesehen hat: dieselbe Interpolation zwischen
///       zwei Snapshots (<see cref="PlayerSnapshot.Lerp"/>) und dieselbe Pose (<see cref="PlayerPose"/>).</item>
/// <item>Gegen diese zurückgespulten Trefferboxen prüft er den Strahl. Blöcke prüft er in der aktuellen Welt.</item>
/// </list>
/// Wie weit zurückgespult wird, ist begrenzt (<c>maxRewindSeconds</c> in combat.json), damit ein Client mit
/// riesigem Ping nicht um Ecken treffen kann.
/// </para>
/// </summary>
public sealed partial class Match
{
    private readonly PlayerPose _pose;
    private readonly List<ServerProjectile> _projectiles = new();
    private readonly List<BlockPos> _destroyed = new();
    private readonly List<BlockPos> _damaged = new();
    private int _nextProjectileId;

    /// <summary>Schaden an Blöcken (nur beschädigte Blöcke).</summary>
    public BlockDamage BlockDamage { get; }

    /// <summary>Wird nach jedem Hitscan-Schuss aufgerufen (Tests, Protokoll).</summary>
    public event Action<ShotReport>? ShotResolved;

    /// <summary>Fliegende Geschosse.</summary>
    public IEnumerable<Projectile> Projectiles => _projectiles.Select(p => p.Projectile);

    /// <summary>So sehen andere Clients diesen Spieler gerade (Inhalt des Snapshots).</summary>
    private static PlayerSnapshot ViewOf(ServerPlayer p) => new()
    {
        Id = p.Id,
        Position = p.State.Position,
        Yaw = p.LastInput.Yaw,
        Pitch = p.LastInput.Pitch,
        Crouching = p.State.Crouching,
        OnGround = p.State.OnGround,
        Alive = p.Alive,
        Life = p.Life,
        Weapon = p.WeaponsState.Slot,
        Class = p.Class.Id,
        Animation = p.AnimationState,
    };

    // ---- Tod und Respawn ----

    private void RespawnDue()
    {
        foreach (var p in _players.Values)
        {
            if (p.Alive || p.Team == Team.None || Tick < p.RespawnAt) continue;
            Respawn(p);
            _log.LogDebug("{Name} ist wieder da", p.Name);
        }
    }

    /// <summary>
    /// Schaden zufügen. Bei 0 Lebenspunkten stirbt der Spieler und erscheint nach <c>respawnSeconds</c> neu. Kein Schaden in der
    /// Spawnzone, nach dem Matchende und (ohne Teambeschuss) von Mitspielern. Der Kill eines Gegners ist ein Punkt für sein Team.
    /// </summary>
    internal void Damage(ServerPlayer victim, byte attacker, float amount, byte weapon, byte zone)
    {
        if (!victim.Alive || !CanDamage(victim, FindPlayer(attacker))) return;
        var damage = (int)Math.Ceiling(amount - 0.001f);
        if (damage <= 0) return;
        damage = Math.Min(damage, victim.Health);
        victim.Health -= damage;

        _scratch.Clear();
        WorldEvents.WritePlayerDamaged(_scratch, victim.Id, attacker, weapon, zone, (byte)Math.Min(damage, 255), (byte)victim.Health);
        Broadcast(_scratch);
        if (victim.Alive) return;

        victim.RespawnAt = Tick + (uint)_content.Combat.RespawnTicks;
        victim.Deaths++;
        var killer = FindPlayer(attacker);
        var enemyKill = killer != null && killer != victim && killer.Team != Team.None && killer.Team != victim.Team;
        if (enemyKill) killer!.Kills++;
        _scratch.Clear();
        WorldEvents.WritePlayerKilled(_scratch, victim.Id, attacker, weapon, zone);
        Broadcast(_scratch);
        _log.LogInformation("{Victim} wurde von {Killer} getötet ({Weapon})", victim.Name,
            killer == null ? "?" : killer == victim ? "sich selbst" : killer.Name, _content.Combat.Weapon(weapon)?.DisplayName);
        BroadcastStats(victim);
        if (enemyKill)
        {
            BroadcastStats(killer!);
            Score(killer!.Team);
        }
    }

    // ---- Schießen ----

    /// <summary>Die Waffe auf Platz <paramref name="slot"/> der Klasse feuert in diesem Eingabe-Tick (laut <see cref="WeaponSim"/>).</summary>
    private void Fire(ServerPlayer shooter, int slot, PlayerInput input)
    {
        var weapon = shooter.Class.Weapon(slot);
        if (weapon == null) return;
        var eye = shooter.State.Eye(shooter.Class.Movement);
        var dir = PlayerMovement.LookDirection(input.Yaw, input.Pitch);
        if (weapon.Kind == WeaponKind.Hitscan) FireHitscan(shooter, weapon, eye, dir, input.ViewTick);
        else SpawnProjectile(shooter, weapon, eye, dir);
    }

    private void FireHitscan(ServerPlayer shooter, WeaponSettings weapon, Vec3 eye, Vec3 dir, double viewTick)
    {
        var blockHit = VoxelRaycast.Cast(World, eye, dir, weapon.Range, out var hit);
        var nearest = blockHit ? hit.Distance : weapon.Range;
        // Die Grenze einer Spawnzone hält den Schuss auf wie eine Wand (kein Schießen in den Spawn hinein).
        if (Zones.Raycast(eye, dir, nearest, out var wall, out _, out _, out _))
        {
            nearest = wall;
            blockHit = false;
        }

        // Lag-Ausgleich: jeden Gegner so zurückholen, wie der Schütze ihn gesehen hat. Mitspieler (ohne Teambeschuss) lassen
        // den Schuss durch.
        var view = ClampViewTick(viewTick);
        ServerPlayer? victim = null;
        var zone = HitZone.Torso;
        foreach (var other in _players.Values)
        {
            if (other == shooter || !other.Alive || PassesThrough(shooter, other) || !TryRewind(other, view, out var seen) || !seen.Alive) continue;
            // Trefferzonen: Grundkörper plus die Ausrüstung der Klasse, die der Schütze gesehen hat.
            _pose.Evaluate(seen.Animation, view, seen.Pitch, seen.Weapon);
            var gear = _content.Classes.Get(seen.Class)?.Gear;
            if (!_pose.Raycast(seen.Position, seen.Yaw, gear, eye, dir, nearest, out var distance, out var box)) continue;
            nearest = distance;
            victim = other;
            zone = _content.Rig.Boxes[box].Zone;
        }

        var end = eye + dir * nearest;
        var result = ShotHit.Nothing;
        if (victim != null)
        {
            result = ShotHit.Player;
            // Grundschaden mit Abfall über die Entfernung, dann der Faktor der Trefferzone.
            Damage(victim, shooter.Id, weapon.DamageAt(nearest) * _content.Combat.ZoneFactor(zone), (byte)weapon.Id, (byte)zone);
        }
        else if (blockHit)
        {
            result = ShotHit.Block;
            DamageBlock(hit.X, hit.Y, hit.Z, weapon.BlockDamage);
        }

        _scratch.Clear();
        WorldEvents.WriteShotFired(_scratch, shooter.Id, (byte)weapon.Id, eye, end, result);
        BroadcastExcept(shooter.Id, _scratch);
        ShotResolved?.Invoke(new ShotReport(shooter.Id, victim?.Id, zone, view, eye, end));
    }

    /// <summary>Gesehener Zeitpunkt, begrenzt auf <c>maxRewindSeconds</c> zurück und höchstens den letzten gemerkten Tick.</summary>
    private double ClampViewTick(double viewTick)
    {
        double latest = Tick - 1, earliest = Math.Max(0, latest - _content.Combat.MaxRewindTicks);
        return viewTick < earliest ? earliest : viewTick > latest ? latest : viewTick;
    }

    /// <summary>
    /// Was ein Client zum Tick <paramref name="tick"/> von <paramref name="target"/> gezeigt hat: Interpolation zwischen den
    /// beiden Snapshots davor und danach, wie in <see cref="Shared.Net.Client.ClientSession"/>. Ist der Snapshot danach noch
    /// nicht verschickt, zeigt der Client den letzten (keine Vorhersage), also auch hier.
    /// </summary>
    internal bool TryRewind(ServerPlayer target, double tick, out PlayerSnapshot view)
    {
        var a = (uint)(Math.Floor(tick / SnapshotInterval) * SnapshotInterval);
        var b = a + SnapshotInterval;
        var haveA = target.TryGetView(a, out var va);
        if (b > Tick - 1 || !target.TryGetView(b, out var vb))
        {
            view = va;
            return haveA;
        }
        if (!haveA)
        {
            view = vb; // war beim Tick davor noch nicht im Spiel
            return true;
        }
        view = PlayerSnapshot.Lerp(va, vb, (float)((tick - a) / SnapshotInterval));
        return true;
    }

    private void DamageBlock(int x, int y, int z, float amount)
    {
        if (amount <= 0 || !Zones.CanEditBlock(x, y, z)) return;
        if (BlockDamage.Add(x, y, z, amount))
        {
            RemoveBlock(x, y, z);
            Falling.CheckAfterRemoval(x, y, z);
            return;
        }
        _scratch.Clear();
        WorldEvents.WriteBlockDamaged(_scratch, x, y, z, BlockDamage.Level(x, y, z));
        Broadcast(_scratch);
    }

    private void RemoveBlock(int x, int y, int z)
    {
        World.SetBlock(x, y, z, BlockRegistry.Air);
        _scratch.Clear();
        WorldEvents.WriteBlockSet(_scratch, x, y, z, BlockRegistry.Air);
        Broadcast(_scratch);
    }

    // ---- Geschosse ----

    private void SpawnProjectile(ServerPlayer shooter, WeaponSettings weapon, Vec3 eye, Vec3 dir)
    {
        var p = new ServerProjectile
        {
            Projectile = new Projectile { Id = ++_nextProjectileId, Weapon = (byte)weapon.Id, Owner = shooter.Id, Position = eye, Velocity = dir * weapon.Speed },
            SpawnTick = Tick,
        };
        _projectiles.Add(p);
        _scratch.Clear();
        WorldEvents.WriteProjectileSpawned(_scratch, p.Projectile, Tick);
        Broadcast(_scratch);
    }

    /// <summary>
    /// Alle Geschosse einen Tick weiter. Geschosse aus diesem Tick fliegen erst im nächsten los, genau wie der Client sie
    /// ab dem gemeldeten Tick nachrechnet. Raketen explodieren auch an Spielern (Kollisionsquader, außer dem Schützen).
    /// </summary>
    private void StepProjectiles()
    {
        var dt = _content.Movement.DeltaTime;
        for (var i = 0; i < _projectiles.Count; i++)
        {
            var sp = _projectiles[i];
            if (sp.SpawnTick == Tick) continue;
            var weapon = _content.Combat.Weapon(sp.Projectile.Weapon)!;
            var start = sp.Projectile.Position;

            float? playerHit = null;
            if (weapon.Kind == WeaponKind.Rocket)
            {
                var v = sp.Projectile.Velocity;
                v.Y -= weapon.Gravity * dt;
                playerHit = HitPlayer(start, v * dt, sp.Projectile.Owner);
            }

            var explode = ProjectileSim.Step(ref sp.Projectile, weapon, World, dt, Zones);
            if (playerHit is { } t && (!explode || t <= Vec3.Distance(start, sp.Projectile.Position)))
            {
                sp.Projectile.Position = start + sp.Projectile.Velocity.Normalized * t;
                explode = true;
            }
            if (!explode) continue;
            _projectiles.RemoveAt(i--);
            Explode(sp.Projectile, weapon);
        }
    }

    /// <summary>Trifft die Strecke den Kollisionsquader eines Spielers (außer <paramref name="owner"/>)? Liefert die Entfernung.</summary>
    private float? HitPlayer(Vec3 start, Vec3 move, byte owner)
    {
        var length = move.Length;
        if (length <= 0) return null;
        var dir = move * (1 / length);
        var shooter = FindPlayer(owner);
        float? nearest = null;
        foreach (var p in _players.Values)
        {
            if (p.Id == owner || !p.Alive || PassesThrough(shooter, p)) continue;
            var m = p.Class.Movement;
            var feet = p.State.Position;
            var min = new Vec3(feet.X - m.HalfWidth, feet.Y, feet.Z - m.HalfWidth);
            var max = new Vec3(feet.X + m.HalfWidth, feet.Y + m.Height(p.State.Crouching), feet.Z + m.HalfWidth);
            if (Geometry.RayAabb(start, dir, min, max, nearest ?? length, out var t)) nearest = t;
        }
        return nearest;
    }

    /// <summary>
    /// Explosion: erst Spieler (Sichtlinie in der Welt vor der Explosion), dann Blöcke. Zerstörte Blöcke verschwinden,
    /// danach fallen Gruppen, die keine Verbindung zum Boden mehr haben.
    /// </summary>
    private void Explode(Projectile p, WeaponSettings weapon)
    {
        var e = weapon.Explosion;
        _scratch.Clear();
        WorldEvents.WriteProjectileExploded(_scratch, p.Id, p.Weapon, p.Position, Tick);
        Broadcast(_scratch);

        foreach (var victim in _players.Values.ToList())
        {
            if (!victim.Alive) continue;
            var m = victim.Class.Movement;
            var amount = Explosions.PlayerDamage(World, p.Position, e.Radius, e.Damage, victim.State.Position, m.HalfWidth, m.Height(victim.State.Crouching));
            if (victim.Id == p.Owner) amount *= _content.Combat.SelfDamage;
            Damage(victim, p.Owner, amount, p.Weapon, WorldEvent.ExplosionZone);
        }

        _destroyed.Clear();
        _damaged.Clear();
        Explosions.DamageBlocks(World, BlockDamage, p.Position, e.BlockRadius, e.BlockDamage, _destroyed, _damaged, Zones);
        foreach (var b in _destroyed) RemoveBlock(b.X, b.Y, b.Z);
        foreach (var b in _damaged)
        {
            _scratch.Clear();
            WorldEvents.WriteBlockDamaged(_scratch, b.X, b.Y, b.Z, BlockDamage.Level(b.X, b.Y, b.Z));
            Broadcast(_scratch);
        }
        if (_destroyed.Count > 0) Falling.CheckAfterRemovals(_destroyed);
    }

    private void BroadcastExcept(byte except, NetWriter singleEvent)
    {
        foreach (var id in _events.Keys)
        {
            if (id == except) continue;
            BeginEvent(id);
            _events[id].WriteBytes(singleEvent.Buffer, 0, singleEvent.Length);
        }
    }
}
