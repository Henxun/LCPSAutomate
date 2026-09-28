using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LCPSAutomate
{
    public class Records
    {
        public string Qr { get; set; } = string.Empty;

        /// <summary>
        /// 是否已成功提交到 HandyClient。
        /// </summary>
        public bool IsProcessed { get; set; }

        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public string UpdatedAtUtc { get; set; } = string.Empty;
    }

    public class FileReadRecord
    {
        public string FilePath { get; set; } = string.Empty;
        public long LastPosition { get; set; }
    }
}
