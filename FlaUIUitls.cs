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
        public static bool DetectWindow()
        {
            var logger = NLog.LogManager.GetCurrentClassLogger();
            var isReady = true;
            try
            {
                using var automation = new UIA3Automation();
                var desktop = automation.GetDesktop();
                var winElement = desktop.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window).And(cf.ByName(TARGET_WINDOW_TITLE)));
                if (winElement != null)
                {
                    var window = winElement.AsWindow();
                    var expected = window.FindFirstDescendant(cf => cf.ByAutomationId(TARGET_PANEL_AUTOMATION_ID));
                    if (expected == null)
                    {
                        logger.Warn($"窗口存在，但未检测到期望元素（{TARGET_PANEL_AUTOMATION_ID}）。");
                        isReady = false;
                    }
                }
                else
                {
                    logger.Warn($"未找到窗口：\"{TARGET_WINDOW_TITLE}\"。");
                    isReady = false;
                }
            }
            catch (Exception ex)
            {
                logger.Error("监测循环异常: " + ex.Message);
                isReady = false;
            }
            return isReady;
        }
    }
}
