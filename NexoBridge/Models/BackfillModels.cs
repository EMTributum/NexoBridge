using System.Collections.Generic;

namespace NexoBridge.Models
{
    public class BackfillClientRef
    {
        public string Nip { get; set; }
        public string DatabaseName { get; set; }
        public string ClientName { get; set; }
    }

    public class BackfillEnumerateJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public int Year { get; set; }
        public List<BackfillClientRef> Clients { get; set; } = new List<BackfillClientRef>();
    }

    public class BackfillManifestRow
    {
        public string ClientNip { get; set; }
        public string DatabaseName { get; set; }
        public string EntityType { get; set; }
        public int RachmistrzId { get; set; }
        public string VendorNip { get; set; }
        public string NumerDokumentu { get; set; }
        public int? RecordYear { get; set; }
        public bool HasPdf { get; set; }
        public string PdfBase64 { get; set; }
    }

    public class BackfillEnumerateReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } = "PENDING";
        public string Message { get; set; }
        public List<BackfillManifestRow> Rows { get; set; } = new List<BackfillManifestRow>();
        public List<string> ClientErrors { get; set; } = new List<string>();
        public int ClientsOk { get; set; }
        public int ClientsFailed { get; set; }
    }

    public class BackfillCommentRow
    {
        public string DatabaseName { get; set; }
        public string EntityType { get; set; }
        public int RachmistrzId { get; set; }
        public string ViewerUrl { get; set; }
        public string NumerDokumentu { get; set; }
    }

    public class BackfillWriteCommentsJob
    {
        public string JobId { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public List<BackfillCommentRow> Rows { get; set; } = new List<BackfillCommentRow>();
    }

    public class BackfillWriteCommentsReport
    {
        public string JobId { get; set; }
        public string Status { get; set; } = "PENDING";
        public string Message { get; set; }
        public int Written { get; set; }
        public int SkippedAlreadyLinked { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }
}
