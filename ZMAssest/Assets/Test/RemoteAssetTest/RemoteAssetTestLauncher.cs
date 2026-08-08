using UnityEngine;

/// <summary>
/// RemoteAsset 测试工具启动器：挂到任意场景物体上，自动搭建 ZMUI 环境并弹出测试窗口。
/// </summary>
public class RemoteAssetTestLauncher : MonoBehaviour
{
    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        EnsureUIRoot();
        UIModule.Instance.Initialize();
    }

    private void Start()
    {
        UIModule.Instance.PopUpWindow<RemoteAssetTestWindow>();
    }

    /// <summary>
    /// ZMUI 依赖场景中的 UICamera/UIRoot，缺失时自动补齐（测试环境兜底）。
    /// </summary>
    private static void EnsureUIRoot()
    {
        if (GameObject.Find("UICamera") == null)
        {
            GameObject uiCamera = new GameObject("UICamera");
            Camera camera = uiCamera.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Depth;
            camera.cullingMask = 1 << 5; // UI Layer
            camera.orthographic = true;
            camera.nearClipPlane = -10f;
            camera.farClipPlane = 10f;
            camera.depth = 100;
        }

        if (GameObject.Find("UIRoot") == null)
        {
            new GameObject("UIRoot");
        }
    }
}
