#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Extensions.SourceToMat;
using OpenCVForUnity.UnityIntegration.Helper.SourceToMat;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace ARFoundationWithOpenCVForUnity.UnityIntegration.Helper.SourceToMat
{
    /// <summary>
    /// AR Foundation camera-backed <see cref="MatSourceBase"/> that implements
    /// <see cref="ICameraMatSource"/> and <see cref="IUnityCameraPoseProvider"/>.
    /// </summary>
    /// <remarks>
    /// Does not apply an orientation corrector and does not implement
    /// <see cref="ICameraFacingControllable"/> or
    /// <see cref="IUnityCameraMatSource"/>.
    /// <see cref="FrameMatDelivered"/> is raised on the <c>XRCpuImage.ConvertAsync</c> completion callback while
    /// <see cref="MatSourceBase.IsPlaying"/> is <see langword="true"/>.
    /// <see cref="MatSourceBase.Width"/>, <see cref="MatSourceBase.Height"/>, and
    /// <see cref="MatSourceBase.FrameMat"/> use the output size after
    /// <see cref="ARFoundationCameraFrameGrabber.NeedsRotate90"/>, not the sensor native size.
    /// Those properties stay in sync. When <see cref="MatSourceBase.UpdateFrameMatOnTick"/> is
    /// <see langword="false"/>, owned <see cref="MatSourceBase.FrameMat"/> pixels may be a placeholder.
    /// </remarks>
    public sealed class ARFoundationCameraMatSource : MatSourceBase, ICameraMatSource, IUnityCameraPoseProvider
    {
        // Private Fields
        private readonly ARFoundationCameraFrameGrabber _arGrabber;
        private Mat _colorConversionScratch;
        private int _produceConvertGeneration;
        private bool _usedUnsupportedColorFallback;

        // Public Properties
        /// <inheritdoc/>
        public string DeviceName => _arGrabber.DeviceName;

        /// <inheritdoc/>
        public string RequestedDeviceName { get; set; } = string.Empty;

        /// <inheritdoc/>
        public int RequestedWidth { get; set; } = 640;

        /// <inheritdoc/>
        public int RequestedHeight { get; set; } = 480;

        /// <inheritdoc/>
        public float RequestedFPS { get; set; } = 30f;

        /// <inheritdoc/>
        public float FPS => _arGrabber.FPS;

        /// <summary>
        /// Gets or sets whether the user-facing camera is requested. Applied when the grabber opens.
        /// </summary>
        public bool RequestedIsFrontFacing { get; set; }

        /// <inheritdoc/>
        public IReadOnlyList<CameraDeviceInfo> SupportedDevices => Array.Empty<CameraDeviceInfo>();

        /// <inheritdoc/>
        /// <remarks>
        /// Shortcut for <see cref="GetSupportedResolutions(string)"/>. AR Foundation camera
        /// configurations do not depend on <see cref="RequestedDeviceName"/>.
        /// </remarks>
        public IReadOnlyList<CameraResolution> SupportedResolutions => GetSupportedResolutions(RequestedDeviceName);

        /// <inheritdoc/>
        /// <remarks>
        /// Returns the AR camera camera-to-world matrix from
        /// <see cref="ARFoundationCameraFrameGrabber"/>, not <c>Camera.main</c>.
        /// </remarks>
        public Matrix4x4 CameraToWorldMatrix => _arGrabber.CameraToWorldMatrix;

        /// <inheritdoc/>
        /// <remarks>
        /// Returns the AR camera projection matrix from
        /// <see cref="ARFoundationCameraFrameGrabber"/>, not <c>Camera.main</c>.
        /// </remarks>
        public Matrix4x4 ProjectionMatrix => _arGrabber.ProjectionMatrix;

        /// <summary>
        /// Gets the latest display-adjusted AR camera intrinsics from <see cref="ARFoundationCameraFrameGrabber"/>.
        /// </summary>
        public XRCameraIntrinsics Intrinsics => _arGrabber.Intrinsics;

        /// <summary>
        /// Gets the latest AR camera timestamp in nanoseconds from <see cref="ARFoundationCameraFrameGrabber"/>.
        /// </summary>
        public long TimestampNs => _arGrabber.TimestampNs;

        /// <summary>
        /// Gets the latest display rotation in degrees from <see cref="ARFoundationCameraFrameGrabber"/>.
        /// </summary>
        public int DisplayRotationAngle => _arGrabber.DisplayRotationAngle;

        /// <summary>
        /// Gets whether the latest published snapshot flips vertically for display.
        /// </summary>
        public bool DisplayFlipVertical => _arGrabber.DisplayFlipVertical;

        /// <summary>
        /// Gets whether the latest published snapshot flips horizontally for display.
        /// </summary>
        public bool DisplayFlipHorizontal => _arGrabber.DisplayFlipHorizontal;

        // Public Events
        /// <summary>
        /// Raised on the ConvertAsync completion callback when a new converted frame is available.
        /// </summary>
        /// <remarks>
        /// Fires only while <see cref="MatSourceBase.IsPlaying"/> is <see langword="true"/>
        /// (<see cref="MatSourceState.Paused"/>, <see cref="MatSourceState.Ready"/>, and
        /// uninitialized states do not raise).
        /// <see cref="FrameMatDeliveredEventArgs.Mat"/> is a newly allocated buffer; the subscriber
        /// must dispose it. <see cref="MatSourceBase.FrameMat"/> is not passed.
        /// Color conversion and 90-degree rotation use the settings snapshotted at the start
        /// of conversion. User flip is applied by the grabber via <c>XRCpuImage.Transformation</c>
        /// and is not applied again here. The event is not marshaled to the Unity main thread.
        /// </remarks>
        public event EventHandler<FrameMatDeliveredEventArgs> FrameMatDelivered;

        // Constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="ARFoundationCameraMatSource"/> class.
        /// </summary>
        /// <param name="grabber">AR Foundation grabber that supplies raw RGBA frames and pose metadata.</param>
        /// <exception cref="ArgumentNullException"><paramref name="grabber"/> is <see langword="null"/>.</exception>
        public ARFoundationCameraMatSource(ARFoundationCameraFrameGrabber grabber)
            : base(grabber, orientationCorrector: null)
        {
            _arGrabber = grabber;
            _arGrabber.SetDeliveryMatSource(this);
            _arGrabber.ErrorOccurred += OnGrabberError;
            OnDisposed += OnMatSourceDisposed;
        }

        // Public Methods
        /// <inheritdoc/>
        public IReadOnlyList<CameraResolution> GetSupportedResolutions(string deviceNameOrIndex)
        {
            _ = deviceNameOrIndex;

            return _arGrabber.GetSupportedResolutions();
        }

        /// <inheritdoc/>
        public override async Task PlayAsync(CancellationToken cancellationToken = default)
        {
            if (IsInitializing)
            {
                await base.PlayAsync(cancellationToken);
                return;
            }

            if (State == MatSourceState.Playing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncGrabberUserTransforms();
                await BeginGrabberPlaybackAsync(cancellationToken);
                return;
            }

            bool startFromReady = State == MatSourceState.Ready;
            bool resumeFromPause = State == MatSourceState.Paused;
            await base.PlayAsync(cancellationToken);

            if (startFromReady || resumeFromPause)
            {
                SyncGrabberUserTransforms();
                await BeginGrabberPlaybackAsync(cancellationToken);
            }
        }

        /// <inheritdoc/>
        public override async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            if (IsInitializing)
            {
                await base.PauseAsync(cancellationToken);
                return;
            }

            await base.PauseAsync(cancellationToken);
            await PauseGrabberPlaybackAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public override async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (IsInitializing)
            {
                await base.StopAsync(cancellationToken);
                return;
            }

            await base.StopAsync(cancellationToken);
            await StopGrabberPlaybackAsync(cancellationToken);
        }

        // Protected Properties
        /// <inheritdoc/>
        protected override bool EffectiveRotate90Degree => _arGrabber.NeedsRotate90;

        // Protected Methods
        /// <inheritdoc/>
        protected override void SyncGrabberBeforeOpen()
        {
            _arGrabber.RequestedWidth = RequestedWidth;
            _arGrabber.RequestedHeight = RequestedHeight;
            _arGrabber.RequestedFPS = RequestedFPS;
            _arGrabber.RequestedIsFrontFacing = RequestedIsFrontFacing;
            SyncGrabberUserTransforms();
        }

        /// <inheritdoc/>
        protected override void OnRotate90DegreeChanged()
        {
            SyncGrabberUserTransforms();
        }

        /// <inheritdoc/>
        protected override SourceToMatColorFormat InferGrabberColorFormat(Mat workingMat)
        {
            _ = workingMat;

            return SourceToMatColorFormat.RGBA;
        }

        // Internal Methods
        /// <summary>
        /// Produces a delivered mat from the grabber-owned raw frame. Does not raise
        /// <see cref="FrameMatDelivered"/>.
        /// </summary>
        /// <remarks>
        /// Must be called while the grabber raw-frame lock is held. Does not dispose
        /// <paramref name="rawUnderLock"/>. When there is no <see cref="FrameMatDelivered"/>
        /// subscriber or this source is not playing, returns <see langword="false"/> without producing.
        /// User flip is already applied by ConvertAsync and is not applied again.
        /// </remarks>
        /// <param name="rawUnderLock">Grabber-owned RGBA frame. Read-only when a copy is not required.</param>
        /// <param name="needsRotate90">Whether the delivered frame should rotate 90 degrees clockwise.</param>
        /// <param name="convertGeneration">Convert generation captured with this frame.</param>
        /// <param name="deliveredMat">
        /// Newly produced mat when this method returns <see langword="true"/>; otherwise
        /// <see langword="null"/>. The caller must raise <see cref="FrameMatDelivered"/> or dispose it.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="deliveredMat"/> was produced; otherwise
        /// <see langword="false"/>.
        /// </returns>
        internal bool TryProduceDeliveredMatUnderLock(
            Mat rawUnderLock,
            bool needsRotate90,
            int convertGeneration,
            out Mat deliveredMat)
        {
            deliveredMat = null;
            _usedUnsupportedColorFallback = false;
            _produceConvertGeneration = convertGeneration;

            if (!IsPlaying || FrameMatDelivered == null)
            {
                return false;
            }

            if (rawUnderLock == null || rawUnderLock.IsDisposed || rawUnderLock.empty())
            {
                return false;
            }

            int frameWidth = rawUnderLock.cols();
            int frameHeight = rawUnderLock.rows();
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return false;
            }

            SourceToMatColorFormat outputColorFormat = OutputColorFormat;
            SourceToMatColorFormat sourceColorFormat = SourceToMatColorFormat.RGBA;
            SyncGrabberUserTransforms();

            bool canReadSourceWithoutCopy = SourceToMatUtils.CanReadSourceWithoutCopy(
                sourceColorFormat,
                outputColorFormat,
                needsRotate90,
                flipVertical: false,
                flipHorizontal: false);

            Mat ownedSourceMat = null;
            Mat producedMat = null;
            bool ownershipTransferred = false;

            try
            {
                Mat produceInputMat;
                if (canReadSourceWithoutCopy)
                {
                    produceInputMat = rawUnderLock;
                }
                else
                {
                    ownedSourceMat = rawUnderLock.clone();
                    if (ownedSourceMat == null || ownedSourceMat.IsDisposed || ownedSourceMat.empty())
                    {
                        return false;
                    }

                    produceInputMat = ownedSourceMat;
                }

                if (needsRotate90
                    && outputColorFormat != sourceColorFormat
                    && (_colorConversionScratch == null || _colorConversionScratch.IsDisposed))
                {
                    _colorConversionScratch = new Mat();
                }

                producedMat = SourceToMatUtils.ProduceOutputMat(
                    produceInputMat,
                    sourceColorFormat,
                    outputColorFormat,
                    needsRotate90,
                    flipVertical: false,
                    flipHorizontal: false,
                    out bool usedUnsupportedColorFallback,
                    _colorConversionScratch);

                _usedUnsupportedColorFallback = usedUnsupportedColorFallback;

                if (ownedSourceMat != null && !ReferenceEquals(producedMat, ownedSourceMat))
                {
                    ownedSourceMat.Dispose();
                }

                ownedSourceMat = null;

                if (producedMat == null || producedMat.IsDisposed || ReferenceEquals(producedMat, rawUnderLock))
                {
                    return false;
                }

                deliveredMat = producedMat;
                ownershipTransferred = true;
                return true;
            }
            finally
            {
                ownedSourceMat?.Dispose();
                if (!ownershipTransferred && !ReferenceEquals(producedMat, rawUnderLock))
                {
                    producedMat?.Dispose();
                }
            }
        }

        /// <summary>
        /// Raises <see cref="FrameMatDelivered"/> after the grabber raw-frame lock has been released.
        /// </summary>
        /// <remarks>
        /// Rechecks playing state, convert generation, and whether a subscriber is present.
        /// On failure, disposes <paramref name="deliveredMat"/> and returns <see langword="false"/>.
        /// On success, ownership of <paramref name="deliveredMat"/> transfers to the subscriber.
        /// Must not be called while the grabber raw-frame lock is held.
        /// </remarks>
        /// <param name="deliveredMat">Mat produced by <see cref="TryProduceDeliveredMatUnderLock"/>.</param>
        /// <param name="projectionMatrix">Projection matrix snapshotted with this frame.</param>
        /// <param name="cameraToWorldMatrix">Camera-to-world matrix snapshotted with this frame.</param>
        /// <param name="intrinsics">Display-adjusted camera intrinsics snapshotted with this frame.</param>
        /// <param name="timestampNs">Camera timestamp in nanoseconds snapshotted with this frame.</param>
        /// <param name="convertGeneration">Current grabber convert generation.</param>
        /// <returns>
        /// <see langword="true"/> when <see cref="FrameMatDelivered"/> was raised; otherwise
        /// <see langword="false"/>.
        /// </returns>
        internal bool RaiseFrameMatDeliveredAfterLock(
            Mat deliveredMat,
            Matrix4x4 projectionMatrix,
            Matrix4x4 cameraToWorldMatrix,
            XRCameraIntrinsics intrinsics,
            long timestampNs,
            int convertGeneration)
        {
            bool ownershipTransferred = false;
            try
            {
                if (_usedUnsupportedColorFallback)
                {
                    _usedUnsupportedColorFallback = false;
                    RaiseError(SourceToMatErrorCode.UNKNOWN, "Unsupported color conversion.");
                }

                if (deliveredMat == null || deliveredMat.IsDisposed)
                {
                    return false;
                }

                if (!IsPlaying || convertGeneration != _produceConvertGeneration)
                {
                    return false;
                }

                EventHandler<FrameMatDeliveredEventArgs> handler = FrameMatDelivered;
                if (handler == null)
                {
                    return false;
                }

                handler(
                    this,
                    new FrameMatDeliveredEventArgs(
                        deliveredMat,
                        projectionMatrix,
                        cameraToWorldMatrix,
                        intrinsics,
                        timestampNs));
                ownershipTransferred = true;
                return true;
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    deliveredMat?.Dispose();
                }
            }
        }

        // Private Methods
        private void SyncGrabberUserTransforms()
        {
            _arGrabber.FlipVertical = FlipVertical;
            _arGrabber.FlipHorizontal = FlipHorizontal;
            _arGrabber.Rotate90Degree = Rotate90Degree;
        }

        private Task BeginGrabberPlaybackAsync(CancellationToken cancellationToken)
        {
            return _arGrabber.BeginPlaybackAsync(cancellationToken);
        }

        private Task PauseGrabberPlaybackAsync(CancellationToken cancellationToken)
        {
            return _arGrabber.PausePlaybackAsync(cancellationToken);
        }

        private Task StopGrabberPlaybackAsync(CancellationToken cancellationToken)
        {
            return _arGrabber.StopPlaybackAsync(cancellationToken);
        }

        private void OnGrabberError(SourceToMatErrorCode errorCode, string message)
        {
            RaiseError(errorCode, message);
        }

        private void OnMatSourceDisposed()
        {
            OnDisposed -= OnMatSourceDisposed;
            _arGrabber.SetDeliveryMatSource(null);
            _arGrabber.ErrorOccurred -= OnGrabberError;
            _colorConversionScratch?.Dispose();
            _colorConversionScratch = null;
        }
    }
}

#endif
