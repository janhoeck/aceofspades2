using AoS2.Net;
using AoS2.Server;
using AoS2.Shared;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;

namespace AoS2.LoadTest;

/// <summary>
/// Ein simulierter Spieler: echte <see cref="ClientSession"/> und <see cref="NetClient"/> wie im Spiel, nur die Eingaben kommen
/// von einer einfachen Steuerung. Er wählt eine zufällige Klasse und automatisch ein Team, läuft Richtung Kartenmitte, springt
/// ab und zu, schießt mit der ersten Waffe seiner Klasse auf den nächsten sichtbaren Gegner, nimmt gelegentlich eine andere
/// (Granaten, Pistole …) und gräbt Blöcke ab.
/// </summary>
public sealed class Bot : IDisposable
{
    private const byte Spade = 0, Primary = 1;

    private readonly Random _random;
    private readonly Func<double> _clock;
    private double _next;
    private bool _teamRequested;
    private long _ticks;
    private float _heading;
    private int _specialUntil;
    private byte _special;
    private Vec3 _goal;

    public Bot(string name, GameContent content, Func<double> clock, int seed)
    {
        Name = name;
        _clock = clock;
        _random = new Random(seed);
        Net = new NetClient();
        Session = new ClientSession(content.Registry, content.Rules, content.RigJson, map => map == content.MapName ? content.MapFileBytes : null, Net, clock);
        Session.CombatEvent += e =>
        {
            if (e.Type == WorldEventType.PlayerKilled && e.Attacker == Session.PlayerId && e.PlayerId != e.Attacker) Kills++;
        };
        Net.Session = Session;
    }

    public string Name { get; }
    public NetClient Net { get; }
    public ClientSession Session { get; }
    public PlayerInput Input;
    public int Kills { get; private set; }
    public int ShotsFired { get; private set; }
    public int SnapshotsAtStart { get; set; }

    public bool InTeam => Session.Phase == ClientPhase.Playing && Session.Team != Team.None;
    public bool Disconnected => Session.Phase == ClientPhase.Disconnected;

    public void Connect(ServerAddress address) =>
        Net.Connect(address, DevJoinTicket.Create(Guid.NewGuid(), Name, DateTimeOffset.UtcNow));

    /// <summary>Netzwerk abfragen und so viele Ticks rechnen, wie seit dem letzten Aufruf fällig sind (60/s).</summary>
    public void Update()
    {
        Net.Poll();
        Session.Update();
        if (Session.Phase == ClientPhase.Playing && !_teamRequested)
        {
            var classes = Session.Classes.All;
            Session.RequestClass(classes[_random.Next(classes.Count)]);
            Session.RequestTeam(Team.None);
            _teamRequested = true;
            _goal = MapCenter();
        }
        var now = _clock();
        if (_next == 0) _next = now;
        while (now >= _next)
        {
            _next += 1.0 / 60;
            if (Session.Phase != ClientPhase.Playing) continue;
            Think();
            if (Session.Tick(Input) != 0) ShotsFired++;
            if (Input.Weapon == Spade && _ticks % 15 == 0) Dig();
        }
    }

    private Vec3 MapCenter() =>
        new(Session.World.SizeX / 2f + (float)(_random.NextDouble() - 0.5) * 40, 0, Session.World.SizeZ / 2f + (float)(_random.NextDouble() - 0.5) * 40);

    private void Think()
    {
        _ticks++;
        var me = Session.Prediction.State;
        var eye = me.Eye(Session.Movement);
        Input.Buttons = InputButtons.Forward;
        if (_random.Next(90) == 0) Input.Buttons |= InputButtons.Jump;

        // Ab und zu etwas anderes: eine andere Waffe der Klasse oder Graben.
        if (_ticks >= _specialUntil && _random.Next(600) == 0)
        {
            _special = (byte)_random.Next(Session.Class.SlotCount + 1); // 0 = Spaten
            _specialUntil = (int)_ticks + 60;
        }
        var weapon = _ticks < _specialUntil ? _special : Primary;

        var target = NearestEnemy(me.Position, out var distance);
        if (target != null && weapon != Spade)
        {
            var aim = target.Current.Position + new Vec3(0, 1.4f, 0);
            Look(eye, aim, jitter: 1.5f);
            Input.Weapon = weapon;
            if (distance < 10) Input.Buttons &= ~InputButtons.Forward;
            Input.Buttons |= InputButtons.Fire;
            return;
        }

        // Kein Gegner in Sicht: Richtung Ziel in der Kartenmitte laufen, gelegentlich ein neues Ziel.
        if (_ticks % 240 == 0 || Horizontal(me.Position, _goal) < 4) _goal = MapCenter();
        _heading = (float)(Math.Atan2(_goal.X - me.Position.X, _goal.Z - me.Position.Z) * 180 / Math.PI) + (float)(_random.NextDouble() - 0.5) * 30;
        Input.Yaw = _heading;
        Input.Pitch = weapon == Spade ? 45 : 0;
        Input.Weapon = weapon;
        Input.Buttons |= InputButtons.Sprint;
    }

    private RemotePlayer? NearestEnemy(Vec3 from, out float distance)
    {
        RemotePlayer? best = null;
        distance = 60;
        foreach (var p in Session.RemotePlayers.Values)
        {
            if (!p.HasState || !p.Current.Alive || p.Team == Team.None || p.Team == Session.Team) continue;
            var d = Vec3.Distance(from, p.Current.Position);
            if (d >= distance) continue;
            distance = d;
            best = p;
        }
        return best;
    }

    private void Look(Vec3 eye, Vec3 target, float jitter)
    {
        double dx = target.X - eye.X, dy = target.Y - eye.Y, dz = target.Z - eye.Z;
        Input.Yaw = (float)(Math.Atan2(dx, dz) * 180 / Math.PI + (_random.NextDouble() - 0.5) * jitter);
        Input.Pitch = (float)(-Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI + (_random.NextDouble() - 0.5) * jitter);
        Input.Pitch = Math.Clamp(Input.Pitch, -89, 89);
        while (Input.Yaw < 0) Input.Yaw += 360;
        while (Input.Yaw >= 360) Input.Yaw -= 360;
    }

    private void Dig()
    {
        var state = Session.Prediction.State;
        var eye = state.Eye(Session.Movement);
        if (VoxelRaycast.Cast(Session.World, eye, PlayerMovement.LookDirection(Input.Yaw, Input.Pitch), BlockEditing.Reach, out var hit))
            Session.RequestRemove(hit.X, hit.Y, hit.Z, hit.Distance);
    }

    private static float Horizontal(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Dispose()
    {
        Net.Disconnect();
        Net.Poll();
        Net.Dispose();
    }
}
