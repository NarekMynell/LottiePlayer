namespace Mynell.Lottie
{
    /// <summary>Inspector tooltips of the Lottie components, so the same setting reads the same everywhere.</summary>
    static class LottieTooltips
    {
        internal const string Asset =
            "The animation to play. Drop a .json or .lottie file into the project and it is imported as a Lottie Asset.";
        internal const string PlayOnEnable = "Start playing as soon as the object is enabled.";
        internal const string PlayOnAttach = "Start playing as soon as the element is added to a panel.";
        internal const string LoopMode =
            "Once: play to the end and hold the last frame.\nLoop: start over from the beginning.\n" +
            "Ping Pong: play forward, then backward.";
        internal const string LoopCount =
            "How many times to play before stopping. With Ping Pong, there and back counts as one. 0 = forever.";
        internal const string Speed = "Playback speed. 1 is normal, 2 is twice as fast, and negative values play backward.";
        internal const string UnscaledTime =
            "Ignore Time.timeScale, so the animation keeps playing while the game is paused or in slow motion.";
        internal const string Subframes =
            "Draw in-between frames for smooth motion, like lottie-web does. Turn it off to show only whole frames, as " +
            "they were made in After Effects.";
        internal const string MaxTextureSize =
            "Largest width and height of the texture, in pixels. Keeps a very large animation from using too much " +
            "memory and render time.";
        internal const string OnComplete =
            "Called when playback ends: at the last frame in Once mode, or when the Loop Count is reached.";
        internal const string PreserveAspect = "Keep the animation's proportions inside the rectangle instead of stretching it.";
        internal const string ResolutionScale =
            "Texture resolution compared to the size on screen. 1 is pixel-perfect; lower values render faster but " +
            "look softer.\n\nMultiplied by the project's Resolution Scale (Project Settings > Mynell Lottie).";
        internal const string Color = "Tint multiplied with the animation's colors. Alpha fades the whole animation.";
        internal const string PixelsPerUnit =
            "How many pixels of the animation fit in one world unit. At 100, a 512 x 512 animation is 5.12 units wide.";
        internal const string Pivot =
            "The point the object is placed and rotated around, as a fraction of the size: (0.5, 0.5) is the center, " +
            "(0, 0) the bottom-left corner.";
        internal const string TextureScale =
            "Texture resolution compared to the animation's own size. Raise it when the object is shown bigger than " +
            "that size on screen; lower it to render faster.\n\nMultiplied by the project's Resolution Scale " +
            "(Project Settings > Mynell Lottie).";
        internal const string Flip = "Mirror the animation horizontally (X) or vertically (Y).";
    }
}
