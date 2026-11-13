using System.Collections.Generic;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Colossal.Entities;
using Game.Citizens;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The people on the bus: the game's passengers on board shown as figures in the bus model's seats and standing places, in
    /// proportion (the game's load, passengers over capacity, on the model's places: BusRiders.Figures), the seats first and a
    /// few standing by the doors (BusRiders.Split). Each figure stands for a real passenger (a resident's creature in the bus's
    /// Passenger buffer) and looks like them: a child small, the elderly grey, women with long hair; when that passenger gets
    /// off, that figure leaves. (The game's own citizen models cannot sit in a bus: aboard a bus the game unspawns them, and the
    /// only way it draws a person in a vehicle plays the driving / cycling animation.) While the doors are open they come and go
    /// one by one, as the validator beeps; anyone already aboard keeps the place they took. The driver sits at the wheel when the bus is seen from
    /// outside. Standing riders lean a little with the bus, and the hand straps swing like pendulums with the body's
    /// accelerations, braking, cornering and bumps alike (StrapSwing). With the option "Game citizens" the figures are the game's
    /// own citizens (BusCitizens: each passenger as the game made that resident, posed seated, standing, holding a strap, the
    /// driver at the wheel); the simple figures when it is off or cannot be used.
    /// </summary>
    public sealed partial class BusCabSystem
    {
        private const float kBoardingSeconds = 0.55f;
        // The straps: a pendulum of about 1 Hz (a 0.25 m strap on its stiff loop), lightly damped (it swings a few times).
        private const float kStrapOmega = 6.2f, kStrapDamping = 0.12f, kStrapMax = BusInterior.StrapSwingLimit;
        private const float kRiderLean = 0.12f;
        // The smallest change written (radians): the straps' swing (under a millimetre at a 0.25 m strap's handle) and the
        // standing riders' lean (a few millimetres at the head). Below it the transforms keep what they have, so straps and
        // riders at rest write nothing (the pendulum's angle never stands exactly still).
        private const float kStrapStep = 0.002f, kLeanStep = 0.002f;

        private static Task<KitModelData> s_RidersBuild;
        private static CabModel s_RidersModel;
        private static bool s_RidersFailed;

        private float m_NextBoarding;
        private readonly System.Random m_RiderRandom = new System.Random(20261013);

        private sealed class RiderState
        {
            internal List<BusInterior.RiderSlot> Slots;
            internal int[] Part;                 // the figure at each slot (-1: empty)
            internal Entity[] Person;            // the passenger it stands for (Entity.Null: anonymous, for the proportion)
            internal GameObject[] Figures;
            internal int Seats, Standing, Driver = -1;
            internal Transform Root;
            internal DriverFeetData? DriverFeet;
            internal CabModel AnchoredDriver;
            internal bool AnchoredDriverAttempted;
            internal int Made;                   // figures made so far (a new one stands upright until the lean is written)
            internal int LeanMade = -1;          // Made when the lean was last written
            internal float2 Lean;                // the lean last written (radians)
            // a passenger's seat as the camera (Core API 14): the place it is at shows no figure (the camera is that passenger)
            internal int HiddenSlot = -1;        // the place whose figure is hidden now
            internal int SlotCamera = -1, SlotKey = -1, CameraSlot = -1;   // the camera and set the place was found for, and the place
        }

        // A seat camera this near a rider's place (across the floor) is that rider's: their figure is not drawn.
        private const float kCameraRiderRadius = 0.45f;

        private sealed class SwingState
        {
            internal bool HasLast;
            internal Vector3 LastPosition, LastVelocity, Accel;
            internal float2 Angle, Rate;
            internal float2 Shown;               // the swing the straps were last given (Settled)
        }

        private readonly Dictionary<Section, RiderState> m_Riders = new Dictionary<Section, RiderState>();
        private readonly Dictionary<Section, SwingState> m_Swings = new Dictionary<Section, SwingState>();

        /// <summary>The straps' swing for a section drawn at this pose this frame (radians, see BusInterior.Pose.StrapSwing).</summary>
        private float2 StrapSwing(Section section, Vector3 position, Quaternion rotation, float dt)
        {
            if (!m_Swings.TryGetValue(section, out SwingState s))
                m_Swings[section] = s = new SwingState();
            if (dt <= 0f)
                return Settled(s);

            Vector3 velocity = s.HasLast ? (position - s.LastPosition) / dt : Vector3.zero;
            if (!s.HasLast || (position - s.LastPosition).sqrMagnitude > 25f)
            {
                s.HasLast = true;
                s.LastPosition = position;
                s.LastVelocity = Vector3.zero;
                s.Accel = Vector3.zero;
                return Settled(s);
            }

            Vector3 accel = Vector3.ClampMagnitude((velocity - s.LastVelocity) / dt, 15f);
            s.Accel = Vector3.Lerp(s.Accel, accel, 1f - Mathf.Exp(-dt / 0.06f));
            s.LastPosition = position;
            s.LastVelocity = velocity;

            // The strap hangs along gravity less the body's acceleration, seen from the body (its roll and pitch included).
            Vector3 g = Quaternion.Inverse(rotation) * (new Vector3(0f, -9.81f, 0f) - s.Accel);
            float2 target = new float2(Mathf.Atan2(-g.z, -g.y), Mathf.Atan2(g.x, -g.y));
            int steps = Mathf.Max(1, Mathf.CeilToInt(dt / 0.005f));
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                s.Rate += (kStrapOmega * kStrapOmega * (target - s.Angle) - 2f * kStrapDamping * kStrapOmega * s.Rate) * h;
                s.Angle = math.clamp(s.Angle + s.Rate * h, -kStrapMax, kStrapMax);
            }

            return Settled(s);
        }

        /// <summary>
        /// The swing the straps are drawn at: the pendulum's angle, held while it moves less than kStrapStep from the one last
        /// given (the model's parts then get the very same pose and write nothing).
        /// </summary>
        private static float2 Settled(SwingState s)
        {
            if (math.cmax(math.abs(s.Angle - s.Shown)) >= kStrapStep)
                s.Shown = s.Angle;
            return s.Shown;
        }


        // The passengers on board (the vehicle's Passenger buffer: the residents' creature entities), refreshed a few times a second
        // into the spare set, which takes over (the same order of the passengers as filling one set again).
        private HashSet<Entity> m_Aboard = new HashSet<Entity>(), m_AboardNext = new HashSet<Entity>();
        private readonly List<Entity> m_Unseen = new List<Entity>();
        private float m_NextAboard;
        private BusCitizens m_Citizens;
        private bool m_CitizensShown;
        // The sections' capacity (the prefabs', read with the passengers and for another bus), the places last counted, and
        // whether the figures may have to change: the passengers, the places or the capacity changed, a section's places were
        // made, or the last fill moved someone (one at a time while boarding). Otherwise Fill would find nothing to do.
        private int m_Capacity;
        private Entity m_CapacityBus = Entity.Null;
        private int m_Places = -1;
        private bool m_RidersDirty = true;

        /// <summary>Fills the sections' places with figures for the passengers on board, and poses them (the driver, the lean).</summary>
        private void UpdateRiders(float dt, bool cab, bool exterior)
        {
            BusSettings settings = Mod.Settings;
            if (settings == null || !settings.ShowRiders || m_Sections.Count == 0 || !EnsureRidersModel())
            {
                ReleaseRiders();
                return;
            }

            // switching between the game's citizens and the simple figures: everyone again
            bool citizens = settings.GameCitizens && !(m_Citizens?.Failed ?? false);
            if (citizens != m_CitizensShown)
            {
                ReleaseRiders();
                m_CitizensShown = citizens;
            }
            if (citizens)
                m_Citizens?.RequestTextures(cab ? 512f : 256f);

            float now = UnityEngine.Time.unscaledTime;
            int places = 0;
            long tp = CabBlocks.Now;
            foreach (Section section in m_Sections)
            {
                RiderState state = Riders(section);
                if (state != null)
                    places += state.Seats + state.Standing;
            }
            CabBlocks.Add(CabBlocks.RiderPlaces, tp);
            if (places != m_Places)
            {
                m_Places = places;
                m_RidersDirty = true;
            }

            bool refresh = now >= m_NextAboard;
            if (refresh || m_CapacityBus != m_Bus)
            {
                int capacity = 0;
                foreach (Section section in m_Sections)
                    if (EntityManager.TryGetComponent(section.Entity, out PrefabRef prefabRef) && EntityManager.TryGetComponent(prefabRef.m_Prefab, out PublicTransportVehicleData data))
                        capacity += data.m_PassengerCapacity;
                m_CapacityBus = m_Bus;
                if (capacity != m_Capacity)
                {
                    m_Capacity = capacity;
                    m_RidersDirty = true;
                }
            }

            if (refresh)
            {
                m_NextAboard = now + 0.25f;
                m_AboardNext.Clear();
                // Boarding puts each passenger in whichever section has room (ResidentAISystem.TryFindVehicle over the layout), so
                // an articulated bus keeps its riders in every section's own buffer; the capacity above is the sections' sum too.
                foreach (Section section in m_Sections)
                    if (EntityManager.TryGetBuffer(section.Entity, true, out DynamicBuffer<Passenger> buffer))
                        for (int i = 0; i < buffer.Length; i++)
                            if (!EntityManager.HasComponent<Game.Creatures.Pet>(buffer[i].m_Passenger))      // a dog is not a person
                                m_AboardNext.Add(buffer[i].m_Passenger);
                if (!m_AboardNext.SetEquals(m_Aboard))
                    m_RidersDirty = true;
                (m_Aboard, m_AboardNext) = (m_AboardNext, m_Aboard);
            }

            int figures = BusRiders.Figures(m_Aboard.Count, m_Capacity, places);
            bool boarding = BusState.Bus == m_Bus && BusState.DoorsOpen || BusRide.Bus == m_Bus && BusRide.DoorsOpen;
            bool step = !boarding || now >= m_NextBoarding;
            bool fill = step && m_RidersDirty;
            if (fill)
            {
                // passengers aboard without a figure yet
                m_Unseen.Clear();
                foreach (Entity person in m_Aboard)
                {
                    bool shown = false;
                    foreach (RiderState state in m_Riders.Values)
                        shown |= System.Array.IndexOf(state.Person, person) >= 0;
                    if (!shown)
                        m_Unseen.Add(person);
                }
            }

            bool changed = false;
            int left = figures;
            for (int i = 0; i < m_Sections.Count; i++)
            {
                Section section = m_Sections[i];
                RiderState state = Riders(section);
                if (state == null)
                    continue;

                int sectionPlaces = state.Seats + state.Standing;
                int share = i == m_Sections.Count - 1 ? left : Mathf.Min(left, Mathf.RoundToInt((float)figures * sectionPlaces / Mathf.Max(1, places)));
                left -= share;
                BusRiders.Split(share, state.Seats, state.Standing, out int seated, out int standing);
                if (fill && !changed)
                    changed = Fill(state, seated, standing, boarding);
                Pose(state, section, i, cab, exterior, dt);
            }

            // every section filled and nobody moved: nothing more to do until something changes
            if (fill && !changed)
                m_RidersDirty = false;
            if (changed && boarding)
                m_NextBoarding = now + kBoardingSeconds;
        }

        /// <summary>The rider places of a section, made when its model is there (the figures hang under its root).</summary>
        private RiderState Riders(Section section)
        {
            if (section.Instance == null || !section.Instance.Alive)
            {
                if (m_Riders.TryGetValue(section, out RiderState dead))
                {
                    Clear(dead);
                    m_Riders.Remove(section);
                    m_RidersDirty = true;
                }

                return null;
            }

            if (m_Riders.TryGetValue(section, out RiderState state) && state.Root == section.Instance.Root)
                return state;

            if (state != null)
                Clear(state);
            m_RidersDirty = true;                   // new empty places to fill
            state = new RiderState { Slots = BusInterior.RiderSlotsOf(section.Spec), Root = section.Instance.Root,
                DriverFeet = BusInterior.DriverCab(section.Spec).DriverFeet };
            int n = state.Slots.Count;
            state.Part = new int[n];
            state.Figures = new GameObject[n];
            state.Person = new Entity[n];
            for (int i = 0; i < n; i++)
            {
                state.Part[i] = -1;
                switch (state.Slots[i].Kind)
                {
                    case BusInterior.RiderKind.Seated: state.Seats++; break;
                    case BusInterior.RiderKind.Driver: state.Driver = i; break;
                    default: state.Standing++; break;
                }
            }

            m_Riders[section] = state;
            return state;
        }

        /// <summary>
        /// Brings the figures to the passengers: first the ones whose passenger has got off leave, then seated and standing
        /// figures come or go to the targets; a newcomer is a passenger without a figure yet (anonymous when the model shows more
        /// people than the game carries: the proportion), takes a free place at random and looks like that resident (age,
        /// gender). Everyone at once (taking over, a shift starting) or one at a time while the doors are open.
        /// </summary>
        private bool Fill(RiderState state, int seated, int standing, bool oneByOne)
        {
            bool changed = false;
            // Everyone at once is spread over frames: a game citizen's figure costs 1 to 2 ms to make (two compute buffers, property
            // blocks, dozens of objects), and a full bus's 60 to 100 figures in one frame was a hitch of 200 ms at the take-over (the
            // block table of 18 October: riders, worst frame 202.6 ms). The caller asks again next frame while anything changed.
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = (long)(kFillBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            for (int i = 0; i < state.Slots.Count; i++)
            {
                if (state.Part[i] < 0 || state.Person[i] == Entity.Null || m_Aboard.Contains(state.Person[i]) || i == state.Driver)
                    continue;
                Leave(state, i);
                changed = true;
                if (oneByOne || System.Diagnostics.Stopwatch.GetTimestamp() - start >= budget)
                    return true;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                bool seats = pass == 0;
                int target = seats ? seated : standing, count = 0;
                for (int i = 0; i < state.Slots.Count; i++)
                    if (state.Part[i] >= 0 && IsSeat(state, i) == seats && i != state.Driver) count++;
                while (count != target)
                {
                    int slot = PickSlot(state, seats, count < target);
                    if (slot < 0)
                        break;

                    if (count < target)
                    {
                        Entity person = Entity.Null;
                        if (m_Unseen.Count > 0)
                        {
                            int pick = m_RiderRandom.Next(m_Unseen.Count);
                            person = m_Unseen[pick];
                            m_Unseen.RemoveAt(pick);
                        }

                        state.Person[slot] = person;
                        state.Part[slot] = BusRiders.PartFor(KindFor(state.Slots[slot].Kind, person, out float scale), LookFor(person));
                        state.Figures[slot] = MakeFigure(state, slot);
                        state.Made++;
                        if (state.Figures[slot] != null && scale != 1f && state.Figures[slot].GetComponent<BusCitizens.Figure>() == null)
                            state.Figures[slot].transform.localScale = new Vector3(scale, scale, scale);
                        count++;
                    }
                    else
                    {
                        Leave(state, slot);
                        count--;
                    }

                    changed = true;
                    if (oneByOne || System.Diagnostics.Stopwatch.GetTimestamp() - start >= budget)
                        return true;
                }
            }

            return changed;
        }

        private const double kFillBudgetMs = 3.0;

        private void Leave(RiderState state, int slot)
        {
            if (state.Figures[slot] != null)
                DestroyFigure(state.Figures[slot]);
            state.Figures[slot] = null;
            state.Part[slot] = -1;
            state.Person[slot] = Entity.Null;
        }

        /// <summary>The resident behind a passenger (age, gender), when it is one.</summary>
        private bool TryCitizen(Entity person, out Citizen citizen)
        {
            citizen = default;
            return person != Entity.Null && EntityManager.TryGetComponent(person, out Game.Creatures.Resident resident) &&
                EntityManager.TryGetComponent(resident.m_Citizen, out citizen);
        }

        /// <summary>A look for the person: a woman's or a man's, grey for the elderly, the clothes by the person (stable).</summary>
        private int LookFor(Entity person)
        {
            if (!TryCitizen(person, out Citizen citizen))
                return m_RiderRandom.Next(BusRiders.Looks);
            int hash = (int)(((uint)person.Index * 7919u) % 65521u);     // unsigned: the product overflowed to a negative index
            bool male = (citizen.m_State & CitizenFlags.Male) != 0;
            if (citizen.GetAge() == CitizenAge.Elderly)
                return male ? BusRiders.ElderlyMaleLook : BusRiders.ElderlyFemaleLook;
            return male ? BusRiders.MaleLooks[hash % BusRiders.MaleLooks.Length] : BusRiders.FemaleLooks[hash % BusRiders.FemaleLooks.Length];
        }

        /// <summary>The figure's pose and size: a child is smaller (and does not reach the straps: hands down).</summary>
        private BusInterior.RiderKind KindFor(BusInterior.RiderKind kind, Entity person, out float scale)
        {
            scale = 1f;
            if (!TryCitizen(person, out Citizen citizen))
                return kind;
            CitizenAge age = citizen.GetAge();
            if (age == CitizenAge.Child)
                scale = 0.68f;
            else if (age == CitizenAge.Teen)
                scale = 0.93f;
            return age == CitizenAge.Child && kind == BusInterior.RiderKind.Holding ? BusInterior.RiderKind.Standing : kind;
        }

        private static bool IsSeat(RiderState state, int slot) => state.Slots[slot].Kind == BusInterior.RiderKind.Seated;

        /// <summary>
        /// A random free or occupied place of the kind; -1 when there is none. Leaving (free = false), anonymous figures go
        /// first: the passengers the game really carries stay as long as they are aboard.
        /// </summary>
        private int PickSlot(RiderState state, bool seats, bool free)
        {
            int n = 0, pick = -1;
            for (int pass = 0; pass < 2 && pick < 0; pass++)
            {
                for (int i = 0; i < state.Slots.Count; i++)
                {
                    if (i == state.Driver || IsSeat(state, i) != seats || (state.Part[i] < 0) != free)
                        continue;
                    if (!free && pass == 0 && state.Person[i] != Entity.Null)
                        continue;
                    if (m_RiderRandom.Next(++n) == 0)
                        pick = i;
                }

                if (free)
                    break;
            }

            return pick;
        }

        private GameObject MakeFigure(RiderState state, int slot)
        {
            int part = state.Part[slot];
            // Explicit foot anchors currently select the existing procedural driver.
            // Other citizens and all profiles without anchors retain the exact old path.
            if (slot==state.Driver && state.DriverFeet.HasValue && state.Root!=null)
            {
                if (!state.AnchoredDriverAttempted)
                {
                    state.AnchoredDriverAttempted=true;
                    try
                    {
                        var pose=new DriverFootPose(state.DriverFeet.Value,state.Slots[slot]);
                        KitModelData data=KitBuild.BuildAll(new DriverFootFigure(pose));
                        if (data.Error!=null) throw new System.InvalidOperationException(data.Error);
                        state.AnchoredDriver=new CabModel(data,3);
                    }
                    catch (System.Exception ex)
                    { Mod.log.Warn("Driver foot anchors could not be applied; using the legacy visible driver: "+ex.Message); }
                }
                if (state.AnchoredDriver!=null)
                    return SimpleFigure(state.AnchoredDriver,0,state.Root,state.Slots[slot],"anchored driver",s_RiderLevels);
            }
            if (m_CitizensShown && state.Root != null)
            {
                // the game's citizen: the passenger as the game made them (a stable pose variant per person), the driver per bus
                if (m_Citizens == null)
                    m_Citizens = new BusCitizens(EntityManager, World.GetOrCreateSystemManaged<PrefabSystem>(),
                        GetEntityQuery(ComponentType.ReadOnly<HumanData>(), ComponentType.ReadOnly<PrefabData>()));
                Entity person = state.Person[slot];
                int variety = slot == state.Driver ? m_Bus.Index : person != Entity.Null ? (int)(((uint)person.Index * 2654435761u) >> 1) : m_RiderRandom.Next();
                GameObject citizen = m_Citizens.Make(state.Root, state.Slots[slot], PoseOf(part), person, variety);
                if (citizen != null)
                    return citizen;
                if (!m_Citizens.Failed && UnityEngine.Time.unscaledTime < m_Citizens.RetryAt)
                    return null;                // a model still loading: the place waits for its citizen (Pose tries again)
            }

            return state.Root != null ? SimpleFigure(s_RidersModel, part, state.Root, state.Slots[slot], "rider " + slot, s_RiderLevels) : null;
        }

        // For the developer benchmark (BusCitizenProbe): the simple figures without their lighter levels.
        internal static bool s_RiderLevels = true;

        // The simple figures' levels: the screen height (share of the view) under which the next one shows; under the last, nothing.
        private static readonly float[] kRiderHeights = { 0.25f, 0.08f, 0.01f };

        /// <summary>
        /// A simple figure (a BusRiders part) at a rider slot: its full mesh, and with levels its lighter ones under a LODGroup
        /// (the lightest casts no shadow; nothing when it is tiny on screen).
        /// </summary>
        internal static GameObject SimpleFigure(CabModel model, int part, Transform parent, BusInterior.RiderSlot s, string name, bool levels)
        {
            Mesh mesh = part >= 0 && part < model.Meshes.Length ? model.Meshes[part] : null;
            if (mesh == null)
                return null;

            var go = new GameObject(name) { hideFlags = HideFlags.DontSave, layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(s.Position.x, s.Position.y, s.Position.z);
            go.transform.localRotation = Quaternion.Euler(0f, s.Yaw * Mathf.Rad2Deg, 0f);
            MeshRenderer full = FigureRenderer(go, mesh, model.Masks[part], true);
            int count = levels && model.Levels != null ? model.Levels.Length : 0;
            if (count == 0)
                return go;

            var lods = new List<LOD> { new LOD(kRiderHeights[0], new Renderer[] { full }) };
            for (int l = 0; l < count; l++)
            {
                Mesh lighter = model.Levels[l][part];
                if (lighter == null)
                    break;
                var child = new GameObject("lod" + (l + 1)) { hideFlags = HideFlags.DontSave, layer = go.layer };
                child.transform.SetParent(go.transform, false);
                MeshRenderer r = FigureRenderer(child, lighter, model.LevelMasks[l][part], l + 1 < count);
                lods.Add(new LOD(kRiderHeights[Mathf.Min(l + 1, kRiderHeights.Length - 1)], new Renderer[] { r }));
            }
            LOD[] array = lods.ToArray();
            array[array.Length - 1].screenRelativeTransitionHeight = kRiderHeights[kRiderHeights.Length - 1];
            LODGroup group = go.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            group.SetLODs(array);
            group.localReferencePoint = mesh.bounds.center;
            group.size = 1.9f;
            return go;
        }

        private static MeshRenderer FigureRenderer(GameObject go, Mesh mesh, ushort mask, bool shadows)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            r.receiveShadows = true;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            r.renderingLayerMask = uint.MaxValue;
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            r.sharedMaterials = CabMaterials.Set(mask, KitLights.Off);
            return r;
        }

        /// <summary>
        /// The driver at the wheel when the bus is seen from outside or from another seat (from the driver's seat the camera is the driver);
        /// the place of a passenger's seat camera left empty; the lean.
        /// </summary>
        private void Pose(RiderState state, Section section, int sectionIndex, bool cab, bool exterior, float dt)
        {
            int hide = cab ? CameraSlot(state, sectionIndex) : -1;
            if (hide != state.HiddenSlot)
            {
                if (state.HiddenSlot >= 0 && state.HiddenSlot < state.Figures.Length && state.Figures[state.HiddenSlot] != null)
                    state.Figures[state.HiddenSlot].SetActive(true);
                state.HiddenSlot = hide;
            }
            if (hide >= 0 && state.Figures[hide] != null && state.Figures[hide].activeSelf)
                state.Figures[hide].SetActive(false);      // also a figure made there since

            if (state.Driver >= 0)
            {
                bool show = (!cab && exterior) || (cab && m_Core.Camera != 0);
                if (show && state.Figures[state.Driver] == null)
                {
                    state.Part[state.Driver] = BusRiders.Driver;
                    state.Figures[state.Driver] = MakeFigure(state, state.Driver);
                }
                else if (!show && state.Figures[state.Driver] != null)
                {
                    DestroyFigure(state.Figures[state.Driver]);
                    state.Figures[state.Driver] = null;
                    state.Part[state.Driver] = -1;
                }

                // the hands follow the wheel (the figure's steering wheel turns by the same angle: SteerDeg)
                if (show && m_Citizens != null && state.Figures[state.Driver] != null)
                    m_Citizens.DriveHands(state.Figures[state.Driver], m_Core.WheelAngle * Mathf.Rad2Deg * m_Core.SteeringRatio, dt);
            }

            // places whose game citizen could not be made yet (a model still loading): again once it may have loaded
            if (m_CitizensShown && m_Citizens != null && !m_Citizens.Failed && UnityEngine.Time.unscaledTime >= m_Citizens.RetryAt)
                for (int i = 0; i < state.Slots.Count; i++)
                    if (state.Part[i] >= 0 && state.Figures[i] == null && i != state.Driver)
                    {
                        state.Figures[i] = MakeFigure(state, i);
                        state.Made++;
                        if (state.Figures[i] == null)
                            break;
                    }

            // the lean: written again when it has changed by kLeanStep or a figure has been made since
            float2 swing = m_Swings.TryGetValue(section, out SwingState s) ? s.Angle : float2.zero;
            float2 angles = new float2(swing.x * kRiderLean, swing.y * kRiderLean);
            if (state.LeanMade == state.Made && math.cmax(math.abs(angles - state.Lean)) < kLeanStep)
                return;
            state.LeanMade = state.Made;
            state.Lean = angles;
            Quaternion lean = Quaternion.Euler(angles.x * Mathf.Rad2Deg, 0f, angles.y * Mathf.Rad2Deg);
            for (int i = 0; i < state.Slots.Count; i++)
            {
                BusInterior.RiderSlot slot = state.Slots[i];
                if (state.Figures[i] == null || (slot.Kind != BusInterior.RiderKind.Holding && slot.Kind != BusInterior.RiderKind.Standing))
                    continue;
                state.Figures[i].transform.localRotation = lean * Quaternion.Euler(0f, slot.Yaw * Mathf.Rad2Deg, 0f);
            }
        }

        /// <summary>The place of this section the camera in use sits at (a seat camera of the bus other than the driver's), or -1.</summary>
        private int CameraSlot(RiderState state, int sectionIndex)
        {
            int camera = m_Core.Camera;
            if (camera <= 0 || camera > m_OtherCameras.Count)
                return -1;
            if (state.SlotCamera == camera && state.SlotKey == m_CamerasKey)
                return state.CameraSlot;

            state.SlotCamera = camera;
            state.SlotKey = m_CamerasKey;
            state.CameraSlot = -1;
            (VehicleProfile.CameraData c, int onSection) = m_OtherCameras[camera - 1];
            if (c.Mounted || onSection != sectionIndex)
                return -1;
            float best = kCameraRiderRadius * kCameraRiderRadius;
            for (int i = 0; i < state.Slots.Count; i++)
            {
                if (i == state.Driver)
                    continue;
                float3 p = state.Slots[i].Position;
                float d = (p.x - c.Eye.x) * (p.x - c.Eye.x) + (p.z - c.Eye.z) * (p.z - c.Eye.z);
                if (d < best && math.abs(p.y - c.Eye.y) < 2f)
                {
                    best = d;
                    state.CameraSlot = i;
                }
            }

            return state.CameraSlot;
        }

        private static BusCitizens.Pose PoseOf(int part) =>
            part == BusRiders.Driver ? BusCitizens.Pose.Driver : part >= BusRiders.DownFirst ? BusCitizens.Pose.Standing :
            part >= BusRiders.HoldingFirst ? BusCitizens.Pose.Holding : BusCitizens.Pose.Seated;

        private void DestroyCitizens()
        {
            m_Citizens?.Dispose();
            m_Citizens = null;
        }

        // The riders' model (88 000 triangles in three levels) is built on the prebuild thread and uploaded a couple of milliseconds a
        // frame while the player is not driving (PrepareRidersAhead); the first drive finds it ready, or finishes the upload.
        private static CabModel s_RidersWarming;
        private static long s_RidersUploadTicks;
        private static int s_RidersUploadFrames;
        private static bool s_RidersAhead;

        private static void StartRidersBuild()
        {
            if (s_RidersBuild == null && s_RidersModel == null && !s_RidersFailed)
                s_RidersBuild = CabPrebuild.Enqueue(() => KitBuild.BuildAll(new BusRiders(), new MeshKit()));
        }

        /// <summary>Not driving: build the riders' model in the background and upload it a little each frame.</summary>
        private static void PrepareRidersAhead(long budgetTicks)
        {
            if (s_RidersModel != null || s_RidersFailed)
                return;
            StartRidersBuild();
            if (s_RidersWarming == null)
            {
                if (s_RidersBuild == null || !s_RidersBuild.IsCompleted)
                    return;
                KitModelData data = s_RidersBuild.Status == TaskStatus.RanToCompletion ? s_RidersBuild.Result : null;
                if (data == null || data.Error != null)
                    return;                       // the first drive reports it (EnsureRidersModel)
                s_RidersBuild = null;
                s_RidersWarming = new CabModel(data, true, 3);
                s_RidersAhead = true;
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool done = s_RidersWarming.Step(budgetTicks);
            s_RidersUploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            s_RidersUploadFrames++;
            if (done)
                AdoptRidersModel();
        }

        private static void AdoptRidersModel()
        {
            s_RidersModel = s_RidersWarming;
            s_RidersWarming = null;
            Mod.log.Info($"Bus riders built: {s_RidersModel.Data.Parts.Length} figures, {s_RidersModel.Triangles} triangles" +
                (s_RidersAhead ? $", prepared ahead (uploaded in {s_RidersUploadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms over {s_RidersUploadFrames} frame(s))." : "."));
        }

        /// <summary>The riders' model once it is uploaded: what is left of its upload takes the frame's construction budget (false until then).</summary>
        private bool EnsureRidersModel()
        {
            if (s_RidersModel != null)
                return true;
            if (s_RidersFailed)
                return false;
            if (s_RidersWarming != null)
            {
                long budget = BuildBudget();
                if (budget <= 0 || !s_RidersWarming.Step(budget))
                    return false;
                AdoptRidersModel();
                return true;
            }

            StartRidersBuild();
            if (!s_RidersBuild.IsCompleted)
                return false;

            KitModelData data = s_RidersBuild.Status == TaskStatus.RanToCompletion ? s_RidersBuild.Result : null;
            s_RidersBuild = null;
            if (data == null || data.Error != null)
            {
                s_RidersFailed = true;
                Mod.log.Warn($"Bus riders could not be built: {(data != null ? data.Error : "build failed")}");
                return false;
            }

            s_RidersWarming = new CabModel(data, true, 3);
            return false;                                    // uploaded from the next frame on, within the budget
        }

        private static void Clear(RiderState state)
        {
            for (int i = 0; i < state.Figures.Length; i++)
            {
                if (state.Figures[i] != null)
                {
                    DestroyFigure(state.Figures[i]);
                }
                state.Figures[i] = null;
                state.Part[i] = -1;
                state.Person[i] = Entity.Null;
            }
            if (state.AnchoredDriver!=null)
            {
                state.AnchoredDriver.Dispose();
                state.AnchoredDriver=null;
            }
            state.AnchoredDriverAttempted=false;
        }

        private void ReleaseRiders()
        {
            var failures = new List<System.Exception>();
            foreach (Section section in new List<Section>(m_Riders.Keys))
                CabCleanup.Try(() => { Clear(m_Riders[section]); m_Riders.Remove(section); }, failures);
            m_Swings.Clear();
            m_Aboard.Clear();
            m_AboardNext.Clear();
            m_CapacityBus = Entity.Null;
            m_Places = -1;
            m_RidersDirty = true;
            CabCleanup.ThrowIfAny(failures, "Bus rider cleanup failed.");
        }

        private static void DestroyFigure(GameObject figure)
        {
            if (figure == null) return;
            figure.SetActive(false);
            foreach (MeshFilter filter in figure.GetComponentsInChildren<MeshFilter>(true)) filter.sharedMesh = null;
            foreach (MeshRenderer renderer in figure.GetComponentsInChildren<MeshRenderer>(true)) renderer.sharedMaterials = System.Array.Empty<Material>();
            Object.Destroy(figure);
        }

    }
}
