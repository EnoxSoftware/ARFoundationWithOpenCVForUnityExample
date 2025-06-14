#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Scripting;
using System.IO;

[InitializeOnLoad]
public static class ARFoundationVersionRecorder
{
    static ListRequest _request;

    static ARFoundationVersionRecorder()
    {
        EditorApplication.delayCall += StartVersionCheck;
    }

    static void StartVersionCheck()
    {
        _request = Client.List(true); // true = include indirect dependencies
        EditorApplication.update += CheckRequest;
    }

    static void CheckRequest()
    {
        if (!_request.IsCompleted)
            return;

        if (_request.Status == StatusCode.Success)
        {
            foreach (var package in _request.Result)
            {
                if (package.name == "com.unity.xr.arfoundation")
                {
                    string version = package.version;
                    string path = "Assets/ARFoundationWithOpenCVForUnityExample/Resources/ARFoundationVersion.txt";

                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, version);

                    //Debug.Log($"[ARFoundationVersionRecorder] ARFoundation version: {version} written to: {path}");
                    break;
                }
            }
        }
        else if (_request.Status >= StatusCode.Failure)
        {
            Debug.LogError($"[ARFoundationVersionRecorder] Failed to get ARFoundation version: {_request.Error.message}");
        }

        EditorApplication.update -= CheckRequest;
    }
}
#endif
