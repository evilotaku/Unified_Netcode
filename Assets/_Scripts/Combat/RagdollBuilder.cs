using System.Collections.Generic;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Works out a ragdoll for any skinned model: which bones get a body part, the collider of each part, and its mass.
    /// Used by <see cref="ActiveRagdoll"/>.
    ///
    /// Each mesh vertex belongs to the bone that weighs it most. On a Humanoid rig, the parts are the main humanoid bones.
    /// On any other rig, a bone gets a part if it drives enough of the mesh, and the smallest leaf parts are merged into
    /// their parents until there are at most <see cref="Settings.MaxParts"/>. Bones without a part give their vertices to
    /// their nearest ancestor part, so hands are in the forearm part, for example. Each part's capsule is then fitted
    /// to its vertices: along their main axis, as wide as the body's surface around it.
    ///
    /// Vertex data needs the mesh's Read/Write option. Without it, parts are picked from the skeleton alone and sized
    /// from bone lengths, which is rougher.
    /// </summary>
    public static class RagdollBuilder
    {
        public struct Settings
        {
            public float TotalMass;
            public int MaxParts;
            public float MinPartVertexShare; // generic rigs: share of all vertices a bone needs to get its own part
            public float ColliderScale;      // shrinks fitted colliders so they sit inside the surface
        }

        public class PartSpec
        {
            public Transform Bone;
            public int Parent = -1;          // index of the parent part, -1 for the root (always index 0)
            public Vector3 Center;           // collider center, world space
            public Vector3 Axis;             // collider axis, world space, unit length
            public float Radius;
            public float Height;             // full capsule length including the caps, at least 2 * Radius
            public float Mass;
        }

        // The humanoid fast path: the bones that get parts on a Humanoid rig. Parents come before children.
        static readonly HumanBodyBones[] k_HumanoidBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Head, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        const int k_MinVerticesToFit = 8;
        const float k_MinMassShare = 0.01f;

        /// <summary>Builds the part list for the model under <paramref name="root"/>. Empty if it has no skeleton.</summary>
        public static List<PartSpec> Build(Transform root, Animator animator, Settings settings)
        {
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var modelHeight = MeasureHeight(renderers);

            var vertices = new Dictionary<Transform, List<Vector3>>();
            bool haveMeshData = CollectVertices(renderers, vertices, out int totalVertices);
            if (!haveMeshData && renderers.Length > 0)
                Debug.LogWarning($"[{nameof(ActiveRagdoll)}] {root.name}: enable Read/Write on the model's mesh so the ragdoll can fit its colliders to it. Using bone lengths instead.", root);

            var parts = PickPartBones(renderers, animator, vertices, totalVertices, haveMeshData, modelHeight, settings);
            if (parts.Count == 0) return new List<PartSpec>();

            // Root: the deepest bone above every part. Parts in separate hierarchies hang off the first part.
            var rootBone = parts[0];
            foreach (var bone in parts)
                while (rootBone != null && !bone.IsChildOf(rootBone)) rootBone = rootBone.parent;
            if (rootBone == null) rootBone = parts[0];
            parts.Remove(rootBone);
            parts.Sort((a, b) => Depth(a).CompareTo(Depth(b))); // parents before children
            parts.Insert(0, rootBone);

            var specs = new List<PartSpec>(parts.Count);
            var index = new Dictionary<Transform, int>();
            foreach (var bone in parts)
            {
                index[bone] = specs.Count;
                var parentBone = NearestPartAncestor(bone.parent, index);
                specs.Add(new PartSpec { Bone = bone, Parent = specs.Count == 0 ? -1 : parentBone != null ? index[parentBone] : 0 });
            }

            // Every vertex goes to the part of its bone, or of the nearest ancestor with a part, or to the root.
            var partVertices = new List<Vector3>[specs.Count];
            for (int i = 0; i < specs.Count; i++) partVertices[i] = new List<Vector3>();
            foreach (var (bone, list) in vertices)
            {
                var owner = NearestPartAncestor(bone, index);
                partVertices[owner != null ? index[owner] : 0].AddRange(list);
            }

            for (int i = 0; i < specs.Count; i++)
            {
                if (partVertices[i].Count >= k_MinVerticesToFit) FitCapsule(specs[i], partVertices[i], settings.ColliderScale);
                else FitFromSkeleton(specs[i], specs, modelHeight);
            }

            AssignMasses(specs, settings.TotalMass);
            return specs;
        }

        static List<Transform> PickPartBones(SkinnedMeshRenderer[] renderers, Animator animator, Dictionary<Transform, List<Vector3>> vertices,
            int totalVertices, bool haveMeshData, float modelHeight, Settings settings)
        {
            var parts = new List<Transform>();

            if (animator != null && animator.isHuman)
            {
                foreach (var humanBone in k_HumanoidBones)
                {
                    var bone = animator.GetBoneTransform(humanBone);
                    if (bone != null) parts.Add(bone);
                }
                if (parts.Count > 0) return parts;
            }

            if (haveMeshData)
            {
                // Bones that drive enough of the mesh.
                int minVertices = Mathf.Max(k_MinVerticesToFit, Mathf.CeilToInt(totalVertices * settings.MinPartVertexShare));
                var counts = new Dictionary<Transform, int>();
                Transform busiest = null;
                foreach (var (bone, list) in vertices)
                {
                    counts[bone] = list.Count;
                    if (list.Count >= minVertices) parts.Add(bone);
                    if (busiest == null || list.Count > counts[busiest]) busiest = bone;
                }
                if (parts.Count == 0 && busiest != null) parts.Add(busiest); // a tiny mesh: one part for the lot

                // Too many: merge the smallest leaf part into its nearest ancestor part, repeatedly.
                while (parts.Count > Mathf.Max(1, settings.MaxParts))
                {
                    Transform smallest = null;
                    int smallestCount = int.MaxValue;
                    foreach (var bone in parts)
                    {
                        if (HasPartDescendant(bone, parts)) continue;
                        int count = PartVertexCount(bone, parts, counts);
                        if (count < smallestCount) { smallest = bone; smallestCount = count; }
                    }
                    if (smallest == null) break;
                    parts.Remove(smallest);
                }
                return parts;
            }

            // No mesh data: every skinned bone long enough to matter, longest first.
            var bones = new HashSet<Transform>();
            foreach (var renderer in renderers)
                foreach (var bone in renderer.bones)
                    if (bone != null) bones.Add(bone);
            var minLength = modelHeight * 0.08f;
            foreach (var bone in bones)
                if (SegmentLength(bone) >= minLength) parts.Add(bone);
            parts.Sort((a, b) => SegmentLength(b).CompareTo(SegmentLength(a)));
            if (parts.Count > settings.MaxParts) parts.RemoveRange(settings.MaxParts, parts.Count - settings.MaxParts);
            return parts;
        }

        /// <summary>World positions of every readable skinned vertex, grouped by the bone that weighs it most.</summary>
        static bool CollectVertices(SkinnedMeshRenderer[] renderers, Dictionary<Transform, List<Vector3>> vertices, out int total)
        {
            total = 0;
            bool any = false;
            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                var bones = renderer.bones;
                if (mesh == null || !mesh.isReadable || bones == null || bones.Length == 0) continue;

                var positions = mesh.vertices;
                var weights = mesh.boneWeights;
                var bindposes = mesh.bindposes;
                if (weights.Length != positions.Length) continue;
                any = true;

                for (int v = 0; v < positions.Length; v++)
                {
                    int b = weights[v].boneIndex0; // Unity sorts weights, so the first is the largest
                    if (b < 0 || b >= bones.Length || b >= bindposes.Length || bones[b] == null) continue;
                    // Mesh space -> bone space at bind time -> world space in the current pose.
                    var world = bones[b].localToWorldMatrix.MultiplyPoint3x4(bindposes[b].MultiplyPoint3x4(positions[v]));
                    if (!vertices.TryGetValue(bones[b], out var list)) vertices[bones[b]] = list = new List<Vector3>();
                    list.Add(world);
                    total++;
                }
            }
            return any;
        }

        /// <summary>
        /// Fits a capsule to a part's vertices: along their main axis (from principal component analysis), spanning
        /// them lengthwise, and as wide as most of them are from that axis.
        /// </summary>
        static void FitCapsule(PartSpec spec, List<Vector3> points, float scale)
        {
            var mean = Vector3.zero;
            foreach (var p in points) mean += p;
            mean /= points.Count;

            // Covariance, then its main eigenvector by power iteration.
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in points)
            {
                var d = p - mean;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            var axis = new Vector3(1f, 1.1f, 0.9f).normalized; // not aligned with any axis, so it can't start orthogonal
            for (int i = 0; i < 16; i++)
            {
                var next = new Vector3(xx * axis.x + xy * axis.y + xz * axis.z,
                                       xy * axis.x + yy * axis.y + yz * axis.z,
                                       xz * axis.x + yz * axis.y + zz * axis.z);
                if (next.sqrMagnitude < 1e-12f) break;
                axis = next.normalized;
            }

            // Extent along the axis, ignoring a few stray vertices, and distance from it.
            var along = new List<float>(points.Count);
            var across = new List<float>(points.Count);
            foreach (var p in points)
            {
                var d = p - mean;
                float t = Vector3.Dot(d, axis);
                along.Add(t);
                across.Add((d - axis * t).magnitude);
            }
            along.Sort();
            across.Sort();
            float tMin = Percentile(along, 0.02f), tMax = Percentile(along, 0.98f);
            // Vertices are on the surface, so a typical distance from the axis is the body's radius there.
            float radius = Mathf.Max(0.005f, Percentile(across, 0.6f) * scale);

            spec.Axis = axis;
            spec.Center = mean + axis * ((tMin + tMax) * 0.5f);
            spec.Radius = radius;
            spec.Height = Mathf.Max((tMax - tMin) * scale, radius * 2f);
        }

        /// <summary>Without vertices: a capsule from the bone towards its child part or child bone.</summary>
        static void FitFromSkeleton(PartSpec spec, List<PartSpec> specs, float modelHeight)
        {
            var bone = spec.Bone;
            Vector3? end = null;
            foreach (var other in specs)
                if (other.Parent >= 0 && specs[other.Parent] == spec) { end = other.Bone.position; break; }
            if (end == null && bone.childCount > 0) end = bone.GetChild(0).position;

            var segment = end.HasValue ? end.Value - bone.position : Vector3.zero;
            if (segment.magnitude < modelHeight * 0.02f)
            {
                // A leaf: a ball at the bone.
                spec.Axis = Vector3.up;
                spec.Center = bone.position;
                spec.Radius = modelHeight * 0.04f;
                spec.Height = spec.Radius * 2f;
                return;
            }
            spec.Axis = segment.normalized;
            spec.Center = bone.position + segment * 0.5f;
            spec.Radius = Mathf.Clamp(segment.magnitude * 0.25f, modelHeight * 0.015f, modelHeight * 0.09f);
            spec.Height = segment.magnitude + spec.Radius * 2f;
        }

        /// <summary>Mass by collider volume, with a floor so tiny parts don't destabilize the joints.</summary>
        static void AssignMasses(List<PartSpec> specs, float totalMass)
        {
            float total = 0f;
            foreach (var spec in specs)
            {
                float r = spec.Radius, cylinder = Mathf.Max(0f, spec.Height - 2f * r);
                spec.Mass = Mathf.PI * r * r * cylinder + 4f / 3f * Mathf.PI * r * r * r;
                total += spec.Mass;
            }
            foreach (var spec in specs) spec.Mass = total > 0f ? spec.Mass / total : 1f / specs.Count;

            float sum = 0f;
            foreach (var spec in specs) sum += spec.Mass = Mathf.Max(spec.Mass, k_MinMassShare);
            foreach (var spec in specs) spec.Mass *= totalMass / sum;
        }

        static float MeasureHeight(SkinnedMeshRenderer[] renderers)
        {
            if (renderers.Length == 0) return 1.8f;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            return Mathf.Max(0.1f, bounds.size.y);
        }

        static Transform NearestPartAncestor(Transform t, Dictionary<Transform, int> index)
        {
            for (; t != null; t = t.parent)
                if (index.ContainsKey(t)) return t;
            return null;
        }

        static bool HasPartDescendant(Transform bone, List<Transform> parts)
        {
            foreach (var other in parts)
                if (other != bone && other.IsChildOf(bone)) return true;
            return false;
        }

        /// <summary>Vertices the part would own: its bone's and those of descendant bones without parts.</summary>
        static int PartVertexCount(Transform bone, List<Transform> parts, Dictionary<Transform, int> counts)
        {
            int total = 0;
            foreach (var (other, count) in counts)
            {
                if (!other.IsChildOf(bone)) continue;
                var owner = other;
                while (owner != null && !parts.Contains(owner)) owner = owner.parent;
                if (owner == bone) total += count;
            }
            return total;
        }

        static float SegmentLength(Transform bone)
        {
            float longest = 0f;
            foreach (Transform child in bone)
                longest = Mathf.Max(longest, Vector3.Distance(bone.position, child.position));
            return longest;
        }

        static int Depth(Transform t)
        {
            int depth = 0;
            for (; t.parent != null; t = t.parent) depth++;
            return depth;
        }

        static float Percentile(List<float> sorted, float p) =>
            sorted.Count == 0 ? 0f : sorted[Mathf.Clamp(Mathf.RoundToInt(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }
}
