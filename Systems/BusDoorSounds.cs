using System;
using System.IO;
using System.Reflection;
using Game.Audio;
using UnityEngine;
using UnityEngine.Audio;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The doors' sound: the pneumatic hiss and the clunk of a real bus door (recording by Jedo, CC0) when the shift opens and
    /// closes the doors, at the front door of the driven bus, through the game's sound effects volume. The WAVs are loaded from
    /// the mod folder's Audio subfolder once.
    /// </summary>
    internal sealed class BusDoorSounds
    {
        internal static string AudioDirectory;

        private GameObject m_Root;
        private AudioSource m_Source;
        private AudioClip m_Open;
        private AudioClip m_Close;
        private bool m_Failed;
        private bool m_WasOpen;

        /// <summary>Call every frame during a shift with the doors' state and where the front door is.</summary>
        internal void Update(bool open, Vector3 doorPosition)
        {
            if (open == m_WasOpen)
            {
                if (m_Source != null && m_Source.isPlaying)
                    m_Root.transform.position = doorPosition;
                return;
            }

            m_WasOpen = open;
            if (!Ensure())
                return;

            m_Root.transform.position = doorPosition;
            AudioClip clip = open ? m_Open : m_Close;
            if (clip != null)
                m_Source.PlayOneShot(clip, 0.9f);
        }

        internal void Reset() => m_WasOpen = false;

        private bool Ensure()
        {
            if (m_Source != null)
                return true;
            if (m_Failed || string.IsNullOrEmpty(AudioDirectory))
                return false;

            try
            {
                m_Open = ReadWav(Path.Combine(AudioDirectory, "bus_door_open.wav"));
                m_Close = ReadWav(Path.Combine(AudioDirectory, "bus_door_close.wav"));
                m_Root = new GameObject("CDLC Bus Driver doors") { hideFlags = HideFlags.DontSave };
                UnityEngine.Object.DontDestroyOnLoad(m_Root);
                m_Source = m_Root.AddComponent<AudioSource>();
                m_Source.playOnAwake = false;
                m_Source.spatialBlend = 0.7f;
                m_Source.minDistance = 4f;
                m_Source.maxDistance = 60f;
                m_Source.rolloffMode = AudioRolloffMode.Linear;
                m_Source.dopplerLevel = 0f;
                m_Source.outputAudioMixerGroup = WorldGroup();
                return true;
            }
            catch (Exception ex)
            {
                m_Failed = true;
                Mod.log.Warn($"Door sounds unavailable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal void Destroy()
        {
            if (m_Root != null)
                UnityEngine.Object.Destroy(m_Root);
            if (m_Open != null)
                UnityEngine.Object.Destroy(m_Open);
            if (m_Close != null)
                UnityEngine.Object.Destroy(m_Close);
            m_Root = null;
            m_Source = null;
            m_Open = m_Close = null;
        }

        /// <summary>The game's "World" mixer group (the sound effects volume), as the driving mod uses it.</summary>
        internal static AudioMixerGroup WorldGroup()
        {
            try
            {
                FieldInfo field = typeof(AudioManager).GetField("m_WorldGroup", BindingFlags.Instance | BindingFlags.NonPublic);
                return AudioManager.instance != null && field != null ? field.GetValue(AudioManager.instance) as AudioMixerGroup : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>16-bit PCM WAV to an AudioClip.</summary>
        internal static AudioClip ReadWav(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int channels = 1, rate = 44100, offset = 12, dataStart = -1, dataLength = 0;
            while (offset + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                int length = BitConverter.ToInt32(bytes, offset + 4);
                // a damaged file (this also reads cached announcements): a negative or oversized chunk would loop or overflow
                if (length < 0 || length > bytes.Length - offset - 8)
                {
                    if (id != "data")
                        break;
                    length = bytes.Length - offset - 8;
                }

                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(bytes, offset + 10);
                    rate = BitConverter.ToInt32(bytes, offset + 12);
                }
                else if (id == "data")
                {
                    dataStart = offset + 8;
                    dataLength = Math.Min(length, bytes.Length - dataStart);
                    break;
                }

                offset += 8 + length + (length & 1);
            }

            if (dataStart < 0)
                throw new InvalidDataException("No audio data in " + Path.GetFileName(file));

            if (channels < 1 || channels > 8 || rate < 1000 || rate > 192000)
                throw new InvalidDataException("Unsupported audio format in " + Path.GetFileName(file));
            int samples = dataLength / 2 / channels * channels;
            if (samples < channels)
                throw new InvalidDataException("No audio data in " + Path.GetFileName(file));
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
                data[i] = BitConverter.ToInt16(bytes, dataStart + i * 2) / 32768f;

            AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(file), samples / channels, channels, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
