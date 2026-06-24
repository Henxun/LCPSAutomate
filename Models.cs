using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LCPSAutomate
{
    public class Records
    {
        public string Qr { get; set; }

        /// <summary>
        /// 是否提交
        /// </summary>
        public bool IsProcessed { get; set; }
    }

    public class FileReadRecord
    {
        public string FilePath { get; set; }
        public long LastPosition { get; set; }
    }
}
