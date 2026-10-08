namespace ARFoundationWithOpenCVForUnity.UnityIntegration.Helper.SourceToMat
{
    /// <summary>
    /// One-shot open options for AR Foundation camera-backed sources.
    /// </summary>
    /// <remarks>
    /// Projects <see cref="Width"/>, <see cref="Height"/>, <see cref="FPS"/>, and <see cref="IsFrontFacing"/> onto
    /// <see cref="ARFoundationCameraToMatHelper"/> before initialization.
    /// <see cref="ARFoundationCameraToMatHelper.XROrigin"/>, autofocus, light estimation, device name, and
    /// AsyncGPU readback are not included; Inspector source settings and helper properties supply those values.
    /// </remarks>
    public sealed class ARFoundationCameraOpenOptions
    {
        // Public Properties
        /// <summary>
        /// Gets or sets the requested frame width in pixels.
        /// </summary>
        public int Width { get; set; } = 640;

        /// <summary>
        /// Gets or sets the requested frame height in pixels.
        /// </summary>
        public int Height { get; set; } = 480;

        /// <summary>
        /// Gets or sets the requested frame rate in frames per second.
        /// </summary>
        public float FPS { get; set; } = 30f;

        /// <summary>
        /// Gets or sets whether the user-facing camera is requested.
        /// </summary>
        /// <remarks>
        /// Maps to <see cref="ARFoundationCameraToMatHelper.RequestedIsFrontFacing"/>. On AR Foundation this
        /// selects <c>CameraFacingDirection.User</c> when <see langword="true"/> and
        /// <c>CameraFacingDirection.World</c> when <see langword="false"/>.
        /// </remarks>
        public bool IsFrontFacing { get; set; }
    }
}
