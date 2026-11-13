using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Plays the stop announcements: the chime, then the spoken sentence. The speech is a file TtsCache made in the background; this
// class only reads finished WAV files and plays clips, so the game
// never waits for a voice: a sentence not ready in time is skipped after its chime. One AudioSource, not spatial (the saloon's
// speakers are all round you), the game's sound-effects mixer group.
namespace CDLC.BusDriver
{
    internal sealed class BusAnnouncer
    {
        const float kChimeGapSeconds = 0.2f;    // the voice starts this long after the chime's main part
        const float kVoiceWaitSeconds = 8f;     // a voice not ready by then is skipped (the chime was heard)
        const float kStaleSeconds = 25f;        // a queued announcement older than this is dropped
        const int kMaxClips = 40;               // decoded clips kept (a line's sentences); the oldest are released

        sealed class Item
        {
            internal AnnounceKind Kind;
            internal string Text;
            internal float Queued;
        }

        enum Phase : byte { Idle, Chime, WaitVoice, Voice }

        readonly TtsCache m_Tts;
        readonly Queue<Item> m_Queue = new Queue<Item>();
        readonly Dictionary<string, AudioClip> m_Clips = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        readonly List<string> m_ClipOrder = new List<string>();
        GameObject m_Root;
        AudioSource m_Source;
        AudioClip m_Chime;
        bool m_Failed;
        Item m_Current;
        Phase m_Phase;
        float m_PhaseEnd;
        float m_VoiceSince;
        bool m_VoiceFailLogged;

        internal BusAnnouncer(TtsCache tts)
        {
            m_Tts = tts;
        }

        internal float Volume = 0.8f;
        internal bool Chime = true;
        internal bool Voice = true;
        internal int Pending => m_Queue.Count + (m_Current != null ? 1 : 0);

        /// <summary>Queues a sentence (nothing when the same one is queued or playing) and asks for its voice now.</summary>
        internal void Enqueue(AnnounceKind kind, string text, float now)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (m_Current != null && m_Current.Text == text)
                return;
            foreach (Item item in m_Queue)
            {
                if (item.Text == text)
                    return;
            }

            m_Queue.Enqueue(new Item { Kind = kind, Text = text, Queued = now });
            if (Voice)
                m_Tts.Request(text, true);
        }

        internal void Clear()
        {
            m_Queue.Clear();
            m_Current = null;
            m_Phase = Phase.Idle;
            if (m_Source != null)
                m_Source.Stop();
        }

        internal void Update(float now)
        {
            if (m_Current == null)
            {
                while (m_Queue.Count > 0 && now - m_Queue.Peek().Queued > kStaleSeconds)
                    m_Queue.Dequeue();
                if (m_Queue.Count == 0)
                    return;
                if (!Ensure())
                {
                    m_Queue.Clear();
                    return;
                }

                m_Current = m_Queue.Dequeue();
                if (Chime && m_Chime != null)
                {
                    m_Source.PlayOneShot(m_Chime, Volume);
                    m_Phase = Phase.Chime;
                    m_PhaseEnd = now + Mathf.Max(0.3f, m_Chime.length * 0.7f) + kChimeGapSeconds;
                }
                else
                {
                    m_Phase = Phase.WaitVoice;
                    m_VoiceSince = now;
                }

                return;
            }

            switch (m_Phase)
            {
                case Phase.Chime:
                    if (now >= m_PhaseEnd)
                    {
                        m_Phase = Phase.WaitVoice;
                        m_VoiceSince = now;
                    }
                    break;
                case Phase.WaitVoice:
                    WaitVoice(now);
                    break;
                case Phase.Voice:
                    if (now >= m_PhaseEnd)
                        Finish();
                    break;
            }
        }

        void Finish()
        {
            m_Current = null;
            m_Phase = Phase.Idle;
        }

        void WaitVoice(float now)
        {
            if (!Voice)
            {
                Finish();
                return;
            }

            if (m_Clips.TryGetValue(m_Current.Text, out AudioClip clip) && clip != null)
            {
                Play(clip, now);
                return;
            }

            TtsCache.Status status = m_Tts.StateOf(m_Current.Text, out string path);
            if (status == TtsCache.Status.Unknown)
                m_Tts.Request(m_Current.Text, true);
            else if (status == TtsCache.Status.Failed)
            {
                LogVoiceFail("the voice could not be made");
                Finish();
                return;
            }
            else if (status == TtsCache.Status.Ready && path != null)
            {
                AudioClip wav = null;
                string error = null;
                try { wav = BusDoorSounds.ReadWav(path); }
                catch (Exception ex) { error = ex.Message; }
                if (wav != null)
                {
                    Remember(m_Current.Text, wav);
                    Play(wav, now);
                    return;
                }

                m_Tts.Reject(m_Current.Text);
                LogVoiceFail("the sound file could not be read: " + error);
                Finish();
                return;
            }

            if (now - m_VoiceSince > kVoiceWaitSeconds)
                Finish();
        }

        void Play(AudioClip clip, float now)
        {
            m_Source.PlayOneShot(clip, Volume);
            m_Phase = Phase.Voice;
            m_PhaseEnd = now + clip.length + 0.4f;
        }

        void Remember(string text, AudioClip clip)
        {
            if (m_Clips.ContainsKey(text))
                return;
            m_Clips[text] = clip;
            m_ClipOrder.Add(text);
            while (m_ClipOrder.Count > kMaxClips)
            {
                string oldest = m_ClipOrder[0];
                m_ClipOrder.RemoveAt(0);
                if (m_Clips.TryGetValue(oldest, out AudioClip old) && old != null)
                    UnityEngine.Object.Destroy(old);
                m_Clips.Remove(oldest);
            }
        }

        void LogVoiceFail(string why)
        {
            if (m_VoiceFailLogged)
                return;
            m_VoiceFailLogged = true;
            Mod.log.Warn($"Announcement voice skipped ({why}); the chime still plays. Later failures are not logged.");
        }

        bool Ensure()
        {
            if (m_Source != null)
                return true;
            if (m_Failed)
                return false;

            try
            {
                string dir = BusDoorSounds.AudioDirectory;
                m_Chime = !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "bus_chime.wav")) ? BusDoorSounds.ReadWav(Path.Combine(dir, "bus_chime.wav")) : null;
                m_Root = new GameObject("CDLC Bus Driver announcements") { hideFlags = HideFlags.DontSave };
                UnityEngine.Object.DontDestroyOnLoad(m_Root);
                m_Source = m_Root.AddComponent<AudioSource>();
                m_Source.playOnAwake = false;
                m_Source.spatialBlend = 0f;
                m_Source.dopplerLevel = 0f;
                m_Source.outputAudioMixerGroup = BusDoorSounds.WorldGroup();
                return true;
            }
            catch (Exception ex)
            {
                m_Failed = true;
                Mod.log.Warn($"Announcements unavailable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal void Destroy()
        {
            Clear();
            foreach (AudioClip clip in m_Clips.Values)
            {
                if (clip != null)
                    UnityEngine.Object.Destroy(clip);
            }

            m_Clips.Clear();
            m_ClipOrder.Clear();
            if (m_Chime != null)
                UnityEngine.Object.Destroy(m_Chime);
            if (m_Root != null)
                UnityEngine.Object.Destroy(m_Root);
            m_Chime = null;
            m_Root = null;
            m_Source = null;
        }
    }
}
