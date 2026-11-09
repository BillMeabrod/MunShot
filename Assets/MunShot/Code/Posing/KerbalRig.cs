namespace MunShot.Posing
{
    /// <summary>Names on the game's kerbal rig.</summary>
    internal static class KerbalRig
    {
        /// <summary>Every bone of the kerbal skeleton is named bone_kerbal_* (bone_kerbal_head, …).</summary>
        public const string BonePrefix = "bone_kerbal_";

        // Bones, without the prefix
        public const string Head = "head";
        public const string Neck = "neck";

        /// <summary>The bone at the base of the neck: the neck and the helmet hang from it.</summary>
        public const string NeckBase = "neck_ach";

        public const string Helmet = "helmet";

        /// <summary>The suit's hardware (air hose, chest box), skinned to the spine, pelvis and neck.</summary>
        public const string HoseMesh = "mesh_spacesuit_01_hardware";

        // The thumbs' rest rotations: the base as in body_spacesuit_01.prefab (local euler angles, from
        // Tools/RigDiscovery/out/body_spacesuit_01.prefab.axes.txt), beside the open hand; the outer joints bent 10°
        // each, where the prefab has them bent about 25° (a hook once the thumb is spread out) and straight looks too
        // long
        private static readonly System.Collections.Generic.Dictionary<string, UnityEngine.Vector3> RestEulers =
            new System.Collections.Generic.Dictionary<string, UnityEngine.Vector3>
            {
                ["l_thumb_01"] = new UnityEngine.Vector3(3.34f, 50.18f, 15.06f),
                ["l_thumb_02"] = new UnityEngine.Vector3(0f, 350f, 0f),
                ["l_thumb_03"] = new UnityEngine.Vector3(0f, 350f, 0f),
                ["r_thumb_01"] = new UnityEngine.Vector3(356.66f, 309.82f, 15.06f),
                ["r_thumb_02"] = new UnityEngine.Vector3(0f, 10f, 0f),
                ["r_thumb_03"] = new UnityEngine.Vector3(0f, 10f, 0f),
            };

        /// <summary>A bone's rest rotation (relative to its parent), for the bones that have one listed.</summary>
        public static bool TryGetRest(string bone, out UnityEngine.Quaternion rest)
        {
            bool known = RestEulers.TryGetValue(bone, out UnityEngine.Vector3 euler);
            rest = known ? UnityEngine.Quaternion.Euler(euler) : UnityEngine.Quaternion.identity;
            return known;
        }
    }
}
