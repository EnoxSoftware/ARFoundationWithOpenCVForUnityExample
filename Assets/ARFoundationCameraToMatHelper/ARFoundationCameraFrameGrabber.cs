#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Extensions;
using OpenCVForUnity.Extensions.SourceToMat;
using OpenCVForUnity.Extensions.SourceToMat.Grabbers;
using OpenCVForUnity.UnityIntegration.Helper.SourceToMat.Grabbers;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace ARFoundationWithOpenCVForUnity.UnityIntegration.Helper.SourceToMat
{
    /// <summary>
    /// Camera-callback payload raised by <see cref="ARFoundationCameraFrameGrabber.RawFrameAcquired"/>.
    /// </summary>
    /// <remarks>
    /// Raised from the <c>XRCpuImage.ConvertAsync</c> completion callback, which may run off the Unity main thread.
    /// <see cref="RawFrameMat"/> is a grabber-owned buffer that is reused on later frames; clone the pixels
    /// before returning from the handler. Color conversion and 90-degree rotation are not applied.
    /// User flip is already applied via <c>XRCpuImage.Transformation</c>.
    /// </remarks>
    public sealed class ARFoundationCameraRawFrameEventArgs : EventArgs
    {
        // Public Properties
        /// <summary>
        /// Gets the grabber-owned RGBA frame. The buffer is reused; clone before returning.
        /// </summary>
        public Mat RawFrameMat { get; }

        /// <summary>
        /// Gets the frame width in pixels.
        /// </summary>
        public int FrameWidth { get; }

        /// <summary>
        /// Gets the frame height in pixels.
        /// </summary>
        public int FrameHeight { get; }

        /// <summary>
        /// Gets the projection matrix snapshotted when the CPU image was acquired.
        /// </summary>
        public Matrix4x4 ProjectionMatrix { get; }

        /// <summary>
        /// Gets the camera-to-world matrix snapshotted when the CPU image was acquired.
        /// </summary>
        public Matrix4x4 CameraToWorldMatrix { get; }

        /// <summary>
        /// Gets the display-adjusted camera intrinsics snapshotted when the CPU image was acquired.
        /// </summary>
        public XRCameraIntrinsics Intrinsics { get; }

        /// <summary>
        /// Gets the camera timestamp in nanoseconds snapshotted when the CPU image was acquired.
        /// </summary>
        public long TimestampNs { get; }

        /// <summary>
        /// Gets the display rotation in degrees snapshotted when the CPU image was acquired.
        /// </summary>
        public int DisplayRotationAngle { get; }

        /// <summary>
        /// Gets whether the display transform flips the image vertically.
        /// </summary>
        public bool DisplayFlipVertical { get; }

        /// <summary>
        /// Gets whether the display transform flips the image horizontally.
        /// </summary>
        public bool DisplayFlipHorizontal { get; }

        /// <summary>
        /// Gets whether the subscriber should rotate the frame 90 degrees clockwise.
        /// </summary>
        public bool NeedsRotate90 { get; }

        // Constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="ARFoundationCameraRawFrameEventArgs"/> class.
        /// </summary>
        /// <param name="rawFrameMat">Grabber-owned RGBA frame for this capture. The buffer is reused.</param>
        /// <param name="frameWidth">Frame width in pixels.</param>
        /// <param name="frameHeight">Frame height in pixels.</param>
        /// <param name="projectionMatrix">Projection matrix for this capture.</param>
        /// <param name="cameraToWorldMatrix">Camera-to-world matrix for this capture.</param>
        /// <param name="intrinsics">Display-adjusted camera intrinsics for this capture.</param>
        /// <param name="timestampNs">Camera timestamp in nanoseconds for this capture.</param>
        /// <param name="displayRotationAngle">Display rotation in degrees for this capture.</param>
        /// <param name="displayFlipVertical">Whether the display transform flips vertically.</param>
        /// <param name="displayFlipHorizontal">Whether the display transform flips horizontally.</param>
        /// <param name="needsRotate90">Whether the subscriber should rotate the frame 90 degrees clockwise.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rawFrameMat"/> is <see langword="null"/>.</exception>
        public ARFoundationCameraRawFrameEventArgs(
            Mat rawFrameMat,
            int frameWidth,
            int frameHeight,
            Matrix4x4 projectionMatrix,
            Matrix4x4 cameraToWorldMatrix,
            XRCameraIntrinsics intrinsics,
            long timestampNs,
            int displayRotationAngle,
            bool displayFlipVertical,
            bool displayFlipHorizontal,
            bool needsRotate90)
        {
            if (rawFrameMat == null)
            {
                throw new ArgumentNullException(nameof(rawFrameMat), "Parameter cannot be null.");
            }

            RawFrameMat = rawFrameMat;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
            ProjectionMatrix = projectionMatrix;
            CameraToWorldMatrix = cameraToWorldMatrix;
            Intrinsics = intrinsics;
            TimestampNs = timestampNs;
            DisplayRotationAngle = displayRotationAngle;
            DisplayFlipVertical = displayFlipVertical;
            DisplayFlipHorizontal = displayFlipHorizontal;
            NeedsRotate90 = needsRotate90;
        }
    }

    /// <summary>
    /// AR Foundation <see cref="IFrameGrabber"/> that converts <c>ARCameraManager.frameReceived</c> into
    /// pull frames via <c>XRCpuImage.ConvertAsync</c>.
    /// </summary>
    /// <remarks>
    /// Copies raw RGBA plus the pose / intrinsics / timestamp snapshotted on <c>frameReceived</c>.
    /// Color conversion and 90-degree rotation are not applied here. User flip is applied as
    /// <c>XRCpuImage.Transformation</c> together with the display transform.
    /// <see cref="OpenAsync"/> waits for the first converted frame, then unsubscribes from
    /// <c>frameReceived</c> so <c>Ready</c> does not keep streaming. Playback is started again from
    /// <see cref="IUnityFrameGrabberPlayback.BeginPlaybackAsync"/>.
    /// The Open body runs inside <see cref="SourceToMatSynchronizationContextScope"/>.
    /// </remarks>
    public sealed class ARFoundationCameraFrameGrabber :
        IFrameGrabber,
        IFrameGrabberLayoutFrame,
        IFrameGrabberOpenState,
        IUnityFrameGrabberPlayback,
        ISourceToMatErrorSource
    {
        // Constants
#if UNITY_IOS
        private const int FACING_DIRECTION_CHANGE_WAIT_FRAME_COUNT = 30;
        private const int RESOLUTION_CHANGE_WAIT_FRAME_COUNT = 30;
#else
        private const int FACING_DIRECTION_CHANGE_WAIT_FRAME_COUNT = 10;
        private const int RESOLUTION_CHANGE_WAIT_FRAME_COUNT = 5;
#endif

        // Private Fields
        private readonly XROrigin _xROrigin;
        private readonly int _mainThreadId;
        private readonly SynchronizationContext _mainThreadContext;
        private readonly object _latestRawFrameLockObject = new object();
        private readonly object _snapshotLock = new object();

        private ARCameraManager _cameraManager;
        private Mat _rawFrameMat;
        private Mat _frameMat;
        private bool _isOpen;
        private bool _isPlaybackActive;
        private bool _isWaitingForFirstFrame;
        private bool _hasUnreportedFrame;
        private bool _hasCachedSnapshot;
        private bool _disposed;
        private int _requestedWidth = 640;
        private int _requestedHeight = 480;
        private float _requestedFPS = 30f;
        private bool _requestedIsFrontFacing;
        private bool _autoFocusRequested = true;
        private LightEstimation _requestedLightEstimation;
        private bool _flipVertical;
        private bool _flipHorizontal;
        private bool _rotate90Degree;
        private long _lastDeliveredCaptureSequence = -1;
        private long _captureSequence;
        private int _convertGeneration;
        private ARFoundationCameraMatSource _deliveryMatSource;
        private CaptureSnapshot _latestSnapshot;
        private volatile bool _hasFirstFrameArrived;

        // Public Properties
        /// <summary>
        /// Gets or sets the requested frame width in pixels. Must be set before <see cref="OpenAsync"/>.
        /// </summary>
        public int RequestedWidth
        {
            get => _requestedWidth;
            set => _requestedWidth = (int)Mathf.Clamp(value, 0f, float.MaxValue);
        }

        /// <summary>
        /// Gets or sets the requested frame height in pixels. Must be set before <see cref="OpenAsync"/>.
        /// </summary>
        public int RequestedHeight
        {
            get => _requestedHeight;
            set => _requestedHeight = (int)Mathf.Clamp(value, 0f, float.MaxValue);
        }

        /// <summary>
        /// Gets or sets the requested frame rate. Must be set before <see cref="OpenAsync"/>.
        /// </summary>
        public float RequestedFPS
        {
            get => _requestedFPS;
            set => _requestedFPS = Mathf.Clamp(value, -1f, float.MaxValue);
        }

        /// <summary>
        /// Gets or sets whether the user-facing camera is requested. Applied in <see cref="OpenAsync"/>.
        /// </summary>
        public bool RequestedIsFrontFacing
        {
            get => _requestedIsFrontFacing;
            set => _requestedIsFrontFacing = value;
        }

        /// <summary>
        /// Gets or sets whether autofocus is requested. Applied in <see cref="OpenAsync"/> and while open.
        /// </summary>
        public bool AutoFocusRequested
        {
            get => _cameraManager != null ? _cameraManager.autoFocusRequested : _autoFocusRequested;
            set
            {
                _autoFocusRequested = value;
                if (_cameraManager != null)
                {
                    _cameraManager.autoFocusRequested = value;
                }
            }
        }

        /// <summary>
        /// Gets whether autofocus is currently enabled, or <see langword="false"/> when the camera manager is unavailable.
        /// </summary>
        public bool AutoFocusEnabled => _cameraManager != null && _cameraManager.autoFocusEnabled;

        /// <summary>
        /// Gets or sets the requested light estimation. Applied in <see cref="OpenAsync"/> and while open.
        /// </summary>
        public LightEstimation RequestedLightEstimation
        {
            get => _cameraManager != null ? _cameraManager.requestedLightEstimation : _requestedLightEstimation;
            set
            {
                _requestedLightEstimation = value;
                if (_cameraManager != null)
                {
                    _cameraManager.requestedLightEstimation = value;
                }
            }
        }

        /// <summary>
        /// Gets the current light estimation, or <c>default</c> when the camera manager is unavailable.
        /// </summary>
        public LightEstimation CurrentLightEstimation =>
            _cameraManager != null ? _cameraManager.currentLightEstimation : default;

        /// <summary>
        /// Gets or sets whether the image is flipped vertically. Combined with the display transform in ConvertAsync.
        /// </summary>
        public bool FlipVertical
        {
            get => _flipVertical;
            set => _flipVertical = value;
        }

        /// <summary>
        /// Gets or sets whether the image is flipped horizontally. Combined with the display transform in ConvertAsync.
        /// </summary>
        public bool FlipHorizontal
        {
            get => _flipHorizontal;
            set => _flipHorizontal = value;
        }

        /// <summary>
        /// Gets or sets whether a 90-degree clockwise rotation is requested. Stored in the capture snapshot as
        /// <see cref="ARFoundationCameraRawFrameEventArgs.NeedsRotate90"/>; rotation itself is not applied here.
        /// </summary>
        public bool Rotate90Degree
        {
            get => _rotate90Degree;
            set => _rotate90Degree = value;
        }

        /// <summary>
        /// Gets whether the raw captured frame should be rotated 90 degrees clockwise for output.
        /// </summary>
        /// <remarks>
        /// Combines the latest published display rotation (90 or 270 degrees) with the current
        /// <see cref="Rotate90Degree"/> value. Inspector changes are reflected before the next snapshot.
        /// </remarks>
        public bool NeedsRotate90 => ComputeNeedsRotate90(DisplayRotationAngle, _rotate90Degree);

        /// <inheritdoc/>
        public bool IsOpen => _isOpen;

        /// <summary>
        /// Gets whether camera permission has been granted, or <see langword="false"/> when the camera manager is unavailable.
        /// </summary>
        public bool PermissionGranted => _cameraManager != null && _cameraManager.permissionGranted;

        /// <summary>
        /// Gets whether the active camera is user-facing, or <see langword="false"/> when the camera manager is unavailable.
        /// </summary>
        public bool IsFrontFacing =>
            _cameraManager != null && _cameraManager.currentFacingDirection == CameraFacingDirection.User;

        /// <summary>
        /// Gets the current AR camera facing direction, or <c>default</c> when the camera manager is unavailable.
        /// </summary>
        public CameraFacingDirection CurrentFacingDirection =>
            _cameraManager != null ? _cameraManager.currentFacingDirection : default;

        /// <summary>
        /// Gets the AR camera manager GameObject name after a successful open, or an empty string when closed.
        /// </summary>
        public string DeviceName =>
            IsCameraManagerRunning() ? _cameraManager.name : string.Empty;

        /// <summary>
        /// Gets the effective capture frame rate after a successful open, or <c>-1</c> when closed or unknown.
        /// </summary>
        public float FPS
        {
            get
            {
                if (!_isOpen || !IsCameraManagerRunning())
                {
                    return -1f;
                }

                XRCameraConfiguration? configuration = _cameraManager.currentConfiguration;
                if (!configuration.HasValue || !configuration.Value.framerate.HasValue)
                {
                    return -1f;
                }

                return configuration.Value.framerate.Value;
            }
        }

        /// <summary>
        /// Gets the captured frame width in pixels, or <c>0</c> before the first published snapshot.
        /// </summary>
        public int FrameWidth =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) ? snapshot.FrameWidth : 0;

        /// <summary>
        /// Gets the captured frame height in pixels, or <c>0</c> before the first published snapshot.
        /// </summary>
        public int FrameHeight =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) ? snapshot.FrameHeight : 0;

        /// <summary>
        /// Gets the latest camera-to-world matrix snapshotted with the most recent frame.
        /// </summary>
        public Matrix4x4 CameraToWorldMatrix =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) ? snapshot.CameraToWorldMatrix : Matrix4x4.identity;

        /// <summary>
        /// Gets the latest projection matrix snapshotted with the most recent frame.
        /// </summary>
        public Matrix4x4 ProjectionMatrix =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) ? snapshot.ProjectionMatrix : Matrix4x4.identity;

        /// <summary>
        /// Gets the latest display-adjusted camera intrinsics snapshotted with the most recent frame.
        /// </summary>
        public XRCameraIntrinsics Intrinsics =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) ? snapshot.Intrinsics : default;

        /// <summary>
        /// Gets the latest camera timestamp in nanoseconds snapshotted with the most recent frame.
        /// </summary>
        public long TimestampNs =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) ? snapshot.TimestampNs : 0L;

        /// <summary>
        /// Gets the latest display rotation in degrees snapshotted with the most recent frame.
        /// </summary>
        public int DisplayRotationAngle =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) ? snapshot.DisplayRotationAngle : 0;

        /// <summary>
        /// Gets whether the latest published snapshot flips vertically for display.
        /// </summary>
        public bool DisplayFlipVertical =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) && snapshot.DisplayFlipVertical;

        /// <summary>
        /// Gets whether the latest published snapshot flips horizontally for display.
        /// </summary>
        public bool DisplayFlipHorizontal =>
            TryGetPublishedSnapshot(out CaptureSnapshot snapshot) && snapshot.DisplayFlipHorizontal;

        // Public Events
        /// <inheritdoc/>
        public event Action<SourceToMatErrorCode, string> ErrorOccurred;

        /// <summary>
        /// Raised when a new raw RGBA frame has been copied from ConvertAsync.
        /// </summary>
        /// <remarks>
        /// Raised from the ConvertAsync completion callback and is not marshaled to the Unity main thread.
        /// <see cref="ARFoundationCameraRawFrameEventArgs.RawFrameMat"/> is grabber-owned and reused; clone before returning.
        /// Pose, intrinsics, timestamp, and display flags are snapshotted on <c>frameReceived</c>, not re-read in the callback.
        /// </remarks>
        public event EventHandler<ARFoundationCameraRawFrameEventArgs> RawFrameAcquired;

        // Constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="ARFoundationCameraFrameGrabber"/> class.
        /// Construct on the Unity main thread so host marshaling can capture the player-loop context.
        /// </summary>
        /// <param name="xROrigin">
        /// XR origin used to resolve the AR camera and <see cref="ARCameraManager"/>.
        /// When <see langword="null"/>, <see cref="OpenAsync"/> fails with
        /// <see cref="SourceToMatErrorCode.CAMERA_CANT_OPEN"/>.
        /// </param>
        public ARFoundationCameraFrameGrabber(XROrigin xROrigin)
        {
            _xROrigin = xROrigin;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _mainThreadContext = SynchronizationContext.Current;
        }

        // Public Methods
        /// <inheritdoc/>
        public async Task OpenAsync(InitTimeoutBudget budget, CancellationToken cancellationToken = default)
        {
            if (budget == null)
            {
                throw new ArgumentNullException(nameof(budget), "Parameter cannot be null.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.IsExpired)
                {
                    throw new OperationCanceledException();
                }

                await EnsureMainThreadAsync(cancellationToken);

                SynchronizationContext hostLoopContext =
                    SourceToMatHostMarshaling.ResolveCapturedSynchronizationContext(
                        _mainThreadId,
                        _mainThreadContext);

                using (new SourceToMatSynchronizationContextScope(hostLoopContext))
                {
                    CloseInternal();

                    if (_xROrigin == null || _xROrigin.Camera == null)
                    {
                        RaiseError(SourceToMatErrorCode.CAMERA_CANT_OPEN, "XROrigin cannot be null.");
                        return;
                    }

                    _cameraManager = _xROrigin.Camera.GetComponent<ARCameraManager>();
                    if (!IsCameraManagerRunning())
                    {
                        RaiseError(SourceToMatErrorCode.CAMERA_CANT_OPEN, "ARCameraManager is not found.");
                        _cameraManager = null;
                        return;
                    }

                    bool granted = await WaitForCameraPermissionAsync(budget, cancellationToken, hostLoopContext);
                    if (!granted)
                    {
                        RaiseError(SourceToMatErrorCode.CAMERA_PERMISSION_DENIED, string.Empty);
                        CloseInternal();
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (budget.IsExpired)
                    {
                        throw new OperationCanceledException();
                    }

                    ApplyRequestedCameraSettings();

                    CameraFacingDirection requestedFacing = _requestedIsFrontFacing
                        ? CameraFacingDirection.User
                        : CameraFacingDirection.World;
                    if (_cameraManager.requestedFacingDirection != requestedFacing
                        || _cameraManager.currentFacingDirection == CameraFacingDirection.None)
                    {
                        _cameraManager.requestedFacingDirection = requestedFacing;
                    }

                    if (!await WaitHostLoopFramesAsync(
                            FACING_DIRECTION_CHANGE_WAIT_FRAME_COUNT,
                            waitedFrames => waitedFrames >= FACING_DIRECTION_CHANGE_WAIT_FRAME_COUNT - 5
                                && _cameraManager != null
                                && _cameraManager.currentFacingDirection == requestedFacing,
                            budget,
                            cancellationToken,
                            hostLoopContext))
                    {
                        throw new OperationCanceledException();
                    }

                    if (!await SourceToMatHostWait.WaitUntilOnHostLoopAsync(
                            HasCameraConfigurations,
                            budget,
                            cancellationToken,
                            hostLoopContext))
                    {
                        throw new OperationCanceledException();
                    }

                    if (!TryApplyNearestCameraConfiguration())
                    {
                        RaiseError(
                            SourceToMatErrorCode.CAMERA_RESOLUTION_UNSUPPORTED,
                            "No supported AR Foundation camera configuration was found.");
                        CloseInternal();
                        return;
                    }

                    if (!await WaitHostLoopFramesAsync(
                            RESOLUTION_CHANGE_WAIT_FRAME_COUNT,
                            extraCompletePredicate: null,
                            budget,
                            cancellationToken,
                            hostLoopContext))
                    {
                        throw new OperationCanceledException();
                    }

                    _hasFirstFrameArrived = false;
                    _isWaitingForFirstFrame = true;
                    SubscribeFrameReceived();

                    bool didUpdate = await SourceToMatHostWait.WaitUntilOnHostLoopAsync(
                        () => _hasFirstFrameArrived,
                        budget,
                        cancellationToken,
                        hostLoopContext);
                    _isWaitingForFirstFrame = false;
                    UnsubscribeFrameReceived();
                    if (!didUpdate)
                    {
                        throw new OperationCanceledException();
                    }

                    if (!CopyRawFrameMatToFrameMat())
                    {
                        RaiseError(SourceToMatErrorCode.CAMERA_START_FAILED, "The first camera frame was empty.");
                        CloseInternal();
                        return;
                    }

                    _isPlaybackActive = false;
                    _hasUnreportedFrame = true;
                    _isOpen = true;
                }
            }
            catch (OperationCanceledException)
            {
                CloseInternal();
                throw;
            }
        }

        /// <inheritdoc/>
        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            return SourceToMatHostMarshaling.RunOnCapturedMainThreadAsync(
                _mainThreadId,
                _mainThreadContext,
                CloseInternal,
                cancellationToken);
        }

        /// <inheritdoc/>
        public bool TryGrab(out Mat frame)
        {
            frame = null;

            if (!_isOpen || _frameMat == null || _frameMat.IsDisposed)
            {
                return false;
            }

            lock (_latestRawFrameLockObject)
            {
                if (!_hasUnreportedFrame)
                {
                    return false;
                }

                if (!CopyRawFrameMatToFrameMatLocked())
                {
                    return false;
                }

                _hasUnreportedFrame = false;
                frame = _frameMat;
                return !frame.empty();
            }
        }

        /// <inheritdoc/>
        public bool TryGetLayoutFrame(out Mat frame)
        {
            frame = null;

            if (!_isOpen || _frameMat == null || _frameMat.IsDisposed || _frameMat.empty())
            {
                return false;
            }

            lock (_latestRawFrameLockObject)
            {
                CopyRawFrameMatToFrameMatLocked();
            }

            frame = _frameMat;
            return !frame.empty();
        }

        /// <summary>
        /// Starts or resumes AR Foundation frame delivery by subscribing to <c>frameReceived</c>.
        /// Prefer <see cref="BeginPlaybackAsync"/> when calling from async paths that may use
        /// <c>ConfigureAwait(false)</c>.
        /// </summary>
        public void BeginPlayback()
        {
            if (!_isOpen || !IsCameraManagerRunning())
            {
                return;
            }

            _isPlaybackActive = true;
            SubscribeFrameReceived();
        }

        /// <inheritdoc/>
        public Task BeginPlaybackAsync(CancellationToken cancellationToken = default)
        {
            return SourceToMatHostMarshaling.RunOnCapturedMainThreadAsync(
                _mainThreadId,
                _mainThreadContext,
                BeginPlayback,
                cancellationToken);
        }

        /// <summary>
        /// Pauses AR Foundation frame delivery while keeping the current frame buffer.
        /// Prefer <see cref="PausePlaybackAsync"/> when calling from async paths that may use
        /// <c>ConfigureAwait(false)</c>.
        /// </summary>
        public void PausePlayback()
        {
            if (!_isOpen)
            {
                return;
            }

            _isPlaybackActive = false;
            UnsubscribeFrameReceived();
            InvalidatePendingConversions();
        }

        /// <inheritdoc/>
        public Task PausePlaybackAsync(CancellationToken cancellationToken = default)
        {
            return SourceToMatHostMarshaling.RunOnCapturedMainThreadAsync(
                _mainThreadId,
                _mainThreadContext,
                PausePlayback,
                cancellationToken);
        }

        /// <summary>
        /// Stops AR Foundation frame delivery and clears pending frame flags.
        /// Prefer <see cref="StopPlaybackAsync"/> when calling from async paths that may use
        /// <c>ConfigureAwait(false)</c>.
        /// </summary>
        public void StopPlayback()
        {
            if (!_isOpen)
            {
                return;
            }

            _isPlaybackActive = false;
            _hasUnreportedFrame = false;
            UnsubscribeFrameReceived();
            InvalidatePendingConversions();
        }

        /// <inheritdoc/>
        public Task StopPlaybackAsync(CancellationToken cancellationToken = default)
        {
            return SourceToMatHostMarshaling.RunOnCapturedMainThreadAsync(
                _mainThreadId,
                _mainThreadContext,
                StopPlayback,
                cancellationToken);
        }

        /// <summary>
        /// Returns supported AR camera configurations as width, height, and frame-rate modes.
        /// </summary>
        /// <returns>
        /// Supported capture modes, or an empty list when the camera manager is unavailable.
        /// Call from the Unity main thread.
        /// </returns>
        public IReadOnlyList<CameraResolution> GetSupportedResolutions()
        {
            if (!IsCameraManagerRunning())
            {
                return Array.Empty<CameraResolution>();
            }

            using (NativeArray<XRCameraConfiguration> configurations = _cameraManager.GetConfigurations(Allocator.Temp))
            {
                if (!configurations.IsCreated || configurations.Length == 0)
                {
                    return Array.Empty<CameraResolution>();
                }

                var result = new CameraResolution[configurations.Length];
                for (int i = 0; i < configurations.Length; i++)
                {
                    XRCameraConfiguration configuration = configurations[i];
                    float fps = configuration.framerate.HasValue ? configuration.framerate.Value : 0f;
                    result[i] = new CameraResolution(configuration.width, configuration.height, fps);
                }

                return result;
            }
        }

        /// <summary>
        /// Releases native resources owned by the grabber.
        /// Prefer <see cref="CloseAsync"/> when an async teardown is available.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CloseInternal();
            SetDeliveryMatSource(null);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Registers the mat source that produces delivered frames while the raw-frame lock is held.
        /// </summary>
        /// <param name="matSource">
        /// Delivery mat source, or <see langword="null"/> to clear the registration.
        /// </param>
        internal void SetDeliveryMatSource(ARFoundationCameraMatSource matSource)
        {
            lock (_latestRawFrameLockObject)
            {
                _deliveryMatSource = matSource;
            }
        }

        // Private Methods
        private Task EnsureMainThreadAsync(CancellationToken cancellationToken)
        {
            return SourceToMatHostMarshaling.EnsureCapturedMainThreadAsync(
                _mainThreadId,
                _mainThreadContext,
                cancellationToken);
        }

        private bool IsCameraManagerRunning()
        {
            return _cameraManager != null
                && _cameraManager.subsystem != null
                && _cameraManager.subsystem.running;
        }

        private bool CanAcceptCameraFrames()
        {
            return !_disposed && (_isPlaybackActive || _isWaitingForFirstFrame);
        }

        private void ApplyRequestedCameraSettings()
        {
            if (_cameraManager == null)
            {
                return;
            }

            _cameraManager.autoFocusRequested = _autoFocusRequested;
            _cameraManager.requestedLightEstimation = _requestedLightEstimation;
        }

        private async Task<bool> WaitForCameraPermissionAsync(
            InitTimeoutBudget budget,
            CancellationToken cancellationToken,
            SynchronizationContext hostLoopContext)
        {
            if (IsArCameraPermissionGranted())
            {
                return true;
            }

            return await budget.RunOutsideTimeoutAsync(
                ct => RequestAndWaitForCameraPermissionAsync(ct, hostLoopContext),
                cancellationToken);
        }

        private bool IsArCameraPermissionGranted()
        {
#if UNITY_IOS
            return Application.HasUserAuthorization(UserAuthorization.WebCam)
                || (_cameraManager != null && _cameraManager.permissionGranted);
#elif UNITY_ANDROID
            return Permission.HasUserAuthorizedPermission(Permission.Camera)
                || (_cameraManager != null && _cameraManager.permissionGranted);
#else
            return true;
#endif
        }

        private async Task<bool> RequestAndWaitForCameraPermissionAsync(
            CancellationToken cancellationToken,
            SynchronizationContext hostLoopContext)
        {
#if UNITY_IOS
            UserAuthorization mode = UserAuthorization.WebCam;
            if (!Application.HasUserAuthorization(mode))
            {
                AsyncOperation request = Application.RequestUserAuthorization(mode);
                while (request != null && !request.isDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsArCameraPermissionGranted())
                    {
                        return true;
                    }

                    await SourceToMatHostMarshaling.YieldCapturedSynchronizationContextAsync(
                        hostLoopContext,
                        cancellationToken);
                }

                if (!Application.HasUserAuthorization(mode) && !IsArCameraPermissionGranted())
                {
                    return false;
                }
            }

            while (!IsArCameraPermissionGranted())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SourceToMatHostMarshaling.YieldCapturedSynchronizationContextAsync(
                    hostLoopContext,
                    cancellationToken);
            }

            return true;
#elif UNITY_ANDROID
            string permission = Permission.Camera;
            TaskCompletionSource<bool> permissionDialogResult = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsArCameraPermissionGranted())
                {
                    return true;
                }

                if (permissionDialogResult == null
                    && !Permission.HasUserAuthorizedPermission(permission))
                {
                    permissionDialogResult = new TaskCompletionSource<bool>();
                    PermissionCallbacks callbacks = new PermissionCallbacks();
                    callbacks.PermissionGranted += _ => permissionDialogResult.TrySetResult(true);
                    callbacks.PermissionDenied += _ => permissionDialogResult.TrySetResult(false);
                    callbacks.PermissionDeniedAndDontAskAgain += _ =>
                        permissionDialogResult.TrySetResult(false);
                    Permission.RequestUserPermission(permission, callbacks);
                }

                if (permissionDialogResult != null && permissionDialogResult.Task.IsCompleted)
                {
                    bool dialogGranted = permissionDialogResult.Task.Result;
                    if (!dialogGranted && !IsArCameraPermissionGranted())
                    {
                        return false;
                    }
                }

                await SourceToMatHostMarshaling.YieldCapturedSynchronizationContextAsync(
                    hostLoopContext,
                    cancellationToken);
            }
#else
            await Task.CompletedTask;
            return true;
#endif
        }

        private async Task<bool> WaitHostLoopFramesAsync(
            int frameCount,
            Func<int, bool> extraCompletePredicate,
            InitTimeoutBudget budget,
            CancellationToken cancellationToken,
            SynchronizationContext hostLoopContext)
        {
            int waitedFrames = 0;
            return await SourceToMatHostWait.WaitUntilOnHostLoopAsync(
                () =>
                {
                    if (waitedFrames >= frameCount)
                    {
                        return true;
                    }

                    if (extraCompletePredicate != null && extraCompletePredicate(waitedFrames))
                    {
                        return true;
                    }

                    waitedFrames++;
                    return false;
                },
                budget,
                cancellationToken,
                hostLoopContext);
        }

        private bool HasCameraConfigurations()
        {
            if (!IsCameraManagerRunning())
            {
                return false;
            }

            using (NativeArray<XRCameraConfiguration> configurations = _cameraManager.GetConfigurations(Allocator.Temp))
            {
                return configurations.IsCreated && configurations.Length > 0;
            }
        }

        private bool TryApplyNearestCameraConfiguration()
        {
            if (!IsCameraManagerRunning())
            {
                return false;
            }

            using (NativeArray<XRCameraConfiguration> configurations = _cameraManager.GetConfigurations(Allocator.Temp))
            {
                if (!configurations.IsCreated || configurations.Length == 0)
                {
                    return false;
                }

                int requestedArea = _requestedWidth * _requestedHeight;
                int minAreaDelta = int.MaxValue;
                int matchedArea = 0;
                for (int i = 0; i < configurations.Length; i++)
                {
                    XRCameraConfiguration configuration = configurations[i];
                    int area = configuration.width * configuration.height;
                    int areaDelta = Mathf.Abs(area - requestedArea);
                    if (areaDelta < minAreaDelta)
                    {
                        minAreaDelta = areaDelta;
                        matchedArea = area;
                    }
                }

                int minFpsDelta = int.MaxValue;
                XRCameraConfiguration selected = default;
                bool found = false;
                for (int i = 0; i < configurations.Length; i++)
                {
                    XRCameraConfiguration configuration = configurations[i];
                    if (configuration.width * configuration.height != matchedArea)
                    {
                        continue;
                    }

                    int framerate = configuration.framerate.HasValue ? configuration.framerate.Value : 0;
                    int fpsDelta = Mathf.Abs(framerate - (int)_requestedFPS);
                    if (!found || fpsDelta < minFpsDelta)
                    {
                        minFpsDelta = fpsDelta;
                        selected = configuration;
                        found = true;
                    }
                }

                if (!found)
                {
                    return false;
                }

                XRCameraConfiguration? current = _cameraManager.currentConfiguration;
                if (!current.HasValue || !current.Value.Equals(selected))
                {
                    _cameraManager.currentConfiguration = selected;
                }

                return true;
            }
        }

        private void OnCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
        {
            if (!CanAcceptCameraFrames() || !IsCameraManagerRunning())
            {
                return;
            }

            if (_hasFirstFrameArrived && !_isPlaybackActive)
            {
                return;
            }

            if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                return;
            }

            try
            {
                if (image.width <= 0 || image.height <= 0)
                {
                    return;
                }

                CaptureSnapshot snapshot;
                if (TryCaptureSnapshot(image, eventArgs, out snapshot))
                {
                    PublishSnapshot(snapshot);
                }
                else if (!TryGetPublishedSnapshot(out snapshot))
                {
                    snapshot = CreateDefaultSnapshot(image.width, image.height);
                }

                if (!EnsureFrameBuffers(image.width, image.height))
                {
                    return;
                }

                XRCpuImage.Transformation transformation = ResolveConversionTransformation(snapshot);
                var conversionParams = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32, transformation);
                long captureSequence = ++_captureSequence;
                int generation = _convertGeneration;
                image.ConvertAsync(
                    conversionParams,
                    (status, _, data) => OnConvertAsyncComplete(status, data, generation, captureSequence, snapshot));
            }
            finally
            {
                image.Dispose();
            }
        }

        private void OnConvertAsyncComplete(
            XRCpuImage.AsyncConversionStatus status,
            NativeArray<byte> data,
            int generation,
            long captureSequence,
            CaptureSnapshot snapshot)
        {
            if (generation != _convertGeneration)
            {
                return;
            }

            if (captureSequence <= _lastDeliveredCaptureSequence)
            {
                return;
            }

            if (status != XRCpuImage.AsyncConversionStatus.Ready)
            {
                return;
            }

            Mat rawFrameMat;
            Mat deliveredMat = null;
            bool producedDeliveredMat = false;
            ARFoundationCameraMatSource deliveryMatSource = null;
            lock (_latestRawFrameLockObject)
            {
                if (generation != _convertGeneration)
                {
                    return;
                }

                if (captureSequence <= _lastDeliveredCaptureSequence)
                {
                    return;
                }

                if (!CopyConversionDataToRawFrameMat(data, snapshot.FrameWidth, snapshot.FrameHeight))
                {
                    return;
                }

                _lastDeliveredCaptureSequence = captureSequence;
                _hasUnreportedFrame = true;
                _hasFirstFrameArrived = true;
                rawFrameMat = _rawFrameMat;
                deliveryMatSource = _deliveryMatSource;
                if (deliveryMatSource != null)
                {
                    producedDeliveredMat = deliveryMatSource.TryProduceDeliveredMatUnderLock(
                        _rawFrameMat,
                        snapshot.NeedsRotate90,
                        generation,
                        out deliveredMat);
                }
            }

            if (producedDeliveredMat)
            {
                bool raised = deliveryMatSource != null
                    && deliveryMatSource.RaiseFrameMatDeliveredAfterLock(
                        deliveredMat,
                        snapshot.ProjectionMatrix,
                        snapshot.CameraToWorldMatrix,
                        snapshot.Intrinsics,
                        snapshot.TimestampNs,
                        _convertGeneration);
                if (!raised)
                {
                    deliveredMat?.Dispose();
                }
            }

            RaiseRawFrameAcquired(snapshot, rawFrameMat);
        }

        private bool CopyConversionDataToRawFrameMat(NativeArray<byte> data, int frameWidth, int frameHeight)
        {
            if (!data.IsCreated || data.Length <= 0)
            {
                return false;
            }

            if (!EnsureFrameBuffersLocked(frameWidth, frameHeight))
            {
                return false;
            }

            MatBufferUtils.CopyToMat<byte>(data, _rawFrameMat);
            return !_rawFrameMat.empty();
        }

        private bool TryCaptureSnapshot(XRCpuImage image, ARCameraFrameEventArgs eventArgs, out CaptureSnapshot snapshot)
        {
            snapshot = CreateDefaultSnapshot(image.width, image.height);

            snapshot.FrameWidth = image.width;
            snapshot.FrameHeight = image.height;
            snapshot.PreviewFramerate = ReadPreviewFramerate();
            snapshot.ProjectionMatrix = eventArgs.projectionMatrix.HasValue
                ? eventArgs.projectionMatrix.Value
                : Matrix4x4.identity;
            snapshot.TimestampNs = eventArgs.timestampNs.HasValue ? eventArgs.timestampNs.Value : 0L;

            if (_xROrigin != null && _xROrigin.Camera != null)
            {
                snapshot.CameraToWorldMatrix = _xROrigin.Camera.cameraToWorldMatrix;
            }

            TryReadDisplayTransform(eventArgs, out snapshot.DisplayRotationAngle, out snapshot.DisplayFlipVertical, out snapshot.DisplayFlipHorizontal);
            snapshot.NeedsRotate90 = ComputeNeedsRotate90(snapshot.DisplayRotationAngle, _rotate90Degree);

            if (_cameraManager != null && _cameraManager.TryGetIntrinsics(out XRCameraIntrinsics cameraIntrinsics))
            {
                snapshot.Intrinsics = ApplyDisplayAdjustedIntrinsics(
                    cameraIntrinsics,
                    snapshot.DisplayRotationAngle,
                    snapshot.DisplayFlipVertical,
                    snapshot.DisplayFlipHorizontal);
            }

            return snapshot.FrameWidth > 0 && snapshot.FrameHeight > 0;
        }

        private int ReadPreviewFramerate()
        {
            if (!IsCameraManagerRunning())
            {
                return -1;
            }

            XRCameraConfiguration? configuration = _cameraManager.currentConfiguration;
            if (!configuration.HasValue || !configuration.Value.framerate.HasValue)
            {
                return -1;
            }

            return configuration.Value.framerate.Value;
        }

        private static void TryReadDisplayTransform(
            ARCameraFrameEventArgs eventArgs,
            out int displayRotationAngle,
            out bool displayFlipVertical,
            out bool displayFlipHorizontal)
        {
            displayRotationAngle = 0;
            displayFlipVertical = false;
            displayFlipHorizontal = false;

#if USE_ARFOUNDATION_5
            // Remove scaling and offset factors from the camera display matrix while maintaining orientation.
            // Decompose that matrix to extract the rotation and flipping factors.
            // https://github.com/Unity-Technologies/arfoundation-samples/blob/88179bab2b180dd90229d9ec995204be47da1cc1/Assets/Scripts/DisplayDepthImage.cs#L333
            if (eventArgs.displayMatrix.HasValue)
            {
                // Copy the display rotation matrix from the camera.
                Matrix4x4 cameraMatrix = eventArgs.displayMatrix ?? Matrix4x4.identity;

                Vector2 affineBasisX = new Vector2(1.0f, 0.0f);
                Vector2 affineBasisY = new Vector2(0.0f, 1.0f);
                Vector2 affineTranslation = new Vector2(0.0f, 0.0f);
#if UNITY_IOS
                affineBasisX = new Vector2(cameraMatrix[0, 0], cameraMatrix[1, 0]);
                affineBasisY = new Vector2(cameraMatrix[0, 1], cameraMatrix[1, 1]);
                affineTranslation = new Vector2(cameraMatrix[2, 0], cameraMatrix[2, 1]);
#endif // UNITY_IOS
#if UNITY_ANDROID
                affineBasisX = new Vector2(cameraMatrix[0, 0], cameraMatrix[0, 1]);
                affineBasisY = new Vector2(cameraMatrix[1, 0], cameraMatrix[1, 1]);
                affineTranslation = new Vector2(cameraMatrix[0, 2], cameraMatrix[1, 2]);
#endif // UNITY_ANDROID

                affineBasisX = affineBasisX.normalized;
                affineBasisY = affineBasisY.normalized;
                Matrix4x4 displayRotationMatrix = Matrix4x4.identity;
                displayRotationMatrix[0, 0] = affineBasisX.x;
                displayRotationMatrix[0, 1] = affineBasisY.x;
                displayRotationMatrix[1, 0] = affineBasisX.y;
                displayRotationMatrix[1, 1] = affineBasisY.y;

#if UNITY_IOS
                Matrix4x4 flipYMatrix = Matrix4x4.Scale(new Vector3(1, -1, 1));
                displayRotationMatrix = flipYMatrix.inverse * displayRotationMatrix;
#endif // UNITY_IOS

                displayRotationAngle = (int)OpenCVARUtils.ExtractRotationFromMatrix(ref displayRotationMatrix).eulerAngles.z;
                Vector3 localScale = OpenCVARUtils.ExtractScaleFromMatrix(ref displayRotationMatrix);
                displayFlipVertical = Mathf.Sign(localScale.y) == -1;
                displayFlipHorizontal = Mathf.Sign(localScale.x) == -1;
            }
#else
            // Remove scaling and offset factors from the camera display matrix while maintaining orientation.
            // Decompose that matrix to extract the rotation and flipping factors.
            // https://github.com/Unity-Technologies/arfoundation-samples/blob/362a2596f35b9e45cb73920a9f8fffb1777e7d36/Assets/Scripts/Runtime/Occlusion/DisplayDepthImage.cs#L358
            if (eventArgs.displayMatrix.HasValue)
            {
                // Copy the display rotation matrix from the camera.
                Matrix4x4 cameraMatrix = eventArgs.displayMatrix ?? Matrix4x4.identity;

                Vector2 affineBasisX = new Vector2(cameraMatrix[0, 0], cameraMatrix[1, 0]);
                Vector2 affineBasisY = new Vector2(cameraMatrix[0, 1], cameraMatrix[1, 1]);
                Vector2 affineTranslation = new Vector2(cameraMatrix[2, 0], cameraMatrix[2, 1]);
                affineBasisX = affineBasisX.normalized;
                affineBasisY = affineBasisY.normalized;
                Matrix4x4 displayRotationMatrix = Matrix4x4.identity;
                displayRotationMatrix[0, 0] = affineBasisX.x;
                displayRotationMatrix[0, 1] = affineBasisY.x;
                displayRotationMatrix[1, 0] = affineBasisX.y;
                displayRotationMatrix[1, 1] = affineBasisY.y;

                Matrix4x4 flipYMatrix = Matrix4x4.Scale(new Vector3(1, -1, 1));
                displayRotationMatrix = flipYMatrix.inverse * displayRotationMatrix;

                displayRotationAngle = (int)OpenCVARUtils.ExtractRotationFromMatrix(ref displayRotationMatrix).eulerAngles.z;
                Vector3 localScale = OpenCVARUtils.ExtractScaleFromMatrix(ref displayRotationMatrix);
                displayFlipVertical = Mathf.Sign(localScale.y) == -1;
                displayFlipHorizontal = Mathf.Sign(localScale.x) == -1;
            }
#endif // USE_ARFOUNDATION_5
        }

        private static XRCameraIntrinsics ApplyDisplayAdjustedIntrinsics(
            XRCameraIntrinsics cameraIntrinsics,
            int displayRotationAngle,
            bool displayFlipVertical,
            bool displayFlipHorizontal)
        {
            Vector2 focalLength = cameraIntrinsics.focalLength;
            Vector2 principalPoint = cameraIntrinsics.principalPoint;
            Vector2Int resolution = cameraIntrinsics.resolution;

            Matrix4x4 translateToOrigin = Matrix4x4.Translate(new Vector3(-resolution.x / 2f, -resolution.y / 2f, 0f));
            principalPoint = translateToOrigin.MultiplyPoint3x4(principalPoint);

            Matrix4x4 displayMatrix = Matrix4x4.TRS(
                Vector3.zero,
                Quaternion.Euler(0f, 0f, displayRotationAngle),
                new Vector3(displayFlipHorizontal ? -1f : 1f, displayFlipVertical ? -1f : 1f, 1f));
            principalPoint = displayMatrix.MultiplyPoint3x4(principalPoint);

            if (displayRotationAngle == 90 || displayRotationAngle == 270)
            {
                focalLength = new Vector2(focalLength.y, focalLength.x);
                resolution = new Vector2Int(resolution.y, resolution.x);
            }

            Matrix4x4 translateToCenter = Matrix4x4.Translate(new Vector3(resolution.x / 2f, resolution.y / 2f, 0f));
            principalPoint = translateToCenter.MultiplyPoint3x4(principalPoint);

            return new XRCameraIntrinsics(focalLength, principalPoint, resolution);
        }

        private static bool ComputeNeedsRotate90(int displayRotationAngle, bool rotate90Degree)
        {
            if (displayRotationAngle == 90 || displayRotationAngle == 270)
            {
                return !rotate90Degree;
            }

            return rotate90Degree;
        }

        private XRCpuImage.Transformation ResolveConversionTransformation(CaptureSnapshot snapshot)
        {
            bool flipVertical;
            bool flipHorizontal;
            if (snapshot.NeedsRotate90)
            {
                if (snapshot.DisplayRotationAngle == 90 || snapshot.DisplayRotationAngle == 270)
                {
                    flipVertical = snapshot.DisplayFlipVertical ? !_flipHorizontal : _flipHorizontal;
                    flipHorizontal = snapshot.DisplayFlipHorizontal ? !_flipVertical : _flipVertical;
                }
                else
                {
                    flipVertical = snapshot.DisplayFlipVertical ? !_flipVertical : _flipVertical;
                    flipHorizontal = snapshot.DisplayFlipHorizontal ? !_flipHorizontal : _flipHorizontal;
                }
            }
            else if (snapshot.DisplayRotationAngle == 90 || snapshot.DisplayRotationAngle == 270)
            {
                flipVertical = snapshot.DisplayFlipVertical ? _flipHorizontal : !_flipHorizontal;
                flipHorizontal = snapshot.DisplayFlipHorizontal ? _flipVertical : !_flipVertical;
            }
            else
            {
                flipVertical = snapshot.DisplayFlipVertical ? !_flipVertical : _flipVertical;
                flipHorizontal = snapshot.DisplayFlipHorizontal ? !_flipHorizontal : _flipHorizontal;
            }

            int flipCode = CalculateFlipCode(snapshot.DisplayRotationAngle, flipVertical, flipHorizontal);
            if (flipCode == int.MinValue)
            {
                return XRCpuImage.Transformation.None;
            }

            if (flipCode == 0)
            {
                return XRCpuImage.Transformation.MirrorX;
            }

            if (flipCode == 1)
            {
                return XRCpuImage.Transformation.MirrorY;
            }

            return XRCpuImage.Transformation.MirrorX | XRCpuImage.Transformation.MirrorY;
        }

        private static int CalculateFlipCode(int displayRotationAngle, bool flipVertical, bool flipHorizontal)
        {
            int flipCode = int.MinValue;

            if (displayRotationAngle == 180 || displayRotationAngle == 270)
            {
                flipCode = -1;
            }

            if (flipVertical)
            {
                if (flipCode == int.MinValue)
                {
                    flipCode = 0;
                }
                else if (flipCode == 0)
                {
                    flipCode = int.MinValue;
                }
                else if (flipCode == 1)
                {
                    flipCode = -1;
                }
                else if (flipCode == -1)
                {
                    flipCode = 1;
                }
            }

            if (flipHorizontal)
            {
                if (flipCode == int.MinValue)
                {
                    flipCode = 1;
                }
                else if (flipCode == 0)
                {
                    flipCode = -1;
                }
                else if (flipCode == 1)
                {
                    flipCode = int.MinValue;
                }
                else if (flipCode == -1)
                {
                    flipCode = 0;
                }
            }

            return flipCode;
        }

        private bool EnsureFrameBuffers(int frameWidth, int frameHeight)
        {
            lock (_latestRawFrameLockObject)
            {
                return EnsureFrameBuffersLocked(frameWidth, frameHeight);
            }
        }

        private bool EnsureFrameBuffersLocked(int frameWidth, int frameHeight)
        {
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return false;
            }

            if (_rawFrameMat == null
                || _rawFrameMat.IsDisposed
                || _rawFrameMat.cols() != frameWidth
                || _rawFrameMat.rows() != frameHeight)
            {
                _rawFrameMat?.Dispose();
                _rawFrameMat = new Mat(frameHeight, frameWidth, CvType.CV_8UC4);
            }

            if (_frameMat == null
                || _frameMat.IsDisposed
                || _frameMat.cols() != frameWidth
                || _frameMat.rows() != frameHeight)
            {
                _frameMat?.Dispose();
                _frameMat = new Mat(frameHeight, frameWidth, CvType.CV_8UC4);
            }

            return _rawFrameMat != null && !_rawFrameMat.IsDisposed && _frameMat != null && !_frameMat.IsDisposed;
        }

        private bool CopyRawFrameMatToFrameMat()
        {
            lock (_latestRawFrameLockObject)
            {
                return CopyRawFrameMatToFrameMatLocked();
            }
        }

        private bool CopyRawFrameMatToFrameMatLocked()
        {
            if (_rawFrameMat == null || _rawFrameMat.IsDisposed || _rawFrameMat.empty())
            {
                return false;
            }

            if (!EnsureFrameBuffersLocked(_rawFrameMat.cols(), _rawFrameMat.rows()))
            {
                return false;
            }

            _rawFrameMat.copyTo(_frameMat);
            return !_frameMat.empty();
        }

        private void SubscribeFrameReceived()
        {
            if (!IsCameraManagerRunning())
            {
                return;
            }

            _cameraManager.frameReceived -= OnCameraFrameReceived;
            _cameraManager.frameReceived += OnCameraFrameReceived;
        }

        private void UnsubscribeFrameReceived()
        {
            if (_cameraManager == null)
            {
                return;
            }

            _cameraManager.frameReceived -= OnCameraFrameReceived;
        }

        private void InvalidatePendingConversions()
        {
            _convertGeneration++;
        }

        private void CloseInternal()
        {
            _isOpen = false;
            _isPlaybackActive = false;
            _isWaitingForFirstFrame = false;
            _hasUnreportedFrame = false;
            _hasFirstFrameArrived = false;
            UnsubscribeFrameReceived();
            InvalidatePendingConversions();
            ClearPublishedSnapshot();
            _captureSequence = 0;

            lock (_latestRawFrameLockObject)
            {
                _lastDeliveredCaptureSequence = -1;
                _hasUnreportedFrame = false;
                _rawFrameMat?.Dispose();
                _rawFrameMat = null;
                _frameMat?.Dispose();
                _frameMat = null;
            }

            _cameraManager = null;
        }

        private void RaiseError(SourceToMatErrorCode errorCode, string message)
        {
            ErrorOccurred?.Invoke(errorCode, message);
        }

        private void RaiseRawFrameAcquired(CaptureSnapshot snapshot, Mat rawFrameMat)
        {
            EventHandler<ARFoundationCameraRawFrameEventArgs> handler = RawFrameAcquired;
            if (handler == null || rawFrameMat == null || rawFrameMat.IsDisposed)
            {
                return;
            }

            handler(
                this,
                new ARFoundationCameraRawFrameEventArgs(
                    rawFrameMat,
                    snapshot.FrameWidth,
                    snapshot.FrameHeight,
                    snapshot.ProjectionMatrix,
                    snapshot.CameraToWorldMatrix,
                    snapshot.Intrinsics,
                    snapshot.TimestampNs,
                    snapshot.DisplayRotationAngle,
                    snapshot.DisplayFlipVertical,
                    snapshot.DisplayFlipHorizontal,
                    snapshot.NeedsRotate90));
        }

        private void PublishSnapshot(CaptureSnapshot snapshot)
        {
            lock (_snapshotLock)
            {
                _latestSnapshot = snapshot;
                _hasCachedSnapshot = true;
            }
        }

        private bool TryGetPublishedSnapshot(out CaptureSnapshot snapshot)
        {
            lock (_snapshotLock)
            {
                if (!_hasCachedSnapshot)
                {
                    snapshot = default;
                    return false;
                }

                snapshot = _latestSnapshot;
                return true;
            }
        }

        private void ClearPublishedSnapshot()
        {
            lock (_snapshotLock)
            {
                _hasCachedSnapshot = false;
                _latestSnapshot = default;
            }
        }

        private static CaptureSnapshot CreateDefaultSnapshot(int frameWidth, int frameHeight)
        {
            return new CaptureSnapshot
            {
                FrameWidth = frameWidth,
                FrameHeight = frameHeight,
                PreviewFramerate = -1,
                ProjectionMatrix = Matrix4x4.identity,
                CameraToWorldMatrix = Matrix4x4.identity,
                Intrinsics = default,
                TimestampNs = 0L,
                DisplayRotationAngle = 0,
                DisplayFlipVertical = false,
                DisplayFlipHorizontal = false,
                NeedsRotate90 = false
            };
        }

        private struct CaptureSnapshot
        {
            public int FrameWidth;
            public int FrameHeight;
            public int PreviewFramerate;
            public Matrix4x4 ProjectionMatrix;
            public Matrix4x4 CameraToWorldMatrix;
            public XRCameraIntrinsics Intrinsics;
            public long TimestampNs;
            public int DisplayRotationAngle;
            public bool DisplayFlipVertical;
            public bool DisplayFlipHorizontal;
            public bool NeedsRotate90;
        }
    }
}

#endif
