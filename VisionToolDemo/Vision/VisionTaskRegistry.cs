using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VisionToolDemo.Vision
{
    public static class VisionTaskRegistry
    {
        private static readonly Dictionary<string, IVisionTask> _taskDict = [];
        private static readonly Dictionary<string, Type> _taskTypes = [];

        static VisionTaskRegistry()
        {
            // 反射扫描当前程序集所有实现 IVisionTask 的类，自动注册。
            // 用 GetLoadableTypes 而不是 GetTypes：程序集里混着 WPF 界面类型（引用
            // PresentationFramework），在无 WPF 的环境（CI/冒烟/无头测试）里 GetTypes
            // 会抛 ReflectionTypeLoadException 导致**整个注册表都加载不了**。
            // 这里跳过加载失败的类型，只注册能用的算子 —— 界面类型本来就不该进注册表。
            var assembly = Assembly.GetExecutingAssembly();
            var taskTypes = GetLoadableTypes(assembly)
                .Where(t => t.IsClass && !t.IsAbstract && t.GetInterfaces().Contains(typeof(IVisionTask)));

            foreach (var type in taskTypes)
            {
                if (Activator.CreateInstance(type) is IVisionTask task)
                {
                    _taskDict[task.TaskName] = task;
                    _taskTypes[task.TaskName] = type;
                }
            }
        }

        /// <summary>程序集可加载类型（跳过因缺少依赖程序集而加载失败的类型）</summary>
        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
        }

        /// <summary>获取所有工具名称（含自动化专用算子）</summary>
        public static List<string> GetAllTaskNames()
        {
            return _taskDict.Keys.ToList();
        }

        /// <summary>
        /// 获取**视觉流水线**可用的工具名称（排除自动化专用算子），供算子下拉框绑定。
        ///
        /// 下拉框必须用这个而不是 GetAllTaskNames：自动化算子不符合
        /// "输入图像 -> 输出图像"的流水线契约，混进来既会误导用户，
        /// 也可能让改参数这个动作产生真实的鼠标/键盘副作用。
        /// </summary>
        public static List<string> GetVisionToolNames()
        {
            return _taskDict
                .Where(kv => kv.Value is not Automation.IAutomationNode)
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary>该工具是否只用于自动化节点页</summary>
        public static bool IsAutomationOnly(string taskName)
        {
            return _taskDict.TryGetValue(taskName, out var t)
                && t is Automation.IAutomationNode;
        }

        /// <summary>根据名称获取任务实例</summary>
        public static IVisionTask GetTask(string taskName)
        {
            _taskDict.TryGetValue(taskName, out var task);
            return task;
        }

        /// <summary>
        /// 创建算子的全新实例（流水线加载/复制步骤用）。
        /// 注册表实例是按名称共享的，直接引用会让多个步骤共享同一份卡尺几何/模板状态。
        /// </summary>
        public static IVisionTask CreateTask(string taskName)
        {
            return _taskTypes.TryGetValue(taskName, out var type)
                ? Activator.CreateInstance(type) as IVisionTask
                : null;
        }
    }
}