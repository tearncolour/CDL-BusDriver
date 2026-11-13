using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Colossal.Entities;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The driven bus's signs when it is a bus asset by someone else with a sign overlay (SignOverlays): our signs on the game's own
    /// body in the chase view, under a root that follows the body's pose, with the same text as our buses' signs. Kept apart from
    /// the cab model (Run), which another bus without our interior lets go of every frame; the option "Bus model outside too"
    /// (our outside extras) switches them as it does our buses' signs.
    /// </summary>
    public sealed partial class BusCabSystem
    {
        private Entity m_OverlayBus;
        private SignOverlay m_Overlay;
        private GameObject m_OverlayRoot;
        private ISign m_OverlayFront, m_OverlaySide, m_OverlayRear;
        private readonly List<GameObject> m_OverlayBoards = new List<GameObject>(3);
        private bool m_OverlayHd, m_OverlayPosed;

        private void UpdateOverlay()
        {
            BusSettings settings = Mod.Settings;
            bool want = settings != null && settings.ShowExterior && m_Core.HasApi4 && m_Core.Driving;
            Entity vehicle = want ? m_Core.Vehicle : Entity.Null;
            if (vehicle != m_OverlayBus)
            {
                ReleaseOverlay();
                m_OverlayBus = vehicle;
                m_Overlay = vehicle != Entity.Null && IsBus(vehicle) && !IsOurAsset(vehicle) ? OverlayOf(vehicle) : null;
                if (m_Overlay != null)
                    Mod.log.Info($"Bus signs for {vehicle}: its sign overlay ({m_Overlay.Source}), on its own body.");
            }
            if (m_Overlay == null || !LedFontReady())
                return;

            if (m_OverlayRoot == null)
                m_OverlayRoot = new GameObject("CDLC sign overlay") { hideFlags = HideFlags.HideAndDontSave };
            if (m_Core.BodyPosed)
            {
                m_OverlayRoot.transform.SetPositionAndRotation(m_Core.BodyPosition, m_Core.BodyRotation);
                m_OverlayPosed = true;
            }

            bool hdNow = HdFonts.Wanted && HdFonts.Get() != null;
            if (OverlayMade() && m_OverlayHd != hdNow)
                ReleaseOverlaySigns();      // the option changed: made again in the other style
            if (!OverlayMade() && !MakeOverlaySigns())
                return;

            // the signs' dimming follows the daylight (Run sets it only while our cab model is drawn)
            CabMaterials.SetDaylight(Daylight());
            bool show = m_OverlayPosed && !m_Core.CabView;
            foreach (GameObject board in m_OverlayBoards)
                if (board.activeSelf != show) board.SetActive(show);
            ShowSigns(m_OverlayBus, m_OverlayFront, m_OverlaySide, m_OverlayRear, m_OverlayHd, show);
        }

        private bool OverlayMade() => (m_OverlayFront ?? m_OverlaySide ?? m_OverlayRear) != null &&
            (m_OverlayFront == null || m_OverlayFront.Alive) && (m_OverlaySide == null || m_OverlaySide.Alive) && (m_OverlayRear == null || m_OverlayRear.Alive);

        /// <summary>The overlay's signs and boards; false while a font they need still loads (made a frame or two later).</summary>
        private bool MakeOverlaySigns()
        {
            ReleaseOverlaySigns();
            Material lit = CabMaterials.DimmedLamp(s_LedAmber, kSignNits, kSignNight);
            if (lit == null)
                return false;
            if (HdFonts.Wanted && HdFonts.Get() == null && !HdFonts.Failed)
                return false;
            if (m_Overlay.HasCards && CardFonts.Get() == null && !CardFonts.Failed)
                return false;
            HdFontSet hd = HdFonts.Wanted ? HdFonts.Get() : null;
            Material[] inks = hd != null ? HdFonts.Inks(PrefixColour()) : null;
            m_OverlayHd = hd != null && inks != null;

            SignOverlay o = m_Overlay;
            Transform root = m_OverlayRoot.transform;
            if (o.FrontSign.Matrix.x > 0)
                m_OverlayFront = OverlaySign(root, o.FrontSign, o.FrontRotation, o.FrontBacking, hd, inks, lit);
            if (o.SideSign.Matrix.x > 0)
                m_OverlaySide = OverlaySign(root, o.SideSign, o.SideRotation, o.SideBacking, hd, inks, lit);
            if (o.RearSign.Matrix.x > 0)
                m_OverlayRear = OverlaySign(root, o.RearSign, o.RearRotation, o.RearBacking, hd, inks, lit);
            m_LastSignKey = int.MinValue;
            return true;
        }

        private ISign OverlaySign(Transform root, in VehicleProfile.Sign sign, quaternion rotation, in SignOverlay.Backing backing,
            HdFontSet hd, Material[] inks, Material lit)
        {
            if (backing.Present)
            {
                var board = new GameObject("sign board") { hideFlags = HideFlags.DontSave };
                m_OverlayBoards.Add(board);
                board.transform.SetParent(root, false);
                float3 c = SignOverlay.BackingCentre(sign.Centre, rotation);
                board.transform.localPosition = new Vector3(c.x, c.y, c.z);
                board.transform.localRotation = new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);
                board.transform.localScale = new Vector3(backing.Size.x, backing.Size.y, 1f);
                board.AddComponent<MeshFilter>().sharedMesh = CardFonts.Quad();
                CardFonts.Renderer(board).sharedMaterial = SignOverlays.BoardMaterial(backing);
            }
            return NewSign(root, sign.Centre, sign.Size, rotation, sign.Matrix, sign, m_OverlayHd, hd, inks, lit);
        }

        private SignOverlay OverlayOf(Entity vehicle)
        {
            PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            return EntityManager.TryGetComponent(vehicle, out PrefabRef prefabRef) && prefabs != null &&
                prefabs.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) ? SignOverlays.ForPrefab(prefab) : null;
        }

        private void ReleaseOverlaySigns()
        {
            var failures = new List<Exception>();
            foreach (ISign sign in new[] { m_OverlayFront, m_OverlaySide, m_OverlayRear })
                if (sign != null) CabCleanup.Try(sign.Destroy, failures);
            m_OverlayFront = m_OverlaySide = m_OverlayRear = null;
            foreach (GameObject board in m_OverlayBoards)
                CabCleanup.Try(() => UnityEngine.Object.Destroy(board), failures);
            m_OverlayBoards.Clear();
            CabCleanup.ThrowIfAny(failures, "Sign overlay cleanup failed.");
        }

        private void ReleaseOverlay()
        {
            ReleaseOverlaySigns();
            if (m_OverlayRoot != null) UnityEngine.Object.Destroy(m_OverlayRoot);
            m_OverlayRoot = null;
            m_OverlayPosed = false;
            m_Overlay = null;
            m_OverlayBus = Entity.Null;
        }
    }
}
