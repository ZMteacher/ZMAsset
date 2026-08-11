using System;
using System.Collections.Generic;

namespace ZM.Asset
{
    /// <summary>
    /// 统一管理所有热更新模块共享的下载线程预算。
    /// 该类只负责分配额度，不创建网络线程，因此单模块与多模块事务可以共用同一套上限。
    /// </summary>
    internal sealed class HotDownloadScheduler
    {
        private readonly List<HotAssetsModule> mActiveModules = new List<HotAssetsModule>();
        private int mTotalThreadCount = 1;

        /// <summary>
        /// 当前允许同时占用下载额度的模块数；协调器据此分批启动模块。
        /// </summary>
        public int ModuleConcurrency => mTotalThreadCount;

        /// <summary>
        /// 设置所有热更新任务共享的线程总预算，并立即重新计算当前模块额度。
        /// </summary>
        public void SetTotalThreadCount(int threadCount)
        {
            mTotalThreadCount = Math.Max(1, threadCount);
            Rebalance();
        }

        /// <summary>
        /// 登记一个正在下载或即将下载的模块；登记后会参与全局额度分配。
        /// </summary>
        public void Register(HotAssetsModule module)
        {
            if (module == null || mActiveModules.Contains(module))
                return;

            mActiveModules.Add(module);
            Rebalance();
        }

        /// <summary>
        /// 移除已结束模块，并把释放的额度重新分给剩余模块。
        /// </summary>
        public void Unregister(HotAssetsModule module)
        {
            if (module == null || !mActiveModules.Remove(module))
                return;

            Rebalance();
        }

        /// <summary>
        /// 按稳定登记顺序分配余数，例如 3 个线程分给 2 个模块时得到 2、1。
        /// </summary>
        private void Rebalance()
        {
            int moduleCount = mActiveModules.Count;
            if (moduleCount == 0)
                return;

            int[] allocations = CalculateThreadAllocations(mTotalThreadCount, moduleCount);
            for (int index = 0; index < moduleCount; index++)
            {
                mActiveModules[index].SetDownLoadThreadCount(allocations[index]);
            }
        }

        /// <summary>
        /// 纯计算入口便于验证全局预算不超额；上层必须保证模块数不大于线程数。
        /// </summary>
        internal static int[] CalculateThreadAllocations(int totalThreadCount, int moduleCount)
        {
            if (totalThreadCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(totalThreadCount));
            if (moduleCount <= 0 || moduleCount > totalThreadCount)
                throw new ArgumentOutOfRangeException(nameof(moduleCount));

            int[] allocations = new int[moduleCount];
            int baseCount = totalThreadCount / moduleCount;
            int remainder = totalThreadCount % moduleCount;
            for (int index = 0; index < moduleCount; index++)
                allocations[index] = baseCount + (index < remainder ? 1 : 0);
            return allocations;
        }
    }
}
