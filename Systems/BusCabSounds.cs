using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver.Interior.Models;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The small sounds of a city bus around the driver, through the game's sound effects volume: the stop-request bell when a
    /// passenger presses STOP ("Bus stop button", Aleksanterisuuri, CC0), the warning beeps while the doors close
    /// ("bus_door_alarm", 13FPanska_Sychra_Petr, CC0), the card reader's beep for every passenger who boards (a Chinese reader:
    /// "嘀" for an IC card or an NFC phone, "嘀嘀" for a QR code; synthesised, the old validator recording ("bus_ticket machine",
    /// same author, CC0) without them) and the indicator relay's tick and tock on the dashboard (synthesised). Each source sits
    /// where the sound comes from in the bus (the ceiling speaker, the front door's reader, the dashboard) and follows the body.
    /// </summary>
    internal sealed class BusCabSounds
    {
        private GameObject m_Root;
        private AudioSource m_Ceiling, m_Door, m_Dash;
        private AudioClip m_Bell, m_DoorWarning, m_Validator, m_CardTap, m_QrScan, m_RelayOn, m_RelayOff;
        private readonly System.Random m_Random = new System.Random();

        /// <summary>When the reader last beeped (unscaled time): the cab system flashes its OK lamp with it.</summary>
        internal float LastBeep { get; private set; } = -10f;
        private bool m_Failed;
        private ShiftState m_LastState = ShiftState.Idle;
        private int m_LastBoarded;
        private int m_PendingBeeps;
        private float m_NextBeep;
        private bool m_Placed;
        private bool m_HasReader = true;    // the bus has a card reader (BusInterior.CardReaders): its beeps play

        /// <summary>Every frame: follows the body, plays the door warning and the validator from the shift's state.</summary>
        internal void Update(Vector3 position, Quaternion rotation, in BusInteriorSpec spec, bool inShift)
        {
            if (!inShift)
            {
                m_LastState = ShiftState.Idle;
                m_PendingBeeps = 0;
                m_LastBoarded = 0;
                return;
            }

            if (!Ensure())
                return;

            m_Root.transform.SetPositionAndRotation(position, rotation);
            if (!m_Placed)
            {
                Place(spec);
                m_Placed = true;
            }

            ShiftState state = BusState.State;
            if (state == ShiftState.DoorsClosing && m_LastState != ShiftState.DoorsClosing && m_DoorWarning != null)
                m_Door.PlayOneShot(m_DoorWarning, 0.7f);
            if (state == ShiftState.DoorsOpening && m_LastState != ShiftState.DoorsOpening)
                m_LastBoarded = 0;
            m_LastState = state;

            int boarded = BusState.StopBoarded;
            if (boarded > m_LastBoarded)
                m_PendingBeeps = Math.Min(m_PendingBeeps + boarded - m_LastBoarded, 6);
            m_LastBoarded = boarded;
            float now = Time.unscaledTime;
            if (!m_HasReader)
                m_PendingBeeps = 0;         // a bus without a reader (its profile lists none: a coins-only bus of the past) does not beep
            if (m_PendingBeeps > 0 && now >= m_NextBeep)
            {
                // most pay by QR code today, the rest tap a card or a phone
                AudioClip clip = m_Random.NextDouble() < 0.6 ? m_QrScan ?? m_CardTap : m_CardTap ?? m_QrScan;
                clip = clip ?? m_Validator;
                if (clip != null)
                    m_Door.PlayOneShot(clip, 0.5f);
                LastBeep = now;
                m_PendingBeeps--;
                m_NextBeep = now + 0.6f + (float)m_Random.NextDouble() * 0.5f;
            }
        }

        /// <summary>Moves the sources to their places in the bus (model space of the spec).</summary>
        internal void Place(in BusInteriorSpec spec)
        {
            if (m_Root == null)
                return;

            var s = spec.Snapped();
            m_HasReader = BusInterior.CardReaders(s).Count > 0;
            m_Ceiling.transform.localPosition = new Vector3(0f, s.RoofY - 0.35f, s.FrontZ - 2.5f);
            m_Door.transform.localPosition = new Vector3(s.DriverSide * (s.HalfWidth - 1.10f - 0.18f), s.FloorY + 1.1f, s.FrontZ - 1.1f);   // the reader on the fare box
            m_Dash.transform.localPosition = new Vector3(s.DriverSide * (s.HalfWidth - 0.66f), s.FloorY + 1.1f, s.FrontZ - 0.5f);
        }

        internal void Replace() => m_Placed = false;

        /// <summary>The stop-request bell (a passenger pressed STOP).</summary>
        internal void StopRequest()
        {
            if (Ensure() && m_Bell != null)
                m_Ceiling.PlayOneShot(m_Bell, 0.6f);
        }

        /// <summary>The indicator relay: tick when the lamps come on, tock when they go off.</summary>
        internal void Relay(bool on)
        {
            if (!Ensure())
                return;
            AudioClip clip = on ? m_RelayOn : m_RelayOff;
            if (clip != null)
                m_Dash.PlayOneShot(clip, 0.5f);
        }

        private bool Ensure()
        {
            if (m_Root != null)
                return true;
            if (m_Failed || string.IsNullOrEmpty(BusDoorSounds.AudioDirectory))
                return false;

            try
            {
                string dir = BusDoorSounds.AudioDirectory;
                AudioClip Load(string name)
                {
                    string path = Path.Combine(dir, name + ".wav");
                    return File.Exists(path) ? BusDoorSounds.ReadWav(path) : null;
                }

                m_Bell = Load("bus_stop_request");
                m_DoorWarning = Load("bus_door_warning");
                m_Validator = Load("bus_validator");
                m_CardTap = Load("bus_card_tap");
                m_QrScan = Load("bus_qr_scan");
                m_RelayOn = Load("bus_relay_on");
                m_RelayOff = Load("bus_relay_off");
                m_Root = new GameObject("CDLC Bus Driver cab sounds") { hideFlags = HideFlags.DontSave };
                UnityEngine.Object.DontDestroyOnLoad(m_Root);
                m_Ceiling = Source("ceiling", 0.5f, 3f, 30f);
                m_Door = Source("door", 0.6f, 3f, 30f);
                m_Dash = Source("dash", 0.4f, 1f, 12f);
                return true;
            }
            catch (Exception ex)
            {
                m_Failed = true;
                Mod.log.Warn($"Bus cab sounds unavailable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private AudioSource Source(string name, float blend, float min, float max)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(m_Root.transform, false);
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = blend;
            source.minDistance = min;
            source.maxDistance = max;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.dopplerLevel = 0f;
            source.outputAudioMixerGroup = BusDoorSounds.WorldGroup();
            return source;
        }

        internal void Destroy()
        {
            if (m_Root != null)
                UnityEngine.Object.Destroy(m_Root);
            foreach (AudioClip clip in new[] { m_Bell, m_DoorWarning, m_Validator, m_CardTap, m_QrScan, m_RelayOn, m_RelayOff })
                if (clip != null)
                    UnityEngine.Object.Destroy(clip);
            m_Root = null;
            m_Bell = m_DoorWarning = m_Validator = m_CardTap = m_QrScan = m_RelayOn = m_RelayOff = null;
        }
    }
}
