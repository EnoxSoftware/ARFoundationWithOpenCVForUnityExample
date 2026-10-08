using System;
using OpenCVForUnity.CoreModule;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace ARFoundationWithOpenCVForUnity.UnityIntegration.Helper.SourceToMat
{
    /// <summary>
    /// Payload for a newly delivered camera frame <see cref="Mat"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Mat"/> is a newly allocated buffer. The subscriber must dispose it when finished.
    /// Do not use <see cref="OpenCVForUnity.Extensions.SourceToMat.IMatSource.FrameMat"/> as this payload.
    /// On AR Foundation, delivery runs on the <c>XRCpuImage.ConvertAsync</c> completion callback, which may be off the
    /// Unity main thread. On WebCam fallback, delivery runs on the Unity main thread.
    /// </remarks>
    public sealed class FrameMatDeliveredEventArgs : EventArgs
    {
        // Public Properties
        /// <summary>
        /// Gets the newly allocated frame image. The subscriber must dispose this <see cref="Mat"/>.
        /// </summary>
        public Mat Mat { get; }

        /// <summary>
        /// Gets the AR camera projection matrix.
        /// </summary>
        public Matrix4x4 ProjectionMatrix { get; }

        /// <summary>
        /// Gets the AR camera camera-to-world matrix.
        /// </summary>
        public Matrix4x4 CameraToWorldMatrix { get; }

        /// <summary>
        /// Gets the display-adjusted AR camera intrinsics. WebCam fallback uses <see langword="default"/>.
        /// </summary>
        public XRCameraIntrinsics Intrinsics { get; }

        /// <summary>
        /// Gets the AR camera timestamp in nanoseconds. WebCam fallback uses <c>0</c>.
        /// </summary>
        public long TimestampNs { get; }

        // Constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="FrameMatDeliveredEventArgs"/> class.
        /// </summary>
        /// <param name="mat">Newly allocated frame image. The subscriber owns this <see cref="Mat"/>.</param>
        /// <param name="projectionMatrix">AR camera projection matrix, or from <see cref="OpenCVForUnity.UnityIntegration.Helper.SourceToMat.IUnityCameraPoseProvider"/> on WebCam fallback.</param>
        /// <param name="cameraToWorldMatrix">AR camera camera-to-world matrix, or from <see cref="OpenCVForUnity.UnityIntegration.Helper.SourceToMat.IUnityCameraPoseProvider"/> on WebCam fallback.</param>
        /// <param name="intrinsics">Display-adjusted AR camera intrinsics, or <see langword="default"/> on WebCam fallback.</param>
        /// <param name="timestampNs">AR camera timestamp in nanoseconds, or <c>0</c> on WebCam fallback.</param>
        /// <exception cref="ArgumentNullException"><paramref name="mat"/> is <see langword="null"/>.</exception>
        public FrameMatDeliveredEventArgs(
            Mat mat,
            Matrix4x4 projectionMatrix,
            Matrix4x4 cameraToWorldMatrix,
            XRCameraIntrinsics intrinsics,
            long timestampNs = 0L)
        {
            if (mat == null)
            {
                throw new ArgumentNullException(nameof(mat), "Parameter cannot be null.");
            }

            Mat = mat;
            ProjectionMatrix = projectionMatrix;
            CameraToWorldMatrix = cameraToWorldMatrix;
            Intrinsics = intrinsics;
            TimestampNs = timestampNs;
        }
    }
}
