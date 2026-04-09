using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NexoBridge.Models
{
    public class ImportJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public List<EppFilePayload> Files { get; set; } = new List<EppFilePayload>();
    }

    public class EppFilePayload
    {
        public string FileName { get; set; }
        public byte[] Content { get; set; }
    }
}