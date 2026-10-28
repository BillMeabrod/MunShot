using UnityEngine;

namespace MunShot.Capture
{
    /// <summary>
    /// The shape of the photo: the screen's own, or a common photo shape the saved photo is cropped to (the middle of
    /// the screen at that shape, as big as fits). Photo mode shades what's cut off on screen.
    /// </summary>
    public enum FrameShape
    {
        Full,
        Wide16By9,
        Photo3By2,
        Classic4By3,
        Square,
        Cinema21By9,
        Portrait9By16
    }

    /// <summary>Where a <see cref="FrameShape"/> sits in a picture: the same for the guides on screen and the crop.</summary>
    public static class PhotoFrame
    {
        /// <summary>A shape's width over its height; 0 for the screen's own.</summary>
        public static float AspectOf(FrameShape shape) => shape switch
        {
            FrameShape.Wide16By9 => 16f / 9f,
            FrameShape.Photo3By2 => 3f / 2f,
            FrameShape.Classic4By3 => 4f / 3f,
            FrameShape.Square => 1f,
            FrameShape.Cinema21By9 => 21f / 9f,
            FrameShape.Portrait9By16 => 9f / 16f,
            _ => 0f
        };

        /// <summary>
        /// The biggest rectangle of <paramref name="shape"/> that fits a <paramref name="width"/> by
        /// <paramref name="height"/> picture, in its middle, from its corner; the whole picture for <see cref="FrameShape.Full"/>.
        /// </summary>
        public static Rect Fit(float width, float height, FrameShape shape)
        {
            float aspect = AspectOf(shape);
            if (aspect <= 0f || width <= 0f || height <= 0f) return new Rect(0f, 0f, width, height);
            float fitWidth = width, fitHeight = height;
            if (width / height > aspect) fitWidth = height * aspect;
            else fitHeight = width / aspect;
            return new Rect((width - fitWidth) * 0.5f, (height - fitHeight) * 0.5f, fitWidth, fitHeight);
        }
    }
}
