using System.Collections.Generic;
using AoS2.Shared;
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
    ///
    /// Steuerung: WASD laufen, Maus schauen, Leertaste springen, Strg ducken, Shift sprinten,
    /// Linksklick abbauen, Rechtsklick bauen, 1–9 / Mausrad Blocktyp, Esc gibt die Maus frei (zweimal: beenden).
    /// </summary>
    public sealed class LocalPlayer : MonoBehaviour
    {
        private const float MouseSensitivity = 0.1f; // Grad pro Pixel
        private const float CrouchSmoothing = 10f;
        private const float StepSmoothing = 14f;

        private LocalGame _game;
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
        private float _visualEyeHeight;
        private float _stepOffset;
        private readonly List<BlockType> _buildable = new List<BlockType>();

        public PlayerState State => _state;
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        public BlockType SelectedBlock => _buildable.Count > 0 ? _buildable[SelectedIndex] : null;
        public int SelectedIndex { get; private set; }

        /// <summary>Block unter dem Fadenkreuz (in Reichweite), sonst <c>null</c>.</summary>
        public VoxelHit? Target { get; private set; }

        /// <summary>Für Tests: ersetzt die Tasten (Maus und Tastatur werden dann nicht gelesen).</summary>
        public InputButtons? TestButtons { get; set; }

        /// <summary>Ergebnis der letzten Aktion und wann sie war (für das HUD).</summary>
        public BlockEditResult LastEditResult { get; private set; } = BlockEditResult.Ok;
        public float LastEditTime { get; private set; }

        public void Initialize(LocalGame game, Vec3 spawn, Material highlightMaterial)
        {
            _game = game;
            _camera = GetComponent<Camera>();
            _state = new PlayerState { Position = spawn };
            _previous = _state;
            _visualEyeHeight = game.Movement.EyeHeight(false);
            _highlight = BlockHighlight.Create(highlightMaterial);
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

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (Cursor.lockState == CursorLockMode.Locked) SetCursorLocked(false);
                else Application.Quit();
            }
            if (Cursor.lockState != CursorLockMode.Locked)
            {
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
            else
            {
                _removeRequested |= mouse.leftButton.isPressed;
                _placeRequested |= mouse.rightButton.isPressed;
            }

            if (_buildable.Count > 0)
            {
                for (var i = 0; i < Mathf.Min(9, _buildable.Count); i++)
                    if (keyboard[Key.Digit1 + i].wasPressedThisFrame) SelectedIndex = i;
                var scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0) SelectedIndex = (SelectedIndex + (scroll < 0 ? 1 : -1) + _buildable.Count) % _buildable.Count;
            }
        }

        public void Tick()
        {
            _previous = _state;
            var input = new PlayerInput { Buttons = ReadButtons(), Yaw = _yaw, Pitch = _pitch };
            _state = PlayerMovement.Step(_state, input, _game.World, _game.Movement);

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

        /// <summary>Kamera zwischen zwei Ticks setzen (alpha = 0 … 1).</summary>
        public void UpdateVisuals(float alpha)
        {
            var movement = _game.Movement;
            var dt = Time.deltaTime;
            _visualEyeHeight = Mathf.Lerp(_visualEyeHeight, movement.EyeHeight(_state.Crouching), 1f - Mathf.Exp(-CrouchSmoothing * dt));
            _stepOffset = Mathf.Lerp(_stepOffset, 0f, 1f - Mathf.Exp(-StepSmoothing * dt));

            var feet = Vector3.Lerp(ToUnity(_previous.Position), ToUnity(_state.Position), alpha);
            transform.SetPositionAndRotation(feet + Vector3.up * (_visualEyeHeight + _stepOffset), Quaternion.Euler(_pitch, _yaw, 0));

            Target = Raycast(out var hit) ? hit : (VoxelHit?)null;
            _highlight.Show(Target);
        }

        private void EditBlock(bool remove)
        {
            if (!Raycast(out var hit)) return;
            LastEditResult = remove
                ? _game.TryRemoveBlock(hit.X, hit.Y, hit.Z, hit.Distance)
                : SelectedBlock == null ? BlockEditResult.NotBuildable : _game.TryPlaceBlock(hit.PlaceX, hit.PlaceY, hit.PlaceZ, SelectedBlock.Id, hit.Distance, _state);
            LastEditTime = Time.time;
            if (LastEditResult == BlockEditResult.Ok) _cooldownTicks = BlockEditing.CooldownTicks;
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
        }

        private static Vector3 ToUnity(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
