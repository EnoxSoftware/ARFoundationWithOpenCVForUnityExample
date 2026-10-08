using System.Collections;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ARFoundationWithOpenCVForUnityExample
{
    public class ARFoundationWithOpenCVForUnityExample : MonoBehaviour
    {
        // Constants
#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
        private static float _verticalNormalizedPosition = 1f;

        // Public Fields
        [Header("UI")]
        public Text ExampleTitle;
        public Text VersionInfo;
        public ScrollRect ScrollRect;

        // Unity Lifecycle Methods
        private void Awake()
        {
            //QualitySettings.vSyncCount = 0;
            //Application.targetFrameRate = 60;
        }

        private IEnumerator Start()
        {
            ExampleTitle.text = "ARFoundationWithOpenCVForUnity Example " + Application.version;

            VersionInfo.text = "ARFoundation " + GetARFoundationVersion();
            VersionInfo.text += " / " + OpenCVForUnity.CoreModule.Core.NATIVE_LIBRARY_NAME + " " + OpenCVForUnityEnv.GetVersion() + " (" + OpenCVForUnity.CoreModule.Core.VERSION + ")";
            VersionInfo.text += " / UnityEditor " + Application.unityVersion;
            VersionInfo.text += " / ";
#if UNITY_EDITOR
            VersionInfo.text += "Editor";
#elif UNITY_STANDALONE_WIN
            VersionInfo.text += "Windows";
#elif UNITY_STANDALONE_OSX
            VersionInfo.text += "Mac OSX";
#elif UNITY_STANDALONE_LINUX
            VersionInfo.text += "Linux";
#elif UNITY_ANDROID
            VersionInfo.text += "Android";
#elif UNITY_IOS
            VersionInfo.text += "iOS";
#elif UNITY_WSA
            VersionInfo.text += "WSA";
#elif UNITY_WEBGL
            VersionInfo.text += "WebGL";
#endif
            VersionInfo.text += " ";
#if ENABLE_MONO
            VersionInfo.text += "Mono";
#elif ENABLE_IL2CPP
            VersionInfo.text += "IL2CPP";
#elif ENABLE_DOTNET
            VersionInfo.text += ".NET";
#endif

            ScrollRect.verticalNormalizedPosition = _verticalNormalizedPosition;

            // #if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
            //             RuntimePermissionHelper runtimePermissionHelper = GetComponent<RuntimePermissionHelper>();
            //             yield return runtimePermissionHelper.HasUserAuthorizedCameraPermission();
            // #endif

            yield break;
        }

        // Public Methods
        public static string GetARFoundationVersion()
        {
            return Resources.Load<TextAsset>("ARFoundationVersion").text;
        }

        public void OnScrollRectValueChanged()
        {
            _verticalNormalizedPosition = ScrollRect.verticalNormalizedPosition;
        }

        public void OnShowSystemInfoButtonClick()
        {
            SceneManager.LoadScene("ShowSystemInfo");
        }

        public void OnShowLicenseButtonClick()
        {
            SceneManager.LoadScene("ShowLicense");
        }

        public void OnARFoundationCameraToMatExampleButtonClick()
        {
            SceneManager.LoadScene("ARFoundationCameraToMatExample");
        }

        public void OnConvertAsyncARFoundationCameraToMatExampleButtonClick()
        {
            SceneManager.LoadScene("ConvertAsyncARFoundationCameraToMatExample");
        }

        public void OnARFoundationCameraToMatHelperExampleButtonClick()
        {
            SceneManager.LoadScene("ARFoundationCameraToMatHelperExample");
        }

        public void OnARFoundationCameraArUcoExampleButtonClick()
        {
            SceneManager.LoadScene("ARFoundationCameraArUcoExample");
        }
    }
}
