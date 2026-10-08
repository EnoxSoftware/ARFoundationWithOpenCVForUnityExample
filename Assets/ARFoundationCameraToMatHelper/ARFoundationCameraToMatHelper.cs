using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Extensions.SourceToMat;
using OpenCVForUnity.UnityIntegration.Helper.SourceToMat;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering;
#endif
#if UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
using UnityEditor;
#endif

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
using OpenCVForUnity.Extensions.SourceToMat.Grabbers;
using OpenCVForUnity.UnityIntegration.Helper.SourceToMat.Grabbers;
using OpenCVForUnity.UnityIntegration.Helper.SourceToMat.Orientation;
#endif

namespace ARFoundationWithOpenCVForUnity.UnityIntegration.Helper.SourceToMat
{
    /// <summary>
    /// Unity helper that reads AR Foundation camera frames and exposes them as OpenCV
    /// <see cref="OpenCVForUnity.CoreModule.Mat"/> buffers. On Editor and non-iOS/Android device platforms it falls back to
    /// <c>WebCamTextureMatSource</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Primary Unity entry for AR Foundation camera capture. Use
    /// <see cref="SourceToMatHelperBase.MatSource"/> for advanced access.
    /// </para>
    /// <para>
    /// Source selection is compile-time: iOS/Android device with AR Foundation enabled wires
    /// <c>ARFoundationCameraMatSource</c>; otherwise it wires
    /// <c>WebCamTextureMatSource</c>. There is no runtime source switch.
    /// </para>
    /// <para>
    /// Initialization completes in <see cref="MatSourceState.Ready"/>; call
    /// <see cref="SourceToMatHelperBase.Play"/> or <see cref="SourceToMatHelperBase.PlayAsync"/> to start
    /// delivering frames. <see cref="FrameMatDelivered"/> fires only while
    /// <see cref="SourceToMatHelperBase.IsPlaying"/> is <see langword="true"/>.
    /// </para>
    /// <para>
    /// <see cref="FrameMatDelivered"/> is the unified frame API.
    /// On AR Foundation it is forwarded from <c>ARFoundationCameraMatSource.FrameMatDelivered</c> on the
    /// <c>XRCpuImage.ConvertAsync</c> completion callback and is not marshaled to the Unity main thread.
    /// The helper subscribes to that source event only while <see cref="FrameMatDelivered"/> has at least one subscriber.
    /// On WebCam fallback the helper clones <see cref="SourceToMatHelperBase.FrameMat"/> on the main thread
    /// after <see cref="IMatSourceEvents.OnFrameMatUpdated"/>.
    /// <see cref="FrameMatDeliveredEventArgs.Mat"/> is a newly allocated buffer; the subscriber must dispose it.
    /// Do not subscribe to both <see cref="FrameMatDelivered"/> and
    /// <see cref="SourceToMatHelperBase.OnFrameMatUpdated"/> for the same processing path.
    /// </para>
    /// <para>
    /// <see cref="RequestedIsFrontFacing"/> is the facing request stored on the source inspector settings.
    /// <see cref="RequestedFacingDirection"/> is an AR Foundation facade for the same request
    /// (<c>User</c> when front-facing is requested, <c>World</c> otherwise) and is not an independent setting.
    /// </para>
    /// During Play mode, inspector changes are applied as follows:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="RequestedWidth"/>, <see cref="RequestedHeight"/>, <see cref="RequestedFPS"/>, and
    /// <see cref="RequestedIsFrontFacing"/>: release and re-initialize the wrapped source.
    /// On AR Foundation, <see cref="AutoFocusRequested"/> and <see cref="RequestedLightEstimation"/> also
    /// re-initialize. When already <see cref="MatSourceState.Playing"/> or <see cref="MatSourceState.Paused"/>,
    /// that state is restored before <see cref="SourceToMatHelperBase.OnInitialized"/> is raised.
    /// </description></item>
    /// <item><description>
    /// <see cref="RequestedDeviceName"/> and <see cref="RequestedUseAsyncGPUReadback"/>: applied on WebCam fallback
    /// (including AsyncGPU grabber rewire when the effective path changes). Ignored on the AR Foundation path.
    /// </description></item>
    /// <item><description>
    /// <see cref="SourceToMatHelperBase.Rotate90Degree"/>, <see cref="SourceToMatHelperBase.OutputColorFormat"/>:
    /// sync to the wrapped source, which re-establishes the output layout and raises
    /// <see cref="SourceToMatHelperBase.OnFrameMatLayoutChanged"/>.
    /// On the AR Foundation path, <see cref="SourceToMatHelperBase.Width"/>,
    /// <see cref="SourceToMatHelperBase.Height"/>, and <see cref="SourceToMatHelperBase.FrameMat"/>
    /// use the size after <see cref="ARFoundationCameraFrameGrabber.NeedsRotate90"/>
    /// (display 90/270 combined with <see cref="SourceToMatHelperBase.Rotate90Degree"/>) and stay in sync.
    /// Display orientation changes update that size on the next camera frame without calling
    /// <see cref="Initialize()"/>.
    /// When <see cref="UpdateFrameMatOnTick"/> is <see langword="false"/>, owned FrameMat pixels may be a placeholder.
    /// </description></item>
    /// <item><description>
    /// <see cref="SourceToMatHelperBase.FlipVertical"/>, <see cref="SourceToMatHelperBase.FlipHorizontal"/>,
    /// <see cref="SourceToMatHelperBase.InitTimeoutMs"/>, and <see cref="UpdateFrameMatOnTick"/>:
    /// applied in place without re-initialization.
    /// WebCam fallback always sets the wrapped source <see cref="MatSourceBase.UpdateFrameMatOnTick"/> to
    /// <see langword="true"/>.
    /// </description></item>
    /// </list>
    /// One-shot <see cref="Initialize(ARFoundationCameraOpenOptions)"/> /
    /// <see cref="InitializeAsync(ARFoundationCameraOpenOptions, CancellationToken)"/>
    /// project Width / Height / FPS / IsFrontFacing. <see cref="XROrigin"/> remains an Inspector field.
    /// WebCam-only Inspector fields remain on the source inspector settings.
    /// </remarks>
    public class ARFoundationCameraToMatHelper : SourceToMatHelperBase,
        ICameraToMatHelperControls,
        ICameraFacingToMatHelperControls,
        IUnityCameraToMatHelperControls
    {
        // Private Fields
        private int _appliedWidth;
        private int _appliedHeight;
        private float _appliedFPS;
        private bool _appliedIsFrontFacing;
        private bool _appliedUpdateFrameMatOnTick;
        private EventHandler<FrameMatDeliveredEventArgs> _frameMatDelivered;
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
        private bool _autoFocusRequested = true;
        private LightEstimation _requestedLightEstimation;
        private bool _appliedAutoFocusRequested;
        private LightEstimation _appliedRequestedLightEstimation;
        private ARFoundationCameraFrameGrabber _arGrabber;
        private ARFoundationCameraMatSource _arCameraMatSource;
        private XROrigin _wiredXROrigin;
        private bool _hasLoggedArPathXROriginWarning;
#else
        private string _appliedDeviceName;
        private bool _appliedEffectiveUseAsyncGPUReadback;
#endif

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
        private WebCamTextureMatSource _webCamTextureMatSource;
        private WebCamFrameOrientationCorrector _orientationCorrector;
        private IMatSourceEvents _subscribedWebCamFrameUpdated;
        private bool _wiredUseAsyncGPUReadback;
        private ScreenOrientation _lastScreenOrientation;
        private bool _hasLoggedWebGpuAsyncGPUReadbackForce;
#endif
#if UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
        private XROriginAssignmentIssue _lastEditorXROriginIssue;
#endif

        [Header("AR Foundation")]

        [SerializeField]
        [Tooltip("The XROrigin used to resolve the AR Camera and ARCameraManager. Required on the AR Foundation path.")]
        private XROrigin _xROrigin;

        [Header("Source")]

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
        [SerializeField]
        [Tooltip("Source-specific inspector settings. Width / Height / FPS / facing apply on every platform. Device name and AsyncGPU readback apply to WebCam fallback only.")]
        private WebCamTextureSourceInspectorSettings _sourceSettings = CreateDefaultSourceSettings();
#else
        [SerializeField]
        [Tooltip("Requested camera frame width in pixels.")]
        private int _requestedWidth = 640;

        [SerializeField]
        [Tooltip("Requested camera frame height in pixels.")]
        private int _requestedHeight = 480;

        [SerializeField]
        [Tooltip("Requested camera frame rate in frames per second.")]
        private float _requestedFPS = 30f;

        private string _requestedDeviceName = string.Empty;
        private bool _requestedIsFrontFacing;
        private bool _requestedUseAsyncGPUReadback;
#endif

        [SerializeField]
        [Tooltip("When enabled, Playing ticks copy grabber frames into the owned FrameMat. When disabled, ticks still grab but leave FrameMat as a layout placeholder unless derived frames are registered. WebCam fallback always updates FrameMat on tick.")]
        private bool _updateFrameMatOnTick = false;

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
        /// <summary>
        /// Gets the source inspector settings stored on this helper.
        /// </summary>
        internal WebCamTextureSourceInspectorSettings GetSourceInspectorSettings()
        {
            EnsureSourceSettings();
            return _sourceSettings;
        }
#endif

        // Public Properties
        /// <summary>
        /// Gets or sets the XR origin used to resolve the AR camera and <c>ARCameraManager</c>.
        /// </summary>
        /// <remarks>
        /// Inspector-only. Not projected from <see cref="ARFoundationCameraOpenOptions"/>.
        /// When unset, AR Foundation <see cref="Initialize()"/> fails to open with
        /// <see cref="SourceToMatErrorCode.CAMERA_CANT_OPEN"/>.
        /// In the Editor, <see cref="OnValidate"/> logs a warning when the active build target is iOS or Android
        /// and this reference is missing or has no <see cref="XROrigin.Camera"/>.
        /// On iOS/Android device builds, a one-time warning is logged before the AR grabber is wired.
        /// </remarks>
        public XROrigin XROrigin
        {
            get => _xROrigin;
            set => _xROrigin = value;
        }

        /// <summary>
        /// Gets or sets the requested device name or index string.
        /// </summary>
        /// <remarks>
        /// Used by WebCam fallback. Ignored on the AR Foundation path.
        /// </remarks>
        public string RequestedDeviceName
        {
            get => GetRequestedDeviceName();
            set
            {
                if (GetRequestedDeviceName() == value)
                {
                    return;
                }

                SetRequestedDeviceName(value);
#if !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
                RequestApplyChanges();
#endif
            }
        }

        /// <summary>
        /// Gets or sets the requested frame width in pixels.
        /// </summary>
        public int RequestedWidth
        {
            get => GetRequestedWidth();
            set
            {
                int clampedValue = (int)Mathf.Clamp(value, 0f, float.MaxValue);
                if (GetRequestedWidth() != clampedValue)
                {
                    SetRequestedWidth(clampedValue);
                    RequestApplyChanges();
                }
            }
        }

        /// <summary>
        /// Gets or sets the requested frame height in pixels.
        /// </summary>
        public int RequestedHeight
        {
            get => GetRequestedHeight();
            set
            {
                int clampedValue = (int)Mathf.Clamp(value, 0f, float.MaxValue);
                if (GetRequestedHeight() != clampedValue)
                {
                    SetRequestedHeight(clampedValue);
                    RequestApplyChanges();
                }
            }
        }

        /// <summary>
        /// Gets or sets the requested frame rate in frames per second.
        /// </summary>
        public float RequestedFPS
        {
            get => GetRequestedFPS();
            set
            {
                float clampedValue = Mathf.Clamp(value, -1f, float.MaxValue);
                if (!Mathf.Approximately(GetRequestedFPS(), clampedValue))
                {
                    SetRequestedFPS(clampedValue);
                    RequestApplyChanges();
                }
            }
        }

        /// <summary>
        /// Gets or sets whether the front-facing camera is requested.
        /// </summary>
        /// <remarks>
        /// This is the facing request applied on AR Foundation and WebCam fallback.
        /// On AR Foundation, <see langword="true"/> maps to <see cref="CameraFacingDirection.User"/> and
        /// <see langword="false"/> maps to <see cref="CameraFacingDirection.World"/>.
        /// </remarks>
        public bool RequestedIsFrontFacing
        {
            get => GetRequestedIsFrontFacing();
            set
            {
                if (GetRequestedIsFrontFacing() == value)
                {
                    return;
                }

                SetRequestedIsFrontFacing(value);
                RequestApplyChanges();
            }
        }

        /// <summary>
        /// Gets or sets the requested AR Foundation facing direction.
        /// </summary>
        /// <remarks>
        /// AR Foundation facade for <see cref="RequestedIsFrontFacing"/>, not an independent setting.
        /// Setting <see cref="CameraFacingDirection.User"/> sets <see cref="RequestedIsFrontFacing"/> to
        /// <see langword="true"/>; <see cref="CameraFacingDirection.World"/> sets it to <see langword="false"/>.
        /// <see cref="CameraFacingDirection.None"/> is ignored.
        /// When the camera manager is not connected, the getter returns
        /// <see cref="CameraFacingDirection.User"/> or <see cref="CameraFacingDirection.World"/> from
        /// <see cref="RequestedIsFrontFacing"/>.
        /// </remarks>
        public CameraFacingDirection RequestedFacingDirection
        {
            get => GetRequestedIsFrontFacing() ? CameraFacingDirection.User : CameraFacingDirection.World;
            set
            {
                if (value == CameraFacingDirection.None)
                {
                    return;
                }

                RequestedIsFrontFacing = value == CameraFacingDirection.User;
            }
        }

        /// <summary>
        /// Gets the current AR Foundation facing direction.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <see langword="default"/>.
        /// </remarks>
        public CameraFacingDirection CurrentFacingDirection
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                return _arGrabber != null ? _arGrabber.CurrentFacingDirection : default;
#else
                return default;
#endif
            }
        }

        /// <summary>
        /// Gets or sets whether AsyncGPU readback is requested for WebCam capture.
        /// </summary>
        /// <remarks>
        /// Used by WebCam fallback. Ignored on the AR Foundation path.
        /// When <see cref="RequiresAsyncGPUReadback"/> is <see langword="true"/>, setting this to
        /// <see langword="false"/> is ignored and <see cref="EffectiveUseAsyncGPUReadback"/> remains
        /// <see langword="true"/>.
        /// </remarks>
        public bool RequestedUseAsyncGPUReadback
        {
            get => GetRequestedUseAsyncGPUReadback();
            set
            {
                if (RequiresAsyncGPUReadback && !value)
                {
                    return;
                }

                if (GetRequestedUseAsyncGPUReadback() == value)
                {
                    return;
                }

                SetRequestedUseAsyncGPUReadback(value);
#if !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
                RequestApplyChanges();
#endif
            }
        }

        /// <summary>
        /// Gets whether this environment requires AsyncGPU readback (WebGPU).
        /// </summary>
        public static bool RequiresAsyncGPUReadback
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                return SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Gets whether the AsyncGPU grabber is selected for the next (or current) WebCam wiring.
        /// </summary>
        public bool EffectiveUseAsyncGPUReadback => RequestedUseAsyncGPUReadback || RequiresAsyncGPUReadback;

        /// <summary>
        /// Gets or sets whether Playing ticks copy grabber frames into the owned <see cref="SourceToMatHelperBase.FrameMat"/>.
        /// </summary>
        /// <remarks>
        /// Default is <see langword="false"/>. When the wrapped source is wired, this assignment is copied to
        /// <see cref="MatSourceBase.UpdateFrameMatOnTick"/> immediately on the AR Foundation path, including
        /// while initialization is in progress.
        /// Inspector edits still apply after that delay via <see cref="OnValidate"/>.
        /// WebCam fallback always sets the wrapped source to <see langword="true"/> so tick-cloned
        /// <see cref="FrameMatDelivered"/> delivery keeps working.
        /// When derived frames are registered, the wrapped source still updates
        /// <see cref="SourceToMatHelperBase.FrameMat"/> on tick.
        /// </remarks>
        public bool UpdateFrameMatOnTick
        {
            get => _updateFrameMatOnTick;
            set
            {
                if (_updateFrameMatOnTick == value)
                {
                    return;
                }

                _updateFrameMatOnTick = value;
                ApplyUpdateFrameMatOnTickToWiredMatSource();
                _appliedUpdateFrameMatOnTick = _updateFrameMatOnTick;
            }
        }

        /// <summary>
        /// Gets the effective device name after initialization.
        /// Returns an empty string when the source is not wired.
        /// </summary>
        public string DeviceName
        {
            get
            {
                return MatSource is ICameraMatSource cameraMatSource ? cameraMatSource.DeviceName : string.Empty;
            }
        }

        /// <summary>
        /// Gets the effective frame rate reported by the wrapped source.
        /// Returns <c>-1</c> when the source is not wired.
        /// </summary>
        public float FPS
        {
            get
            {
                return MatSource is ICameraMatSource cameraMatSource ? cameraMatSource.FPS : -1f;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the front-facing camera is active.
        /// </summary>
        public bool IsFrontFacing
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                return _arGrabber != null && _arGrabber.IsFrontFacing;
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
                return _webCamTextureMatSource != null && _webCamTextureMatSource.IsFrontFacing;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Gets the camera-to-world matrix from the wrapped <see cref="IUnityCameraPoseProvider"/>.
        /// Returns <see cref="Matrix4x4.identity"/> when the source is not wired.
        /// </summary>
        public Matrix4x4 CameraToWorldMatrix
        {
            get
            {
                return MatSource is IUnityCameraPoseProvider poseProvider ? poseProvider.CameraToWorldMatrix : Matrix4x4.identity;
            }
        }

        /// <summary>
        /// Gets the projection matrix from the wrapped <see cref="IUnityCameraPoseProvider"/>.
        /// Returns <see cref="Matrix4x4.identity"/> when the source is not wired.
        /// </summary>
        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                return MatSource is IUnityCameraPoseProvider poseProvider ? poseProvider.ProjectionMatrix : Matrix4x4.identity;
            }
        }

        /// <summary>
        /// Gets the latest display-adjusted AR camera intrinsics.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <see langword="default"/>.
        /// </remarks>
        public XRCameraIntrinsics Intrinsics
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                if (_arCameraMatSource != null)
                {
                    return _arCameraMatSource.Intrinsics;
                }
#endif
                return default;
            }
        }

        /// <summary>
        /// Gets the latest AR camera timestamp in nanoseconds.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <c>0</c>.
        /// </remarks>
        public long TimestampNs
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                if (_arCameraMatSource != null)
                {
                    return _arCameraMatSource.TimestampNs;
                }
#endif
                return 0L;
            }
        }

        /// <summary>
        /// Gets the latest display rotation in degrees.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <c>0</c>.
        /// </remarks>
        public int DisplayRotationAngle
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                if (_arCameraMatSource != null)
                {
                    return _arCameraMatSource.DisplayRotationAngle;
                }
#endif
                return 0;
            }
        }

        /// <summary>
        /// Gets whether the latest published snapshot flips vertically for display.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <see langword="false"/>.
        /// </remarks>
        public bool DisplayFlipVertical
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                if (_arCameraMatSource != null)
                {
                    return _arCameraMatSource.DisplayFlipVertical;
                }
#endif
                return false;
            }
        }

        /// <summary>
        /// Gets whether the latest published snapshot flips horizontally for display.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <see langword="false"/>.
        /// </remarks>
        public bool DisplayFlipHorizontal
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                if (_arCameraMatSource != null)
                {
                    return _arCameraMatSource.DisplayFlipHorizontal;
                }
#endif
                return false;
            }
        }

        /// <summary>
        /// Gets whether autofocus is currently enabled.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <see langword="default"/>.
        /// </remarks>
        public bool AutoFocusEnabled
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                return _arGrabber != null && _arGrabber.AutoFocusEnabled;
#else
                return default;
#endif
            }
        }

        /// <summary>
        /// Gets or sets whether autofocus is requested.
        /// </summary>
        /// <remarks>
        /// Applied on the AR Foundation path. WebCam fallback ignores the setter and returns <see langword="default"/>.
        /// </remarks>
        public bool AutoFocusRequested
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                return _arGrabber != null ? _arGrabber.AutoFocusRequested : _autoFocusRequested;
#else
                return default;
#endif
            }
            set
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                if (AutoFocusRequested == value)
                {
                    return;
                }

                _autoFocusRequested = value;
                if (_arGrabber != null)
                {
                    _arGrabber.AutoFocusRequested = value;
                }

                RequestApplyChanges();
#else
                _ = value;
#endif
            }
        }

        /// <summary>
        /// Gets the current AR Foundation light estimation.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <see langword="default"/>.
        /// </remarks>
        public LightEstimation CurrentLightEstimation
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                return _arGrabber != null ? _arGrabber.CurrentLightEstimation : default;
#else
                return default;
#endif
            }
        }

        /// <summary>
        /// Gets or sets the requested AR Foundation light estimation.
        /// </summary>
        /// <remarks>
        /// Applied on the AR Foundation path. WebCam fallback ignores the setter and returns <see langword="default"/>.
        /// </remarks>
        public LightEstimation RequestedLightEstimation
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                return _arGrabber != null ? _arGrabber.RequestedLightEstimation : _requestedLightEstimation;
#else
                return default;
#endif
            }
            set
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                if (RequestedLightEstimation == value)
                {
                    return;
                }

                _requestedLightEstimation = value;
                if (_arGrabber != null)
                {
                    _arGrabber.RequestedLightEstimation = value;
                }

                RequestApplyChanges();
#else
                _ = value;
#endif
            }
        }

        /// <summary>
        /// Gets whether camera permission has been granted.
        /// </summary>
        /// <remarks>
        /// WebCam fallback returns <see langword="default"/>.
        /// </remarks>
        public bool PermissionGranted
        {
            get
            {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                return _arGrabber != null && _arGrabber.PermissionGranted;
#else
                return default;
#endif
            }
        }

        /// <summary>
        /// Gets the list of connected camera devices. May be empty when enumeration is unsupported or unavailable.
        /// </summary>
        public IReadOnlyList<CameraDeviceInfo> SupportedDevices
        {
            get
            {
                return MatSource is ICameraMatSource cameraMatSource
                    ? cameraMatSource.SupportedDevices
                    : Array.Empty<CameraDeviceInfo>();
            }
        }

        /// <summary>
        /// Gets the list of connected Unity WebCam devices, including facing and kind.
        /// May be empty when enumeration is unsupported or unavailable.
        /// </summary>
        public IReadOnlyList<UnityCameraDeviceInfo> SupportedUnityDevices
        {
            get
            {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
                if (_webCamTextureMatSource != null)
                {
                    return _webCamTextureMatSource.SupportedUnityDevices;
                }
#endif
                return Array.Empty<UnityCameraDeviceInfo>();
            }
        }

        /// <summary>
        /// Gets supported resolutions for the currently resolved device.
        /// </summary>
        public IReadOnlyList<CameraResolution> SupportedResolutions
        {
            get
            {
                return MatSource is ICameraMatSource cameraMatSource
                    ? cameraMatSource.SupportedResolutions
                    : Array.Empty<CameraResolution>();
            }
        }

        // Public Events
        /// <summary>
        /// Raised when a new converted frame is available while <see cref="SourceToMatHelperBase.IsPlaying"/>
        /// is <see langword="true"/>.
        /// </summary>
        /// <remarks>
        /// On AR Foundation this is forwarded from <c>ARFoundationCameraMatSource.FrameMatDelivered</c> on the
        /// ConvertAsync completion callback and is not marshaled to the Unity main thread.
        /// The helper subscribes to that source event only while this event has at least one subscriber.
        /// On WebCam fallback the helper clones <see cref="SourceToMatHelperBase.FrameMat"/> on the Unity main
        /// thread when <see cref="SourceToMatHelperBase.DidUpdateThisFrame"/> is <see langword="true"/>.
        /// <see cref="FrameMatDeliveredEventArgs.Mat"/> is a newly allocated buffer; the subscriber must dispose it.
        /// Does not fire while paused, ready, or uninitialized.
        /// </remarks>
        public event EventHandler<FrameMatDeliveredEventArgs> FrameMatDelivered
        {
            add
            {
                _frameMatDelivered += value;
                SyncDeviceFrameMatDeliveredSubscription();
            }
            remove
            {
                _frameMatDelivered -= value;
                SyncDeviceFrameMatDeliveredSubscription();
            }
        }

        // Unity Lifecycle Methods
        protected virtual void Awake()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
            _lastScreenOrientation = Screen.orientation;
#endif
            EnsureMatSourceWired();
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
            if (_orientationCorrector != null)
            {
                _orientationCorrector.NotifyDisplayOrientationChanged((int)_lastScreenOrientation);
            }
#endif
            SyncInspectorToMatSource();
            CaptureAppliedSnapshot();
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
            LogWebGpuAsyncGPUReadbackForceOnce();
#endif
        }

        /// <inheritdoc/>
        protected override void Update()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
            ScreenOrientation currentOrientation = Screen.orientation;
            if (_lastScreenOrientation != currentOrientation)
            {
                _lastScreenOrientation = currentOrientation;
                if (_orientationCorrector != null)
                {
                    _orientationCorrector.NotifyDisplayOrientationChanged((int)currentOrientation);
                }
            }
#endif
            base.Update();
        }

        /// <inheritdoc/>
        protected override void OnValidate()
        {
            EnsureSourceSettings();
            ClampRequestedDimensions();
#if UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            ValidateXROriginInspectorAssignment();
#endif
            base.OnValidate();
        }

        /// <inheritdoc/>
        protected override void OnDestroy()
        {
            UnsubscribeFrameMatDeliveredBridge();
            base.OnDestroy();
        }

        // Public Methods
        /// <summary>
        /// Gets supported resolutions for the specified device name or index string.
        /// </summary>
        /// <param name="deviceNameOrIndex">A device name or index string.</param>
        /// <returns>
        /// Supported resolutions for the device, or an empty list when the device is unknown,
        /// unsupported, or the backend does not enumerate capture modes (platform-dependent).
        /// </returns>
        public IReadOnlyList<CameraResolution> GetSupportedResolutions(string deviceNameOrIndex)
        {
            return MatSource is ICameraMatSource cameraMatSource
                ? cameraMatSource.GetSupportedResolutions(deviceNameOrIndex)
                : Array.Empty<CameraResolution>();
        }

        /// <summary>
        /// Initializes the helper without awaiting completion.
        /// Playback remains in the ready state until <see cref="SourceToMatHelperBase.Play"/> or
        /// <see cref="SourceToMatHelperBase.PlayAsync"/> is called.
        /// </summary>
        public new void Initialize()
        {
            _ = InitializeAsync();
        }

        /// <summary>
        /// Initializes the helper asynchronously.
        /// Playback remains in the ready state until <see cref="SourceToMatHelperBase.PlayAsync"/> is called.
        /// When initialization is already in progress, the call is ignored.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when initialization finishes, or immediately when initialization is already in progress.</returns>
        public new async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitializing)
            {
                return;
            }

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API && !((UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API)
            LogWebGpuAsyncGPUReadbackForceOnce();
#endif
            EnsureMatSourceWired();
            if (MatSource == null)
            {
                return;
            }

            SyncInspectorToMatSource();
            await RunInitializeAsync(PlaybackRestoreMode.Ready, cancellationToken);
            SyncCommonSettingsAndCaptureAppliedSnapshot();
        }

        /// <summary>
        /// Initializes the helper from one-shot AR Foundation camera open options without awaiting completion.
        /// Playback remains in the ready state until <see cref="SourceToMatHelperBase.Play"/> or
        /// <see cref="SourceToMatHelperBase.PlayAsync"/> is called.
        /// </summary>
        /// <param name="options">Open options that project Width / Height / FPS / IsFrontFacing before initialization.</param>
        public void Initialize(ARFoundationCameraOpenOptions options)
        {
            _ = InitializeAsync(options);
        }

        /// <summary>
        /// Initializes the helper from one-shot AR Foundation camera open options asynchronously.
        /// Playback remains in the ready state until <see cref="SourceToMatHelperBase.PlayAsync"/> is called.
        /// When initialization is already in progress, the call is ignored.
        /// </summary>
        /// <param name="options">Open options that project Width / Height / FPS / IsFrontFacing before initialization.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when initialization finishes, or immediately when initialization is already in progress.</returns>
        public async Task InitializeAsync(ARFoundationCameraOpenOptions options, CancellationToken cancellationToken = default)
        {
            if (_isInitializing)
            {
                return;
            }

            if (options != null)
            {
                EnsureSourceSettings();
                SetRequestedWidth((int)Mathf.Clamp(options.Width, 0f, float.MaxValue));
                SetRequestedHeight((int)Mathf.Clamp(options.Height, 0f, float.MaxValue));
                SetRequestedFPS(Mathf.Clamp(options.FPS, -1f, float.MaxValue));
                SetRequestedIsFrontFacing(options.IsFrontFacing);
            }

            await InitializeAsync(cancellationToken);
        }

        // Protected Methods
        /// <inheritdoc/>
        protected override bool HasHeavyRequestedChanges()
        {
            EnsureSourceSettings();
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            return GetRequestedWidth() != _appliedWidth
                || GetRequestedHeight() != _appliedHeight
                || !Mathf.Approximately(GetRequestedFPS(), _appliedFPS)
                || GetRequestedIsFrontFacing() != _appliedIsFrontFacing
                || _autoFocusRequested != _appliedAutoFocusRequested
                || _requestedLightEstimation != _appliedRequestedLightEstimation;
#else
            return GetRequestedDeviceName() != _appliedDeviceName
                || GetRequestedWidth() != _appliedWidth
                || GetRequestedHeight() != _appliedHeight
                || !Mathf.Approximately(GetRequestedFPS(), _appliedFPS)
                || GetRequestedIsFrontFacing() != _appliedIsFrontFacing
                || EffectiveUseAsyncGPUReadback != _appliedEffectiveUseAsyncGPUReadback;
#endif
        }

        /// <inheritdoc/>
        protected override async Task ApplyHeavyRequestedChangesAsync(CancellationToken cancellationToken = default)
        {
            EnsureSourceSettings();
            int applyingWidth = GetRequestedWidth();
            int applyingHeight = GetRequestedHeight();
            float applyingFPS = GetRequestedFPS();
            bool applyingIsFrontFacing = GetRequestedIsFrontFacing();
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            bool applyingAutoFocusRequested = _autoFocusRequested;
            LightEstimation applyingRequestedLightEstimation = _requestedLightEstimation;
#else
            string applyingDeviceName = GetRequestedDeviceName();
            bool applyingEffectiveUseAsyncGPUReadback = EffectiveUseAsyncGPUReadback;
#endif

            try
            {
                if (!IsInitialized)
                {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                    EnsureMatSourceWired();
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
                    RewireMatSourceIfGrabberPathChanged();
#endif
                    SyncInspectorToMatSource();
                    if (_hasCompletedInitialize)
                    {
                        await RecoverInitializeForHeavyApplyAsync(cancellationToken);
                    }

                    return;
                }

                PlaybackRestoreMode mode = CapturePlaybackRestoreMode();
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                bool needsOriginRewire = _wiredXROrigin != _xROrigin;
                if (needsOriginRewire)
                {
                    if (MatSource != null && MatSource.IsInitialized)
                    {
                        await MatSource.ReleaseAsync(cancellationToken);
                    }

                    EnsureMatSourceWired(forceRewire: true);
                    SyncInspectorToMatSource();
                    await RunInitializeAsync(mode, cancellationToken);
                    return;
                }
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
                bool needsGrabberRewire = EffectiveUseAsyncGPUReadback != _wiredUseAsyncGPUReadback;
                if (needsGrabberRewire)
                {
                    if (MatSource != null && MatSource.IsInitialized)
                    {
                        await MatSource.ReleaseAsync(cancellationToken);
                    }

                    RewireMatSourceIfGrabberPathChanged();
                    SyncInspectorToMatSource();
                    await RunInitializeAsync(mode, cancellationToken);
                    return;
                }
#endif
                SyncInspectorToMatSource();
                await RunReinitializeAsync(mode, cancellationToken);
            }
            finally
            {
                SyncCommonSettingsAndCaptureAppliedSnapshot();
                _appliedWidth = applyingWidth;
                _appliedHeight = applyingHeight;
                _appliedFPS = applyingFPS;
                _appliedIsFrontFacing = applyingIsFrontFacing;
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
                _appliedAutoFocusRequested = applyingAutoFocusRequested;
                _appliedRequestedLightEstimation = applyingRequestedLightEstimation;
#else
                _appliedDeviceName = applyingDeviceName;
                _appliedEffectiveUseAsyncGPUReadback = applyingEffectiveUseAsyncGPUReadback;
#endif
            }
        }

        /// <inheritdoc/>
        protected override void CaptureAppliedSnapshot()
        {
            EnsureSourceSettings();
            _appliedWidth = GetRequestedWidth();
            _appliedHeight = GetRequestedHeight();
            _appliedFPS = GetRequestedFPS();
            _appliedIsFrontFacing = GetRequestedIsFrontFacing();
            _appliedUpdateFrameMatOnTick = _updateFrameMatOnTick;
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            _appliedAutoFocusRequested = _autoFocusRequested;
            _appliedRequestedLightEstimation = _requestedLightEstimation;
#else
            _appliedDeviceName = GetRequestedDeviceName();
            _appliedEffectiveUseAsyncGPUReadback = EffectiveUseAsyncGPUReadback;
#endif
            base.CaptureAppliedSnapshot();
        }

        /// <inheritdoc/>
        protected override bool HasCheapRequestedChanges()
        {
            return base.HasCheapRequestedChanges() || _updateFrameMatOnTick != _appliedUpdateFrameMatOnTick;
        }

        /// <inheritdoc/>
        protected override void ApplyCheapRequestedChanges()
        {
            ApplyUpdateFrameMatOnTickToWiredMatSource();
            _appliedUpdateFrameMatOnTick = _updateFrameMatOnTick;
            base.ApplyCheapRequestedChanges();
        }

        // Private Methods
        private enum XROriginAssignmentIssue
        {
            None = 0,
            MissingOrigin = 1,
            MissingCamera = 2,
        }

        private XROriginAssignmentIssue GetXROriginAssignmentIssue()
        {
            if (_xROrigin == null)
            {
                return XROriginAssignmentIssue.MissingOrigin;
            }

            if (_xROrigin.Camera == null)
            {
                return XROriginAssignmentIssue.MissingCamera;
            }

            return XROriginAssignmentIssue.None;
        }

        private void LogXROriginAssignmentIssueWarning(XROriginAssignmentIssue issue)
        {
            switch (issue)
            {
                case XROriginAssignmentIssue.MissingOrigin:
                    Debug.LogWarning(
                        "ARFoundationCameraToMatHelper: XROrigin is not assigned. iOS/Android device builds use the "
                        + "AR Foundation path and Initialize() will fail with CAMERA_CANT_OPEN. Assign the scene "
                        + "XROrigin in the Inspector.",
                        this);
                    break;
                case XROriginAssignmentIssue.MissingCamera:
                    Debug.LogWarning(
                        "ARFoundationCameraToMatHelper: XROrigin is assigned but Camera is null. Check that the XR "
                        + "Origin references an AR camera with ARCameraManager.",
                        this);
                    break;
            }
        }

#if UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
        private void ValidateXROriginInspectorAssignment()
        {
            BuildTarget activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            if (activeBuildTarget != BuildTarget.Android && activeBuildTarget != BuildTarget.iOS)
            {
                _lastEditorXROriginIssue = XROriginAssignmentIssue.None;
                return;
            }

            XROriginAssignmentIssue issue = GetXROriginAssignmentIssue();
            if (issue == _lastEditorXROriginIssue)
            {
                return;
            }

            _lastEditorXROriginIssue = issue;
            if (issue == XROriginAssignmentIssue.None)
            {
                return;
            }

            LogXROriginAssignmentIssueWarning(issue);
        }
#endif

#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
        private void WarnOnceIfXROriginInvalidForArPath()
        {
            if (_hasLoggedArPathXROriginWarning)
            {
                return;
            }

            XROriginAssignmentIssue issue = GetXROriginAssignmentIssue();
            if (issue == XROriginAssignmentIssue.None)
            {
                return;
            }

            _hasLoggedArPathXROriginWarning = true;
            LogXROriginAssignmentIssueWarning(issue);
        }
#endif

        private void EnsureMatSourceWired(bool forceRewire = false)
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            EnsureArMatSourceWired(forceRewire);
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureWebCamMatSourceWired(forceRewire);
#else
            _ = forceRewire;
            Debug.LogWarning(
                "ARFoundationCameraToMatHelper: no camera source is available. Enable AR Foundation on iOS/Android or the WebCamTexture API.",
                this);
#endif
        }

#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
        private void EnsureArMatSourceWired(bool forceRewire)
        {
            if (!forceRewire && _arCameraMatSource != null && _wiredXROrigin == _xROrigin)
            {
                return;
            }

            WarnOnceIfXROriginInvalidForArPath();

            UnsubscribeFrameMatDeliveredBridge();
            IMatSource previousMatSource = MatSource;
            _arGrabber = new ARFoundationCameraFrameGrabber(_xROrigin);
            _arCameraMatSource = new ARFoundationCameraMatSource(_arGrabber);
            _wiredXROrigin = _xROrigin;
            SetMatSource(_arCameraMatSource);
            _arCameraMatSource.UpdateFrameMatOnTick = _updateFrameMatOnTick;
            SyncDeviceFrameMatDeliveredSubscription();

            if (previousMatSource != null && !ReferenceEquals(previousMatSource, MatSource))
            {
                previousMatSource.Dispose();
            }
        }

        private void OnArFrameMatDelivered(object sender, FrameMatDeliveredEventArgs e)
        {
            _ = sender;
            if (e == null)
            {
                return;
            }

            EventHandler<FrameMatDeliveredEventArgs> handler = _frameMatDelivered;
            if (!IsPlaying || handler == null)
            {
                e.Mat?.Dispose();
                return;
            }

            handler(this, e);
        }
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
        private void RewireMatSourceIfGrabberPathChanged()
        {
            if (_webCamTextureMatSource != null && EffectiveUseAsyncGPUReadback == _wiredUseAsyncGPUReadback)
            {
                return;
            }

            IMatSource previousMatSource = MatSource;
            EnsureMatSourceWired(forceRewire: true);

            if (previousMatSource != null && !ReferenceEquals(previousMatSource, MatSource))
            {
                previousMatSource.Dispose();
            }
        }

        private void EnsureWebCamMatSourceWired(bool forceRewire)
        {
            bool wantAsyncGPUReadback = EffectiveUseAsyncGPUReadback;
            if (!forceRewire && _webCamTextureMatSource != null && _wiredUseAsyncGPUReadback == wantAsyncGPUReadback)
            {
                return;
            }

            if (_orientationCorrector == null)
            {
                _orientationCorrector = new WebCamFrameOrientationCorrector();
            }

            IFrameGrabber grabber;
            if (wantAsyncGPUReadback)
            {
                grabber = new WebCamTextureAsyncGPUReadbackFrameGrabber();
            }
            else
            {
                grabber = new WebCamTextureFrameGrabber();
            }

            UnsubscribeFrameMatDeliveredBridge();
            _webCamTextureMatSource = new WebCamTextureMatSource(grabber, _orientationCorrector);
            _wiredUseAsyncGPUReadback = wantAsyncGPUReadback;
            SetMatSource(_webCamTextureMatSource);
            _webCamTextureMatSource.UpdateFrameMatOnTick = true;
            SubscribeWebCamFrameMatUpdated();
        }

        private void SubscribeWebCamFrameMatUpdated()
        {
            UnsubscribeFrameMatDeliveredBridge();
            if (_webCamTextureMatSource is not IMatSourceEvents events)
            {
                return;
            }

            events.OnFrameMatUpdated += OnWebCamFrameMatUpdated;
            _subscribedWebCamFrameUpdated = events;
        }

        private void OnWebCamFrameMatUpdated()
        {
            RaiseFrameMatDeliveredFromFrameMat();
        }

        private void RaiseFrameMatDeliveredFromFrameMat()
        {
            if (!IsPlaying || !DidUpdateThisFrame)
            {
                return;
            }

            EventHandler<FrameMatDeliveredEventArgs> handler = _frameMatDelivered;
            if (handler == null)
            {
                return;
            }

            IMatSource matSource = MatSource;
            if (matSource == null)
            {
                return;
            }

            Mat frameMat = matSource.FrameMat;
            if (frameMat == null || frameMat.IsDisposed)
            {
                return;
            }

            Mat deliveredMat = frameMat.clone();
            bool ownershipTransferred = false;
            try
            {
                if (deliveredMat == null || deliveredMat.IsDisposed || deliveredMat.empty())
                {
                    return;
                }

                if (!IsPlaying)
                {
                    return;
                }

                handler = _frameMatDelivered;
                if (handler == null)
                {
                    return;
                }

                handler(
                    this,
                    new FrameMatDeliveredEventArgs(
                        deliveredMat,
                        ProjectionMatrix,
                        CameraToWorldMatrix,
                        default,
                        0L));
                ownershipTransferred = true;
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    deliveredMat?.Dispose();
                }
            }
        }

        private void LogWebGpuAsyncGPUReadbackForceOnce()
        {
            if (!RequiresAsyncGPUReadback || _hasLoggedWebGpuAsyncGPUReadbackForce)
            {
                return;
            }

            _hasLoggedWebGpuAsyncGPUReadbackForce = true;
            Debug.Log(
                "ARFoundationCameraToMatHelper: WebGPU requires AsyncGPU readback; EffectiveUseAsyncGPUReadback is forced to true.",
                this);
        }
#endif

        private void SyncDeviceFrameMatDeliveredSubscription()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            if (_arCameraMatSource == null)
            {
                return;
            }

            _arCameraMatSource.FrameMatDelivered -= OnArFrameMatDelivered;
            if (_frameMatDelivered != null)
            {
                _arCameraMatSource.FrameMatDelivered += OnArFrameMatDelivered;
            }
#endif
        }

        private void UnsubscribeFrameMatDeliveredBridge()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            if (_arCameraMatSource != null)
            {
                _arCameraMatSource.FrameMatDelivered -= OnArFrameMatDelivered;
            }
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            if (_subscribedWebCamFrameUpdated != null)
            {
                _subscribedWebCamFrameUpdated.OnFrameMatUpdated -= OnWebCamFrameMatUpdated;
                _subscribedWebCamFrameUpdated = null;
            }
#endif
        }

        private void SyncInspectorToMatSource()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            if (_arCameraMatSource != null)
            {
                _arCameraMatSource.RequestedDeviceName = GetRequestedDeviceName();
                _arCameraMatSource.RequestedWidth = GetRequestedWidth();
                _arCameraMatSource.RequestedHeight = GetRequestedHeight();
                _arCameraMatSource.RequestedFPS = GetRequestedFPS();
                _arCameraMatSource.RequestedIsFrontFacing = GetRequestedIsFrontFacing();
            }

            if (_arGrabber != null)
            {
                _arGrabber.AutoFocusRequested = _autoFocusRequested;
                _arGrabber.RequestedLightEstimation = _requestedLightEstimation;
            }
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            if (_webCamTextureMatSource != null)
            {
                _webCamTextureMatSource.RequestedDeviceName = GetRequestedDeviceName();
                _webCamTextureMatSource.RequestedWidth = GetRequestedWidth();
                _webCamTextureMatSource.RequestedHeight = GetRequestedHeight();
                _webCamTextureMatSource.RequestedFPS = GetRequestedFPS();
                _webCamTextureMatSource.RequestedIsFrontFacing = GetRequestedIsFrontFacing();
            }
#endif
            if (MatSource == null)
            {
                return;
            }

            ApplyUpdateFrameMatOnTickToWiredMatSource();
            SyncCommonSettingsToMatSource();
            SyncDerivedFramesToMatSource();
        }

        private void ApplyUpdateFrameMatOnTickToWiredMatSource()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR && !DISABLE_ARFOUNDATION_API
            SyncUpdateFrameMatOnTickToMatSource(_updateFrameMatOnTick);
#elif !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            SyncUpdateFrameMatOnTickToMatSource(true);
#endif
        }

        private void EnsureSourceSettings()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            if (_sourceSettings == null)
            {
                _sourceSettings = CreateDefaultSourceSettings();
            }
#endif
        }

        private void ClampRequestedDimensions()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            _sourceSettings.ClampDimensions();
#else
            _requestedWidth = (int)Mathf.Clamp(_requestedWidth, 0f, float.MaxValue);
            _requestedHeight = (int)Mathf.Clamp(_requestedHeight, 0f, float.MaxValue);
            _requestedFPS = Mathf.Clamp(_requestedFPS, -1f, float.MaxValue);
#endif
        }

        private string GetRequestedDeviceName()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            return _sourceSettings.RequestedDeviceName;
#else
            return _requestedDeviceName ?? string.Empty;
#endif
        }

        private void SetRequestedDeviceName(string value)
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            _sourceSettings.RequestedDeviceName = value;
#else
            _requestedDeviceName = value ?? string.Empty;
#endif
        }

        private int GetRequestedWidth()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            return _sourceSettings.RequestedWidth;
#else
            return _requestedWidth;
#endif
        }

        private void SetRequestedWidth(int value)
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            _sourceSettings.RequestedWidth = value;
#else
            _requestedWidth = value;
#endif
        }

        private int GetRequestedHeight()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            return _sourceSettings.RequestedHeight;
#else
            return _requestedHeight;
#endif
        }

        private void SetRequestedHeight(int value)
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            _sourceSettings.RequestedHeight = value;
#else
            _requestedHeight = value;
#endif
        }

        private float GetRequestedFPS()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            return _sourceSettings.RequestedFPS;
#else
            return _requestedFPS;
#endif
        }

        private void SetRequestedFPS(float value)
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            _sourceSettings.RequestedFPS = value;
#else
            _requestedFPS = value;
#endif
        }

        private bool GetRequestedIsFrontFacing()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            return _sourceSettings.RequestedIsFrontFacing;
#else
            return _requestedIsFrontFacing;
#endif
        }

        private void SetRequestedIsFrontFacing(bool value)
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            _sourceSettings.RequestedIsFrontFacing = value;
#else
            _requestedIsFrontFacing = value;
#endif
        }

        private bool GetRequestedUseAsyncGPUReadback()
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            return _sourceSettings.RequestedUseAsyncGPUReadback;
#else
            return _requestedUseAsyncGPUReadback;
#endif
        }

        private void SetRequestedUseAsyncGPUReadback(bool value)
        {
#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
            EnsureSourceSettings();
            _sourceSettings.RequestedUseAsyncGPUReadback = value;
#else
            _requestedUseAsyncGPUReadback = value;
#endif
        }

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
        private static WebCamTextureSourceInspectorSettings CreateDefaultSourceSettings()
        {
            return new WebCamTextureSourceInspectorSettings
            {
                RequestedWidth = 640,
                RequestedHeight = 480,
                RequestedFPS = 30f
            };
        }
#endif
    }
}
