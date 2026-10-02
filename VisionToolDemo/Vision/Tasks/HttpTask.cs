using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using OpenCvSharp;

namespace VisionToolDemo.Vision.Tasks
{
    /// <summary>
    /// HTTP 请求：把结果传给外部系统（MES / 看板 / Webhook），或从接口取参数。
    ///
    /// 工业现场"检测完把结果发出去"几乎是必选项；这一步用 .NET 自带的 HttpClient，
    /// **不需要任何额外依赖**（参考项目里对应的是 communication 类节点）。
    ///
    /// 字符串槽：0 URL   1 正文（JSON/文本，支持 {变量} 插值）   2 结果变量名（响应正文写进去）
    /// 结果：节点输出「是否存在(1/0)」= 是否 2xx，文字结果 = 响应正文（超出会截断）。
    /// 干跑只打印"本来会发什么"，**不发请求**。
    /// </summary>
    public class HttpTask : IVisionTask, IResultReporter, Automation.IAutomationNode,
        Automation.INodeStringSource
    {
        private static readonly HttpClient Client = new HttpClient();

        public string TaskName => "HTTP请求";

        public string LastSummary { get; private set; } = "";
        public Func<int, string> NodeStringProvider { get; set; }

        public int StatusCode { get; private set; }
        public string ResponseBody { get; private set; } = "";
        public bool Success { get; private set; }
        public bool Skipped { get; private set; }

        public TaskParamDesc[] ParamDescriptions =>
        [
            new TaskParamDesc { ParamName = "方法 0GET1POST2PUT", Min = 0, Max = 2, DefaultValue = 1,
                DisplayFormat = "方法:{0}", Group = "通讯",
                Tip = "POST（默认）用于上报结果；GET 用于取参数/探活。" },
            new TaskParamDesc { ParamName = "超时秒", Min = 1, Max = 600, DefaultValue = 15,
                DisplayFormat = "超时:{0}s", Group = "通讯",
                Tip = "网络不通时不要让整条流程卡死。" },
            new TaskParamDesc { ParamName = "正文类型 0JSON1文本2表单", Min = 0, Max = 2, DefaultValue = 0,
                DisplayFormat = "正文:{0}", Group = "通讯",
                Tip = "0 JSON（application/json）/ 1 纯文本 / 2 表单（application/x-www-form-urlencoded）。" },
            new TaskParamDesc { ParamName = "响应截断字符数", Min = 0, Max = 100000, DefaultValue = 1000,
                DisplayFormat = "截断:{0}", Group = "通讯",
                Tip = "响应正文最多留多少字符（0 = 不截断）。" },
        ];

        public Mat Execute(Mat srcMat, int[] paramValues)
        {
            LastSummary = "";
            StatusCode = 0;
            ResponseBody = "";
            Success = false;
            Skipped = false;

            Mat dst = VisionHelper.ToBgrCopy(srcMat);

            int method = Math.Clamp(paramValues.Length > 0 ? paramValues[0] : 1, 0, 2);
            int timeoutSec = paramValues.Length > 1 ? Math.Clamp(paramValues[1], 1, 600) : 15;
            int bodyKind = Math.Clamp(paramValues.Length > 2 ? paramValues[2] : 0, 0, 2);
            int maxChars = paramValues.Length > 3 ? Math.Max(0, paramValues[3]) : 1000;

            string url = Automation.AutomationContext.ExpandVariables((NodeStringProvider?.Invoke(0) ?? "").Trim());
            string body = Automation.AutomationContext.ExpandVariables(NodeStringProvider?.Invoke(1) ?? "");
            string varName = Automation.AutomationContext.ExpandVariables((NodeStringProvider?.Invoke(2) ?? "").Trim());

            if (url.Length == 0)
            {
                Skipped = true;
                LastSummary = "HTTP请求: 跳过 —— 没填 URL";
                return dst;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                Skipped = true;
                LastSummary = string.Format("HTTP请求: 跳过 —— URL 不合法（要 http:// 或 https:// 开头）：{0}", url);
                return dst;
            }

            string methodName = method switch { 0 => "GET", 2 => "PUT", _ => "POST" };

            if (Automation.AutomationContext.DryRun)
            {
                LastSummary = string.Format("HTTP请求:（干跑，未发送）{0} {1}{2}", methodName, url,
                    body.Length == 0 ? "" : "  正文 " + OneLine(body));
                return dst;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(new HttpMethod(methodName), uri);
                if (methodName != "GET" && body.Length > 0)
                {
                    string contentType = bodyKind switch
                    {
                        1 => "text/plain",
                        2 => "application/x-www-form-urlencoded",
                        _ => "application/json",
                    };
                    req.Content = new StringContent(body, Encoding.UTF8, contentType);
                }

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var resp = Client.Send(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                StatusCode = (int)resp.StatusCode;
                Success = resp.IsSuccessStatusCode;
                string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                text = text.Replace("\r\n", "\n").TrimEnd();
                if (maxChars > 0 && text.Length > maxChars) text = text.Substring(0, maxChars) + "…（已截断）";
                ResponseBody = text;
                sw.Stop();

                if (varName.Length > 0) Automation.AutomationContext.SetVariable(varName, ResponseBody);

                LastSummary = string.Format("HTTP请求: {0} {1} → {2} {3}，{4}ms{5}", methodName, url,
                    StatusCode, Success ? "(成功)" : "(失败)", sw.ElapsedMilliseconds,
                    ResponseBody.Length == 0 ? "" : "  响应: " + OneLine(ResponseBody));
                if (!Success) Skipped = true;      // 非 2xx 也算"这一步没成功"，配合跳过即停能刹车
                return dst;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Skipped = true;
                LastSummary = string.Format("HTTP请求: 失败 —— {0} {1}：{2}", methodName, url,
                    (ex.InnerException ?? ex).Message.Split('\n')[0]);
                return dst;
            }
        }

        private static string OneLine(string s)
        {
            string t = (s ?? "").Replace("\n", " ⏎ ");
            return t.Length <= 120 ? t : t.Substring(0, 120) + "…";
        }
    }
}
