using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using ZM.Asset;

namespace ZM.Asset.Examples
{
    /// <summary>
    /// Loading 场景中的 Shader 变体预热示例。
    /// 将组件挂到常驻对象后，可自动预热，也可由 UI Button 调用 StartWarmUp/CancelWarmUp。
    /// </summary>
    public sealed class ShaderVariantPrewarmExample : MonoBehaviour
    {
        [Header("生成配置")]
        [SerializeField] private string m_ModuleName = "Hall";
        [SerializeField] private string m_ProfileName = "Default";

        [Header("分帧策略")]
        [SerializeField, Min(1)] private int m_VariantsPerFrame = 32;
        [SerializeField, Min(0.1f)] private float m_TimeoutSeconds = 30f;
        [SerializeField] private bool m_WarmUpOnStart = true;

        public event Action<ShaderVariantPrewarmProgress> ProgressChanged;
        public event Action<ShaderVariantPrewarmResult> Completed;

        public bool IsRunning => m_OperationCancellation != null;
        public float NormalizedProgress { get; private set; }
        public ShaderVariantPrewarmResult LastResult { get; private set; }

        private CancellationToken m_DestroyCancellationToken;
        private CancellationTokenSource m_OperationCancellation;

        private void Awake()
        {
            m_DestroyCancellationToken = this.GetCancellationTokenOnDestroy();
        }

        private void Start()
        {
            if (m_WarmUpOnStart)
                StartWarmUp();
        }

        /// <summary>
        /// 启动一次预热。重复点击不会创建并发任务。
        /// </summary>
        public void StartWarmUp()
        {
            if (IsRunning)
            {
                Debug.LogWarning($"Shader 变体正在预热，已忽略重复请求：{m_ModuleName}/{m_ProfileName}", this);
                return;
            }

            if (string.IsNullOrWhiteSpace(m_ModuleName) || string.IsNullOrWhiteSpace(m_ProfileName))
            {
                Debug.LogError("Shader 预热模块名和配置档不能为空。", this);
                return;
            }

            NormalizedProgress = 0f;
            LastResult = null;
            CancellationTokenSource operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(m_DestroyCancellationToken);
            m_OperationCancellation = operationCancellation;
            WarmUpAsync(operationCancellation).Forget(Debug.LogException);
        }

        /// <summary>
        /// 取消当前预热；适用于切换 Loading 流程或用户返回登录界面。
        /// </summary>
        public void CancelWarmUp()
        {
            m_OperationCancellation?.Cancel();
        }

        private async UniTask WarmUpAsync(CancellationTokenSource operationCancellation)
        {
            try
            {
                var options = new ShaderVariantPrewarmOptions(
                    m_VariantsPerFrame,
                    m_TimeoutSeconds);
                var progress = new Progress<ShaderVariantPrewarmProgress>(OnProgress);

                ShaderVariantPrewarmResult result = await ZMAsset.ShaderVariants.WarmUpModuleAsync(
                    m_ModuleName.Trim(),
                    m_ProfileName.Trim(),
                    options,
                    progress,
                    operationCancellation.Token);

                LastResult = result;
                if (result.IsSuccess)
                    Debug.Log(
                        $"Shader 预热完成：{result.ModuleName}/{result.ProfileName}，" +
                        $"{result.WarmedVariantCount}/{result.TotalVariantCount}，" +
                        $"{result.ElapsedSeconds:F2}s（{result.Status}）",
                        this);
                else if (result.Status != ShaderVariantPrewarmStatus.Cancelled)
                    Debug.LogError($"Shader 预热失败：{result.Status}，{result.Message}", this);

                Completed?.Invoke(result);
            }
            finally
            {
                if (ReferenceEquals(m_OperationCancellation, operationCancellation))
                    m_OperationCancellation = null;
                operationCancellation.Dispose();
            }
        }

        private void OnProgress(ShaderVariantPrewarmProgress progress)
        {
            NormalizedProgress = progress.NormalizedProgress;
            ProgressChanged?.Invoke(progress);
        }

        private void OnValidate()
        {
            m_VariantsPerFrame = Mathf.Max(1, m_VariantsPerFrame);
            m_TimeoutSeconds = Mathf.Max(0.1f, m_TimeoutSeconds);
        }

        private void OnDestroy()
        {
            m_OperationCancellation?.Cancel();
        }
    }
}
