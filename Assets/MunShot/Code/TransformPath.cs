using System.Collections.Generic;
using UnityEngine;

namespace MunShot
{
    /// <summary>Names a transform by its path of child names, for finding it again in a copy and for logs.</summary>
    internal static class TransformPath
    {
        /// <summary>
        /// The path from <paramref name="root"/> (not included) down to <paramref name="t"/>, e.g. "rig/arm/hand"; from
        /// the top of the scene when <paramref name="root"/> is null. Empty when <paramref name="t"/> is the root.
        /// </summary>
        public static string From(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (Transform c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
