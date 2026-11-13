using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Game.UI.Localization;
using Game.UI.Menu;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using LedGeometry = CDLC.Asset.Led.LedGeometry;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The bus model's live extras: the outside lamps (daytime running and head lamps in their beams, tail, brake, reversing), the
    /// indicators and the hazard lamps, set by the driver's keys (BusInput: no automatic signals) and cancelled by the wheel coming back after a turn; the LED destination signs on the
    /// front, the kerb side and the rear (route number and terminus, "Not in service" off shift); the livery in the line's colour;
    /// the instrument cluster's warning lamps and the gear on its display; the stop request (a passenger presses STOP).
    /// </summary>
    public sealed partial class BusCabSystem
    {
        // Lamps in nits (HDRP emissive), by day (bright enough for the sun) and by night (dimmed like real LEDs: the game opens
        // its exposure at night, and the day levels bloomed the signs into unreadable blobs): CabMaterials.DimmedLamp. The head
        // lamps' daytime strip is always on while driving; the brake lamps stay about four times the tail lamps at either level.
        private const float kHeadNits = 2000f, kTailNits = 400f, kBrakeNits = 1800f, kIndicatorNits = 1800f, kReverseNits = 1200f;
        private const float kHeadNight = 420f, kTailNight = 80f, kBrakeNight = 340f, kIndicatorNight = 340f, kReverseNight = 240f;
        private const float kSignNits = AiSignRules.SignNits, kSignNight = AiSignRules.SignNight, kTelltaleNits = 120f, kTelltaleNight = 25f;
        private static readonly float3 s_White = new float3(1f, 0.97f, 0.9f), s_Red = new float3(1f, 0.06f, 0.04f), s_Amber = new float3(1f, 0.5f, 0.05f);
        private static readonly float3 s_LedAmber = new float3(1f, 0.62f, 0.12f), s_Green = new float3(0.2f, 1f, 0.3f), s_Blue = new float3(0.25f, 0.45f, 1f);

        // The head lamps by beam: the part is one material (the daytime strip and the projectors together), so the beam changes its
        // brightness and tint: the strip alone (kHeadNits above), the dipped beam brighter, the main beam brightest and bluer.
        private const float kHeadLowNits = 3200f, kHeadLowNight = 760f, kHeadHighNits = 4600f, kHeadHighNight = 1300f;
        private static readonly float3 s_HighWhite = new float3(0.86f, 0.93f, 1f);

        // Indicators flash at Flasher.Hz (90 a minute, UN R48: 60-120), on first. The stalk cancels itself once the steering wheel has
        // been turned at least kCancelWheelDeg the way it indicates and comes back under kCancelBackDeg (the cancelling cam).
        private const float kCancelWheelDeg = 200f;
        private const float kCancelBackDeg = 60f;
        private float m_CancelPeak;

        // m_RearSideSign: an articulated bus's rear section's own side sign, when its profile declares one
        private ISign m_FrontSign, m_SideSign, m_RearSign, m_RearSideSign;
        private readonly List<ISign> m_ExtraSideSigns = new List<ISign>();
        private LedSign m_GearSign;
        private CabTelltales m_Telltales;
        private int m_LastSignKey = int.MinValue;
        private float m_NextSignText;
        private string m_SignPrefix = "", m_SignBlock = "", m_SignText = "";
        // the high-density signs (option "LED sign style"): the style they were made in, the stop's English line, the rear's warning
        private bool m_SignsHd;
        // A missing front sign is a valid layout, not an unbuilt sign set.
        private bool m_SignsCreated;
        private string m_SignEnglish = "";
        private RearSignState m_RearState;

        // ---------------------------------------------------------------- indicators and the stop request

        private int m_Indicate;               // -1 left, 0 none, +1 right (the stalk)
        private bool m_Hazard;                // the hazard warning lamps
        private bool m_BlinkOn;
        private bool m_LeftLit, m_RightLit;   // the indicator lamps on this instant (stalk or hazard, flash on)
        private double m_BlinkStart;
        private Entity m_SeenVehicle;         // the vehicle DrivingBus last looked at, and whether it is a bus
        private bool m_SeenIsBus;
        private Entity m_SwitchVehicle;       // the vehicle the head lamp switch and the stalk belong to
        private readonly BusLightState m_LightSwitch = new BusLightState();
        private Beam m_Beam = Beam.Off;       // what the head lamps give now (the flash included)
        private ShiftState m_LastState = ShiftState.Idle;
        private float m_DepartedAt = -100f;
        private bool m_StopRequested;
        private int m_SeenGear;

        /// <summary>Someone wants to get off at the next stop and has pressed STOP (the sign is lit, the bell rang).</summary>
        private bool StopRequested() => m_StopRequested;

        /// <summary>Is the player driving a bus (any)? Looked up once per vehicle taken.</summary>
        private bool DrivingBus()
        {
            if (!m_Core.Driving)
                return false;
            Entity vehicle = m_Core.Vehicle;
            if (vehicle != m_SeenVehicle)
            {
                m_SeenVehicle = vehicle;
                m_SeenIsBus = vehicle != Entity.Null && IsBus(vehicle);
            }
            return m_SeenIsBus;
        }

        /// <summary>
        /// The driver's switches, on any bus being driven (on a shift or not): which way it indicates (the keys, BusInput.Indicator,
        /// cancelled by the wheel after a turn), the hazard warning lamps and the head lamp switch (our own buses only: another
        /// bus keeps the game's lights). Taking another bus starts from the option's switch position, no stalk and no hazard.
        /// </summary>
        private void UpdateSignals(float now)
        {
            bool driving = DrivingBus();
            BusState.DrivingBus = driving;
            bool inShift = BusState.InShift && BusState.Bus != Entity.Null && m_Core.Driving && m_Core.Vehicle == BusState.Bus;
            ShiftState state = BusState.State;
            if (state == ShiftState.Driving && (m_LastState == ShiftState.DoorsClosing || m_LastState == ShiftState.DoorsOpen))
                m_DepartedAt = now;

            if (state == ShiftState.DoorsOpening || state == ShiftState.DoorsOpen)
                m_StopRequested = false;
            m_LastState = state;

            // A passenger presses STOP a few seconds after the bus has left the stop before (or as soon as the target is known).
            if (inShift && state == ShiftState.Driving && BusState.Alighting > 0 && now - m_DepartedAt > 4f && !m_StopRequested)
            {
                m_StopRequested = true;
                m_CabSounds.StopRequest();
            }

            if (!inShift)
                m_StopRequested = false;

            Entity vehicle = driving ? m_Core.Vehicle : Entity.Null;
            if (vehicle != m_SwitchVehicle)
            {
                m_SwitchVehicle = vehicle;
                m_LightSwitch.Reset(Mod.Settings != null ? Mod.Settings.LightsMode : LightMode.Auto);
                m_CancelPeak = 0f;
                BusInput.CancelIndicator();
                BusInput.CancelHazard();
                Core.SetManualSignals(0, true);         // the driving mod forgets its signals when a vehicle is taken or let go
            }

            if (BusInput.PollIndicators())
            {
                m_CancelPeak = 0f;
                GamepadHaptics.Cue(HapticCue.Indicator);
            }
            if (BusInput.PollHazard())
                GamepadHaptics.Cue(HapticCue.Hazard);
            int indicate = driving ? BusInput.Indicator : 0;
            if (!driving && BusInput.Indicator != 0)
                BusInput.CancelIndicator();
            if (indicate != 0)
            {
                // the cancelling cam: the wheel turned the way it indicates and back to near straight
                float wheel = m_Core.WheelAngle * Mathf.Rad2Deg * m_Core.SteeringRatio * indicate;
                m_CancelPeak = Mathf.Max(m_CancelPeak, wheel);
                if (m_CancelPeak >= kCancelWheelDeg && wheel < kCancelBackDeg)
                {
                    BusInput.CancelIndicator();
                    indicate = 0;
                    m_CancelPeak = 0f;
                    GamepadHaptics.Cue(HapticCue.IndicatorCancel);     // the stalk's cancelling cam
                }
            }

            bool hazard = driving && BusInput.Hazard;
            if (indicate != m_Indicate || hazard != m_Hazard)
            {
                m_Indicate = indicate;
                m_Hazard = hazard;
                m_BlinkStart = UnityEngine.Time.unscaledTimeAsDouble;     // a new flash starts lit
            }

            bool flashing = Flasher.Flashing(m_Indicate, m_Hazard);
            bool on = flashing && Flasher.On(UnityEngine.Time.unscaledTimeAsDouble - m_BlinkStart);
            if (on != m_BlinkOn)
            {
                m_BlinkOn = on;
                if (flashing || !on)
                    m_CabSounds.Relay(on);
            }
            Flasher.Sides(m_Indicate, m_Hazard, m_BlinkOn, out m_LeftLit, out m_RightLit);

            // API 16 also exposes these steady stalk bits to companions (e.g. trolleybus frogs).
            // Publishing intent must not depend on which renderer draws this bus's lamps.
            bool signalOwner = driving && m_Bus == vehicle;
            Core.SetManualSignals(signalOwner ? (m_Hazard ? DrivingSignals.Hazard : m_Indicate < 0 ? DrivingSignals.Left : m_Indicate > 0 ? DrivingSignals.Right : 0) : 0);

            // The head lamp switch: L steps it, the flash key gives the main beam while it is held. Only our own buses (the
            // model with the lamps and the lights); another bus keeps the game's lights and the cab only shows its own telltale.
            bool own = driving && m_OwnAsset && m_Bus == vehicle;
            if (own && BusInput.LightsPressed())
            {
                LightsNotice(m_LightSwitch.Cycle());
                GamepadHaptics.Cue(HapticCue.LightSwitch);
            }
            bool flashHeld = own && BusInput.FlashHeld();
            if (flashHeld && !m_LightSwitch.Flashing)
                GamepadHaptics.Cue(HapticCue.Flash);
            m_LightSwitch.Flash(flashHeld);
            // a bus the game drives: its dipped beam in the dark (the game's body would have its own lamps lit)
            m_Beam = own ? m_LightSwitch.Update(Daylight()) : m_Core.Riding && m_OwnAsset && Dark() ? Beam.Low : Beam.Off;
        }

        private BusInterior.ExteriorLamps OutsideLamps(float speed, KitLights lights)
        {
            var l = BusInterior.ExteriorLamps.Head;                      // the daytime strip always; the beam is the lamp material (HeadMaterial)
            if (m_LightSwitch.Tail)
                l |= BusInterior.ExteriorLamps.Tail;
            if (m_Core.Braking || m_Core.ParkingBrake)             // both false without API 4
                l |= BusInterior.ExteriorLamps.Brake;
            if (m_Core.Reversing)
                l |= BusInterior.ExteriorLamps.Reverse;
            if (m_LeftLit)
                l |= BusInterior.ExteriorLamps.Left;
            if (m_RightLit)
                l |= BusInterior.ExteriorLamps.Right;
            return l;
        }

        /// <summary>The head lamp material for a beam: the daytime strip (nothing), the dipped beam, the main beam.</summary>
        private static Material HeadMaterial(Beam beam)
        {
            switch (beam)
            {
                case Beam.Low: return CabMaterials.DimmedLamp(s_White, kHeadLowNits, kHeadLowNight);
                case Beam.High: return CabMaterials.DimmedLamp(s_HighWhite, kHeadHighNits, kHeadHighNight);
                default: return CabMaterials.DimmedLamp(s_White, kHeadNits, kHeadNight);
            }
        }

        private const string kLightsNotice = "CDLC.BusDriver.Lights";

        /// <summary>The head lamp switch moved: a notice in the game's notification list, which fades after a moment.</summary>
        private void LightsNotice(LightMode mode)
        {
            string key = "CDLC.BusDriver.Lights." + mode;
            try
            {
                NotificationUISystem notifications = World.GetExistingSystemManaged<NotificationUISystem>();
                notifications?.RemoveNotification(kLightsNotice, 2.5f,
                    LocalizedString.Id("CDLC.BusDriver.Piper.Title"),
                    LocalizedString.Id(key));
            }
            catch (Exception ex)
            {
                Mod.log.Info($"{key} ({ex.GetType().Name} while showing the notice)");
            }
        }

        private Color32 LiveryColour()
        {
            if (BusState.InShift && BusState.Bus == m_Bus) return BusState.LineColor;
            if (BusRide.Riding && BusRide.Bus == m_Bus) return BusRide.LineColor;
            VehicleProfile model = m_Sections.Count > 0 ? VehicleModels.Find(m_Sections[0].Spec.ModelVariant) : null;
            if (model == null) return new Color32(0, 110, 200, 255);
            float3 c = model.DefaultBodyColor;
            return new Color32((byte)c.x, (byte)c.y, (byte)c.z, 255);
        }

        /// <summary>The instance's own materials: the lamp parts lit in their colours (switched on and off with Show).</summary>
        private static void Dress(Section section)
        {
            CabInstance t = section.Instance;
            t.OverrideMaterial(BusInterior.HeadLamps, CabMaterials.DimmedLamp(s_White, kHeadNits, kHeadNight));
            t.OverrideMaterial(BusInterior.TailLamps, CabMaterials.DimmedLamp(s_Red, kTailNits, kTailNight));
            t.OverrideMaterial(BusInterior.BrakeLamps, CabMaterials.DimmedLamp(s_Red, kBrakeNits, kBrakeNight));
            t.OverrideMaterial(BusInterior.IndicatorL, CabMaterials.DimmedLamp(s_Amber, kIndicatorNits, kIndicatorNight));
            t.OverrideMaterial(BusInterior.IndicatorR, CabMaterials.DimmedLamp(s_Amber, kIndicatorNits, kIndicatorNight));
            t.OverrideMaterial(BusInterior.ReverseLamps, CabMaterials.DimmedLamp(s_White, kReverseNits, kReverseNight));
            section.LiveryGloss = VehicleModels.Find(section.Spec.ModelVariant)?.Mesh.LiveryGloss == true;
            section.Dressed = true;
        }

        // ---------------------------------------------------------------- the LED signs outside

        /// <summary>
        /// The destination signs (front, kerb side; an articulated bus's rear section's own kerb side sign when its profile declares one)
        /// and the rear route number, drawn only when the bus is seen from outside.
        /// </summary>
        private void UpdateOutside(bool show)
        {
            if (m_Sections.Count == 0 || !LedFontReady())
                return;

            Section first = m_Sections[0], last = m_Sections[m_Sections.Count - 1];
            if (first.Instance == null || !first.Instance.Alive || last.Instance == null || !last.Instance.Alive)
                return;

            // the option changed: the signs are made again in the other style
            if (m_SignsCreated && m_SignsHd != (HdFonts.Wanted && HdFonts.Get() != null))
                ReleaseSigns();

            if (!m_SignsCreated || m_FrontSign?.Alive == false || m_SideSign?.Alive == false ||
                m_RearSign?.Alive == false || m_RearSideSign?.Alive == false || m_ExtraSideSigns.Exists(s => !s.Alive))
            {
                ReleaseSigns();
                Material lit = CabMaterials.DimmedLamp(s_LedAmber, kSignNits, kSignNight);
                if (lit == null)
                    return;
                if (HdFonts.Wanted && HdFonts.Get() == null && !HdFonts.Failed)
                    return;     // the high-density font is still loading: the signs are made a frame or two later
                HdFontSet hd = HdFonts.Wanted ? HdFonts.Get() : null;
                Material[] inks = hd != null ? HdFonts.Inks(PrefixColour()) : null;
                m_SignsHd = hd != null && inks != null;

                BusInteriorSpec fs = first.Spec;
                VehicleProfile frontModel = VehicleModels.Find(fs.ModelVariant), rearModel = VehicleModels.Find(last.Spec.ModelVariant);
                bool anyCard = frontModel != null && (frontModel.FrontSign.IsCard || frontModel.SideSign.IsCard) ||
                    rearModel != null && (rearModel.RearSign.IsCard || last != first && rearModel.SideSign.IsCard);
                anyCard |= frontModel != null && Array.Exists(frontModel.ExtraSideSigns, s => s.IsCard) ||
                    last != first && rearModel != null && Array.Exists(rearModel.ExtraSideSigns, s => s.IsCard);
                if (anyCard && CardFonts.Get() == null && !CardFonts.Failed)
                    return;     // the card font is still loading: the signs are made a frame or two later

                if (fs.Section != BusSection.Rear)
                {
                    BusInterior.FrontSignFrame(fs, out float3 c, out float2 size);
                    m_FrontSign = NewSign(first.Instance.Root, c, size, quaternion.RotateY(math.PI), BusInterior.FrontSignMatrix(fs), frontModel?.FrontSign, m_SignsHd, hd, inks, lit);
                    if (BusInterior.SideSignFrame(fs, out float3 sc, out float2 ss, out float kerb))
                        m_SideSign = NewSign(first.Instance.Root, sc, ss, BusInterior.SideSignRotation(fs, kerb), BusInterior.SideSignMatrix(fs), frontModel?.SideSign, m_SignsHd, hd, inks, lit);
                }

                if (last.Spec.Section != BusSection.Front)
                {
                    BusInterior.RearRouteFrame(last.Spec, out float3 rc, out float2 rs);
                    m_RearSign = NewSign(last.Instance.Root, rc, rs, quaternion.identity, BusInterior.RearRouteMatrix(last.Spec), rearModel?.RearSign, m_SignsHd, hd, inks, lit);
                    if (last != first && rearModel != null && BusInterior.SideSignFrame(last.Spec, out float3 rsc, out float2 rss, out float rkerb))
                        m_RearSideSign = NewSign(last.Instance.Root, rsc, rss, BusInterior.SideSignRotation(last.Spec, rkerb), BusInterior.SideSignMatrix(last.Spec), rearModel.SideSign, m_SignsHd, hd, inks, lit);
                }

                if (frontModel != null)
                    foreach (var sign in frontModel.ExtraSideSigns)
                        m_ExtraSideSigns.Add(NewSign(first.Instance.Root, sign.Centre, sign.Size,
                            BusInterior.SideSignRotation(math.sign(sign.Centre.x), sign.Tilt), sign.Matrix, sign, m_SignsHd, hd, inks, lit));
                if (last != first && rearModel != null)
                    foreach (var sign in rearModel.ExtraSideSigns)
                        m_ExtraSideSigns.Add(NewSign(last.Instance.Root, sign.Centre, sign.Size,
                            BusInterior.SideSignRotation(math.sign(sign.Centre.x), sign.Tilt), sign.Matrix, sign, m_SignsHd, hd, inks, lit));
                m_LastSignKey = int.MinValue;
                m_SignsCreated = true;
            }

            ShowSigns(m_Bus, m_FrontSign, m_SideSign, m_RearSign, m_SignsHd, show, m_RearSideSign, m_ExtraSideSigns);
        }

        /// <summary>A sign as its model says: a printed board (style "card", the card font there), else high-density or classic dots.</summary>
        private static ISign NewSign(Transform parent, float3 centre, float2 size, quaternion rotation, int2 matrix, VehicleProfile.Sign? model,
            bool hdSigns, HdFontSet hd, Material[] inks, Material lit) =>
            model.HasValue && model.Value.Matrix.x == 0 ? null
            : model.HasValue && model.Value.IsCard && CardFonts.Get() != null ? NewCard(parent, centre, size, rotation, model.Value)
            : hdSigns ? NewHd(parent, centre, size, rotation, hd, inks)
            : new LedSign(parent, centre, size, matrix.x, matrix.y, lit, s_LedFont, rotation, 20f);

        /// <summary>The signs' text (the shift of <paramref name="bus"/>, BusState) and visibility; the rear's warnings in the high-density style.</summary>
        private void ShowSigns(Entity bus, ISign front, ISign side, ISign rear, bool hdSigns, bool show, ISign rearSide = null, List<ISign> extraSides = null)
        {
            if (front != null) front.Visible = show;
            if (side != null) side.Visible = show;
            if (rear != null) rear.Visible = show;
            if (rearSide != null) rearSide.Visible = show;
            if (extraSides != null) foreach (var sign in extraSides) sign.Visible = show;
            if (!show)
                return;

            // The text: the route number and the terminus (the line's name without one); off shift "Not in service".
            float now = UnityEngine.Time.unscaledTime;
            bool inShift = BusState.InShift && BusState.Bus == bus;
            int key = inShift ? 1 : 0;
            if (key != m_LastSignKey || now >= m_NextSignText)
            {
                m_LastSignKey = key;
                m_NextSignText = now + kLedTextRefreshSeconds;
                m_SignPrefix = inShift ? BusState.LinePrefix ?? "" : "";
                m_SignBlock = inShift ? BusState.LineNumber ?? "" : "";
                Material prefix = CabMaterials.DimmedLamp(PrefixColour(), kSignNits, kSignNight);
                CardRgb prefixInk = CardFonts.Rgb01(PrefixColour());
                foreach (ISign sign in new[] { front, side, rear, rearSide })
                {
                    if (sign is LedSign led) led.PrefixMaterial = prefix;
                    else if (sign is HdSign hd) hd.SetPrefix(PrefixColour());
                    else if (sign is CardSign card) card.PrefixInk = prefixInk;
                }
                if (extraSides != null)
                    foreach (var sign in extraSides)
                    {
                        if (sign is LedSign led) led.PrefixMaterial = prefix;
                        else if (sign is HdSign hd) hd.SetPrefix(PrefixColour());
                        else if (sign is CardSign card) card.PrefixInk = prefixInk;
                    }

                m_SignText = inShift
                    ? (!string.IsNullOrEmpty(BusState.Terminus) ? BusState.Terminus : BusState.LineName ?? "")
                    : BusLocale.Text("LED.NotInService");
                // a printed board names the line by its two ends ("A <-> B"), as the boards did
                m_CardText = inShift ? CardText.Route(BusState.Origin, BusState.Terminus, BusState.LineName, BusState.LineNumber) : m_SignText;
                // the Shanghai style's English line: the stop's in pinyin, "Not in Service" under 暂停营运
                m_SignEnglish = inShift ? HdFonts.English(m_SignText) : HdFonts.Fixed(m_SignText, BusLocale.English("LED.NotInService"));
            }

            double t = UnityEngine.Time.unscaledTimeAsDouble;
            if (hdSigns)
            {
                var route = HdContent.Route(m_SignPrefix, m_SignBlock, HdFonts.Suffix(m_SignBlock), m_SignText, m_SignEnglish);
                if (front is HdSign hdFront) hdFront.Show(route, t); else front?.Show(m_SignPrefix, m_SignBlock, m_CardText, t);
                if (side is HdSign hdSide) hdSide.Show(route, t); else side?.Show(m_SignPrefix, m_SignBlock, m_CardText, t);
                if (rearSide is HdSign hdRearSide) hdRearSide.Show(route, t); else rearSide?.Show(m_SignPrefix, m_SignBlock, m_CardText, t);
                if (extraSides != null)
                    foreach (var sign in extraSides)
                        if (sign is HdSign hd) hd.Show(route, t); else sign.Show(m_SignPrefix, m_SignBlock, m_CardText, t);
                // the rear: the label alone, centred, or "刹车注意" braking on the move, "车辆左转 / 右转" indicating (not the hazard lamps)
                RearMessage message = HdFonts.RearWarnings
                    ? RearSignRules.Next(ref m_RearState, m_Core.Braking, m_Hazard ? 0 : m_Indicate, m_Core.WorldSpeed, t)
                    : RearMessage.Route;
                if (rear is HdSign hdRear) hdRear.Show(HdFonts.Rear(message, m_SignPrefix, m_SignBlock), t); else rear?.Show(m_SignPrefix, m_SignBlock, "", t);
                return;
            }

            front?.Show(m_SignPrefix, m_SignBlock, front is CardSign ? m_CardText : m_SignText, t);
            side?.Show(m_SignPrefix, m_SignBlock, side is CardSign ? m_CardText : m_SignText, t);
            rearSide?.Show(m_SignPrefix, m_SignBlock, rearSide is CardSign ? m_CardText : m_SignText, t);
            if (extraSides != null)
                foreach (var sign in extraSides) sign.Show(m_SignPrefix, m_SignBlock, sign is CardSign ? m_CardText : m_SignText, t);
            rear?.Show(m_SignPrefix, m_SignBlock, "", t);      // the label alone, centred
        }

        /// <summary>A high-density sign (the Shanghai style): 32 rows of square dots on the sign's size.</summary>
        private static ISign NewHd(Transform parent, float3 centre, float2 size, quaternion rotation, HdFontSet fonts, Material[] inks) =>
            new HdSign(parent, centre, size, LedGeometry.HdMatrix(size).x, inks, HdFonts.Inks(PrefixColour(), true), fonts.Font, s_LedFont, rotation, AiSignRules.DotsPerSecond);

        /// <summary>The colour of the line's route prefix (option "Route prefix colours").</summary>
        private static float3 PrefixColour() => PrefixColour(BusState.LinePrefix);
        private static float3 PrefixColour(string prefix) => LineLabel.ColorFor(prefix, Mod.Settings != null ? Mod.Settings.RoutePrefixColors : null);

        private string m_CardText = "";

        /// <summary>A printed board in the profile's colours (the defaults: a warm white board, red paint).</summary>
        private static ISign NewCard(Transform parent, float3 centre, float2 size, quaternion rotation, in VehicleProfile.Sign sign) =>
            new CardSign(parent, centre, size, rotation, CardFonts.Get(), s_LedFont, CardFonts.Rgb(sign.Board, CardRgb.DefaultBoard),
                CardFonts.Rgb(sign.Ink, CardRgb.DefaultInk));

        private void ReleaseSigns()
        {
            m_FrontSign?.Destroy();
            m_SideSign?.Destroy();
            m_RearSign?.Destroy();
            m_RearSideSign?.Destroy();
            foreach (var sign in m_ExtraSideSigns) sign.Destroy();
            m_ExtraSideSigns.Clear();
            m_FrontSign = m_SideSign = m_RearSign = m_RearSideSign = null;
            m_SignsCreated = false;
        }

        private readonly CabLights m_Headlights = new CabLights();
        private CabInstance m_HeadlightInstance;

        /// <summary>
        /// The head lamps' real lights (option "Real headlights"): made on the front section of our own bus, lit with the beam the
        /// lamps give. They hang under the model's root, so they go with the model when it is hidden.
        /// </summary>
        private void UpdateHeadlights(bool show)
        {
            BusSettings settings = Mod.Settings;
            Section front = m_Sections.Count > 0 ? m_Sections[0] : null;
            bool want = show && m_OwnAsset && settings != null && settings.RealHeadlights && front != null && front.Spec.Section != BusSection.Rear &&
                front.Instance != null && front.Instance.Alive && front.Instance.Root != null && s_HeadLamps.ContainsKey(front.Key);
            if (!want)
            {
                if (m_Headlights.Alive)
                    m_Headlights.Update(Beam.Off, 1f, 0f);
                return;
            }

            if (!m_Headlights.Alive || m_HeadlightInstance != front.Instance)
            {
                m_HeadlightInstance = front.Instance;
                (float3 first, float3 second) = s_HeadLamps[front.Key];
                if (!m_Headlights.Create(front.Instance.Root, first, second))
                    return;
            }

            m_Headlights.Update(m_Beam, Daylight(), Mathf.Clamp(settings.HeadlightStrength, 25, 300) / 100f);
        }

        private void ReleaseOutside()
        {
            ReleaseReaders();
            ReleaseSigns();
            m_Headlights.Destroy();
            m_HeadlightInstance = null;
            ReleaseCluster();
        }

        private void ReleaseCluster()
        {
            m_GearSign?.Destroy();
            m_Telltales?.Destroy();
            m_GearSign = null;
            m_Telltales = null;
        }

        // ---------------------------------------------------------------- the instrument cluster

        // Telltale slots of the cluster's lower row (BusInterior.TelltaleFrame).
        private const int kTellLeft = 0, kTellHighBeam = 1, kTellRetarder = 2, kTellStopBrake = 3, kTellDoors = 4, kTellKneel = 5, kTellAir = 6, kTellLights = 7, kTellStop = 8, kTellRight = 11;
        private int[] m_TellIndex;

        /// <summary>The cluster in the driver's view: the warning lamps and the gear (D1 .. D6, N, R) on the central display.</summary>
        private void UpdateCluster(bool show)
        {
            if (m_Sections.Count == 0 || m_Sections[0].Spec.Section == BusSection.Rear)
                return;

            Section front = m_Sections[0];
            if (front.Instance == null || !front.Instance.Alive)
                return;

            if (m_Telltales == null || !m_Telltales.Alive)
            {
                m_Telltales?.Destroy();
                m_Telltales = new CabTelltales();
                m_TellIndex = new int[BusInterior.TelltaleSlots];
                for (int i = 0; i < m_TellIndex.Length; i++)
                {
                    float3 colour = i == kTellLeft || i == kTellRight || i == kTellLights ? s_Green : i == kTellHighBeam ? s_Blue :
                        i == kTellStopBrake || i == kTellDoors || i == kTellAir ? s_Red : s_Amber;
                    BusInterior.TelltaleFrame(front.Spec, i, out float3 c, out quaternion r, out float2 size);
                    m_TellIndex[i] = m_Telltales.Add(front.Instance.Root, c, r, size, CabMaterials.DimmedLamp(colour, kTelltaleNits, kTelltaleNight));
                }
            }

            if ((m_GearSign == null || !m_GearSign.Alive) && LedFontReady() && CabMaterials.Glow() != null)
            {
                m_GearSign?.Destroy();
                BusInterior.ClusterDisplayFrame(front.Spec, out float3 gc, out quaternion gr, out float2 gs);
                m_GearSign = new LedSign(front.Instance.Root, gc, gs, 20, 16, CabMaterials.Glow(), s_LedFont, gr, 0f);
            }

            bool api4 = m_Core.HasApi4;
            Tell(kTellLeft, show && m_LeftLit);
            Tell(kTellRight, show && m_RightLit);
            Tell(kTellHighBeam, show && m_OwnAsset && m_Beam == Beam.High);
            Tell(kTellRetarder, show && api4 && Core.RetarderLevel() > 0.15f);
            Tell(kTellStopBrake, show && api4 && m_Core.ParkingBrake);
            Tell(kTellDoors, show && m_Doors > 0.001f);
            Tell(kTellKneel, show && api4 && Core.KneelLevel() > 0.05f);
            float air = api4 ? Core.AirPressureBar() : 0f;
            Tell(kTellAir, show && air > 0.1f && air < 6.5f);
            Tell(kTellLights, show && (m_OwnAsset ? m_Beam != Beam.Off : Dark()));      // our bus: the switch's lamps; another bus: the dark, as its own lights
            Tell(kTellStop, show && StopRequested());

            if (m_GearSign == null)
                return;

            m_GearSign.Visible = show;
            if (!show)
                return;

            m_GearSign.Show(GearText(), "", UnityEngine.Time.unscaledTimeAsDouble);
        }

        private void Tell(int slot, bool on)
        {
            if (m_TellIndex != null && slot < m_TellIndex.Length)
                m_Telltales.Set(m_TellIndex[slot], on);
        }

        // "D1" .. "D9" made once (the cluster asks every frame).
        private static readonly string[] s_GearTexts = { "D", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9" };

        /// <summary>The gear on the cluster: D and the gear of the automatic gearbox (only D on an electric bus), N standing, R.</summary>
        private string GearText()
        {
            bool reversing = m_Core.Reversing;                      // false without API 4
            if (reversing)
                return "R";
            float kmh = Mathf.Abs(m_Core.WorldSpeed) * 3.6f;
            int gear = m_Core.HasApi4 && Core.HasDriveline() ? Core.Gear() : -1;
            if (gear > m_SeenGear)
                m_SeenGear = gear;
            if (kmh < 0.5f && m_Core.ParkingBrake)
                return "N";
            // one ratio (an electric bus): just D
            if (gear < 1 || m_SeenGear <= 1)
                return "D";
            return gear < s_GearTexts.Length ? s_GearTexts[gear] : "D" + gear;
        }
    }
}
