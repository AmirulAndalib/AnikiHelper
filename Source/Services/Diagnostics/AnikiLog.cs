using Playnite.SDK;
using System;

namespace AnikiHelper
{
    internal static class AnikiLog
    {
        private static bool Enabled => AnikiHelper.Instance?.Settings?.EnableDebugLogs == true;

        public static void Debug(ILogger logger, string message)
        {
            if (!Enabled || logger == null)
            {
                return;
            }

            logger.Debug(message);
        }

        public static void Debug(ILogger logger, Exception exception, string message)
        {
            if (!Enabled || logger == null)
            {
                return;
            }

            logger.Debug(exception, message);
        }
    }
}
