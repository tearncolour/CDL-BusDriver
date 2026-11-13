using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Game.Rendering;
using Unity.Mathematics;
using CAnimationClip = Colossal.Animations.AnimationClip;
using CAnimationType = Colossal.Animations.AnimationType;
using CAnimationLayer = Colossal.Animations.AnimationLayer;
using ElementRaw = Colossal.Animations.Animation.ElementRaw;

namespace CDLC.BusDriver.Interior
{
    /// <summary>
    /// The game's citizen skeleton (a CharacterStyle: 147 bones for the adults) posed on the CPU from the game's own clips, for
    /// drawing a citizen's meshes through the game's GPU skinning with a bone buffer of our own. The rest pose is the style's clip
    /// of type RestPose without a target (the meshes are modelled in it); a body clip is additive: a bone's local position is the
    /// rest's plus the clip's, its rotation the rest's times the clip's (as the game's RenderPrefabRenderer applies it). The global
    /// transform of a bone is its parents' times its local one, and the skin matrix the posed global times the inverse of the rest
    /// global (as the game's ConvertLocalCoordinates kernel writes boneBuffer). Body shapes are not blended: shape slot 0.
    /// </summary>
    internal sealed class CitizenSkeleton
    {
        internal readonly int Count;
        internal readonly int[] Parent;
        private readonly float3[] m_RestPos;
        private readonly quaternion[] m_RestRot;
        private readonly float4x4[] m_RestGlobal, m_RestInverse;

        private CitizenSkeleton(CAnimationClip rest)
        {
            Count = rest.boneCount;
            Parent = rest.m_BoneHierarchy;
            int[] inv = rest.GetInverseBoneIndices();
            m_RestPos = new float3[Count];
            m_RestRot = new quaternion[Count];
            for (int b = 0; b < Count; b++)
            {
                ElementRaw e = inv[b] >= 0 ? rest.m_Animation.GetBoneSample(inv[b], 0, 0) : ElementRaw.Identity;
                m_RestPos[b] = e.position;
                m_RestRot[b] = math.normalizesafe(new quaternion(e.rotation), quaternion.identity);
            }
            m_RestGlobal = Globals(m_RestPos, m_RestRot);
            m_RestInverse = new float4x4[Count];
            for (int b = 0; b < Count; b++)
                m_RestInverse[b] = math.inverse(m_RestGlobal[b]);
        }

        /// <summary>The style's skeleton from its rest pose clip; null without one.</summary>
        internal static CitizenSkeleton Load(CharacterStyle style)
        {
            int i = Find(style, info => info.type == CAnimationType.RestPose && info.target == null);
            CAnimationClip clip = i >= 0 ? style.GetAnimation(i)?.Load() : null;
            return clip != null && clip.m_BoneHierarchy != null && clip.boneCount > 0 ? new CitizenSkeleton(clip) : null;
        }

        /// <summary>The style's additive body clip for an activity (BenchSitting, Standing ...); null when it has none.</summary>
        internal static CAnimationClip LoadClip(CharacterStyle style, ActivityType activity, out string name)
        {
            int i = Find(style, info => info.activity == activity && info.layer == CAnimationLayer.BodyLayer && info.type == CAnimationType.Additive && info.target == null);
            name = i >= 0 ? style.m_Animations[i].name : null;
            return i >= 0 ? style.GetAnimation(i)?.Load() : null;
        }

        /// <summary>The style's body clip by its name (Bench_Sitting, Idle ...); null when it has none.</summary>
        internal static CAnimationClip LoadClip(CharacterStyle style, string name)
        {
            int i = Find(style, info => info.name == name && info.layer == CAnimationLayer.BodyLayer && info.target == null);
            return i >= 0 ? style.GetAnimation(i)?.Load() : null;
        }

        private static int Find(CharacterStyle style, System.Func<CharacterStyle.AnimationInfo, bool> match)
        {
            if (style?.m_Animations == null)
                return -1;
            for (int i = 0; i < style.m_Animations.Length; i++)
                if (style.m_Animations[i] != null && match(style.m_Animations[i]))
                    return i;
            return -1;
        }

        /// <summary>The local transforms of a pose: the rest pose with the clip's frame added (no clip: the rest pose).</summary>
        internal void Local(CAnimationClip clip, int frame, float3[] pos, quaternion[] rot)
        {
            int[] inv = clip?.GetInverseBoneIndices();
            int frames = clip != null ? math.max(1, clip.m_Animation.frameCount) : 1;
            frame = math.clamp(frame, 0, frames - 1);
            for (int b = 0; b < Count; b++)
            {
                pos[b] = m_RestPos[b];
                rot[b] = m_RestRot[b];
                if (inv == null || b >= inv.Length || inv[b] < 0)
                    continue;
                ElementRaw d = clip.m_Animation.GetBoneSample(inv[b], 0, frame);
                pos[b] += d.position;
                rot[b] = math.normalizesafe(math.mul(rot[b], new quaternion(d.rotation)), rot[b]);
            }
        }

        /// <summary>The global (model space) transforms from local ones: each bone's parents' times its own.</summary>
        internal float4x4[] Globals(float3[] pos, quaternion[] rot)
        {
            var g = new float4x4[Count];
            var done = new bool[Count];
            for (int b = 0; b < Count; b++)
                Global(b, pos, rot, g, done);
            return g;
        }

        private void Global(int b, float3[] pos, quaternion[] rot, float4x4[] g, bool[] done)
        {
            if (done[b])
                return;
            float4x4 local = float4x4.TRS(pos[b], rot[b], 1f);
            int p = Parent[b];
            if (p >= 0 && p < Count && p != b)
            {
                Global(p, pos, rot, g, done);
                g[b] = math.mul(g[p], local);
            }
            else
                g[b] = local;
            done[b] = true;
        }

        /// <summary>The skin matrices for a posed skeleton (its global transforms) into a bone buffer's elements.</summary>
        internal void Skin(float4x4[] global, BoneElement[] into, int offset = 0)
        {
            for (int b = 0; b < Count; b++)
                into[offset + b].m_Matrix = math.mul(global[b], m_RestInverse[b]);
        }

        // The arms' chains (the adults' 147-bone skeleton): upper arm, forearm, hand; the right arm at +X.
        internal const int kLeftUpperArm = 90, kRightUpperArm = 114, kPelvis = 1;

        /// <summary>
        /// Turns an arm of a pose (its local transforms, changed in place) so the wrist reaches a target in model space, the
        /// elbow towards the pole (TwoBoneIk). False when the skeleton has no such chain (another rig).
        /// </summary>
        internal bool ReachArm(float3[] pos, quaternion[] rot, int upper, float3 target, float3 pole)
        {
            int lower = upper + 1, hand = upper + 2;
            if (hand >= Count || Parent[lower] != upper || Parent[hand] != lower || Parent[upper] < 0)
                return false;
            float4x4[] g = Globals(pos, rot);
            quaternion ra = new quaternion(new float3x3(g[upper])), rb = new quaternion(new float3x3(g[lower]));
            quaternion rp = new quaternion(new float3x3(g[Parent[upper]]));
            TwoBoneIk.Solve(g[upper].c3.xyz, g[lower].c3.xyz, g[hand].c3.xyz, ra, rb, target, pole, out quaternion na, out quaternion nb, out _, out _);
            rot[upper] = math.normalize(math.mul(math.inverse(rp), na));
            rot[lower] = math.normalize(math.mul(math.inverse(na), nb));
            return true;
        }

        /// <summary>A bone's position in the rest pose (model space).</summary>
        internal float3 RestPosition(int b) => m_RestGlobal[b].c3.xyz;

        /// <summary>The bones in the rest pose: index, parent, model-space position (for finding the arms and legs).</summary>
        internal IEnumerable<(int bone, int parent, float3 at)> Bones()
        {
            for (int b = 0; b < Count; b++)
                yield return (b, Parent[b], RestPosition(b));
        }
    }
}
