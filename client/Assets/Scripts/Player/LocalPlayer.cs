using System.Collections.Generic;
using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;
using UnityEngine;
using UnityEngine.InputSystem;
using PlayerInput = AoS2.Shared.Movement.PlayerInput; // nicht UnityEngine.InputSystem.PlayerInput

namespace AoS2.Client.Player
{
    /// <summary>
    /// Der eigene Spieler: liest Maus und Tastatur, rechnet die Bewegung mit dem gemeinsamen Code
    /// (<see cref="PlayerMovement"/>) und setzt die Kamera. Abbauen (Linksklick) und Bauen (Rechtsklick)
    /// laufen ebenfalls im Tick, mit kurzer Pause zwischen zwei Aktionen.
    /// Online rechnet die <see cref="AoS2.Shared.Net.Client.ClientSession"/> den Tick (Vorhersage) und schickt die Eingabe an den Server.
    ///
    /// Online kommen die Waffen der eigenen Klasse dazu (Platz 2 … 5): Linksklick feuert, R lädt nach, rechte Maustaste zielt
    /// mit dem Zielfernrohr (Waffen mit <c>zoom</c>). Die Waffen rechnet die Session wie die Bewegung voraus; feuert sie,
    /// zeigt der Spieler sofort Mündungsfeuer und Leuchtspur, getroffen hat aber erst, was der Server bestätigt.
    ///
    /// Steuerung: WASD laufen, Maus schauen, Leertaste springen, Strg ducken, Shift sprinten, 1 Spaten, 2–5 Waffen,
    /// Mausrad Waffe wechseln, mit Spaten: Linksklick abbauen, Rechtsklick bauen, B Baublock; mit Waffe: Linksklick feuern,
    /// R nachladen, Rechtsklick zielen. M Klasse und Team, K Klasse. Esc gibt die Maus frei (zweimal: zurück ins Startmenü).
    /// </summary>
    public sealed class LocalPlayer : MonoBehaviour
    {
        private const float MouseSensitivity = 0.1f; // Grad pro Pixel
        private const float CrouchSmoothing = 10f;
        private const float StepSmoothing = 14f;
        private const float ZoomSmoothing = 18f;

        /// <summary>Senkrechtes Sichtfeld der Kamera in Grad (ohne Zielfernrohr).</summary>
        public const float FieldOfView = 75f;

        /// <summary>Wie schnell eine Korrektur durch den Server ausgeglichen wird (größer = schneller).</summary>
        private const float CorrectionSmoothing = 12f;

        /// <summary>Größere Korrekturen (in Blöcken) werden nicht geglättet, sondern sofort übernommen.</summary>
        private const float MaxSmoothedCorrection = 2f;

        private Vector3 _correctionOffset;

        private GameClient _game;
        private Camera _camera;
        private BlockHighlight _highlight;
        private PlayerState _state;
        private PlayerState _previous;
        private float _yaw;
        private float _pitch;
        private bool _removeRequested;
        private bool _placeRequested;
        private bool _waitForRelease;
        private int _cooldownTicks;
        private bool _fireHeld;
        private bool _testFireHeld;
        private bool _fireRequested;
        private bool _reloadRequested;
        private bool _aimHeld;
        private bool _testAimHeld;
        private WeaponView _weaponView;
        private float _visualEyeHeight;
        private float _stepOffset;
        private readonly List<BlockType> _buildable = new List<BlockType>();

        public PlayerState State => _state;
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        public BlockType SelectedBlock => _buildable.Count > 0 ? _buildable[SelectedIndex] : null;
        public int SelectedIndex { get; private set; }

        /// <summary>Gewählter Platz: 0 = Spaten, 1 … = Waffen der Klasse (offline nur der Spaten).</summary>
        public int Slot { get; private set; }

        /// <summary>Waffe auf dem gewählten Platz (<c>null</c> = Spaten oder offline).</summary>
        public WeaponSettings Weapon => _game.Session?.Class.Weapon(Slot);

        /// <summary>Rechte Maustaste mit einer Waffe mit Zielfernrohr: Die Kamera vergrößert (<see cref="WeaponSettings.Zoom"/>).</summary>
        public bool Zoomed => (_aimHeld || _testAimHeld) && Weapon != null && Weapon.Zoom > 1 && (_game.Session == null || _game.Session.Alive);

        /// <summary>Waffen, wie die Vorhersage sie gerade sieht (offline leer).</summary>
        public WeaponState Weapons => _game.Session?.Prediction?.Weapons ?? default;

        /// <summary>Wie oft die eigene Waffe laut Vorhersage gefeuert hat (für Tests).</summary>
        public int ShotsFired { get; private set; }

        /// <summary>Block unter dem Fadenkreuz (in Reichweite), sonst <c>null</c>.</summary>
        public VoxelHit? Target { get; private set; }

        /// <summary>Tab gedrückt: Punktetabelle zeigen.</summary>
        public bool ScoreboardHeld { get; private set; }

        /// <summary>Für Tests: ersetzt die Tasten (Maus und Tastatur werden dann nicht gelesen).</summary>
        public InputButtons? TestButtons { get; set; }

        /// <summary>Für Tests: Blickrichtung setzen, ohne die Position anzufassen (online wichtig, die gehört dem Server).</summary>
        public void TestLook(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = pitch;
        }

        /// <summary>Für Tests: Waffenplatz wählen (wie die Zifferntasten).</summary>
        public void TestSelectSlot(int slot) => SelectSlot(slot);

        /// <summary>Für Tests: rechte Maustaste (Zielfernrohr) halten oder loslassen.</summary>
        public void TestAim(bool held) => _testAimHeld = held;

        /// <summary>Für Tests: Feuern gedrückt halten oder loslassen.</summary>
        public void TestFire(bool held)
        {
            _testFireHeld = held;
            _fireRequested |= held;
        }

        /// <summary>Für Tests: wie ein Klick – im nächsten Tick den anvisierten Block abbauen oder bauen.</summary>
        public void TestEdit(bool remove)
        {
            if (remove) _removeRequested = true;
            else _placeRequested = true;
        }

        /// <summary>Ergebnis der letzten Aktion und wann sie war (für das HUD).</summary>
        public BlockEditResult LastEditResult { get; private set; } = BlockEditResult.Ok;
        public float LastEditTime { get; private set; }

        public void Initialize(GameClient game, Vec3 spawn, Material highlightMaterial)
        {
            _game = game;
            _camera = GetComponent<Camera>();
            _state = new PlayerState { Position = spawn };
            _previous = _state;
            _visualEyeHeight = game.Movement.EyeHeight(false);
            _highlight = BlockHighlight.Create(highlightMaterial);
            if (game.EffectMaterial != null) _weaponView = WeaponView.Create(transform, game.EffectMaterial);
            foreach (var type in game.Registry.All)
                if (type.Buildable) _buildable.Add(type);
            transform.SetParent(null);
            UpdateVisuals(1);
        }

        /// <summary>Setzt Position und Blickrichtung direkt (für Tests).</summary>
        public void Teleport(Vec3 feet, float yaw, float pitch)
        {
            _state = new PlayerState { Position = feet };
            _previous = _state;
            _yaw = yaw;
            _pitch = pitch;
            UpdateVisuals(1);
        }

        /// <summary>Einmal pro Bild: Blickrichtung, Klicks merken, Blockauswahl, Mausfang.</summary>
        public void ReadFrameInput()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (mouse == null || keyboard == null) return;

            ScoreboardHeld = keyboard.tabKey.isPressed;

            // Menü „Klasse und Team“ (online): M öffnet es ganz, K nur mit den Klassen; dieselbe Taste oder Esc schließt.
            // Solange es offen ist, gehört die Maus dem Menü.
            if (_game.Mode == GameMode.Online)
            {
                if (_game.TeamMenuOpen)
                {
                    _fireHeld = false;
                    _aimHeld = false;
                    if (keyboard.mKey.wasPressedThisFrame || keyboard.kKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame)
                    {
                        _game.CloseTeamMenu();
                        if (!_game.TeamMenuOpen && keyboard.escapeKey.wasPressedThisFrame) return; // Esc schließt nur das Menü
                    }
                    if (_game.TeamMenuOpen) return;
                    _waitForRelease = true;
                }
                else if (keyboard.mKey.wasPressedThisFrame)
                {
                    _game.OpenTeamMenu();
                    return;
                }
                else if (keyboard.kKey.wasPressedThisFrame)
                {
                    _game.OpenClassMenu();
                    return;
                }
            }

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (Cursor.lockState == CursorLockMode.Locked) SetCursorLocked(false);
                else
                {
                    _game.LeaveToMenu();
                    return;
                }
            }
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                _fireHeld = false; // Maus freigegeben: nicht weiterschießen
                _aimHeld = false;
                if (mouse.leftButton.wasPressedThisFrame && Application.isFocused)
                {
                    SetCursorLocked(true);
                    _waitForRelease = true; // Der Klick zum Mausfangen soll keinen Block abbauen.
                }
                return;
            }

            var delta = mouse.delta.ReadValue();
            _yaw = Mathf.Repeat(_yaw + delta.x * MouseSensitivity, 360f);
            _pitch = Mathf.Clamp(_pitch - delta.y * MouseSensitivity, -89f, 89f);

            // Gedrückt halten wiederholt die Aktion nach jeder Pause; ein kurzer Klick zwischen zwei Ticks geht nicht verloren.
            if (_waitForRelease) _waitForRelease = mouse.leftButton.isPressed || mouse.rightButton.isPressed;
            else if (Slot == 0)
            {
                _removeRequested |= mouse.leftButton.isPressed;
                _placeRequested |= mouse.rightButton.isPressed;
            }
            else
            {
                _fireHeld = mouse.leftButton.isPressed;
                _fireRequested |= mouse.leftButton.wasPressedThisFrame;
            }
            _aimHeld = Slot != 0 && mouse.rightButton.isPressed;
            _reloadRequested |= keyboard.rKey.wasPressedThisFrame;

            for (var i = 0; i <= CombatSettings.MaxWeapons; i++)
                if (keyboard[Key.Digit1 + i].wasPressedThisFrame) SelectSlot(i);
            var scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0) SelectSlot((Slot + (scroll < 0 ? 1 : -1) + SlotCount + 1) % (SlotCount + 1));
            if (keyboard.bKey.wasPressedThisFrame && _buildable.Count > 0) SelectedIndex = (SelectedIndex + 1) % _buildable.Count;
        }

        /// <summary>Waffenplätze der eigenen Klasse außer dem Spaten (offline und als Zuschauer keine).</summary>
        private int SlotCount => _game.Session?.Class.SlotCount ?? 0;

        private void SelectSlot(int slot)
        {
            if (slot < 0 || slot > SlotCount) return;
            Slot = slot;
            _fireHeld = false;
            _fireRequested = false;
        }

        public void Tick()
        {
            _previous = _state;
            if (Slot > SlotCount) Slot = 0; // neue Klasse mit weniger Waffen (Respawn)
            var input = new PlayerInput { Buttons = ReadButtons(), Yaw = _yaw, Pitch = _pitch, Weapon = (byte)Slot };
            if (Slot != 0 && (_fireHeld || _testFireHeld || _fireRequested)) input.Buttons |= InputButtons.Fire;
            if (Slot != 0 && _reloadRequested) input.Buttons |= InputButtons.Reload;
            _fireRequested = false;
            _reloadRequested = false;
            var session = _game.Session;
            if (session != null)
            {
                // Online: vorhersagen und die Eingabe an den Server schicken (siehe ClientPrediction).
                var fired = session.Tick(input);
                _state = session.Prediction.State;
                if (fired != 0) OnFired(fired, input);
            }
            else
            {
                _state = PlayerMovement.Step(_state, input, _game.World, _game.Movement);
            }

            // Stufe hochgestiegen: Kamera gleitet nach, statt einen Block hochzuspringen. Die Höhe darf dann
            // nicht zusätzlich zwischen den Ticks interpoliert werden, sonst sackt die Kamera kurz einen Block ab.
            var rise = _state.Position.Y - _previous.Position.Y;
            if (_state.OnGround && rise > 0.3f)
            {
                _stepOffset -= rise;
                _previous.Position.Y = _state.Position.Y;
            }

            if (_cooldownTicks > 0) _cooldownTicks--;
            else if (_removeRequested || _placeRequested) EditBlock(_removeRequested);
            _removeRequested = false;
            _placeRequested = false;
        }

        /// <summary>
        /// Online: Der Abgleich mit dem Server hat die Position um <paramref name="correction"/> verschoben.
        /// Die Simulation übernimmt das sofort, die Kamera gleitet in wenigen Bildern hinterher statt zu springen.
        /// </summary>
        public void ApplyNetworkCorrection(Vec3 correction)
        {
            if (_game.Session == null) return;
            _state = _game.Session.Prediction.State;
            if (correction.Equals(Vec3.Zero)) return;
            _previous.Position = _previous.Position + correction;
            _correctionOffset -= ToUnity(correction);
            if (_correctionOffset.magnitude > MaxSmoothedCorrection) _correctionOffset = Vector3.zero;
        }

        /// <summary>Der Server hat eine Blockänderung abgelehnt (die Vorhersage ist schon zurückgenommen).</summary>
        public void ShowEditResult(BlockEditResult result)
        {
            LastEditResult = result;
            LastEditTime = Time.time;
        }

        /// <summary>Kamera zwischen zwei Ticks setzen (alpha = 0 … 1).</summary>
        public void UpdateVisuals(float alpha)
        {
            var movement = _game.Movement;
            var dt = Time.deltaTime;
            _visualEyeHeight = Mathf.Lerp(_visualEyeHeight, movement.EyeHeight(_state.Crouching), 1f - Mathf.Exp(-CrouchSmoothing * dt));
            _stepOffset = Mathf.Lerp(_stepOffset, 0f, 1f - Mathf.Exp(-StepSmoothing * dt));
            _correctionOffset = Vector3.Lerp(_correctionOffset, Vector3.zero, 1f - Mathf.Exp(-CorrectionSmoothing * dt));

            var feet = Vector3.Lerp(ToUnity(_previous.Position), ToUnity(_state.Position), alpha) + _correctionOffset;
            transform.SetPositionAndRotation(feet + Vector3.up * (_visualEyeHeight + _stepOffset), Quaternion.Euler(_pitch, _yaw, 0));

            Target = Slot == 0 && Raycast(out var hit) ? hit : (VoxelHit?)null;
            _highlight.Show(Target);
            var session = _game.Session;
            var w = Weapons;
            var weapon = Weapon;
            _weaponView?.Show(weapon, Slot != 0 && (w.Reload > 0 || w.Switch > 0), (session == null || session.Alive) && !Zoomed);

            // Zielfernrohr: Sichtfeld so verkleinern, dass alles zoom-mal so groß erscheint.
            if (_camera != null)
            {
                var fov = Zoomed ? 2 * Mathf.Atan(Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) / weapon.Zoom) * Mathf.Rad2Deg : FieldOfView;
                _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, fov, 1f - Mathf.Exp(-ZoomSmoothing * dt));
            }
        }

        /// <summary>
        /// Die eigene Waffe hat laut Vorhersage gefeuert: Mündungsfeuer und Rückstoß sofort. Beim Gewehr zeigt eine Leuchtspur
        /// bis zum ersten Block, wohin der Schuss geht; ob ein Spieler getroffen ist, meldet der Server (Trefferanzeige).
        /// </summary>
        private void OnFired(int slot, PlayerInput input)
        {
            ShotsFired++;
            var weapon = _game.Session.Class.Weapon(slot);
            _weaponView?.Kick(weapon != null && (weapon.Kind == WeaponKind.Rocket || weapon.Zoom > 1) ? 1 : 0.5f);
            var effects = _game.Effects;
            if (effects == null || weapon == null) return;
            var dir = PlayerMovement.LookDirection(input.Yaw, input.Pitch);
            var muzzle = _weaponView != null ? _weaponView.Muzzle : ToUnity(_state.Eye(_game.Movement));
            effects.MuzzleFlash(muzzle, ToUnity(dir));
            if (weapon.Kind != WeaponKind.Hitscan) return;
            var eye = _state.Eye(_game.Movement);
            var end = eye + dir * weapon.Range;
            var blockHit = VoxelRaycast.Cast(_game.World, eye, dir, weapon.Range, out var hit);
            var zones = _game.Session.Zones;
            if (zones != null && zones.Raycast(eye, dir, blockHit ? hit.Distance : weapon.Range, out var wall, out _, out _, out _))
            {
                end = eye + dir * wall; // der Schuss endet an der Grenze einer Spawnzone, wie auf dem Server
                blockHit = false;
            }
            if (blockHit)
            {
                end = eye + dir * hit.Distance;
                effects.BlockImpact(ToUnity(end), new Vector3(hit.NormalX, hit.NormalY, hit.NormalZ), _game.World.GetBlock(hit.X, hit.Y, hit.Z));
            }
            effects.Tracer(muzzle, ToUnity(end));
        }

        private void EditBlock(bool remove)
        {
            if (!Raycast(out var hit)) return;
            LastEditResult = remove
                ? _game.TryRemoveBlock(hit.X, hit.Y, hit.Z, hit.Distance)
                : SelectedBlock == null ? BlockEditResult.NotBuildable : _game.TryPlaceBlock(hit.PlaceX, hit.PlaceY, hit.PlaceZ, SelectedBlock.Id, hit.Distance, _state);
            LastEditTime = Time.time;
            // Pause bis zur nächsten Aktion: je Klasse (Miner schneller), offline wie bisher.
            if (LastEditResult == BlockEditResult.Ok) _cooldownTicks = _game.Session?.Class.EditCooldownTicks ?? BlockEditing.CooldownTicks;
        }

        /// <summary>Strahl vom Auge in Blickrichtung, so weit man abbauen/bauen kann.</summary>
        private bool Raycast(out VoxelHit hit)
        {
            var eye = _state.Position + new Vec3(0, _game.Movement.EyeHeight(_state.Crouching), 0);
            return VoxelRaycast.Cast(_game.World, eye, PlayerMovement.LookDirection(_yaw, _pitch), BlockEditing.Reach, out hit);
        }

        private InputButtons ReadButtons()
        {
            if (TestButtons.HasValue) return TestButtons.Value;
            var keyboard = Keyboard.current;
            if (keyboard == null || Cursor.lockState != CursorLockMode.Locked) return InputButtons.None;
            var b = InputButtons.None;
            if (keyboard.wKey.isPressed) b |= InputButtons.Forward;
            if (keyboard.sKey.isPressed) b |= InputButtons.Back;
            if (keyboard.aKey.isPressed) b |= InputButtons.Left;
            if (keyboard.dKey.isPressed) b |= InputButtons.Right;
            if (keyboard.spaceKey.isPressed) b |= InputButtons.Jump;
            if (keyboard.leftCtrlKey.isPressed) b |= InputButtons.Crouch;
            if (keyboard.leftShiftKey.isPressed) b |= InputButtons.Sprint;
            return b;
        }

        private static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void OnDestroy()
        {
            if (_highlight != null) Destroy(_highlight.gameObject);
            if (_weaponView != null) Destroy(_weaponView.gameObject);
        }

        private static Vector3 ToUnity(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
