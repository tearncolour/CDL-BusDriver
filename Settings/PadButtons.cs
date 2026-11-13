using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The current gamepad's buttons for <see cref="PadChord"/>: looked up by control name once per pad and kept. Reads the device itself,
    /// not the game's actions, so it works whatever control scheme the game is in. Nothing here throws: no pad, an unknown control or a
    /// pad pulled mid-drive read as "not down".
    /// </summary>
    internal sealed class GamepadButtons : IPadButtons
    {
        private Gamepad m_Pad;
        private readonly Dictionary<string, ButtonControl> m_Controls = new Dictionary<string, ButtonControl>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Looks at the pad in hand (call once a frame before asking); false when there is none.</summary>
        internal bool Refresh()
        {
            Gamepad pad = null;
            try { pad = Gamepad.current; }
            catch (Exception) { }
            if (!ReferenceEquals(pad, m_Pad))
            {
                m_Pad = pad;
                m_Controls.Clear();
            }

            return m_Pad != null && m_Pad.added;
        }

        public bool Down(string control)
        {
            ButtonControl button = Find(control);
            return button != null && button.isPressed;
        }

        public bool Pressed(string control)
        {
            ButtonControl button = Find(control);
            return button != null && button.wasPressedThisFrame;
        }

        private ButtonControl Find(string control)
        {
            if (m_Pad == null || string.IsNullOrEmpty(control))
                return null;
            if (m_Controls.TryGetValue(control, out ButtonControl known))
                return known;
            ButtonControl found = null;
            try { found = m_Pad.TryGetChildControl<ButtonControl>(control); }
            catch (Exception) { }
            m_Controls[control] = found;
            return found;
        }
    }
}
