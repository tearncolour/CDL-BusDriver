using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// The people on the bus: low-poly passengers seated and standing, and the driver, as the cab system places them in the bus model's
// rider slots (BusInterior.RiderSlots) in proportion to the game's passengers on board. One part per figure variant; the cab
// system draws a variant at every occupied slot (part space = the figure's own frame: origin at the seat cushion under the hips
// for a seated figure, between the feet on the floor for a standing one, facing +Z). Twelve looks (clothes, skin, hair style,
// glasses, a backpack or a shoulder bag), each in three poses: seated (hands on the lap, on a bag, or a phone), standing with the
// right hand up on a strap or the rail, standing with the hands down; and the driver with the hands on the wheel.
// Sizes after an average adult: 1.72 m standing, shoulders 0.40 m (women 0.36 m), seated hip 0.09 m over the cushion. The head
// has a skull a little deeper than wide, a jaw, nose, ears, eyes, brows and mouth (LOD0); the torso is a lathe along the spine,
// flattened front to back; shoulders, elbows and knees are balls, so the limbs bend without gaps.
namespace CDLC.BusDriver.Interior.Models
{
    [KitModel("bus-riders", Category = "Cab")]
    public sealed class BusRiders : KitSource
    {
        private DriverFootPose? m_DriverFeet;
        internal static void DrawAnchoredDriver(MeshKit kit,int lod,DriverFootPose feet)
        { new BusRiders { m_DriverFeet=feet }.Seated(kit,s_Uniform,true,lod); }
        public const int Looks = 12;
        public const int SeatedFirst = 0;                       // 0 .. 11: seated, look i
        public const int HoldingFirst = SeatedFirst + Looks;    // 12 .. 23: standing, right hand up on a strap or the rail
        public const int DownFirst = HoldingFirst + Looks;      // 24 .. 35: standing, hands down
        public const int DownCount = Looks;
        public const int Driver = DownFirst + DownCount;        // 36: the driver at the wheel
        public const int PartCount = Driver + 1;

        /// <summary>
        /// How many figures show the passengers on board: the game's load (passengers over its capacity) on this model's places,
        /// so a full bus looks full and a quiet one quiet whatever capacity the game gives the bus; at least one for anyone aboard.
        /// Without a capacity, one figure per passenger.
        /// </summary>
        public static int Figures(int passengers, int capacity, int places)
        {
            if (passengers <= 0 || places <= 0) return 0;
            if (capacity <= 0) return math.min(passengers, places);
            return math.clamp((int)math.round((float)passengers / capacity * places), 1, places);
        }

        /// <summary>
        /// Seated and standing: the seats fill first, but once a few are aboard about one in eight stands by the doors anyway (a
        /// short ride); when the seats are full the rest stand.
        /// </summary>
        public static void Split(int figures, int seats, int standing, out int seated, out int stand)
        {
            figures = math.max(0, figures);
            int prefer = figures >= 6 ? figures / 8 : 0;
            seated = math.min(seats, figures - math.min(prefer, standing));
            stand = math.min(standing, figures - seated);
        }

        /// <summary>The figure (part) for a person at a slot of the given kind, by look 0 .. 11.</summary>
        public static int PartFor(BusInterior.RiderKind kind, int look)
        {
            look = ((look % Looks) + Looks) % Looks;
            switch (kind)
            {
                case BusInterior.RiderKind.Seated: return SeatedFirst + look;
                case BusInterior.RiderKind.Holding: return HoldingFirst + look;
                case BusInterior.RiderKind.Standing: return DownFirst + look;
                default: return Driver;
            }
        }

        // Looks by person: women, men, and the elderly (grey: a woman with a bun and a cardigan, a man with a receding hairline).
        public static readonly int[] FemaleLooks = { 1, 3, 5, 8, 9 };
        public static readonly int[] MaleLooks = { 0, 2, 6, 7, 10 };
        public const int ElderlyMaleLook = 4, ElderlyFemaleLook = 11;

        // The right hand of a holding figure (figure frame): under a strap's handle or on the rail.
        public static readonly float3 HoldingHand = new float3(0.17f, 1.76f, 0.04f);
        // The driver's hands on the wheel's rim (seated frame: the cushion point; the wheel's hub 0.24 m up and 0.42 m ahead).
        public static readonly float3 DriverHandL = new float3(-0.19f, 0.30f, 0.36f), DriverHandR = new float3(0.19f, 0.30f, 0.36f);

        public override KitModelKey Key => new KitModelKey("bus-riders");
        public override int3 RendererBudget => new int3(PartCount + 2, PartCount + 2, PartCount + 2);

        enum Hair : byte { Short, Crop, Long, Bob, Bun, Ponytail, Receding }
        enum Carry : byte { None, Backpack, ShoulderBag }

        struct Look
        {
            public bool Female;
            public Sw Top, Legs, Skin, HairColour, Shoes, Soles;
            public Sw Jacket;                  // over the top, open at the front; Sw.Count: none
            public Hair Hair;
            public bool LongSleeves, Skirt, Glasses, Phone;
            public Carry Carry;
            public Sw Bag;
        }

        const Sw None = Sw.Count;

        static readonly Look[] s_Looks =
        {
            // 0: a man in a blue shirt and jeans, glasses
            new Look { Top = Sw.Blue, LongSleeves = true, Legs = Sw.Denim, Skin = Sw.SkinLight, HairColour = Sw.HairBlack, Hair = Hair.Short, Shoes = Sw.Black, Soles = Sw.DarkGrey, Jacket = None, Glasses = true },
            // 1: a woman in a red top and a black skirt, long hair, a shoulder bag, a phone
            new Look { Female = true, Top = Sw.FabricAccent, Legs = Sw.Black, Skirt = true, Skin = Sw.SkinLight, HairColour = Sw.HairBlack, Hair = Hair.Long, Shoes = Sw.Black, Soles = Sw.Black, Jacket = None, Carry = Carry.ShoulderBag, Bag = Sw.HairBrown, Phone = true },
            // 2: a man in a white shirt, a navy jacket and chinos
            new Look { Top = Sw.White, Jacket = Sw.Navy, Legs = Sw.Khaki, Skin = Sw.SkinMid, HairColour = Sw.HairBlack, Hair = Hair.Crop, Shoes = Sw.DarkGrey, Soles = Sw.Black },
            // 3: a student in green with a ponytail and a backpack
            new Look { Female = true, Top = Sw.Green, LongSleeves = true, Legs = Sw.Denim, Skin = Sw.SkinMid, HairColour = Sw.HairBrown, Hair = Hair.Ponytail, Shoes = Sw.White, Soles = Sw.White, Jacket = None, Carry = Carry.Backpack, Bag = Sw.Navy },
            // 4: an elderly man: beige shirt, grey jacket, receding grey hair, glasses
            new Look { Top = Sw.Beige, Jacket = Sw.DarkGrey, Legs = Sw.Grey, Skin = Sw.SkinLight, HairColour = Sw.Grey, Hair = Hair.Receding, Shoes = Sw.Black, Soles = Sw.Black, Glasses = true },
            // 5: a woman in yellow with a bob, a phone
            new Look { Female = true, Top = Sw.Yellow, Legs = Sw.Denim, Skin = Sw.SkinLight, HairColour = Sw.HairBrown, Hair = Hair.Bob, Shoes = Sw.White, Soles = Sw.White, Jacket = None, Phone = true },
            // 6: a man in an orange T-shirt with a backpack
            new Look { Top = Sw.Orange, Legs = Sw.DarkGrey, Skin = Sw.SkinDark, HairColour = Sw.HairBlack, Hair = Hair.Crop, Shoes = Sw.Black, Soles = Sw.White, Jacket = None, Carry = Carry.Backpack, Bag = Sw.Black },
            // 7: a man in a grey sweater, a phone
            new Look { Top = Sw.Grey, LongSleeves = true, Legs = Sw.Black, Skin = Sw.SkinMid, HairColour = Sw.HairBlack, Hair = Hair.Short, Shoes = Sw.White, Soles = Sw.White, Jacket = None, Phone = true },
            // 8: a woman in pink and a beige skirt, a bun, a shoulder bag
            new Look { Female = true, Top = Sw.Pink, Legs = Sw.Beige, Skirt = true, Skin = Sw.SkinLight, HairColour = Sw.HairBlack, Hair = Hair.Bun, Shoes = Sw.Black, Soles = Sw.Black, Jacket = None, Carry = Carry.ShoulderBag, Bag = Sw.Black },
            // 9: a woman in an office suit (navy blazer, white blouse), long hair, a phone
            new Look { Female = true, Top = Sw.White, Jacket = Sw.Navy, Legs = Sw.Black, Skin = Sw.SkinMid, HairColour = Sw.HairBlack, Hair = Hair.Long, Shoes = Sw.Black, Soles = Sw.Black, Phone = true },
            // 10: a man in a burgundy shirt and jeans
            new Look { Top = Sw.Burgundy, Legs = Sw.Denim, Skin = Sw.SkinLight, HairColour = Sw.HairBrown, Hair = Hair.Short, Shoes = Sw.Black, Soles = Sw.DarkGrey, Jacket = None },
            // 11: an elderly woman: white blouse, burgundy cardigan, grey bun, glasses
            new Look { Female = true, Top = Sw.White, Jacket = Sw.Burgundy, Legs = Sw.Grey, Skin = Sw.SkinLight, HairColour = Sw.Grey, Hair = Hair.Bun, Shoes = Sw.Black, Soles = Sw.Black, Glasses = true },
        };

        // The driver's uniform: light blue shirt, navy trousers.
        static readonly Look s_Uniform = new Look { Top = Sw.ShirtBlue, Legs = Sw.Navy, Skin = Sw.SkinMid, HairColour = Sw.HairBlack, Hair = Hair.Short, Shoes = Sw.Black, Soles = Sw.Black, Jacket = None };

        // Where each part's figure stands on the test board (part space is relative to it: the cab system ignores this).
        static float3 BoardSpot(int part) => new float3((part % 12) * 0.8f, 0f, -(part / 12) * 1.2f);

        public override void Parts(KitPartList p)
        {
            for (int i = 0; i < PartCount; i++)
            {
                string name = i < HoldingFirst ? "Seated" + i : i < DownFirst ? "Holding" + (i - HoldingFirst) : i < Driver ? "Down" + (i - DownFirst) : "Driver";
                p.Add(i, name, -1, BoardSpot(i), quaternion.identity, PartFlags.Details);
            }
        }

        public override void Define(KitModelBuilder b, int lod)
        {
            for (int i = 0; i < PartCount; i++)
            {
                if (!b.Building(i)) continue;
                MeshKit k = b.Part(i);
                using (k.At(BoardSpot(i)))
                {
                    if (i < HoldingFirst) Seated(k, s_Looks[i], false, lod);
                    else if (i < DownFirst) Standing(k, s_Looks[i - HoldingFirst], true, lod);
                    else if (i < Driver) Standing(k, s_Looks[i - DownFirst], false, lod);
                    else Seated(k, s_Uniform, true, lod);
                }
            }
        }

        // ---------------------------------------------------------------- poses

        // Joint positions of a figure in its frame (z forward, y up, x right); the palms: the hands' thickness direction (from the
        // back of the hand to the palm).
        struct Joints
        {
            public float3 Pelvis, Chest, Neck, Head;
            public float3 HipL, KneeL, AnkleL, HipR, KneeR, AnkleR;
            public float3 ShoulderL, ElbowL, HandL, ShoulderR, ElbowR, HandR;
            public float3 PalmL, PalmR;
        }

        void Standing(MeshKit k, Look look, bool holding, int lod)
        {
            float sh = look.Female ? 0.175f : 0.19f;
            var j = new Joints
            {
                Pelvis = new float3(0f, 0.93f, 0f), Chest = new float3(0f, 1.30f, 0.01f), Neck = new float3(0f, 1.47f, 0.01f), Head = new float3(0f, 1.60f, 0.02f),
                HipL = new float3(-0.095f, 0.90f, 0f), KneeL = new float3(-0.10f, 0.49f, 0.02f), AnkleL = new float3(-0.11f, 0.08f, -0.01f),
                HipR = new float3(0.095f, 0.90f, 0f), KneeR = new float3(0.10f, 0.49f, 0.02f), AnkleR = new float3(0.11f, 0.08f, -0.01f),
                ShoulderL = new float3(-sh, 1.40f, 0f), ElbowL = new float3(-sh - 0.04f, 1.12f, 0.02f), HandL = new float3(-sh - 0.035f, 0.87f, 0.05f),
                ShoulderR = new float3(sh, 1.40f, 0f), PalmL = new float3(1f, 0f, 0f), PalmR = new float3(-1f, 0f, 0f),
            };
            if (holding)
            {
                j.ElbowR = new float3(sh + 0.03f, 1.60f, 0.03f);
                j.HandR = HoldingHand;
                j.PalmR = new float3(0f, 0f, 1f);
            }
            else
            {
                j.ElbowR = new float3(sh + 0.04f, 1.12f, 0.0f);
                j.HandR = new float3(sh + 0.035f, 0.87f, 0.04f);
            }
            Body(k, j, look, false, lod);
            Carried(k, j, look, false, lod);
            if (!holding && look.Carry == Carry.None && lod == 0)
                using (k.Paint(Sw.Black)) k.BevelBox(j.HandR + new float3(0.03f, -0.19f, 0f), new float3(0.08f, 0.28f, 0.34f), 0.02f);   // a bag in the hand
        }

        void Seated(MeshKit k, Look look, bool driver, int lod)
        {
            float sh = look.Female ? 0.175f : 0.19f;
            var j = new Joints
            {
                Pelvis = new float3(0f, 0.10f, -0.08f), Chest = new float3(0f, 0.44f, -0.12f), Neck = new float3(0f, 0.61f, -0.11f), Head = new float3(0f, 0.74f, -0.09f),
                HipL = new float3(-0.095f, 0.08f, -0.05f), KneeL = new float3(-0.11f, 0.10f, 0.37f), AnkleL = new float3(-0.12f, -0.39f, 0.43f),
                HipR = new float3(0.095f, 0.08f, -0.05f), KneeR = new float3(0.11f, 0.10f, 0.37f), AnkleR = new float3(0.12f, -0.39f, 0.43f),
                ShoulderL = new float3(-sh, 0.54f, -0.12f), ElbowL = new float3(-sh - 0.02f, 0.30f, -0.04f), HandL = new float3(-0.11f, 0.19f, 0.17f),
                ShoulderR = new float3(sh, 0.54f, -0.12f), ElbowR = new float3(sh + 0.02f, 0.30f, -0.04f), HandR = new float3(0.11f, 0.19f, 0.17f),
                PalmL = new float3(0f, -1f, 0f), PalmR = new float3(0f, -1f, 0f),
            };
            if (look.Phone && !driver)
            {
                // both hands up in front of the chest, the screen towards the face
                j.ElbowR = new float3(0.20f, 0.30f, -0.02f); j.HandR = new float3(0.06f, 0.41f, 0.16f);
                j.ElbowL = new float3(-0.20f, 0.30f, -0.02f); j.HandL = new float3(-0.04f, 0.40f, 0.16f);
                j.PalmL = j.PalmR = math.normalize(new float3(0f, 0.6f, -0.8f));
            }
            else if (look.Carry != Carry.None && !driver)
            {
                // hands on the bag on the lap
                float top = look.Carry == Carry.Backpack ? 0.50f : 0.34f;
                j.HandL = new float3(-0.09f, top + 0.01f, 0.21f); j.HandR = new float3(0.09f, top + 0.01f, 0.21f);
                j.ElbowL = new float3(-sh - 0.03f, 0.33f, 0.0f); j.ElbowR = new float3(sh + 0.03f, 0.33f, 0.0f);
            }
            if (driver)
            {
                // hands on the wheel (its hub 0.24 m up and 0.42 m ahead of the cushion point), feet on the pedals
                j.ElbowL = new float3(-0.24f, 0.36f, 0.10f); j.HandL = DriverHandL;
                j.ElbowR = new float3(0.24f, 0.36f, 0.10f); j.HandR = DriverHandR;
                j.PalmL = j.PalmR = new float3(0f, 0f, 1f);
                j.KneeL = new float3(-0.12f, 0.08f, 0.40f); j.AnkleL = new float3(-0.12f, -0.44f, 0.55f);
                j.KneeR = new float3(0.12f, 0.08f, 0.40f); j.AnkleR = new float3(0.13f, -0.44f, 0.55f);
                if (m_DriverFeet.HasValue)
                {
                    DriverFootPose feet=m_DriverFeet.Value;
                    j.KneeL=feet.LeftKnee; j.AnkleL=feet.LeftAnkle;
                    j.KneeR=feet.RightKnee; j.AnkleR=feet.RightAnkle;
                }
            }
            Body(k, j, look, true, lod);
            if (!driver)
                Carried(k, j, look, true, lod);
            if (look.Phone && !driver && lod == 0)
                using (k.Paint(Sw.Black)) k.BevelBox(new float3(0.01f, 0.43f, 0.17f), new float3(0.075f, 0.15f, 0.01f), quaternion.RotateX(math.radians(-37f)), 0.004f);
        }

        // A backpack (on the back standing, on the lap seated) or a shoulder bag (its strap across the chest, the bag on the hip
        // standing, on the lap seated).
        void Carried(MeshKit k, in Joints j, in Look look, bool seated, int lod)
        {
            if (look.Carry == Carry.None)
                return;
            float depth = (look.Female ? 0.16f : 0.175f) * kTorsoDepth;     // the chest's half depth
            using (k.Paint(look.Bag))
            {
                if (look.Carry == Carry.Backpack)
                {
                    if (seated)
                        k.BevelBox(new float3(0f, 0.32f, 0.22f), new float3(0.30f, 0.36f, 0.14f), 0.03f);
                    else
                    {
                        k.BevelBox(new float3(0f, j.Chest.y - 0.02f, j.Chest.z - depth - 0.08f), new float3(0.30f, 0.40f, 0.15f), 0.03f);
                        if (lod == 0)
                            for (int s = -1; s <= 1; s += 2)
                            {
                                // the shoulder straps: over the shoulder and down the chest
                                float3 top = new float3(s * 0.10f, j.ShoulderR.y + 0.055f, j.Chest.z - 0.02f);
                                float3 front = new float3(s * 0.11f, j.Chest.y + 0.02f, j.Chest.z + depth + 0.012f);
                                float3 low = new float3(s * 0.13f, j.Chest.y - 0.14f, j.Chest.z + depth - 0.005f);
                                k.BoxAlong(top, front, new float2(0.045f, 0.012f), new float3(0f, 0f, 1f));
                                k.BoxAlong(front, low, new float2(0.045f, 0.012f), new float3(0f, 0f, 1f));
                            }
                    }
                }
                else if (seated)
                    k.BevelBox(new float3(0f, 0.26f, 0.21f), new float3(0.28f, 0.16f, 0.09f), 0.02f);
                else
                {
                    k.BevelBox(new float3(-0.21f, j.Pelvis.y + 0.02f, j.Pelvis.z - 0.12f), new float3(0.08f, 0.22f, 0.27f), 0.02f);
                    if (lod == 0)
                    {
                        // the strap from the right shoulder across the chest to the bag on the left hip
                        float3 a = new float3(0.11f, j.ShoulderR.y + 0.05f, j.Chest.z - 0.01f);
                        float3 m = new float3(0.0f, j.Chest.y - 0.02f, j.Chest.z + depth + 0.012f);
                        float3 c = new float3(-0.17f, j.Pelvis.y + 0.10f, j.Pelvis.z + 0.03f);
                        k.BoxAlong(a, m, new float2(0.03f, 0.01f), new float3(0f, 0f, 1f));
                        k.BoxAlong(m, c, new float2(0.03f, 0.01f), new float3(0f, 0f, 1f));
                    }
                }
            }
        }

        // ---------------------------------------------------------------- the body

        const float kTorsoDepth = 0.62f;       // the torso's depth over its width

        // The body from its joints: legs and shoes, hips and torso, arms and hands, neck and head.
        void Body(MeshKit k, in Joints j, in Look look, bool seated, int lod)
        {
            int limb = k.Sides(8), round = k.Sides(14);
            Legs(k, j, look, seated, limb);
            Torso(k, j, look, round);
            Arms(k, j, look, limb, lod);
            using (k.Paint(look.Skin))
            {
                float3 up = math.normalize(j.Head - j.Neck);
                k.Cylinder(j.Neck - up * 0.05f, j.Neck + up * 0.075f, 0.05f, limb);
            }
            Head(k, j.Head + new float3(0f, -0.115f, 0f), look, lod);
        }

        void Legs(MeshKit k, in Joints j, in Look look, bool seated, int limb)
        {
            // trousers, or a skirt to the knees and bare shins
            using (k.Paint(look.Legs))
            {
                if (!look.Skirt || seated)
                {
                    float r = look.Skirt ? 0.088f : 0.079f;
                    Limb(k, j.HipL, j.KneeL, r, r * 0.8f, limb);
                    Limb(k, j.HipR, j.KneeR, r, r * 0.8f, limb);
                    if (look.Skirt)
                        k.BoxAlong(0.5f * (j.HipL + j.HipR) + new float3(0f, 0.01f, 0f), 0.5f * (j.KneeL + j.KneeR) + new float3(0f, 0.01f, 0.03f), new float2(0.30f, 0.13f), new float3(0f, 1f, 0f), true);
                }
                else
                {
                    using (k.At(new float3(0f, j.Pelvis.y + 0.02f, j.Pelvis.z), quaternion.identity, new float3(1f, 1f, 0.72f)))
                        k.Lathe(new[] { new LP(0.215f, -0.42f), new LP(0.19f, -0.2f), new LP(0.172f, 0f) }, k.Sides(14), caps: Caps.A);
                }
            }
            using (k.Paint(look.Skirt ? look.Skin : look.Legs))
            {
                Ball(k, j.KneeL, 0.062f, limb);
                Ball(k, j.KneeR, 0.062f, limb);
                Limb(k, j.KneeL, j.AnkleL, 0.06f, 0.044f, limb);
                Limb(k, j.KneeR, j.AnkleR, 0.06f, 0.044f, limb);
            }
            for (int s = 0; s < 2; s++)
            {
                if (m_DriverFeet.HasValue)
                {
                    DriverFootFrame foot=s==0 ? m_DriverFeet.Value.Left : m_DriverFeet.Value.Right;
                    // An authored support point must retain its actual shoe at every LOD.
                    using (k.KeepSmallStructure())
                    {
                        using (k.Paint(look.Shoes))
                            k.BevelBox(foot.Origin+math.mul(foot.Rotation,new float3(0f,.051f,0f)),new float3(.10f,.075f,.255f),foot.Rotation,.03f);
                        using (k.Paint(look.Soles))
                            k.Box(foot.Origin+math.mul(foot.Rotation,new float3(0f,.008f,0f)),new float3(.104f,.016f,.262f),foot.Rotation);
                    }
                    continue;
                }
                float3 a = s == 0 ? j.AnkleL : j.AnkleR;
                using (k.Paint(look.Shoes))
                    k.BevelBox(new float3(a.x, a.y - 0.04f, a.z + 0.06f), new float3(0.10f, 0.075f, 0.255f), 0.03f);
                using (k.Paint(look.Soles))
                    k.Box(new float3(a.x, a.y - 0.083f, a.z + 0.06f), new float3(0.104f, 0.016f, 0.262f));
            }
        }

        // The torso: a lathe along the spine (pelvis to neck), flattened front to back; the hips in the trousers' colour, the top
        // over them, a jacket open at the front over that. Women narrower at the shoulders and waist, wider at the hips.
        void Torso(MeshKit k, in Joints j, in Look look, int sides)
        {
            float len = math.distance(j.Pelvis, j.Neck) / 0.54f;
            float hip = look.Female ? 0.178f : 0.165f, waist = look.Female ? 0.138f : 0.152f, chest = look.Female ? 0.16f : 0.175f, shoulder = look.Female ? 0.172f : 0.192f;
            using (Spine(k, j.Pelvis, j.Neck - j.Pelvis, new float3(1f, 1f, kTorsoDepth)))
            {
                using (k.Paint(look.Legs))
                    k.Lathe(new[] { new LP(hip * 0.72f, -0.10f * len), new LP(hip, -0.045f * len), new LP(hip * 0.99f, 0.05f * len), new LP(waist * 1.04f, 0.11f * len) }, sides, caps: Caps.A);
                LP[] top =
                {
                    new LP(waist * 1.08f, 0.07f * len), new LP(waist * 1.02f, 0.16f * len), new LP(chest, 0.29f * len), new LP(shoulder, 0.39f * len),
                    new LP(shoulder * 0.93f, 0.445f * len), new LP(shoulder * 0.62f, 0.49f * len), new LP(0.075f, 0.515f * len), new LP(0.058f, 0.53f * len),
                };
                using (k.Paint(look.Top))
                    k.Lathe(top, sides, caps: Caps.B);
                if (look.Jacket != None)
                {
                    // the jacket: a shell over the top from the hips, open at the front (the top shows between its fronts), a collar
                    var shell = new LP[top.Length];
                    shell[0] = new LP(hip * 1.06f, -0.03f * len);
                    for (int i = 1; i < top.Length - 1; i++) shell[i] = new LP(top[i].R + 0.012f, top[i].Y);
                    shell[top.Length - 1] = new LP(0.075f, 0.535f * len);
                    float open = 0.36f;
                    using (k.Paint(look.Jacket))
                    {
                        k.Lathe(shell, sides, 0.5f * math.PI + open, 2.5f * math.PI - open);
                        LatheInside(k, shell, sides, 0.5f * math.PI + open, 2.5f * math.PI - open, 0.004f);   // the lining
                    }
                }
            }
        }

        void Arms(MeshKit k, in Joints j, in Look look, int limb, int lod)
        {
            Sw sleeve = look.Jacket != None ? look.Jacket : look.Top;
            bool longSleeves = look.LongSleeves || look.Jacket != None;
            for (int s = 0; s < 2; s++)
            {
                float3 sh = s == 0 ? j.ShoulderL : j.ShoulderR, el = s == 0 ? j.ElbowL : j.ElbowR, hand = s == 0 ? j.HandL : j.HandR;
                float3 palm = s == 0 ? j.PalmL : j.PalmR;
                float3 d = math.normalize(hand - el);
                float3 wrist = hand - d * 0.035f;
                using (k.Paint(sleeve))
                {
                    Ball(k, sh + new float3(sh.x < 0f ? 0.008f : -0.008f, -0.012f, 0f), 0.057f, limb);
                    if (longSleeves)
                    {
                        Limb(k, sh, el, 0.058f, 0.049f, limb);
                        Ball(k, el, 0.048f, limb);
                        Limb(k, el, wrist, 0.046f, 0.038f, limb);
                    }
                    else
                        Limb(k, sh, math.lerp(sh, el, 0.5f), 0.062f, 0.056f, limb);     // a short sleeve
                }
                using (k.Paint(look.Skin))
                {
                    if (!longSleeves)
                    {
                        Limb(k, math.lerp(sh, el, 0.3f), el, 0.05f, 0.044f, limb);
                        Ball(k, el, 0.044f, limb);
                        Limb(k, el, wrist, 0.042f, 0.032f, limb);
                    }
                    Hand(k, wrist, d, palm, lod);
                }
            }
        }

        // A hand from the wrist along the forearm: a mitten (palm and closed fingers) and the thumb on the inner side.
        static void Hand(MeshKit k, float3 wrist, float3 d, float3 palm, int lod)
        {
            float3 u = math.normalizesafe(palm - d * math.dot(palm, d), new float3(0f, 1f, 0f));
            k.BoxAlong(wrist - d * 0.005f, wrist + d * 0.095f, new float2(0.078f, 0.032f), u, true);
            if (lod > 0)
                return;
            // the thumb on the side towards the body's middle
            float3 body = new float3(wrist.x < 0f ? 1f : -1f, 0f, 0f);
            float3 side = body - d * math.dot(body, d) - u * math.dot(body, u);
            side = math.lengthsq(side) > 1e-4f ? math.normalize(side) : math.normalize(math.cross(u, d));
            float3 t0 = wrist + d * 0.02f + side * 0.034f + u * 0.008f;
            k.Frustum(t0, t0 + d * 0.045f + side * 0.012f + u * 0.018f, 0.014f, 0.011f, 6, Caps.Both);
        }

        // ---------------------------------------------------------------- the head

        // The skull in the head frame (origin at the chin's height under the skull's centre, +Z the face), a little deeper than wide.
        static readonly LP[] s_Skull = { new LP(0.05f, 0.05f), new LP(0.08f, 0.085f), new LP(0.093f, 0.13f), new LP(0.09f, 0.175f), new LP(0.072f, 0.215f), new LP(0.04f, 0.236f), new LP(0f, 0.243f) };
        const float kHeadX = 0.9f, kHeadZ = 1.04f;
        // The jaw and cheeks: a smaller lathe a little forward, flattened.
        static readonly LP[] s_Jaw = { new LP(0f, 0f), new LP(0.03f, 0.004f), new LP(0.055f, 0.025f), new LP(0.07f, 0.06f), new LP(0.08f, 0.105f) };
        const float kJawX = 0.86f, kJawZ = 0.9f, kJawForward = 0.018f;

        static float Radius(LP[] p, float y)
        {
            if (y <= p[0].Y) return p[0].R;
            for (int i = 1; i < p.Length; i++)
                if (y <= p[i].Y) return math.lerp(p[i - 1].R, p[i].R, (y - p[i - 1].Y) / math.max(1e-5f, p[i].Y - p[i - 1].Y));
            return p[p.Length - 1].R;
        }

        // The face's surface (z) at (x, y) of the head frame: the skull, or the jaw where it stands out.
        static float FaceZ(float x, float y)
        {
            float r = Radius(s_Skull, y), xs = x / kHeadX;
            float skull = kHeadZ * math.sqrt(math.max(0f, r * r - xs * xs));
            if (y > s_Jaw[s_Jaw.Length - 1].Y)
                return skull;
            float rj = Radius(s_Jaw, y), xj = x / kJawX;
            return math.max(skull, kJawForward + kJawZ * math.sqrt(math.max(0f, rj * rj - xj * xj)));
        }

        void Head(MeshKit k, float3 at, in Look look, int lod)
        {
            int sides = k.Sides(16);
            using (k.At(at))
            {
                using (k.Paint(look.Skin))
                {
                    using (k.At(float3.zero, quaternion.identity, new float3(kHeadX, 1f, kHeadZ)))
                        k.Lathe(s_Skull, sides);
                    using (k.At(new float3(0f, 0f, kJawForward), quaternion.identity, new float3(kJawX, 1f, kJawZ)))
                        k.Lathe(s_Jaw, sides);
                    if (lod == 0)
                    {
                        // the nose, the ears
                        k.Frustum(new float3(0f, 0.128f, FaceZ(0f, 0.128f) - 0.012f), new float3(0f, 0.098f, FaceZ(0f, 0.098f) + 0.017f), 0.011f, 0.016f, 6, Caps.B);
                        for (int s = -1; s <= 1; s += 2)
                            k.BevelBox(new float3(s * (Radius(s_Skull, 0.115f) * kHeadX + 0.004f), 0.115f, -0.008f), new float3(0.016f, 0.056f, 0.034f), 0.007f);
                    }
                }
                if (lod == 0)
                {
                    for (int s = -1; s <= 1; s += 2)
                    {
                        float x = s * 0.031f;
                        float3 eye = new float3(x, 0.134f, FaceZ(x, 0.134f) + 0.001f);
                        using (k.Paint(Sw.HairBlack))
                            k.Disc(eye, new float3(x * 0.8f, 0f, 1f), 0.0095f, 8);
                        using (k.Paint(look.HairColour))
                            k.BoxAlong(new float3(s * 0.013f, 0.156f, FaceZ(s * 0.013f, 0.156f) + 0.002f), new float3(s * 0.05f, 0.152f, FaceZ(s * 0.05f, 0.152f) + 0.001f), new float2(0.006f, 0.008f), new float3(0f, 1f, 0f));
                    }
                    using (k.Paint(Sw.Lip))
                        k.BoxAlong(new float3(-0.017f, 0.07f, FaceZ(-0.017f, 0.07f) + 0.001f), new float3(0.017f, 0.07f, FaceZ(0.017f, 0.07f) + 0.001f), new float2(0.004f, 0.008f), new float3(0f, 1f, 0f));
                    if (look.Glasses)
                        Glasses(k);
                }
                HairOn(k, look, sides);
            }
        }

        static void Glasses(MeshKit k)
        {
            using (k.Paint(Sw.Black))
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    float x = s * 0.032f, z = FaceZ(x, 0.134f) + 0.012f;
                    k.Tube(new float3(x, 0.134f, z), new float3(x, 0.134f, z + 0.004f), 0.02f, 0.017f, 10, Caps.Both);
                    // the temple from the rim back to the ear
                    float ex = s * (Radius(s_Skull, 0.14f) * kHeadX + 0.003f);
                    k.BoxAlong(new float3(s * 0.052f, 0.138f, z - 0.002f), new float3(ex, 0.135f, -0.01f), new float2(0.004f, 0.005f), new float3(0f, 1f, 0f));
                }
                float zb = FaceZ(0f, 0.138f) + 0.012f;
                k.BoxAlong(new float3(-0.012f, 0.138f, zb), new float3(0.012f, 0.138f, zb), new float2(0.004f, 0.004f), new float3(0f, 1f, 0f));
            }
        }

        // The hair over the skull: a cap over the top down to the hairline at the forehead, and the back and sides down to the nape
        // (short), the shoulders (long) or the chin (bob), leaving the face open; a bun or a ponytail at the back; or only a band round
        // the back (receding).
        void HairOn(MeshKit k, in Look look, int sides)
        {
            float t = look.Hair == Hair.Crop ? 0.005f : look.Hair == Hair.Long || look.Hair == Hair.Bob ? 0.013f : 0.009f;
            float line = look.Female ? 0.168f : 0.178f;                        // the hairline at the forehead
            using (k.Paint(look.HairColour))
            using (k.At(float3.zero, quaternion.identity, new float3(kHeadX, 1f, kHeadZ)))
            {
                if (look.Hair == Hair.Receding)
                {
                    // grey above the ears and low at the back, the crown bare
                    Hairpiece(k, Shell(0.145f, 0.178f, 0.006f), sides, 0.5f * math.PI + 1.2f, 2.5f * math.PI - 1.2f);
                    Hairpiece(k, Shell(0.07f, 0.15f, 0.006f), sides, 1.5f * math.PI - 1.0f, 1.5f * math.PI + 1.0f);
                    return;
                }

                Hairpiece(k, Shell(line, s_Skull[s_Skull.Length - 1].Y, t), sides, 0f, 2f * math.PI);   // the cap
                float face = look.Hair == Hair.Long || look.Hair == Hair.Bob ? 1.12f : 0.95f;   // half the face opening (radians)
                float a0 = 0.5f * math.PI + face, a1 = 2.5f * math.PI - face;
                switch (look.Hair)
                {
                    case Hair.Long:
                    case Hair.Bob:
                    {
                        float end = look.Hair == Hair.Long ? -0.11f : 0.02f, flare = look.Hair == Hair.Long ? 0.0f : 0.012f;
                        var p = new[]
                        {
                            new LP(0.093f + t + flare, end), new LP(0.093f + t + flare * 0.5f, (end + 0.1f) * 0.5f), new LP(Radius(s_Skull, 0.1f) + t, 0.1f),
                            new LP(Radius(s_Skull, 0.13f) + t, 0.13f), new LP(Radius(s_Skull, line + 0.01f) + t, line + 0.01f),
                        };
                        Hairpiece(k, p, sides, a0, a1);
                        // behind the neck, so the inside of the hanging hair never shows through
                        k.BevelBox(new float3(0f, 0.5f * (end + 0.08f), -0.045f), new float3(0.15f, 0.08f - end, 0.07f), 0.01f);
                        // a fringe low over the forehead, above the brows
                        Hairpiece(k, Shell(line - 0.009f, line + 0.012f, t), sides, 0.5f * math.PI - face, 0.5f * math.PI + face);
                        break;
                    }
                    default:
                        // above the ears at the sides, down to the nape at the back
                        Hairpiece(k, Shell(0.145f, line + 0.01f, t), sides, a0, a1);
                        Hairpiece(k, Shell(look.Female ? 0.075f : 0.088f, 0.15f, t), sides, 1.5f * math.PI - 1.0f, 1.5f * math.PI + 1.0f);
                        break;
                }
            }
            if (look.Hair == Hair.Bun)
                using (k.Paint(look.HairColour))
                using (k.At(new float3(0f, 0.205f, -0.1f)))
                    Ball(k, float3.zero, 0.042f, sides);
            else if (look.Hair == Hair.Ponytail)
                using (k.Paint(look.HairColour))
                    k.Frustum(new float3(0f, 0.17f, -0.095f), new float3(0f, 0.0f, -0.135f), 0.032f, 0.012f, k.Sides(8), Caps.Both);
        }

        // A piece of hair: the shell and its inside (its edges stand off the skull); a whole ring (the cap) only its lower edge.
        static void Hairpiece(MeshKit k, LP[] p, int sides, float a0, float a1)
        {
            k.Lathe(p, sides, a0, a1);
            LatheInside(k, p, sides, a0, a1, 0.004f, a1 - a0 < 2f * math.PI - 1e-3f);
        }

        // The skull's profile from y0 to y1, t further out (a shell of hair).
        static LP[] Shell(float y0, float y1, float t)
        {
            var list = new System.Collections.Generic.List<LP> { new LP(Radius(s_Skull, y0) + t, y0) };
            foreach (LP p in s_Skull)
                if (p.Y > y0 && p.Y < y1) list.Add(new LP(p.R + (p.R > 0f ? t : 0f), p.Y + (p.R > 0f ? 0f : t)));
            float r1 = Radius(s_Skull, y1);
            list.Add(new LP(r1 > 1e-4f ? r1 + t : 0f, y1 + (r1 > 1e-4f ? 0f : t)));
            return list.ToArray();
        }

        // ---------------------------------------------------------------- helpers

        static void Limb(MeshKit k, float3 a, float3 b, float ra, float rb, int sides) => k.Frustum(a, b, ra, rb, sides, Caps.Both);

        // The inside of a partial lathe (MeshKit.Lathe draws only the outside): the same rings `inset` further in, facing inwards,
        // and the open edges between the two closed (the sides at a0 and a1, the first and the last ring).
        static void LatheInside(MeshKit k, LP[] p, int sides, float a0, float a1, float inset, bool inside = true)
        {
            float3 In(float a) => new float3(-math.cos(a), 0f, -math.sin(a));
            float3 P(LP q, float a, float d) => new float3(math.max(0f, q.R - d) * math.cos(a), q.Y, math.max(0f, q.R - d) * math.sin(a));
            for (int i = 0; i + 1 < p.Length && inside; i++)
            {
                for (int s = 0; s < sides; s++)
                {
                    float b0 = math.lerp(a0, a1, s / (float)sides), b1 = math.lerp(a0, a1, (s + 1) / (float)sides), bm = 0.5f * (b0 + b1);
                    int v0 = k.V(P(p[i], b0, inset), In(b0)), v1 = k.V(P(p[i], b1, inset), In(b1)), v2 = k.V(P(p[i + 1], b1, inset), In(b1)), v3 = k.V(P(p[i + 1], b0, inset), In(b0));
                    k.Quad(v0, v1, v2, v3, In(bm));
                }
                // the side edges: outside to inside at a0 (facing the opening, towards smaller angles) and at a1
                float3 n0 = new float3(math.sin(a0), 0f, -math.cos(a0)), n1 = new float3(-math.sin(a1), 0f, math.cos(a1));
                k.QuadP(P(p[i], a0, 0f), P(p[i + 1], a0, 0f), P(p[i + 1], a0, inset), P(p[i], a0, inset), n0);
                k.QuadP(P(p[i], a1, 0f), P(p[i + 1], a1, 0f), P(p[i + 1], a1, inset), P(p[i], a1, inset), n1);
            }
            // the first and the last ring's edges, facing away from the shell
            for (int e = 0; e < 2; e++)
            {
                LP q = e == 0 ? p[0] : p[p.Length - 1], next = e == 0 ? p[1] : p[p.Length - 2];
                if (q.R < 1e-4f) continue;
                float dy = q.Y < next.Y ? -1f : 1f;
                for (int s = 0; s < sides; s++)
                {
                    float b0 = math.lerp(a0, a1, s / (float)sides), b1 = math.lerp(a0, a1, (s + 1) / (float)sides);
                    k.QuadP(P(q, b0, 0f), P(q, b1, 0f), P(q, b1, inset), P(q, b0, inset), new float3(0f, dy, 0f));
                }
            }
        }

        static void Ball(MeshKit k, float3 c, float r, int sides)
        {
            using (k.At(c))
                k.Lathe(new[] { new LP(0f, -r), new LP(r * 0.71f, -r * 0.71f), new LP(r, 0f), new LP(r * 0.71f, r * 0.71f), new LP(0f, r) }, sides);
        }

        // A frame along the spine: +Y from the origin along `up`, +Z the figure's front, scaled.
        static MeshKit.Scope Spine(MeshKit k, float3 origin, float3 up, float3 scale)
        {
            float3 u = math.normalize(up);
            float3 f = math.normalize(new float3(0f, 0f, 1f) - u * u.z);
            float3 r = math.cross(u, f);
            return k.Push(math.mul(new float4x4(new float3x3(r, u, f), origin), float4x4.Scale(scale)));
        }
    }
}
