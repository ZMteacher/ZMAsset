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
            DontDestroyOnLoad(uiCamera);
            Camera camera = uiCamera.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Depth;
            camera.cullingMask = -1;      // 全层：兼容 Layer 0 的画布与 UI 层物体
            camera.orthographic = true;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;  // 必须覆盖 Canvas planeDistance=100
            camera.depth = 100;
        }

        if (GameObject.Find("UIRoot") == null)
        {
            GameObject uiRoot = new GameObject("UIRoot");
            DontDestroyOnLoad(uiRoot);
        }

        // 旧输入系统（activeInputHandler=0），无 Input System 包，用 StandaloneInputModule
        if (UnityEngine.EventSystems.EventSystem.current == null)
        {
            GameObject eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystem.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }
    }
}
