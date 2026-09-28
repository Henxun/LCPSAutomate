using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LCPSAutomate
{
    public class FlaUIUitls
    {
        public static readonly string TARGET_WINDOW_TITLE = "HandyClient";
        public static readonly string TARGET_PANEL_AUTOMATION_ID = "Form_Main_New_3_5";
        public static readonly string TARGET_TEXT_BOX_AUTOMATION_ID = "txb_prodBatch";

        // UIA3/COM 调用统一串行化，避免窗口检测与 QR 提交同时访问 UI Automation。
        internal static SemaphoreSlim AutomationGate { get; } = new SemaphoreSlim(1, 1);

        public static bool DetectWindow()
        {
            var logger = NLog.LogManager.GetCurrentClassLogger();
            AutomationGate.Wait();
            try
            {
                const int maxAttempts = 3;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        using var automation = new UIA3Automation();
                        var desktop = automation.GetDesktop();
                        var winElement = desktop.FindFirstChild(cf =>
                            cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)
                                .And(cf.ByName(TARGET_WINDOW_TITLE)));
                        if (winElement == null)
                        {
                            logger.Warn($"未找到窗口：\"{TARGET_WINDOW_TITLE}\"。");
                            return false;
                        }

                        var window = winElement.AsWindow();
                        var expected = window.FindFirstDescendant(cf => cf.ByAutomationId(TARGET_PANEL_AUTOMATION_ID));
                        if (expected == null)
                        {
                            logger.Warn($"窗口存在，但未检测到期望元素（{TARGET_PANEL_AUTOMATION_ID}）。");
                            return false;
                        }

                        return true;
                    }
                    catch (Exception ex) when (attempt < maxAttempts && IsTransientUiAutomationException(ex))
                    {
                        logger.Debug(ex, $"UI Automation 瞬时异常，准备重试 {attempt}/{maxAttempts}");
                        Thread.Sleep(150);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"监测循环异常，已尝试 {attempt}/{maxAttempts} 次");
                        return false;
                    }
                }

                return false;
            }
            finally
            {
                AutomationGate.Release();
            }
        }

        private static bool IsTransientUiAutomationException(Exception ex)
        {
            return ex is System.Runtime.InteropServices.COMException
                || ex is System.ComponentModel.Win32Exception
                || (ex.InnerException != null && IsTransientUiAutomationException(ex.InnerException));
        }
    }
}
