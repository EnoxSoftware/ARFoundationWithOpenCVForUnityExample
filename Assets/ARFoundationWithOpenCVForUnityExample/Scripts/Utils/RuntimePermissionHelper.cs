using System.Collections;
using UnityEngine;

namespace ARFoundationWithOpenCVForUnityExample
{
    public class RuntimePermissionHelper : MonoBehaviour
    {
#if (UNITY_IOS && UNITY_2018_1_OR_NEWER) || (UNITY_ANDROID && UNITY_2018_3_OR_NEWER)
        // Protected Fields
        protected bool _isUserRequestingPermission;
#endif

        // Public Methods
        public virtual IEnumerator HasUserAuthorizedCameraPermission()
        {
#if UNITY_IOS && UNITY_2018_1_OR_NEWER
            UserAuthorization mode = UserAuthorization.WebCam;
            if (!Application.HasUserAuthorization(mode))
            {
                yield return RequestUserAuthorization(mode);
            }
            yield return Application.HasUserAuthorization(mode);
#elif UNITY_ANDROID && UNITY_2018_3_OR_NEWER
            string permission = UnityEngine.Android.Permission.Camera;
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
            {
                yield return RequestUserPermission(permission);
            }
            yield return UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission);
#else
            yield return true;
#endif
        }

        public virtual IEnumerator HasUserAuthorizedMicrophonePermission()
        {
#if UNITY_IOS && UNITY_2018_1_OR_NEWER
            UserAuthorization mode = UserAuthorization.Microphone;
            if (!Application.HasUserAuthorization(mode))
            {
                yield return RequestUserAuthorization(mode);
            }
            yield return Application.HasUserAuthorization(mode);
#elif UNITY_ANDROID && UNITY_2018_3_OR_NEWER
            string permission = UnityEngine.Android.Permission.Microphone;
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
            {
                yield return RequestUserPermission(permission);
            }
            yield return UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission);
#else
            yield return true;
#endif
        }

        public virtual IEnumerator HasUserAuthorizedExternalStorageWritePermission()
        {
#if UNITY_ANDROID && UNITY_2018_3_OR_NEWER
            string permission = UnityEngine.Android.Permission.ExternalStorageWrite;
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
            {
                yield return RequestUserPermission(permission);
            }
            yield return UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission);
#else
            yield return true;
#endif
        }

#if (UNITY_IOS && UNITY_2018_1_OR_NEWER) || (UNITY_ANDROID && UNITY_2018_3_OR_NEWER)
        // Protected Methods
        protected virtual IEnumerator OnApplicationFocus(bool hasFocus)
        {
            yield return null;

            if (_isUserRequestingPermission && hasFocus)
            {
                _isUserRequestingPermission = false;
            }
        }

#if UNITY_IOS
        protected virtual IEnumerator RequestUserAuthorization(UserAuthorization mode)
        {
            _isUserRequestingPermission = true;
            yield return Application.RequestUserAuthorization(mode);

            float timeElapsed = 0;
            while (_isUserRequestingPermission)
            {
                if (timeElapsed > 0.25f)
                {
                    _isUserRequestingPermission = false;
                    yield break;
                }
                timeElapsed += Time.deltaTime;

                yield return null;
            }
            yield break;
        }
#elif UNITY_ANDROID
        protected virtual IEnumerator RequestUserPermission(string permission)
        {
            _isUserRequestingPermission = true;
            UnityEngine.Android.Permission.RequestUserPermission(permission);

            float timeElapsed = 0;
            while (_isUserRequestingPermission)
            {
                if (timeElapsed > 0.25f)
                {
                    _isUserRequestingPermission = false;
                    yield break;
                }
                timeElapsed += Time.deltaTime;

                yield return null;
            }
            yield break;
        }
#endif
#endif
    }
}
