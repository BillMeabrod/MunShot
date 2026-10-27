using UnityEngine;

namespace MunShot.Cameras
{
    /// <summary>
    /// What happens to the camera's picture on its way to the screen that moves things from where the camera projects
    /// them, such as a fisheye bulge, so the mouse can be matched to what's under it. Positions are in screen pixels
    /// (y up), as the mouse and <see cref="Camera.WorldToScreenPoint(Vector3)"/> have them.
    /// </summary>
    public interface IScreenLens
    {
        /// <summary>Whether the picture is bent now; when not, both mappings leave a position as it is.</summary>
        bool Bends { get; }

        /// <summary>
        /// Where on the camera's own projection the pixel seen at <paramref name="screen"/> comes from: what to aim a
        /// ray through for what's under the mouse.
        /// </summary>
        Vector2 ToScene(Vector2 screen);

        /// <summary>Where a point of the camera's own projection shows on screen.</summary>
        Vector2 ToScreen(Vector2 scene);
    }
}
